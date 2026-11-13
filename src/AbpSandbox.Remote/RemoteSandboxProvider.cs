using System.Formats.Tar;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AbpSandbox.Remote;

/// <summary>
/// A provider over a cloud's tasks and object storage (docs/adr/0002). A container is a task started at once with a
/// manifest of signed links; each call on it is a job: a tar written to the store, which the task's courier fetches,
/// and a result tar the courier writes back, which this reads as untrusted (regular files only, within the caps).
/// Sessions and the warm pool work; preparation needs a build service and is refused.
/// </summary>
public class RemoteSandboxProvider(
    ISandboxTaskPlatform platform,
    ISandboxObjectStore store,
    IOptions<RemoteSandboxOptions> options,
    IOptions<SandboxOptions> sandboxOptions,
    TimeProvider clock,
    ILogger<RemoteSandboxProvider> logger) : ISandboxProvider
{
    public virtual string Name => "remote";

    private ISandboxTaskPlatform Platform => platform;

    private ISandboxObjectStore Store => store;

    private TimeProvider Clock => clock;

    private IOptions<RemoteSandboxOptions> Options => options;

    public SandboxCapabilities Capabilities { get; } = new(Sessions: true, Preparation: false);

    public async Task<SandboxAvailability> CheckAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(options.Value.RunnerImage))
        {
            return SandboxAvailability.No("The sandbox's runner image isn't set (AbpSandbox:Remote:RunnerImage).");
        }

        try
        {
            SandboxAvailability tasks = await platform.CheckAsync(cancellationToken);
            return tasks.Available ? await store.CheckAsync(cancellationToken) : tasks;
        }
        catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return SandboxAvailability.No($"The sandbox's cloud ({Name}) doesn't answer: {e.Message}");
        }
    }

    public async Task<ISandboxContainer> CreateAsync(SandboxContainerSpec spec, CancellationToken cancellationToken = default)
    {
        if (spec.Preparation)
        {
            throw new SandboxRefusedException($"The sandbox's provider ({Name}) can't prepare images.");
        }

        if (string.IsNullOrWhiteSpace(options.Value.RunnerImage))
        {
            throw new SandboxRefusedException("The sandbox's runner image isn't set (AbpSandbox:Remote:RunnerImage).");
        }

        string id = Guid.NewGuid().ToString("N");
        string prefix = $"{options.Value.Prefix}{id}/";
        int jobs = spec.Kind == "session" ? options.Value.MaxSessionJobs : 8;
        DateTimeOffset expires = spec.ExpiresAt;
        long idle = Math.Max(60, (long)(expires - clock.GetUtcNow()).TotalSeconds);
        long max = sandboxOptions.Value.MaxOutputBytes + (2 * sandboxOptions.Value.MaxConsoleBytes) + (1024 * 1024);

        StringBuilder manifest = new();
        manifest.Append(CultureInfo.InvariantCulture, $"idle {idle}\nmax {max}\n");
        for (int n = 1; n <= jobs; n++)
        {
            manifest.Append(CultureInfo.InvariantCulture, $"in {n} {store.SignGet($"{prefix}in/{n}", expires)}\n");
            manifest.Append(CultureInfo.InvariantCulture, $"out {n} {store.SignPut($"{prefix}out/{n}", expires)}\n");
        }

        await store.PutAsync(prefix + "manifest", Encoding.UTF8.GetBytes(manifest.ToString()), cancellationToken);
        string task;
        try
        {
            task = await platform.StartAsync(new SandboxTaskSpec
            {
                Name = $"abpsandbox-{spec.Kind}-{id[..12]}",
                WorkImage = spec.Image,
                RunnerImage = options.Value.RunnerImage,
                ManifestUrl = store.SignGet(prefix + "manifest", clock.GetUtcNow() + options.Value.StartTime),
                User = spec.User ?? sandboxOptions.Value.User,
                Environment = spec.Environment,
                MemoryBytes = spec.MemoryBytes,
                Cpus = spec.Cpus,
                Processes = spec.Processes,
                Kind = spec.Kind,
                Node = spec.Node,
                ExpiresAt = expires,
                OwnerHash = spec.OwnerHash,
            }, cancellationToken);
        }
        catch
        {
            await store.DeletePrefixAsync(prefix, CancellationToken.None);
            throw;
        }

        logger.LogDebug("Sandbox task {Task} started ({Kind}).", task, spec.Kind);
        return new RemoteContainer(this, task, prefix, spec.Command, jobs);
    }

    public Task<string?> FindPreparedAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);

    public Task<string> SavePreparedAsync(ISandboxContainer container, string key, CancellationToken cancellationToken = default) =>
        throw new SandboxRefusedException($"The sandbox's provider ({Name}) can't prepare images.");

    public Task<IReadOnlyList<SandboxPreparedImage>> ListPreparedAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<SandboxPreparedImage>>([]);

    public Task RemovePreparedAsync(string reference, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public async Task<int> SweepAsync(DateTimeOffset now, string? node, CancellationToken cancellationToken = default)
    {
        int stopped = 0;
        foreach (SandboxTaskInfo task in await platform.ListAsync(cancellationToken))
        {
            if (task.ExpiresAt <= now || (node is not null && task.Node == node))
            {
                await platform.StopAsync(task.Id, cancellationToken);
                stopped++;
            }
        }

        return stopped;
    }

    /// <summary>A task of the provider's, driven job by job.</summary>
    private sealed class RemoteContainer(RemoteSandboxProvider provider, string task, string prefix, IReadOnlyList<string>? command, int jobs) : ISandboxContainer
    {
        private int next = 1;
        private bool started;
        private int removed;

        public string Id => task;

        public async Task PutFilesAsync(IReadOnlyList<SandboxFile> files, CancellationToken cancellationToken = default)
        {
            if (files.Count == 0)
            {
                return; // the init made the work folder
            }

            Dictionary<string, byte[]> job = new(StringComparer.Ordinal) { ["kind"] = "put"u8.ToArray() };
            foreach (SandboxFile file in files)
            {
                job["files/" + file.Path] = file.Content;
            }

            Result result = await JobAsync(job, TimeSpan.FromMinutes(1), cancellationToken);
            if (result.TimedOut || result.Exit != 0)
            {
                throw new SandboxRefusedException("The sandbox couldn't put the files in its work folder" + (result.TimedOut ? " in time." : $": {result.Text("stderr")}"));
            }
        }

        public Task<SandboxExecResult> RunAsync(TimeSpan time, long maxOutputBytes, CancellationToken cancellationToken = default) =>
            command is null
                ? throw new InvalidOperationException("A container made to be kept has no command of its own: exec in it.")
                : ExecAsync(command, time, maxOutputBytes, cancellationToken);

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask; // started when made

        public async Task<SandboxExecResult> ExecAsync(IReadOnlyList<string> command, TimeSpan time, long maxOutputBytes, CancellationToken cancellationToken = default)
        {
            int seconds = Math.Max(1, (int)Math.Ceiling(time.TotalSeconds));
            Result result = await JobAsync(new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["kind"] = "exec"u8.ToArray(),
                ["time"] = Encoding.ASCII.GetBytes(seconds.ToString(CultureInfo.InvariantCulture)),
                ["cap"] = Encoding.ASCII.GetBytes(maxOutputBytes.ToString(CultureInfo.InvariantCulture)),
                ["cmd.sh"] = Encoding.UTF8.GetBytes("exec " + string.Join(' ', command.Select(Quote)) + "\n"),
            }, time, cancellationToken);
            bool timedOut = result.TimedOut || result.Has("timedout");
            return new SandboxExecResult
            {
                ExitCode = timedOut ? -1 : result.Exit,
                TimedOut = timedOut,
                Stdout = result.Text("stdout"),
                Stderr = result.Text("stderr"),
                OutputTruncated = result.Truncated || result.Has("truncated"),
                Duration = result.Duration,
            };
        }

        public async Task<SandboxFiles> GetFilesAsync(IReadOnlyList<string> paths, long maxBytes, CancellationToken cancellationToken = default)
        {
            Result result = await JobAsync(new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["kind"] = "get"u8.ToArray(),
                ["paths"] = Encoding.UTF8.GetBytes(string.Join('\n', paths) + "\n"),
            }, TimeSpan.FromMinutes(1), cancellationToken);
            if (result.TimedOut || !result.Files.TryGetValue("files.tar", out byte[]? tar))
            {
                return new SandboxFiles([], result.Truncated);
            }

            return ReadFiles(tar, maxBytes, result.Truncated);
        }

        public async Task RemoveAsync(CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref removed, 1) == 1)
            {
                return;
            }

            try
            {
                if (started && next <= jobs)
                {
                    // Ask the task to stop itself; it's stopped from outside too.
                    await provider.Store.PutAsync($"{prefix}in/{next}", Tar(new Dictionary<string, byte[]> { ["kind"] = "stop"u8.ToArray() }), cancellationToken);
                }
            }
            finally
            {
                await provider.Platform.StopAsync(task, cancellationToken);
                await provider.Store.DeletePrefixAsync(prefix, cancellationToken);
            }
        }

        /// <summary>Writes job N, and waits for its result within its time, the grace, and on the first job the task's start.</summary>
        private async Task<Result> JobAsync(Dictionary<string, byte[]> job, TimeSpan time, CancellationToken cancellationToken)
        {
            if (next > jobs)
            {
                throw new SandboxRefusedException("The sandbox session has run all the jobs it can; open it again.");
            }

            int n = next++;
            DateTimeOffset begun = provider.Clock.GetUtcNow();
            DateTimeOffset deadline = begun + time + provider.Options.Value.JobGrace + (started ? TimeSpan.Zero : provider.Options.Value.StartTime);
            started = true;
            await provider.Store.PutAsync($"{prefix}in/{n}", Tar(job), cancellationToken);
            DateTimeOffset checkedTask = begun;
            while (true)
            {
                if (await provider.Store.GetAsync($"{prefix}out/{n}", cancellationToken) is { } bytes)
                {
                    return Result.Read(bytes, provider.Clock.GetUtcNow() - begun);
                }

                DateTimeOffset now = provider.Clock.GetUtcNow();
                if (now >= deadline)
                {
                    return Result.Lost(now - begun);
                }

                if (now - checkedTask >= TimeSpan.FromSeconds(5))
                {
                    checkedTask = now;
                    SandboxTaskState state = await provider.Platform.StateAsync(task, cancellationToken);
                    if (state.Status == SandboxTaskStatus.Stopped && await provider.Store.GetAsync($"{prefix}out/{n}", cancellationToken) is null)
                    {
                        throw new SandboxRefusedException($"The sandbox's task stopped before its job was done{(state.Reason is null ? "" : $": {state.Reason}")}.");
                    }
                }

                await Task.Delay(provider.Options.Value.PollEvery, provider.Clock, cancellationToken);
            }
        }

        private static string Quote(string argument) => "'" + argument.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

        private static byte[] Tar(Dictionary<string, byte[]> files)
        {
            using MemoryStream output = new();
            using (TarWriter writer = new(output, TarEntryFormat.Pax, leaveOpen: true))
            {
                HashSet<string> folders = new(StringComparer.Ordinal);
                foreach ((string path, byte[] content) in files.OrderBy(f => f.Key, StringComparer.Ordinal))
                {
                    string[] parts = path.Split('/');
                    for (int i = 1; i < parts.Length; i++)
                    {
                        string folder = string.Join('/', parts[..i]) + "/";
                        if (folders.Add(folder))
                        {
                            writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, folder) { Mode = (UnixFileMode)0b111_101_101 });
                        }
                    }

                    writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, path)
                    {
                        Mode = (UnixFileMode)0b110_100_100,
                        DataStream = new MemoryStream(content, writable: false),
                    });
                }
            }

            return output.ToArray();
        }

        private static SandboxFiles ReadFiles(byte[] tar, long maxBytes, bool truncated)
        {
            List<SandboxFile> files = [];
            HashSet<string> seen = new(StringComparer.Ordinal);
            long total = 0;
            try
            {
                using TarReader reader = new(new MemoryStream(tar));
                while (reader.GetNextEntry(copyData: false) is { } entry)
                {
                    if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                    {
                        continue; // folders and links: links aren't followed
                    }

                    string path = entry.Name.StartsWith("./", StringComparison.Ordinal) ? entry.Name[2..] : entry.Name;
                    if (path.Length == 0 || path.StartsWith('/') || path.Split('/').Any(p => p is "" or "." or "..") || !seen.Add(path))
                    {
                        continue;
                    }

                    if (total + entry.Length > maxBytes || files.Count >= 5000)
                    {
                        truncated = true;
                        continue;
                    }

                    using MemoryStream copy = new();
                    entry.DataStream?.CopyTo(copy); // an empty file has no data stream
                    total += copy.Length;
                    files.Add(new SandboxFile(path, copy.ToArray()));
                }
            }
            catch (Exception e) when (e is InvalidDataException or EndOfStreamException or FormatException)
            {
                truncated = true; // the result was cut at the courier's cap
            }

            return new SandboxFiles(files, truncated);
        }
    }

    /// <summary>A job's result: the files the work loop wrote for it (exit, stdout, stderr, markers, files.tar).</summary>
    private sealed record Result(Dictionary<string, byte[]> Files, bool Truncated, bool TimedOut, TimeSpan Duration)
    {
        public int Exit => int.TryParse(Text("exit").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int code) ? code : -1;

        public bool Has(string name) => Files.ContainsKey(name);

        public string Text(string name) => Files.TryGetValue(name, out byte[]? bytes) ? Encoding.UTF8.GetString(bytes) : "";

        public static Result Lost(TimeSpan duration) => new(new Dictionary<string, byte[]>(), false, true, duration);

        public static Result Read(byte[] tar, TimeSpan duration)
        {
            Dictionary<string, byte[]> files = new(StringComparer.Ordinal);
            bool truncated = false;
            try
            {
                using TarReader reader = new(new MemoryStream(tar));
                while (reader.GetNextEntry(copyData: false) is { } entry)
                {
                    string name = entry.Name.StartsWith("./", StringComparison.Ordinal) ? entry.Name[2..] : entry.Name;
                    if (entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile && !name.Contains('/'))
                    {
                        using MemoryStream copy = new();
                        entry.DataStream?.CopyTo(copy); // the markers are empty files, with no data stream
                        files[name] = copy.ToArray();
                    }
                }
            }
            catch (Exception e) when (e is InvalidDataException or EndOfStreamException or FormatException)
            {
                truncated = true;
            }

            return new Result(files, truncated, false, duration);
        }
    }
}
