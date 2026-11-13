using System.Reflection;
using Amazon;
using Amazon.ECS;
using Amazon.ECS.Model;
using Amazon.Runtime;
using Amazon.S3;
using AbpSandbox.Fargate;
using AbpSandbox.Remote;
using Microsoft.Extensions.Options;
using Task = System.Threading.Tasks.Task;

namespace AbpSandbox.Tests;

/// <summary>What the Fargate provider asks AWS for, checked without AWS: a fake ECS that records, S3 links signed offline.</summary>
public class FargateTests
{
    private const string Work = "123456789012.dkr.ecr.eu-north-1.amazonaws.com/work@sha256:" + "aa";
    private const string Runner = "123456789012.dkr.ecr.eu-north-1.amazonaws.com/abpsandbox-runner@sha256:" + "bb";

    private static FargateSandboxOptions Options() => new()
    {
        Cluster = "abp-sandbox",
        Subnets = ["subnet-1", "subnet-2"],
        SecurityGroups = ["sg-1"],
        ExecutionRoleArn = "arn:aws:iam::123456789012:role/abp-sandbox-execution",
        Bucket = "abp-sandbox-jobs",
    };

    private static SandboxTaskSpec Spec() => new()
    {
        Name = "abpsandbox-run-1",
        WorkImage = Work,
        RunnerImage = Runner,
        ManifestUrl = new Uri("https://abp-sandbox-jobs.s3.eu-north-1.amazonaws.com/abp-sandbox/1/manifest?X-Amz-Signature=x"),
        User = "65534:65534",
        Environment = new Dictionary<string, string> { ["MODE"] = "check" },
        MemoryBytes = 512L * 1024 * 1024,
        Cpus = 1,
        Processes = 128,
        Kind = "run",
        Node = "api-1",
        ExpiresAt = DateTimeOffset.FromUnixTimeSeconds(2_000_000_000),
        OwnerHash = "0123456789abcdef",
    };

    [Theory]
    [InlineData(0.25, 512, 256, 512)]
    [InlineData(0.5, 512, 512, 1024)]
    [InlineData(1, 512, 1024, 2048)]
    [InlineData(1, 3000, 1024, 3072)]
    [InlineData(2, 2048, 2048, 4096)]
    [InlineData(0.25, 3000, 512, 3072)]
    [InlineData(4, 40000, 8192, 40960)]
    public void A_request_gets_the_smallest_Fargate_size_that_holds_it(double cpus, int memoryMiB, int cpu, int memory) =>
        Assert.Equal((cpu, memory), FargateSizes.Fit(cpus, memoryMiB * 1024L * 1024));

    [Fact]
    public void Past_Fargates_largest_is_refused() =>
        Assert.Throws<SandboxRefusedException>(() => FargateSizes.Fit(32, 1024L * 1024 * 1024));

    [Fact]
    public async Task A_task_is_three_containers_locked_down_in_the_closed_subnets_with_no_task_role()
    {
        FakeEcs ecs = FakeEcs.Create();
        FargateTaskPlatform platform = new(ecs.Client, Microsoft.Extensions.Options.Options.Create(Options()));
        string arn = await platform.StartAsync(Spec());
        Assert.Equal("arn:aws:ecs:eu-north-1:123456789012:task/abp-sandbox/1", arn);

        RegisterTaskDefinitionRequest definition = Assert.Single(ecs.Registered);
        Assert.Null(definition.TaskRoleArn);
        Assert.Equal(("1024", "2048"), (definition.Cpu, definition.Memory));
        Assert.Equal(["init", "work", "courier"], definition.ContainerDefinitions.Select(c => c.Name));

        ContainerDefinition init = definition.ContainerDefinitions[0];
        Assert.Equal((Runner, "0", false), (init.Image, init.User, init.Essential));

        ContainerDefinition work = definition.ContainerDefinitions[1];
        Assert.Equal((Work, "65534:65534", true, true), (work.Image, work.User, work.Essential, work.ReadonlyRootFilesystem));
        Assert.Equal(["ALL"], work.LinuxParameters.Capabilities.Drop);
        Assert.Equal("/sandbox/.abp/bin/busybox", Assert.Single(work.EntryPoint));
        Assert.Equal(("init", ContainerCondition.SUCCESS), (work.DependsOn[0].ContainerName, work.DependsOn[0].Condition));
        Assert.Null(work.LogConfiguration);
        Assert.DoesNotContain(work.MountPoints, m => m.SourceVolume == "private");

        ContainerDefinition courier = definition.ContainerDefinitions[2];
        Assert.Equal((Runner, "65520:65520", true), (courier.Image, courier.User, courier.ReadonlyRootFilesystem));
        Assert.Equal(["ALL"], courier.LinuxParameters.Capabilities.Drop);

        RunTaskRequest run = Assert.Single(ecs.Runs);
        Assert.Equal(AssignPublicIp.DISABLED, run.NetworkConfiguration.AwsvpcConfiguration.AssignPublicIp);
        Assert.Equal(["subnet-1", "subnet-2"], run.NetworkConfiguration.AwsvpcConfiguration.Subnets);
        Assert.Equal("abp-sandbox", run.StartedBy);
        Assert.Equal("MODE", Assert.Single(run.Overrides.ContainerOverrides.Single(c => c.Name == "work").Environment).Name);
        Assert.Equal(Spec().ManifestUrl.ToString(), run.Overrides.ContainerOverrides.Single(c => c.Name == "courier").Environment.Single(e => e.Name == "ABPSANDBOX_MANIFEST").Value);
        Assert.DoesNotContain(run.Overrides.ContainerOverrides.Single(c => c.Name == "work").Environment, e => e.Name == "ABPSANDBOX_MANIFEST");
        Assert.Equal("2000000000", run.Tags.Single(t => t.Key == "abp-sandbox.expires").Value);

        await platform.StartAsync(Spec());
        Assert.Single(ecs.Registered); // the same images and size reuse the definition
    }

    [Fact]
    public async Task Fargates_refusal_says_why()
    {
        FakeEcs ecs = FakeEcs.Create();
        ecs.Failure = "RESOURCE:MEMORY";
        FargateTaskPlatform platform = new(ecs.Client, Microsoft.Extensions.Options.Options.Create(Options()));
        SandboxRefusedException e = await Assert.ThrowsAsync<SandboxRefusedException>(() => platform.StartAsync(Spec()));
        Assert.Contains("RESOURCE:MEMORY", e.Message);
    }

    [Fact]
    public async Task Without_the_templates_network_nothing_starts()
    {
        FakeEcs ecs = FakeEcs.Create();
        FargateTaskPlatform platform = new(ecs.Client, Microsoft.Extensions.Options.Options.Create(Options().WithoutSubnets()));
        await Assert.ThrowsAsync<SandboxRefusedException>(() => platform.StartAsync(Spec()));
        Assert.Empty(ecs.Runs);
    }

    [Fact]
    public void S3_links_are_https_for_one_object_and_at_most_a_week()
    {
        using AmazonS3Client s3 = new(new BasicAWSCredentials("AKIDEXAMPLE", "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY"), RegionEndpoint.EUNorth1);
        S3ObjectStore store = new(s3, Microsoft.Extensions.Options.Options.Create(Options()));
        Uri get = store.SignGet("abp-sandbox/1/in/1", DateTimeOffset.UtcNow.AddDays(30));
        Uri put = store.SignPut("abp-sandbox/1/out/1", DateTimeOffset.UtcNow.AddMinutes(10));
        Assert.Equal("https", get.Scheme);
        Assert.Contains("abp-sandbox-jobs", get.Host + get.AbsolutePath);
        Assert.EndsWith("abp-sandbox/1/in/1", get.AbsolutePath);
        int expires = int.Parse(System.Web.HttpUtility.ParseQueryString(get.Query)["X-Amz-Expires"]!);
        Assert.InRange(expires, 600_000, 604_800);
        Assert.InRange(int.Parse(System.Web.HttpUtility.ParseQueryString(put.Query)["X-Amz-Expires"]!), 500, 600);
    }

    /// <summary>A fake ECS client: records task definitions and runs, and answers like ECS.</summary>
    public class FakeEcs : DispatchProxy
    {
        public List<RegisterTaskDefinitionRequest> Registered { get; } = [];

        public List<RunTaskRequest> Runs { get; } = [];

        public string? Failure { get; set; }

        public IAmazonECS Client { get; private set; } = null!;

        public static FakeEcs Create()
        {
            IAmazonECS client = Create<IAmazonECS, FakeEcs>();
            FakeEcs fake = (FakeEcs)(object)client;
            fake.Client = client;
            return fake;
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
        {
            nameof(IAmazonECS.DescribeTaskDefinitionAsync) when Registered.Count == 0 => throw new ClientException("Unable to describe task definition."),
            nameof(IAmazonECS.DescribeTaskDefinitionAsync) => System.Threading.Tasks.Task.FromResult(new DescribeTaskDefinitionResponse
            {
                TaskDefinition = new TaskDefinition { Status = TaskDefinitionStatus.ACTIVE, TaskDefinitionArn = "arn:aws:ecs:eu-north-1:123456789012:task-definition/x:1" },
            }),
            nameof(IAmazonECS.RegisterTaskDefinitionAsync) => Register((RegisterTaskDefinitionRequest)args![0]!),
            nameof(IAmazonECS.RunTaskAsync) => Run((RunTaskRequest)args![0]!),
            _ => throw new NotSupportedException(method.Name),
        };

        private System.Threading.Tasks.Task<RegisterTaskDefinitionResponse> Register(RegisterTaskDefinitionRequest request)
        {
            Registered.Add(request);
            return System.Threading.Tasks.Task.FromResult(new RegisterTaskDefinitionResponse
            {
                TaskDefinition = new TaskDefinition { TaskDefinitionArn = $"arn:aws:ecs:eu-north-1:123456789012:task-definition/{request.Family}:1" },
            });
        }

        private System.Threading.Tasks.Task<RunTaskResponse> Run(RunTaskRequest request)
        {
            Runs.Add(request);
            return System.Threading.Tasks.Task.FromResult(Failure is null
                ? new RunTaskResponse { Tasks = [new Amazon.ECS.Model.Task { TaskArn = $"arn:aws:ecs:eu-north-1:123456789012:task/abp-sandbox/{Runs.Count}" }] }
                : new RunTaskResponse { Tasks = [], Failures = [new Failure { Reason = Failure }] });
        }
    }
}

internal static class FargateOptionsExtensions
{
    public static FargateSandboxOptions WithoutSubnets(this FargateSandboxOptions options)
    {
        options.Subnets = [];
        return options;
    }
}
