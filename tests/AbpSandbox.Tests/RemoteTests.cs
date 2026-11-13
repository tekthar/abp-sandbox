using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using AbpSandbox.Remote;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AbpSandbox.Tests;

/// <summary>
/// The cloud providers' protocol (docs/adr/0002) end to end, with no cloud: the runner's three containers on Docker,
/// and signed links to an object store in this process. Runs with ABP_SANDBOX_DOCKER=1.
/// </summary>
public sealed class RemoteTests : IAsyncLifetime
{
    private const string Alpine = "alpine:3.20@sha256:d9e853e87e55526f6b2917df91a2115c36dd7c696a35be12163d44e6e2a4b6bc";
    private static readonly Lazy<string> Runner = new(BuildRunner);
    private readonly string node = "abp-sandbox-remote-" + Guid.NewGuid().ToString("N")[..8];
    private LocalObjectStore store = null!;
    private LocalDockerTasks tasks = null!;
    private Sandbox sandbox = null!;
    private RemoteSandboxProvider provider = null!;

    public async Task InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("ABP_SANDBOX_DOCKER") != "1")
        {
            return;
        }

        store = await LocalObjectStore.StartAsync();
        tasks = new LocalDockerTasks();
        SandboxOptions options = new() { Images = new() { ["alpine"] = Alpine }, Node = node, DefaultMemoryBytes = 64L * 1024 * 1024 };
        provider = new RemoteSandboxProvider(tasks, store, Options.Create(new RemoteSandboxOptions { RunnerImage = Runner.Value, StartTime = TimeSpan.FromMinutes(1) }),
            Options.Create(options), TimeProvider.System, NullLogger<RemoteSandboxProvider>.Instance);
        sandbox = new Sandbox(provider, Options.Create(options), TimeProvider.System, NullLogger<Sandbox>.Instance);
    }

    public async Task DisposeAsync()
    {
        if (Environment.GetEnvironmentVariable("ABP_SANDBOX_DOCKER") != "1")
        {
            return;
        }

        await sandbox.CloseAllAsync();
        await provider.SweepAsync(DateTimeOffset.UtcNow, node);
        sandbox.Dispose();
        await store.DisposeAsync();
    }

    private static SandboxRequest Run(params string[] command) => new() { Owner = "tests", Image = "alpine", Command = command };

    [DockerFact]
    public async Task A_run_goes_through_the_courier_and_back()
    {
        SandboxResult result = await sandbox.RunAsync(Run("sh", "-c", "id -u; cat in/hello.txt; mkdir -p out; tr a-z A-Z < in/hello.txt > out/HELLO.txt; : > empty.txt; echo 'it''s quoted'; echo warn >&2", "ignored-arg") with
        {
            Files = [new("in/hello.txt", Encoding.UTF8.GetBytes("hello cloud\n"))],
            Outputs = ["out", "missing.txt", "empty.txt"],
        });

        Assert.True(result.Succeeded, result.Stderr);
        Assert.Equal("65534\nhello cloud\nits quoted", result.Stdout.TrimEnd());
        Assert.Equal("warn", result.Stderr.Trim());
        Assert.Empty(Assert.Single(result.Files.Files, f => f.Path == "empty.txt").Content);
        SandboxFile file = Assert.Single(result.Files.Files, f => f.Path != "empty.txt");
        Assert.Equal(("out/HELLO.txt", "HELLO CLOUD\n"), (file.Path, Encoding.UTF8.GetString(file.Content)));
        Assert.Empty(await tasks.LeftAsync(node));
        Assert.Empty(store.Keys);
    }

    [DockerFact]
    public async Task The_work_has_no_network_no_root_and_no_privileges()
    {
        SandboxResult result = await sandbox.RunAsync(Run("sh", "-c", "touch /etc/x 2>/dev/null && echo root-writable; wget -q -T 2 -O - http://host.docker.internal:1 >/dev/null 2>&1; wget -q -T 2 -O /dev/null http://1.1.1.1 2>&1 | grep -q unreachable && echo offline; grep CapEff /proc/self/status; grep NoNewPrivs /proc/self/status; touch /sandbox/.abp/bin/x 2>/dev/null && echo runner-writable; echo done"));
        Assert.True(result.Succeeded, result.Stderr);
        Assert.DoesNotContain("root-writable", result.Stdout);
        Assert.DoesNotContain("runner-writable", result.Stdout);
        Assert.Contains("offline", result.Stdout);
        Assert.Contains("CapEff:\t0000000000000000", result.Stdout);
        Assert.Contains("NoNewPrivs:\t1", result.Stdout);
    }

    [DockerFact]
    public async Task A_run_past_its_time_is_killed()
    {
        SandboxResult result = await sandbox.RunAsync(Run("sh", "-c", "echo begun; sleep 60") with { Limits = new SandboxLimits { Time = TimeSpan.FromSeconds(2) } });
        Assert.True(result.TimedOut, $"exit {result.ExitCode} after {result.Duration}; stdout [{result.Stdout}] stderr [{result.Stderr}]");
        Assert.Equal(-1, result.ExitCode);
        Assert.Contains("begun", result.Stdout);
        Assert.Empty(await tasks.LeftAsync(node));
    }

    [DockerFact]
    public async Task The_work_cant_make_the_courier_give_away_its_links()
    {
        SandboxResult result = await sandbox.RunAsync(Run("sh", "-c", Script("""
            for n in 1 2 3 4; do
              ln -s /private/manifest /sandbox/.abp/out/$n/stdout2 2>/dev/null
              ln -s /private /sandbox/.abp/out/$n/private 2>/dev/null
            done
            ln -s /private/manifest leak.txt
            cat /private/manifest 2>&1 | head -c 40
            echo
            """)) with { Outputs = ["leak.txt", "."] });

        Assert.Empty(result.Files.Files);
        Assert.DoesNotContain("in 1 http", result.Stdout);
        Assert.All(store.Uploads, upload => Assert.DoesNotContain("in 1 http", Encoding.UTF8.GetString(upload)));
        Assert.All(store.Uploads, upload => Assert.DoesNotContain("sig=", Encoding.UTF8.GetString(upload)));
    }

    [DockerFact]
    public async Task A_session_keeps_its_task_between_jobs()
    {
        ISandboxSession session = await sandbox.OpenSessionAsync(new SandboxSessionRequest { Owner = "tests", Key = "chat", Image = "alpine" });
        try
        {
            await session.PutFilesAsync([new("data.csv", Encoding.UTF8.GetBytes("a,1\nb,2\nc,3\n"))]);
            SandboxExecResult sum = await session.ExecAsync(["sh", "-c", "awk -F, '{s+=$2} END {print s}' data.csv | tee sum.txt"]);
            Assert.Equal("6", sum.Stdout.Trim());
            await session.PutFilesAsync([new("more/d.csv", Encoding.UTF8.GetBytes("d,4\n"))]);
            SandboxExecResult again = await session.ExecAsync(["sh", "-c", "cat data.csv more/d.csv | awk -F, '{s+=$2} END {print s}'"]);
            Assert.Equal("10", again.Stdout.Trim());
            SandboxFiles files = await session.GetFilesAsync(["."]);
            Assert.Equal(["data.csv", "more/d.csv", "sum.txt"], files.Files.Select(f => f.Path).Order());
            Assert.Single(await tasks.LeftAsync(node));
        }
        finally
        {
            await session.CloseAsync();
        }

        Assert.Empty(await tasks.LeftAsync(node));
    }

    [DockerFact]
    public async Task A_warm_task_waits_for_its_job_and_serves_one_run()
    {
        using Sandbox warm = new(provider, Options.Create(new SandboxOptions
        {
            Images = new() { ["alpine"] = Alpine },
            Node = node,
            DefaultMemoryBytes = 64L * 1024 * 1024,
            WarmPool = new() { ["alpine"] = 1 },
        }), TimeProvider.System, NullLogger<Sandbox>.Instance);
        await warm.TopUpWarmAsync();
        await Task.Delay(TimeSpan.FromSeconds(3)); // the task is up and polling before its job is there
        SandboxResult result = await warm.RunAsync(Run("sh", "-c", "cat in.txt") with { Files = [new("in.txt", "warm\n"u8.ToArray())] });
        Assert.True(result.FromWarmPool);
        Assert.Equal("warm", result.Stdout.Trim());
        await warm.CloseAllAsync();
        await provider.SweepAsync(DateTimeOffset.UtcNow, node);
    }

    [DockerFact]
    public async Task A_preparation_is_refused_on_a_cloud()
    {
        await Assert.ThrowsAsync<SandboxRefusedException>(() => sandbox.RunAsync(Run("true") with { Preparation = new SandboxPreparation { Steps = [["true"]] } }));
    }

    private static string Script(string script) => script.ReplaceLineEndings("\n");

    /// <summary>Builds the runner image from runner/ once, and gives its ID.</summary>
    private static string BuildRunner()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AbpSandbox.slnx")))
        {
            dir = dir.Parent;
        }

        string runner = Path.Combine(dir!.FullName, "runner");
        LocalDockerTasks.Docker("build", "-q", "-t", "abpsandbox-runner:test", runner);
        return LocalDockerTasks.Docker("image", "inspect", "-f", "{{.Id}}", "abpsandbox-runner:test").Trim();
    }
}

/// <summary>An object store in the test's process: signed links to a small HTTP server the containers reach.</summary>
internal sealed class LocalObjectStore : ISandboxObjectStore, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, byte[]> objects = new(StringComparer.Ordinal);
    private readonly byte[] secret = RandomNumberGenerator.GetBytes(32);
    private WebApplication app = null!;
    private int port;

    public ConcurrentBag<byte[]> Uploads { get; } = [];

    public IReadOnlyCollection<string> Keys => [.. objects.Keys];

    public static async Task<LocalObjectStore> StartAsync()
    {
        LocalObjectStore store = new();
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        // Docker Desktop reaches the host's loopback as host.docker.internal; a Linux engine reaches its bridge's gateway.
        builder.WebHost.UseKestrel(k => k.Listen(OperatingSystem.IsLinux() ? IPAddress.Any : IPAddress.Loopback, 0));
        builder.Logging.ClearProviders();
        store.app = builder.Build();
        store.app.MapGet("/o/{**key}", (string key, long exp, string sig) =>
            !store.Valid("GET", key, exp, sig) ? Results.StatusCode(403)
            : store.objects.TryGetValue(key, out byte[]? bytes) ? Results.Bytes(bytes) : Results.NotFound());
        store.app.MapPut("/o/{**key}", async (HttpRequest request, string key, long exp, string sig) =>
        {
            if (!store.Valid("PUT", key, exp, sig))
            {
                return Results.StatusCode(403);
            }

            using MemoryStream body = new();
            await request.Body.CopyToAsync(body);
            store.objects[key] = body.ToArray();
            store.Uploads.Add(body.ToArray());
            return Results.Ok();
        });
        await store.app.StartAsync();
        store.port = new Uri(store.app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First()).Port;
        return store;
    }

    public Task PutAsync(string key, byte[] content, CancellationToken cancellationToken = default)
    {
        objects[key] = content;
        return Task.CompletedTask;
    }

    public Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken = default) =>
        Task.FromResult(objects.TryGetValue(key, out byte[]? bytes) ? bytes : null);

    public Task DeletePrefixAsync(string prefix, CancellationToken cancellationToken = default)
    {
        foreach (string key in objects.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)))
        {
            objects.TryRemove(key, out _);
        }

        return Task.CompletedTask;
    }

    public Uri SignGet(string key, DateTimeOffset expires) => Sign("GET", key, expires);

    public Uri SignPut(string key, DateTimeOffset expires) => Sign("PUT", key, expires);

    public async ValueTask DisposeAsync() => await app.DisposeAsync();

    private Uri Sign(string method, string key, DateTimeOffset expires)
    {
        long exp = expires.ToUnixTimeSeconds();
        return new Uri($"http://host.docker.internal:{port}/o/{key}?exp={exp}&sig={Signature(method, key, exp)}");
    }

    private bool Valid(string method, string key, long exp, string sig) =>
        exp >= DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(sig), Encoding.ASCII.GetBytes(Signature(method, key, exp)));

    private string Signature(string method, string key, long exp) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes($"{method}\n{key}\n{exp}")));
}

/// <summary>A task platform on Docker: the runner's init, then the work (no network) and the courier, on shared volumes.</summary>
internal sealed class LocalDockerTasks : ISandboxTaskPlatform
{
    private const string Label = "abp-sandbox-remote-test";

    public Task<string> StartAsync(SandboxTaskSpec spec, CancellationToken cancellationToken = default)
    {
        string id = Guid.NewGuid().ToString("N")[..12];
        string[] user = spec.User.Split(':');
        string[] labels = ["--label", $"{Label}=1", "--label", $"abp-sandbox.node={spec.Node}", "--label",
            $"abp-sandbox.expires={spec.ExpiresAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)}", "--label", $"abp-sandbox.task={id}"];
        string[] locked = ["--read-only", "--cap-drop", "ALL", "--security-opt", "no-new-privileges"];
        Docker("volume", "create", "--label", $"{Label}=1", $"abpsandbox-v-{id}");
        Docker("volume", "create", "--label", $"{Label}=1", $"abpsandbox-p-{id}");
        Docker(["run", "--rm", "--network", "none", .. labels, "-v", $"abpsandbox-v-{id}:/sandbox", "-v", $"abpsandbox-p-{id}:/private",
            "-e", $"ABPSANDBOX_UID={user[0]}", "-e", $"ABPSANDBOX_GID={(user.Length > 1 ? user[1] : user[0])}", "--entrypoint", "sh", spec.RunnerImage, "/abp/init.sh"]);
        Docker(["run", "-d", "--name", $"abpsandbox-w-{id}", "--network", "none", .. labels, .. locked, "--user", spec.User,
            "--memory", spec.MemoryBytes.ToString(CultureInfo.InvariantCulture), "--cpus", spec.Cpus.ToString(CultureInfo.InvariantCulture),
            "--pids-limit", spec.Processes.ToString(CultureInfo.InvariantCulture),
            .. spec.Environment.SelectMany(e => new[] { "-e", $"{e.Key}={e.Value}" }),
            "-v", $"abpsandbox-v-{id}:/sandbox", "--entrypoint", "/sandbox/.abp/bin/busybox", spec.WorkImage, "sh", "/sandbox/.abp/work.sh"]);
        Docker(["run", "-d", "--name", $"abpsandbox-c-{id}", .. labels, .. locked, "--user", "65520:65520", "--add-host", "host.docker.internal:host-gateway",
            "-e", $"ABPSANDBOX_MANIFEST={spec.ManifestUrl}", "-v", $"abpsandbox-v-{id}:/sandbox", "-v", $"abpsandbox-p-{id}:/private",
            "--entrypoint", "sh", spec.RunnerImage, "/abp/courier.sh"]);
        return Task.FromResult(id);
    }

    public Task StopAsync(string taskId, CancellationToken cancellationToken = default)
    {
        TryDocker("rm", "-f", "-v", $"abpsandbox-w-{taskId}", $"abpsandbox-c-{taskId}");
        TryDocker("volume", "rm", "-f", $"abpsandbox-v-{taskId}", $"abpsandbox-p-{taskId}");
        return Task.CompletedTask;
    }

    public Task<SandboxTaskState> StateAsync(string taskId, CancellationToken cancellationToken = default)
    {
        string? status = TryDocker("inspect", "-f", "{{.State.Status}}", $"abpsandbox-c-{taskId}")?.Trim();
        return Task.FromResult(status switch
        {
            "running" => new SandboxTaskState(SandboxTaskStatus.Running),
            "created" => new SandboxTaskState(SandboxTaskStatus.Starting),
            _ => new SandboxTaskState(SandboxTaskStatus.Stopped, status),
        });
    }

    public Task<IReadOnlyList<SandboxTaskInfo>> ListAsync(CancellationToken cancellationToken = default)
    {
        string output = Docker("ps", "-a", "--filter", $"label={Label}=1", "--filter", "name=abpsandbox-c-",
            "--format", "{{.Label \"abp-sandbox.task\"}}|{{.Label \"abp-sandbox.node\"}}|{{.Label \"abp-sandbox.expires\"}}");
        return Task.FromResult<IReadOnlyList<SandboxTaskInfo>>([.. output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split('|'))
            .Select(p => new SandboxTaskInfo(p[0], p[1], DateTimeOffset.FromUnixTimeSeconds(long.Parse(p[2], CultureInfo.InvariantCulture))))]);
    }

    /// <summary>The tasks a node has left.</summary>
    public async Task<List<string>> LeftAsync(string node) =>
        [.. (await ListAsync()).Where(t => t.Node == node).Select(t => t.Id)];

    public static string Docker(params string[] args)
    {
        ProcessStartInfo start = new("docker") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using Process docker = Process.Start(start)!;
        Task<string> error = docker.StandardError.ReadToEndAsync();
        string output = docker.StandardOutput.ReadToEnd();
        docker.WaitForExit();
        return docker.ExitCode == 0 ? output : throw new InvalidOperationException($"docker {args[0]}: {error.Result.Trim()}");
    }

    private static string? TryDocker(params string[] args)
    {
        try
        {
            return Docker(args);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
