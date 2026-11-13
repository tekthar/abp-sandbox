using System.Text;
using AbpSandbox.Docker;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AbpSandbox.Tests;

/// <summary>Runs only where Docker is: set ABP_SANDBOX_DOCKER=1 (and ABP_SANDBOX_DOCKER_ENDPOINT for another engine).</summary>
public sealed class DockerFactAttribute : FactAttribute
{
    public DockerFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("ABP_SANDBOX_DOCKER") != "1")
        {
            Skip = "Set ABP_SANDBOX_DOCKER=1 to run the Docker provider's tests.";
        }
    }
}

/// <summary>The Docker provider against a real engine: what a container can't do, and that nothing is left behind.</summary>
public sealed class DockerTests : IDisposable
{
    private const string Alpine = "alpine:3.20@sha256:d9e853e87e55526f6b2917df91a2115c36dd7c696a35be12163d44e6e2a4b6bc";
    private readonly string node = "abp-sandbox-tests-" + Guid.NewGuid().ToString("N")[..8];
    private readonly DockerSandboxProvider provider;
    private readonly Sandbox sandbox;

    public DockerTests()
    {
        provider = new DockerSandboxProvider(Options.Create(new DockerSandboxOptions { Endpoint = Environment.GetEnvironmentVariable("ABP_SANDBOX_DOCKER_ENDPOINT") }), NullLogger<DockerSandboxProvider>.Instance);
        sandbox = new Sandbox(provider, Options.Create(new SandboxOptions
        {
            Images = new() { ["alpine"] = Alpine, ["agents"] = Alpine },
            Node = node,
            DefaultMemoryBytes = 64L * 1024 * 1024,
        }), TimeProvider.System, NullLogger<Sandbox>.Instance);
    }

    private static SandboxRequest Run(params string[] command) => new() { Owner = "tests", Image = "alpine", Command = command };

    private static string Script(string script) => script.ReplaceLineEndings("\n");

    [DockerFact]
    public async Task A_run_gets_its_files_and_gives_back_its_output()
    {
        SandboxResult result = await sandbox.RunAsync(Run("sh", "-c", "id -u; cat in/hello.txt; mkdir -p out; tr a-z A-Z < in/hello.txt > out/HELLO.txt; : > empty.txt; echo warn >&2") with
        {
            Files = [new("in/hello.txt", Encoding.UTF8.GetBytes("hello sandbox"))],
            Outputs = ["out", "missing.txt", "empty.txt"],
        });

        Assert.True(result.Succeeded, result.Stderr);
        Assert.Equal("65534\nhello sandbox", result.Stdout.TrimEnd());
        Assert.Equal("warn", result.Stderr.Trim());
        Assert.Empty(Assert.Single(result.Files.Files, f => f.Path == "empty.txt").Content);
        SandboxFile file = Assert.Single(result.Files.Files, f => f.Path != "empty.txt");
        Assert.Equal(("out/HELLO.txt", "HELLO SANDBOX"), (file.Path, Encoding.UTF8.GetString(file.Content)));
        Assert.Empty(await LeftAsync());
    }

    [DockerFact]
    public async Task A_run_has_no_network_no_root_and_no_privileges()
    {
        SandboxResult result = await sandbox.RunAsync(Run("sh", "-c", Script("""
            touch /etc/x 2>/dev/null && echo root-writable
            wget -q -T 2 -O - http://1.1.1.1 >/dev/null 2>&1 && echo network
            ip link 2>/dev/null | grep -q eth0 && echo eth0
            grep CapEff /proc/self/status
            grep NoNewPrivs /proc/self/status
            echo done
            """)));

        Assert.True(result.Succeeded, result.Stderr);
        Assert.DoesNotContain("root-writable", result.Stdout);
        Assert.DoesNotContain("network", result.Stdout);
        Assert.DoesNotContain("eth0", result.Stdout);
        Assert.Contains("CapEff:\t0000000000000000", result.Stdout);
        Assert.Contains("NoNewPrivs:\t1", result.Stdout);
        Assert.EndsWith("done", result.Stdout.TrimEnd());
    }

    [DockerFact]
    public async Task A_run_past_its_time_is_killed_and_removed()
    {
        SandboxResult result = await sandbox.RunAsync(Run("sh", "-c", "echo started; sleep 60") with { Limits = new SandboxLimits { Time = TimeSpan.FromSeconds(2) } });
        Assert.True(result.TimedOut);
        Assert.Equal(-1, result.ExitCode);
        Assert.Contains("started", result.Stdout);
        Assert.True(result.Duration < TimeSpan.FromSeconds(20));
        Assert.Empty(await LeftAsync());
    }

    [DockerFact]
    public async Task Output_is_capped_and_links_out_of_the_work_folder_are_not_followed()
    {
        Sandbox small = new(provider, Options.Create(new SandboxOptions
        {
            Images = new() { ["alpine"] = Alpine },
            Node = node,
            MaxConsoleBytes = 1000,
            MaxOutputBytes = 1000,
        }), TimeProvider.System, NullLogger<Sandbox>.Instance);
        SandboxResult result = await small.RunAsync(Run("sh", "-c", "head -c 5000 /dev/zero | tr '\\0' x; ln -s /etc/passwd passwd; head -c 600 /dev/zero > a.bin; head -c 600 /dev/zero > b.bin") with
        {
            Outputs = ["passwd", "a.bin", "b.bin"],
        });

        Assert.Equal(1000, result.Stdout.Length);
        Assert.True(result.OutputTruncated);
        Assert.Equal(["a.bin"], result.Files.Files.Select(f => f.Path));
        Assert.True(result.Files.Truncated);
    }

    [DockerFact]
    public async Task A_session_keeps_its_container_between_calls_until_closed()
    {
        ISandboxSession session = await sandbox.OpenSessionAsync(new SandboxSessionRequest { Owner = "tests", Key = "chat-1", Image = "alpine" });
        try
        {
            await session.PutFilesAsync([new("notes/a.txt", Encoding.UTF8.GetBytes("one"))]);
            SandboxExecResult first = await session.ExecAsync(["sh", "-c", "cat notes/a.txt > copy.txt; echo two >> copy.txt; echo $$ > /tmp/pid"]);
            Assert.True(first.Succeeded, first.Stderr);
            SandboxExecResult second = await session.ExecAsync(["cat", "copy.txt"]);
            Assert.Equal("onetwo", second.Stdout.Replace("\n", ""));
            Assert.Same(session, await sandbox.OpenSessionAsync(new SandboxSessionRequest { Owner = "tests", Key = "chat-1", Image = "alpine" }));
            SandboxFiles files = await session.GetFilesAsync(["."]);
            Assert.Equal(["copy.txt", "notes/a.txt"], files.Files.Select(f => f.Path).Order());
            Assert.Single(await LeftAsync());
        }
        finally
        {
            await session.CloseAsync();
        }

        Assert.Empty(await LeftAsync());
    }

    [DockerFact]
    public async Task A_session_command_past_its_time_ends_the_session()
    {
        ISandboxSession session = await sandbox.OpenSessionAsync(new SandboxSessionRequest { Owner = "tests", Key = "slow", Image = "alpine" });
        SandboxExecResult result = await session.ExecAsync(["sh", "-c", "echo begun; sleep 60"], TimeSpan.FromSeconds(2));
        Assert.True(result.TimedOut);
        Assert.Contains("begun", result.Stdout);
        Assert.True(session.IsClosed);
        Assert.Empty(await LeftAsync());
    }

    [DockerFact]
    public async Task A_preparation_is_saved_as_an_image_and_later_runs_start_from_it()
    {
        SandboxRequest request = Run("cat", "/opt/tool/version") with
        {
            Owner = "tests-" + Guid.NewGuid().ToString("N"),
            Preparation = new SandboxPreparation
            {
                Files = [new("setup/version", Encoding.UTF8.GetBytes("tool 1.2.3"))],
                Steps = [["mkdir", "-p", "/opt/tool"], ["cp", "setup/version", "/opt/tool/version"]],
            },
        };
        string key = SandboxPolicy.PreparationKey(Alpine, request.Owner, request.Preparation!);
        try
        {
            SandboxResult first = await sandbox.RunAsync(request);
            Assert.True(first.Succeeded, first.Stderr);
            Assert.False(first.PreparedFromCache);
            Assert.Equal("tool 1.2.3", first.Stdout.Trim());

            SandboxResult second = await sandbox.RunAsync(request);
            Assert.True(second.PreparedFromCache);
            Assert.Equal("tool 1.2.3", second.Stdout.Trim());

            // The preparation's files were in the work folder, which isn't part of the image.
            SandboxResult work = await sandbox.RunAsync(request with { Command = ["sh", "-c", "ls -A"] });
            Assert.Equal("", work.Stdout.Trim());
            Assert.Empty(await LeftAsync());
        }
        finally
        {
            if (await provider.FindPreparedAsync(key) is { } image)
            {
                await provider.RemovePreparedAsync(image);
            }
        }
    }

    [DockerFact]
    public async Task The_sweep_removes_what_a_restarted_server_left()
    {
        await provider.CreateAsync(new SandboxContainerSpec
        {
            Image = Alpine,
            Command = null,
            MemoryBytes = 64L * 1024 * 1024,
            Cpus = 0.5,
            Processes = 16,
            User = "65534:65534",
            Kind = "session",
            Node = node,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            OwnerHash = "tests",
        });
        Assert.Single(await LeftAsync());
        Assert.Equal(0, await provider.SweepAsync(DateTimeOffset.UtcNow, "some-other-node"));
        Assert.Equal(1, await provider.SweepAsync(DateTimeOffset.UtcNow, node));
        Assert.Empty(await LeftAsync());
    }

    [DockerFact]
    public async Task A_warm_container_runs_at_once_as_locked_down_and_is_replaced()
    {
        using Sandbox warm = new(provider, Options.Create(new SandboxOptions
        {
            Images = new() { ["alpine"] = Alpine },
            Node = node,
            DefaultMemoryBytes = 64L * 1024 * 1024,
            WarmPool = new() { ["alpine"] = 1 },
        }), TimeProvider.System, NullLogger<Sandbox>.Instance);
        await warm.TopUpWarmAsync();
        Assert.Single(await LeftAsync());

        System.Diagnostics.Stopwatch fast = System.Diagnostics.Stopwatch.StartNew();
        SandboxResult result = await warm.RunAsync(Run("sh", "-c", "id -u; cat in.txt; touch /etc/x 2>/dev/null || echo read-only; grep CapEff /proc/self/status; echo made > out.txt") with
        {
            Files = [new("in.txt", Encoding.UTF8.GetBytes("from the pool\n"))],
            Outputs = ["out.txt"],
        });
        fast.Stop();
        System.Diagnostics.Stopwatch cold = System.Diagnostics.Stopwatch.StartNew();
        await sandbox.RunAsync(Run("true"));
        cold.Stop();

        Assert.True(result.FromWarmPool);
        Assert.True(result.Succeeded, result.Stderr);
        Assert.Equal(["65534", "from the pool", "read-only", "CapEff:\t0000000000000000"], result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal("made\n", Encoding.UTF8.GetString(Assert.Single(result.Files.Files).Content));

        SandboxResult slow = await warm.RunAsync(Run("sh", "-c", "sleep 60") with { Limits = new SandboxLimits { Time = TimeSpan.FromSeconds(2) } });
        Assert.True(slow.TimedOut);

        await warm.TopUpWarmAsync();
        Assert.Single(await LeftAsync());
        await warm.CloseAllAsync();
        Assert.Empty(await LeftAsync());
        Console.WriteLine($"warm run {fast.ElapsedMilliseconds} ms, cold run {cold.ElapsedMilliseconds} ms");
    }

    [DockerFact]
    public async Task An_agent_works_in_its_conversations_container()
    {
        AbpSandbox.Agents.SandboxAgentTools tools = new(sandbox, Options.Create(new AbpSandbox.Agents.SandboxAgentToolsOptions()), TimeProvider.System);
        AbpAgent.AgentActing acting = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), false) { AgentId = Guid.NewGuid(), ScopeId = Guid.NewGuid() };
        System.Text.Json.JsonElement Input(object value) => System.Text.Json.JsonSerializer.SerializeToElement(value);

        AbpAgent.AgentToolResult put = await tools.RunAsync("sandbox_put", Input(new { files = new[] { new { path = "data.csv", text = "a,1\nb,2\nc,3\n" } } }), acting, default);
        Assert.False(put.Error, put.Content);
        AbpAgent.AgentToolResult sum = await tools.RunAsync("sandbox_exec", Input(new { script = "awk -F, '{s+=$2} END {print s}' data.csv > sum.txt; cat sum.txt" }), acting, default);
        Assert.False(sum.Error, sum.Content);
        Assert.Contains("stdout:\n6", sum.Content);
        AbpAgent.AgentToolResult get = await tools.RunAsync("sandbox_get", Input(new { paths = new[] { "sum.txt" } }), acting, default);
        Assert.Contains("File sum.txt (2 bytes):\n6", get.Content);
        AbpAgent.AgentToolResult run = await tools.RunAsync("sandbox_run", Input(new { script = "ls; wget -q -T 2 http://1.1.1.1 || echo offline" }), acting, default);
        Assert.Contains("offline", run.Content);
        Assert.DoesNotContain("data.csv", run.Content);
        Assert.Single(await LeftAsync());
    }

    public void Dispose()
    {
        if (Environment.GetEnvironmentVariable("ABP_SANDBOX_DOCKER") == "1")
        {
            provider.SweepAsync(DateTimeOffset.UtcNow, node).GetAwaiter().GetResult();
        }

        sandbox.Dispose();
        provider.Dispose();
    }

    /// <summary>This test's containers still there.</summary>
    private async Task<List<string>> LeftAsync()
    {
        using System.Diagnostics.Process docker = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("docker", $"ps -aq --filter label=abp-sandbox.node={node}")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        })!;
        string output = await docker.StandardOutput.ReadToEndAsync();
        await docker.WaitForExitAsync();
        return [.. output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
    }
}
