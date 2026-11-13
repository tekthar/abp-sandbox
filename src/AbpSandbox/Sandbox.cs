using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AbpSandbox;

/// <summary>
/// The module's <see cref="ISandbox"/>: it checks a request (<see cref="SandboxPolicy"/>), prepares or finds its
/// prepared image, has the provider make the container, and removes it (a run) or keeps it (a session). Runs and
/// preparations take turns past <see cref="SandboxOptions.MaxRunsAtOnce"/>.
/// </summary>
public sealed class Sandbox : ISandbox, IDisposable
{
    private readonly ISandboxProvider provider;
    private readonly SandboxPolicy policy;
    private readonly TimeProvider clock;
    private readonly ILogger<Sandbox> logger;
    private readonly SemaphoreSlim runs;
    private readonly SemaphoreSlim opening = new(1, 1);
    private readonly ConcurrentDictionary<(string Owner, string Key), SandboxSession> sessions = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> preparing = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> preparedUsed = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentQueue<Warm>> warmPool = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim warming = new(1, 1);

    public Sandbox(ISandboxProvider provider, IOptions<SandboxOptions> options, TimeProvider clock, ILogger<Sandbox> logger)
    {
        this.provider = provider;
        policy = new SandboxPolicy(options.Value);
        this.clock = clock;
        this.logger = logger;
        runs = new SemaphoreSlim(Math.Max(1, options.Value.MaxRunsAtOnce));
        Node = string.IsNullOrWhiteSpace(options.Value.Node) ? $"{System.Environment.MachineName}-{System.Environment.ProcessId}" : options.Value.Node;
    }

    /// <summary>This server's name on the containers it makes.</summary>
    public string Node { get; }

    public SandboxCapabilities Capabilities => provider.Capabilities;

    internal SandboxPolicy Policy => policy;

    internal ISandboxProvider Provider => provider;

    internal TimeProvider Clock => clock;

    internal ILogger Logger => logger;

    public async Task<SandboxResult> RunAsync(SandboxRequest request, CancellationToken cancellationToken = default)
    {
        string image = policy.Image(request.Image);
        SandboxPolicy.Resolved limits = policy.Limits(request.Limits);
        SandboxPolicy.Command(request.Command);
        SandboxPolicy.Environment(request.Environment);
        policy.Input(request.Files);
        policy.Outputs(request.Outputs);
        CheckPreparation(request.Preparation);

        await runs.WaitAsync(cancellationToken);
        try
        {
            // A warm container (started before, never used) serves a run that asks for nothing it wasn't made with.
            if (request.Preparation is null && request.Environment.Count == 0 && request.Limits.MemoryBytes is null
                && request.Limits.Cpus is null && request.Limits.Processes is null && TakeWarm(request.Image) is { } warm)
            {
                TopUpLater(request.Image);
                try
                {
                    await warm.PutFilesAsync(request.Files, cancellationToken);
                    SandboxExecResult ran = await warm.ExecAsync(request.Command, limits.Time, policy.Options.MaxConsoleBytes, cancellationToken);
                    SandboxFiles got = request.Outputs.Count == 0
                        ? SandboxFiles.None
                        : await warm.GetFilesAsync(request.Outputs, policy.Options.MaxOutputBytes, cancellationToken);
                    return Result(ran, got, cached: false, fromWarm: true);
                }
                finally
                {
                    await RemoveQuietlyAsync(warm);
                }
            }

            (string reference, bool cached) = request.Preparation is null
                ? (image, false)
                : await PreparedAsync(request.Owner, image, request.Preparation, limits, cancellationToken);
            ISandboxContainer container = await provider.CreateAsync(new SandboxContainerSpec
            {
                Image = reference,
                Command = request.Command,
                Environment = request.Environment,
                MemoryBytes = limits.MemoryBytes,
                Cpus = limits.Cpus,
                Processes = limits.Processes,
                User = policy.Options.User,
                Kind = "run",
                Node = Node,
                ExpiresAt = clock.GetUtcNow() + limits.Time + TimeSpan.FromMinutes(5),
                OwnerHash = SandboxPolicy.OwnerHash(request.Owner),
            }, cancellationToken);
            try
            {
                await container.PutFilesAsync(request.Files, cancellationToken);
                SandboxExecResult run = await container.RunAsync(limits.Time, policy.Options.MaxConsoleBytes, cancellationToken);
                SandboxFiles files = request.Outputs.Count == 0
                    ? SandboxFiles.None
                    : await container.GetFilesAsync(request.Outputs, policy.Options.MaxOutputBytes, cancellationToken);
                return Result(run, files, cached, fromWarm: false);
            }
            finally
            {
                await RemoveQuietlyAsync(container);
            }
        }
        finally
        {
            runs.Release();
        }

        static SandboxResult Result(SandboxExecResult run, SandboxFiles files, bool cached, bool fromWarm) => new()
        {
            ExitCode = run.ExitCode,
            TimedOut = run.TimedOut,
            Stdout = run.Stdout,
            Stderr = run.Stderr,
            OutputTruncated = run.OutputTruncated,
            Duration = run.Duration,
            Files = files,
            PreparedFromCache = cached,
            FromWarmPool = fromWarm,
        };
    }

    /// <summary>
    /// Fills the warm pool (<see cref="SandboxOptions.WarmPool"/>): containers started with the default limits and kept
    /// up, each to serve one run and be removed, so a run doesn't wait for a container to start. Ones older than
    /// <see cref="SandboxOptions.WarmLifetime"/> are replaced. Gives how many it started.
    /// </summary>
    public async Task<int> TopUpWarmAsync(CancellationToken cancellationToken = default)
    {
        await warming.WaitAsync(cancellationToken);
        try
        {
            return await FillWarmAsync(cancellationToken);
        }
        finally
        {
            warming.Release();
        }
    }

    private async Task<int> FillWarmAsync(CancellationToken cancellationToken)
    {
        int started = 0;
        DateTimeOffset now = clock.GetUtcNow();
        foreach ((string name, int count) in policy.Options.WarmPool)
        {
            ConcurrentQueue<Warm> queue = warmPool.GetOrAdd(name, _ => new ConcurrentQueue<Warm>());
            int kept = 0;
            for (int i = queue.Count; i > 0 && queue.TryDequeue(out Warm? w); i--)
            {
                if (now - w.MadeAt >= policy.Options.WarmLifetime)
                {
                    await RemoveQuietlyAsync(w.Container);
                }
                else
                {
                    queue.Enqueue(w);
                    kept++;
                }
            }

            for (int i = kept; i < Math.Min(count, 20); i++)
            {
                SandboxPolicy.Resolved limits = policy.Limits(new SandboxLimits());
                ISandboxContainer container = await provider.CreateAsync(new SandboxContainerSpec
                {
                    Image = policy.Image(name),
                    Command = null,
                    MemoryBytes = limits.MemoryBytes,
                    Cpus = limits.Cpus,
                    Processes = limits.Processes,
                    User = policy.Options.User,
                    Kind = "warm",
                    Node = Node,
                    ExpiresAt = now + policy.Options.WarmLifetime + TimeSpan.FromMinutes(5),
                    OwnerHash = SandboxPolicy.OwnerHash("warm"),
                }, cancellationToken);
                try
                {
                    await container.StartAsync(cancellationToken);
                }
                catch
                {
                    await RemoveQuietlyAsync(container);
                    throw;
                }

                queue.Enqueue(new Warm(container, now));
                started++;
            }
        }

        return started;
    }

    private ISandboxContainer? TakeWarm(string name)
    {
        if (!warmPool.TryGetValue(name, out ConcurrentQueue<Warm>? queue))
        {
            return null;
        }

        DateTimeOffset now = clock.GetUtcNow();
        while (queue.TryDequeue(out Warm? warm))
        {
            if (now - warm.MadeAt < policy.Options.WarmLifetime)
            {
                return warm.Container;
            }

            _ = RemoveQuietlyAsync(warm.Container);
        }

        return null;
    }

    /// <summary>Starts a container in place of the one taken, without making the run wait.</summary>
    private void TopUpLater(string name) => _ = Task.Run(async () =>
    {
        try
        {
            await TopUpWarmAsync();
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "The sandbox's warm pool couldn't be filled after a run took from {Image}.", name);
        }
    });

    private sealed record Warm(ISandboxContainer Container, DateTimeOffset MadeAt);

    public async Task<ISandboxSession> OpenSessionAsync(SandboxSessionRequest request, CancellationToken cancellationToken = default)
    {
        if (!provider.Capabilities.Sessions)
        {
            throw new SandboxRefusedException($"The sandbox's provider ({provider.Name}) keeps no sessions.");
        }

        if (string.IsNullOrWhiteSpace(request.Owner) || string.IsNullOrWhiteSpace(request.Key))
        {
            throw new SandboxRefusedException("A sandbox session needs an owner and a key.");
        }

        string image = policy.Image(request.Image);
        SandboxPolicy.Resolved limits = policy.Limits(request.Limits);
        TimeSpan idle = policy.IdleTime(request.IdleTime);
        SandboxPolicy.Environment(request.Environment);
        CheckPreparation(request.Preparation);
        string shape = request.Image + "\n" + (request.Preparation is null ? "" : SandboxPolicy.PreparationKey(image, request.Owner, request.Preparation));

        await opening.WaitAsync(cancellationToken);
        try
        {
            if (sessions.TryGetValue((request.Owner, request.Key), out SandboxSession? open))
            {
                if (!open.IsClosed && !open.Expired(clock.GetUtcNow()) && open.Shape == shape)
                {
                    open.Touch();
                    return open;
                }

                await open.CloseAsync(CancellationToken.None);
            }

            List<SandboxSession> live = [.. sessions.Values.Where(s => !s.IsClosed)];
            if (live.Count >= policy.Options.MaxSessions)
            {
                throw new SandboxRefusedException("The sandbox has as many sessions open as it keeps; try again later.");
            }

            if (live.Count(s => s.Owner == request.Owner) >= policy.Options.MaxSessionsPerOwner)
            {
                throw new SandboxRefusedException($"At most {policy.Options.MaxSessionsPerOwner} sandbox sessions are open for one owner.");
            }

            string reference = image;
            if (request.Preparation is not null)
            {
                await runs.WaitAsync(cancellationToken);
                try
                {
                    (reference, _) = await PreparedAsync(request.Owner, image, request.Preparation, limits, cancellationToken);
                }
                finally
                {
                    runs.Release();
                }
            }

            DateTimeOffset now = clock.GetUtcNow();
            ISandboxContainer container = await provider.CreateAsync(new SandboxContainerSpec
            {
                Image = reference,
                Command = null,
                Environment = request.Environment,
                MemoryBytes = limits.MemoryBytes,
                Cpus = limits.Cpus,
                Processes = limits.Processes,
                User = policy.Options.User,
                Kind = "session",
                Node = Node,
                ExpiresAt = now + policy.Options.SessionLifetime + TimeSpan.FromMinutes(5),
                OwnerHash = SandboxPolicy.OwnerHash(request.Owner),
            }, cancellationToken);
            try
            {
                await container.StartAsync(cancellationToken);
            }
            catch
            {
                await RemoveQuietlyAsync(container);
                throw;
            }

            SandboxSession session = new(this, container, request.Owner, request.Key, request.Image, shape, limits.Time, idle, now);
            sessions[(request.Owner, request.Key)] = session;
            logger.LogInformation("Sandbox session {Session} opened ({Image}).", session.Id, request.Image);
            return session;
        }
        finally
        {
            opening.Release();
        }
    }

    public ISandboxSession? FindSession(string owner, string key) =>
        sessions.TryGetValue((owner, key), out SandboxSession? s) && !s.IsClosed && !s.Expired(clock.GetUtcNow()) ? s : null;

    /// <summary>Closes the sessions past their idle time or lifetime; gives how many.</summary>
    public async Task<int> CloseExpiredAsync(CancellationToken cancellationToken = default)
    {
        int closed = 0;
        DateTimeOffset now = clock.GetUtcNow();
        foreach (((string, string) key, SandboxSession session) in sessions)
        {
            if (session.IsClosed || session.Expired(now))
            {
                await session.CloseAsync(cancellationToken);
                sessions.TryRemove(new KeyValuePair<(string, string), SandboxSession>(key, session));
                closed++;
            }
        }

        return closed;
    }

    /// <summary>Removes prepared images past the count, the ones used longest ago first; gives how many.</summary>
    public async Task<int> EvictPreparedAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<SandboxPreparedImage> images = await provider.ListPreparedAsync(cancellationToken);
        int extra = images.Count - Math.Max(0, policy.Options.MaxPreparedImages);
        if (extra <= 0)
        {
            return 0;
        }

        int removed = 0;
        foreach (SandboxPreparedImage image in images.OrderBy(i => preparedUsed.TryGetValue(i.Key, out DateTimeOffset used) ? used : i.CreatedAt).Take(extra))
        {
            try
            {
                await provider.RemovePreparedAsync(image.Reference, cancellationToken);
                preparedUsed.TryRemove(image.Key, out _);
                removed++;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogWarning(e, "A prepared sandbox image ({Reference}) couldn't be removed; it may be in use.", image.Reference);
            }
        }

        return removed;
    }

    /// <summary>Closes every session and removes the warm pool (the host is stopping).</summary>
    public async Task CloseAllAsync()
    {
        foreach (ConcurrentQueue<Warm> queue in warmPool.Values)
        {
            while (queue.TryDequeue(out Warm? warm))
            {
                await RemoveQuietlyAsync(warm.Container);
            }
        }

        foreach (SandboxSession session in sessions.Values)
        {
            await session.CloseAsync(CancellationToken.None);
        }

        sessions.Clear();
    }

    public void Dispose()
    {
        runs.Dispose();
        opening.Dispose();
        warming.Dispose();
    }

    internal void Forget(SandboxSession session) =>
        sessions.TryRemove(new KeyValuePair<(string, string), SandboxSession>((session.Owner, session.Key), session));

    internal async Task RemoveQuietlyAsync(ISandboxContainer container)
    {
        try
        {
            await container.RemoveAsync(CancellationToken.None);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "A sandbox container ({Container}) couldn't be removed; the sweep will remove it.", container.Id);
        }
    }

    private void CheckPreparation(SandboxPreparation? preparation)
    {
        if (preparation is null)
        {
            return;
        }

        if (!provider.Capabilities.Preparation)
        {
            throw new SandboxRefusedException($"The sandbox's provider ({provider.Name}) can't prepare images.");
        }

        if (preparation.Steps.Count is 0 or > 50)
        {
            throw new SandboxRefusedException("A sandbox preparation has between 1 and 50 steps.");
        }

        foreach (IReadOnlyList<string> step in preparation.Steps)
        {
            SandboxPolicy.Command(step);
        }

        policy.Input(preparation.Files);
    }

    /// <summary>The prepared image for a request: the one saved under its key, or one made now (once, even when asked for at once).</summary>
    private async Task<(string Reference, bool Cached)> PreparedAsync(string owner, string image, SandboxPreparation preparation, SandboxPolicy.Resolved limits, CancellationToken cancellationToken)
    {
        string key = SandboxPolicy.PreparationKey(image, owner, preparation);
        SemaphoreSlim gate = preparing.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (await provider.FindPreparedAsync(key, cancellationToken) is { } found)
            {
                preparedUsed[key] = clock.GetUtcNow();
                return (found, true);
            }

            DateTimeOffset deadline = clock.GetUtcNow() + policy.Options.PreparationTime;
            ISandboxContainer container = await provider.CreateAsync(new SandboxContainerSpec
            {
                Image = image,
                Command = null,
                MemoryBytes = limits.MemoryBytes,
                Cpus = limits.Cpus,
                Processes = limits.Processes,
                User = null,
                Preparation = true,
                Kind = "prepare",
                Node = Node,
                ExpiresAt = deadline + TimeSpan.FromMinutes(5),
                OwnerHash = SandboxPolicy.OwnerHash(preparation.Shared ? "shared" : owner),
            }, cancellationToken);
            try
            {
                await container.StartAsync(cancellationToken);
                await container.PutFilesAsync(preparation.Files, cancellationToken);
                for (int i = 0; i < preparation.Steps.Count; i++)
                {
                    TimeSpan left = deadline - clock.GetUtcNow();
                    SandboxExecResult result = left <= TimeSpan.Zero
                        ? new SandboxExecResult { ExitCode = -1, TimedOut = true, Stdout = "", Stderr = "", OutputTruncated = false, Duration = TimeSpan.Zero }
                        : await container.ExecAsync(preparation.Steps[i], left, policy.Options.MaxConsoleBytes, cancellationToken);
                    if (!result.Succeeded)
                    {
                        throw new SandboxPreparationFailedException(i, result);
                    }
                }

                string reference = await provider.SavePreparedAsync(container, key, cancellationToken);
                preparedUsed[key] = clock.GetUtcNow();
                logger.LogInformation("Sandbox image prepared ({Key}).", key[..16]);
                return (reference, false);
            }
            finally
            {
                await RemoveQuietlyAsync(container);
            }
        }
        finally
        {
            gate.Release();
        }
    }
}
