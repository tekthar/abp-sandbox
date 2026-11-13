using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AbpSandbox.Tests;

/// <summary>Sessions, preparation and the sweep's bookkeeping, over a provider that only counts.</summary>
public class SessionTests
{
    private static readonly string Pinned = "alpine:3.20@sha256:" + new string('a', 64);

    private static (Sandbox Sandbox, FakeProvider Provider, ManualClock Clock) Make(Action<SandboxOptions>? configure = null)
    {
        SandboxOptions options = new() { Images = new() { ["alpine"] = Pinned, ["other"] = Pinned.Replace("3.20", "3.21") } };
        configure?.Invoke(options);
        FakeProvider provider = new();
        ManualClock clock = new();
        return (new Sandbox(provider, Options.Create(options), clock, NullLogger<Sandbox>.Instance), provider, clock);
    }

    private static SandboxSessionRequest Ask(string owner = "agent-1", string key = "chat-1", string image = "alpine") =>
        new() { Owner = owner, Key = key, Image = image };

    [Fact]
    public async Task Opening_the_same_owner_and_key_gives_the_same_session()
    {
        (Sandbox sandbox, FakeProvider provider, _) = Make();
        ISandboxSession first = await sandbox.OpenSessionAsync(Ask());
        ISandboxSession again = await sandbox.OpenSessionAsync(Ask());
        Assert.Same(first, again);
        Assert.Equal(1, provider.Created);
        Assert.Same(first, sandbox.FindSession("agent-1", "chat-1"));
        Assert.Null(sandbox.FindSession("agent-2", "chat-1"));
    }

    [Fact]
    public async Task Another_image_closes_the_session_open_under_the_key()
    {
        (Sandbox sandbox, FakeProvider provider, _) = Make();
        ISandboxSession first = await sandbox.OpenSessionAsync(Ask());
        ISandboxSession second = await sandbox.OpenSessionAsync(Ask(image: "other"));
        Assert.NotSame(first, second);
        Assert.True(first.IsClosed);
        Assert.Equal(1, provider.Removed);
    }

    [Fact]
    public async Task An_owner_has_at_most_its_sessions_and_the_server_its_own()
    {
        (Sandbox sandbox, _, _) = Make(o => { o.MaxSessionsPerOwner = 1; o.MaxSessions = 2; });
        await sandbox.OpenSessionAsync(Ask(key: "a"));
        await Assert.ThrowsAsync<SandboxRefusedException>(() => sandbox.OpenSessionAsync(Ask(key: "b")));
        await sandbox.OpenSessionAsync(Ask(owner: "agent-2", key: "a"));
        await Assert.ThrowsAsync<SandboxRefusedException>(() => sandbox.OpenSessionAsync(Ask(owner: "agent-3", key: "a")));
    }

    [Fact]
    public async Task An_idle_session_expires_and_the_sweep_closes_it()
    {
        (Sandbox sandbox, FakeProvider provider, ManualClock clock) = Make();
        ISandboxSession session = await sandbox.OpenSessionAsync(Ask() with { IdleTime = TimeSpan.FromMinutes(5) });
        clock.Advance(TimeSpan.FromMinutes(4));
        await session.ExecAsync(["true"]);
        clock.Advance(TimeSpan.FromMinutes(4));
        Assert.Equal(0, await sandbox.CloseExpiredAsync());
        clock.Advance(TimeSpan.FromMinutes(2));
        await Assert.ThrowsAsync<SandboxRefusedException>(() => session.ExecAsync(["true"]));
        Assert.Equal(1, await sandbox.CloseExpiredAsync());
        Assert.True(session.IsClosed);
        Assert.Equal(1, provider.Removed);
    }

    [Fact]
    public async Task A_session_lives_no_longer_than_its_lifetime_however_used()
    {
        (Sandbox sandbox, _, ManualClock clock) = Make(o => o.SessionLifetime = TimeSpan.FromMinutes(30));
        ISandboxSession session = await sandbox.OpenSessionAsync(Ask());
        for (int i = 0; i < 3; i++)
        {
            clock.Advance(TimeSpan.FromMinutes(9));
            await session.ExecAsync(["true"]);
        }

        clock.Advance(TimeSpan.FromMinutes(4));
        Assert.Null(sandbox.FindSession("agent-1", "chat-1"));
    }

    [Fact]
    public async Task A_command_past_its_time_closes_the_session()
    {
        (Sandbox sandbox, FakeProvider provider, _) = Make();
        ISandboxSession session = await sandbox.OpenSessionAsync(Ask());
        provider.NextExecTimesOut = true;
        SandboxExecResult result = await session.ExecAsync(["sleep", "100"]);
        Assert.True(result.TimedOut);
        Assert.True(session.IsClosed);
        Assert.Null(sandbox.FindSession("agent-1", "chat-1"));
        Assert.Equal(1, provider.Removed);
    }

    [Fact]
    public async Task A_preparation_is_made_once_and_found_after()
    {
        (Sandbox sandbox, FakeProvider provider, _) = Make();
        SandboxRequest request = new()
        {
            Owner = "agent-1",
            Image = "alpine",
            Command = ["true"],
            Preparation = new SandboxPreparation { Steps = [["sh", "-c", "echo ready > /opt/ready"]] },
        };
        Assert.False((await sandbox.RunAsync(request)).PreparedFromCache);
        Assert.True((await sandbox.RunAsync(request)).PreparedFromCache);
        Assert.Single(provider.Prepared);
        Assert.False((await sandbox.RunAsync(request with { Owner = "agent-2" })).PreparedFromCache);
        Assert.Equal(2, provider.Prepared.Count);
        Assert.Equal(provider.Created, provider.Removed);
    }

    [Fact]
    public async Task A_failed_preparation_step_is_said_and_nothing_is_saved()
    {
        (Sandbox sandbox, FakeProvider provider, _) = Make();
        provider.FailExec = true;
        SandboxPreparationFailedException e = await Assert.ThrowsAsync<SandboxPreparationFailedException>(() => sandbox.RunAsync(new SandboxRequest
        {
            Owner = "o",
            Image = "alpine",
            Command = ["true"],
            Preparation = new SandboxPreparation { Steps = [["false"]] },
        }));
        Assert.Equal(0, e.Step);
        Assert.Empty(provider.Prepared);
        Assert.Equal(provider.Created, provider.Removed);
    }

    [Fact]
    public async Task Prepared_images_past_the_count_go_used_longest_ago_first()
    {
        (Sandbox sandbox, FakeProvider provider, ManualClock clock) = Make(o => o.MaxPreparedImages = 2);
        SandboxRequest Run(string step) => new() { Owner = "o", Image = "alpine", Command = ["true"], Preparation = new() { Steps = [["sh", "-c", step]] } };
        foreach (string step in new[] { "a", "b", "c" })
        {
            await sandbox.RunAsync(Run(step));
            clock.Advance(TimeSpan.FromMinutes(1));
        }

        await sandbox.RunAsync(Run("a")); // used again: now "b" is the oldest used
        Assert.Equal(1, await sandbox.EvictPreparedAsync());
        Assert.Equal(2, provider.Prepared.Count);
        Assert.True((await sandbox.RunAsync(Run("a"))).PreparedFromCache);
        Assert.False((await sandbox.RunAsync(Run("b"))).PreparedFromCache);
    }

    [Fact]
    public async Task A_run_refuses_what_the_policy_refuses_before_anything_is_made()
    {
        (Sandbox sandbox, FakeProvider provider, _) = Make();
        SandboxRequest ok = new() { Owner = "o", Image = "alpine", Command = ["true"] };
        await Assert.ThrowsAsync<SandboxRefusedException>(() => sandbox.RunAsync(ok with { Image = "anything:latest" }));
        await Assert.ThrowsAsync<SandboxRefusedException>(() => sandbox.RunAsync(ok with { Command = [] }));
        await Assert.ThrowsAsync<SandboxRefusedException>(() => sandbox.RunAsync(ok with { Outputs = ["../x"] }));
        await Assert.ThrowsAsync<SandboxRefusedException>(() => sandbox.RunAsync(ok with { Files = [new("/etc/x", [])] }));
        Assert.Equal(0, provider.Created);
    }

    [Fact]
    public async Task A_warm_container_serves_one_run_and_another_takes_its_place()
    {
        (Sandbox sandbox, FakeProvider provider, _) = Make(o => o.WarmPool = new() { ["alpine"] = 2 });
        Assert.Equal(2, await sandbox.TopUpWarmAsync());
        Assert.Equal(0, await sandbox.TopUpWarmAsync());

        SandboxResult result = await sandbox.RunAsync(new SandboxRequest { Owner = "o", Image = "alpine", Command = ["true"] });
        Assert.True(result.FromWarmPool);
        Assert.Equal(1, provider.Removed);
        await sandbox.TopUpWarmAsync(); // after the top-up the run started: one new container, not two
        Assert.Equal(3, provider.Created);
        Assert.Equal(1, provider.Removed);
    }

    [Fact]
    public async Task A_run_that_asks_for_other_limits_doesnt_take_a_warm_container()
    {
        (Sandbox sandbox, FakeProvider provider, _) = Make(o => o.WarmPool = new() { ["alpine"] = 1 });
        await sandbox.TopUpWarmAsync();
        SandboxRequest run = new() { Owner = "o", Image = "alpine", Command = ["true"] };
        Assert.False((await sandbox.RunAsync(run with { Limits = new SandboxLimits { MemoryBytes = 64L * 1024 * 1024 } })).FromWarmPool);
        Assert.False((await sandbox.RunAsync(run with { Environment = new Dictionary<string, string> { ["A"] = "1" } })).FromWarmPool);
        Assert.False((await sandbox.RunAsync(run with { Image = "other" })).FromWarmPool);
        Assert.Equal(4, provider.Created);
        Assert.Equal(3, provider.Removed);
    }

    [Fact]
    public async Task Warm_containers_are_replaced_after_their_lifetime_and_go_when_the_host_stops()
    {
        (Sandbox sandbox, FakeProvider provider, ManualClock clock) = Make(o => { o.WarmPool = new() { ["alpine"] = 1 }; o.WarmLifetime = TimeSpan.FromMinutes(30); });
        await sandbox.TopUpWarmAsync();
        clock.Advance(TimeSpan.FromMinutes(31));
        Assert.Equal(1, await sandbox.TopUpWarmAsync());
        Assert.Equal((2, 1), (provider.Created, provider.Removed));
        await sandbox.CloseAllAsync();
        Assert.Equal(2, provider.Removed);
    }

    [Fact]
    public async Task Without_a_provider_nothing_runs()
    {
        Sandbox sandbox = new(new NoSandboxProvider(), Options.Create(new SandboxOptions { Images = new() { ["alpine"] = Pinned } }), new ManualClock(), NullLogger<Sandbox>.Instance);
        await Assert.ThrowsAsync<SandboxRefusedException>(() => sandbox.RunAsync(new SandboxRequest { Owner = "o", Image = "alpine", Command = ["true"] }));
        await Assert.ThrowsAsync<SandboxRefusedException>(() => sandbox.OpenSessionAsync(Ask()));
    }
}

internal sealed class ManualClock : TimeProvider
{
    private DateTimeOffset now = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => now;

    public void Advance(TimeSpan by) => now += by;
}

internal sealed class FakeProvider : ISandboxProvider
{
    public int Created;
    public int Removed;
    public bool NextExecTimesOut;
    public bool FailExec;
    public SandboxAvailability Availability = SandboxAvailability.Yes;
    public bool CheckThrows;
    public int Checks;
    public readonly Dictionary<string, SandboxPreparedImage> Prepared = new();
    private int saved;

    public string Name => "fake";

    public SandboxCapabilities Capabilities => new(true, true);

    public Task<SandboxAvailability> CheckAsync(CancellationToken cancellationToken = default)
    {
        Checks++;
        return CheckThrows ? throw new HttpRequestException("refused") : Task.FromResult(Availability);
    }

    public Task<ISandboxContainer> CreateAsync(SandboxContainerSpec spec, CancellationToken cancellationToken = default)
    {
        Created++;
        return Task.FromResult<ISandboxContainer>(new Container(this, $"c{Created}"));
    }

    public Task<string?> FindPreparedAsync(string key, CancellationToken cancellationToken = default) =>
        Task.FromResult(Prepared.TryGetValue(key, out SandboxPreparedImage? image) ? image.Reference : null);

    public Task<string> SavePreparedAsync(ISandboxContainer container, string key, CancellationToken cancellationToken = default)
    {
        saved++;
        Prepared[key] = new SandboxPreparedImage($"img{saved}", key, DateTimeOffset.UnixEpoch.AddMinutes(saved));
        return Task.FromResult($"img{saved}");
    }

    public Task<IReadOnlyList<SandboxPreparedImage>> ListPreparedAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<SandboxPreparedImage>>([.. Prepared.Values]);

    public Task RemovePreparedAsync(string reference, CancellationToken cancellationToken = default)
    {
        foreach (string key in Prepared.Where(p => p.Value.Reference == reference).Select(p => p.Key).ToList())
        {
            Prepared.Remove(key);
        }

        return Task.CompletedTask;
    }

    public Task<int> SweepAsync(DateTimeOffset now, string? node, CancellationToken cancellationToken = default) => Task.FromResult(0);

    private sealed class Container(FakeProvider provider, string id) : ISandboxContainer
    {
        private bool removed;

        public string Id => id;

        public Task PutFilesAsync(IReadOnlyList<SandboxFile> files, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<SandboxExecResult> RunAsync(TimeSpan time, long maxOutputBytes, CancellationToken cancellationToken = default) => Exec();

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<SandboxExecResult> ExecAsync(IReadOnlyList<string> command, TimeSpan time, long maxOutputBytes, CancellationToken cancellationToken = default) => Exec();

        public Task<SandboxFiles> GetFilesAsync(IReadOnlyList<string> paths, long maxBytes, CancellationToken cancellationToken = default) => Task.FromResult(SandboxFiles.None);

        public Task RemoveAsync(CancellationToken cancellationToken = default)
        {
            if (!removed)
            {
                removed = true;
                provider.Removed++;
            }

            return Task.CompletedTask;
        }

        private Task<SandboxExecResult> Exec()
        {
            bool timedOut = provider.NextExecTimesOut;
            provider.NextExecTimesOut = false;
            return Task.FromResult(new SandboxExecResult
            {
                ExitCode = timedOut ? -1 : provider.FailExec ? 1 : 0,
                TimedOut = timedOut,
                Stdout = "",
                Stderr = "",
                OutputTruncated = false,
                Duration = TimeSpan.Zero,
            });
        }
    }
}
