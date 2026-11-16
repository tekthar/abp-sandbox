using System.Text;
using Amazon;
using Amazon.ECS;
using Amazon.S3;
using AbpSandbox.Fargate;
using AbpSandbox.Remote;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AbpSandbox.Tests;

/// <summary>Runs only against a real deployment (deploy/aws): ABP_SANDBOX_FARGATE=1 and the stack's values in the environment.</summary>
public sealed class FargateFactAttribute : FactAttribute
{
    public FargateFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("ABP_SANDBOX_FARGATE") != "1")
        {
            Skip = "Set ABP_SANDBOX_FARGATE=1 and the ABP_SANDBOX_FARGATE_* values from the stack's outputs to run the Fargate provider on AWS.";
        }
    }
}

/// <summary>
/// The Fargate provider on AWS, in the closed network deploy/aws makes: the courier's round trip, what the work can't
/// reach, the time limit and a session. Credentials come from the SDK's usual places. Each test costs a Fargate task.
/// The values: ABP_SANDBOX_FARGATE_REGION, _CLUSTER, _SUBNETS (comma-separated), _SECURITY_GROUP, _EXECUTION_ROLE,
/// _BUCKET, _RUNNER and _IMAGE (both by digest).
/// </summary>
public sealed class FargateLiveTests : IAsyncLifetime
{
    private readonly string node = "abp-sandbox-live-" + Guid.NewGuid().ToString("N")[..8];
    private FargateTaskPlatform platform = null!;
    private S3ObjectStore store = null!;
    private RemoteSandboxProvider provider = null!;
    private Sandbox sandbox = null!;

    private static string Value(string name) =>
        Environment.GetEnvironmentVariable("ABP_SANDBOX_FARGATE_" + name) is { Length: > 0 } value ? value : throw new InvalidOperationException($"ABP_SANDBOX_FARGATE_{name} isn't set.");

    public Task InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("ABP_SANDBOX_FARGATE") != "1")
        {
            return Task.CompletedTask;
        }

        RegionEndpoint region = RegionEndpoint.GetBySystemName(Value("REGION"));
        IOptions<FargateSandboxOptions> fargate = Options.Create(new FargateSandboxOptions
        {
            Region = region.SystemName,
            Cluster = Value("CLUSTER"),
            Subnets = [.. Value("SUBNETS").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)],
            SecurityGroups = [Value("SECURITY_GROUP")],
            ExecutionRoleArn = Value("EXECUTION_ROLE"),
            Bucket = Value("BUCKET"),
        });
        platform = new FargateTaskPlatform(new AmazonECSClient(region), fargate);
        store = new S3ObjectStore(new AmazonS3Client(region), fargate);
        SandboxOptions options = new() { Images = new() { ["alpine"] = Value("IMAGE") }, Node = node, DefaultMemoryBytes = 256L * 1024 * 1024 };
        provider = new RemoteSandboxProvider(platform, store, Options.Create(new RemoteSandboxOptions { RunnerImage = Value("RUNNER") }),
            Options.Create(options), TimeProvider.System, NullLogger<RemoteSandboxProvider>.Instance);
        sandbox = new Sandbox(provider, Options.Create(options), TimeProvider.System, NullLogger<Sandbox>.Instance);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (Environment.GetEnvironmentVariable("ABP_SANDBOX_FARGATE") != "1")
        {
            return;
        }

        await sandbox.CloseAllAsync();
        await provider.SweepAsync(DateTimeOffset.UtcNow, node);
        sandbox.Dispose();
    }

    private static SandboxRequest Run(params string[] command) => new() { Owner = "tests", Image = "alpine", Command = command };

    /// <summary>This test's tasks still running, waiting a little for ECS to finish stopping them.</summary>
    private async Task<List<string>> LeftAsync()
    {
        for (int i = 0; ; i++)
        {
            List<string> left = [.. (await platform.ListAsync()).Where(t => t.Node == node).Select(t => t.Id)];
            if (left.Count == 0 || i == 30)
            {
                return left;
            }

            await Task.Delay(TimeSpan.FromSeconds(2));
        }
    }

    [FargateFact]
    public async Task A_run_goes_through_the_courier_and_back()
    {
        SandboxResult result = await sandbox.RunAsync(Run("sh", "-c", "id -u; cat in/hello.txt; mkdir -p out; tr a-z A-Z < in/hello.txt > out/HELLO.txt; echo warn >&2") with
        {
            Files = [new("in/hello.txt", Encoding.UTF8.GetBytes("hello fargate\n"))],
            Outputs = ["out"],
        });

        Assert.True(result.Succeeded, $"exit {result.ExitCode}; stdout [{result.Stdout}] stderr [{result.Stderr}]");
        Assert.Equal("65534\nhello fargate", result.Stdout.TrimEnd());
        Assert.Equal("warn", result.Stderr.Trim());
        SandboxFile file = Assert.Single(result.Files.Files);
        Assert.Equal(("out/HELLO.txt", "HELLO FARGATE\n"), (file.Path, Encoding.UTF8.GetString(file.Content)));
        Assert.Empty(await LeftAsync());
    }

    [FargateFact]
    public async Task The_work_reaches_nothing_outside_and_has_no_root_or_capabilities()
    {
        SandboxResult result = await sandbox.RunAsync(Run("sh", "-c", string.Join("; ",
            "touch /etc/x 2>/dev/null && echo root-writable",
            "wget -q -T 5 -O /dev/null http://1.1.1.1 2>/dev/null && echo ip-reached",
            "wget -q -T 5 -O /dev/null https://example.com 2>/dev/null && echo name-reached",
            "nslookup example.com >/dev/null 2>&1 && echo dns-answered",
            "nslookup s3.eu-north-1.amazonaws.com >/dev/null 2>&1 && echo aws-dns-answered",
            "grep CapEff /proc/self/status",
            "touch /sandbox/.abp/bin/x 2>/dev/null && echo runner-writable",
            "echo done")));

        Assert.True(result.Succeeded, result.Stderr);
        Assert.Contains("done", result.Stdout);
        Assert.DoesNotContain("root-writable", result.Stdout);
        Assert.DoesNotContain("runner-writable", result.Stdout);
        Assert.DoesNotContain("ip-reached", result.Stdout);
        Assert.DoesNotContain("name-reached", result.Stdout);
        Assert.DoesNotContain("dns-answered\n", result.Stdout.Replace("aws-dns-answered", ""));
        Assert.Contains("aws-dns-answered", result.Stdout);
        Assert.Contains("CapEff:\t0000000000000000", result.Stdout);
    }

    [FargateFact]
    public async Task A_run_past_its_time_is_killed()
    {
        SandboxResult result = await sandbox.RunAsync(Run("sh", "-c", "echo begun; sleep 120") with { Limits = new SandboxLimits { Time = TimeSpan.FromSeconds(5) } });
        Assert.True(result.TimedOut, $"exit {result.ExitCode} after {result.Duration}; stdout [{result.Stdout}] stderr [{result.Stderr}]");
        Assert.Contains("begun", result.Stdout);
        Assert.Empty(await LeftAsync());
    }

    [FargateFact]
    public async Task A_session_keeps_its_task_between_jobs()
    {
        ISandboxSession session = await sandbox.OpenSessionAsync(new SandboxSessionRequest { Owner = "tests", Key = "chat", Image = "alpine" });
        try
        {
            await session.PutFilesAsync([new("data.csv", Encoding.UTF8.GetBytes("a,1\nb,2\nc,3\n"))]);
            SandboxExecResult sum = await session.ExecAsync(["sh", "-c", "awk -F, '{s+=$2} END {print s}' data.csv | tee sum.txt"]);
            Assert.Equal("6", sum.Stdout.Trim());
            SandboxExecResult again = await session.ExecAsync(["cat", "sum.txt"]);
            Assert.Equal("6", again.Stdout.Trim());
            Assert.Single((await platform.ListAsync()).Where(t => t.Node == node));
        }
        finally
        {
            await session.CloseAsync();
        }

        Assert.Empty(await LeftAsync());
    }
}
