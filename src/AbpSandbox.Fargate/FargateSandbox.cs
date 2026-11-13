using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Amazon.ECS;
using Amazon.ECS.Model;
using Amazon.S3;
using Amazon.S3.Model;
using AbpSandbox.Remote;
using Microsoft.Extensions.Options;
using EcsTask = Amazon.ECS.Model.Task;
using Tag = Amazon.ECS.Model.Tag;
using Task = System.Threading.Tasks.Task;

namespace AbpSandbox.Fargate;

/// <summary>
/// The Fargate provider's settings, from "AbpSandbox:Fargate": what the deployment template (deploy/aws) made. The
/// subnets must have no route out but the template's endpoints; the provider can't check that, and without it a task
/// isn't a sandbox (docs/adr/0002, decision 5).
/// </summary>
public class FargateSandboxOptions
{
    public const string Section = "AbpSandbox:Fargate";

    /// <summary>The project's Region (eu-north-1, say); the SDK's own (AWS_REGION, the profile) when null.</summary>
    public string? Region { get; set; }

    /// <summary>The ECS cluster's name or ARN.</summary>
    public string Cluster { get; set; } = "";

    /// <summary>The closed private subnets tasks run in.</summary>
    public List<string> Subnets { get; set; } = [];

    /// <summary>The tasks' security group (out to the endpoints only).</summary>
    public List<string> SecurityGroups { get; set; } = [];

    /// <summary>The task execution role: pulls the images from ECR and writes logs. Tasks get no task role.</summary>
    public string ExecutionRoleArn { get; set; } = "";

    /// <summary>The bucket for jobs and results (with a lifecycle that removes them after a day).</summary>
    public string Bucket { get; set; } = "";

    /// <summary>A CloudWatch Logs group for the runner's own logs (not the work's output, which goes through the bucket); none by default.</summary>
    public string? LogGroup { get; set; }

    /// <summary>X86_64 or ARM64: the images' platform.</summary>
    public string Architecture { get; set; } = "X86_64";

    /// <summary>Fargate Spot, cheaper, may stop a task: for checks that can run again.</summary>
    public bool Spot { get; set; }

    /// <summary>The disk a task has (Fargate's ephemeral storage), in GiB: 21 to 200; Fargate's own 20 when null.</summary>
    public int? DiskGiB { get; set; }
}

/// <summary>
/// The sandbox's tasks on ECS with Fargate (docs/adr/0002). A task definition is registered once for each work
/// image, runner image and size; a task is started with the courier's manifest link and the work's environment as
/// overrides, and tags that say which server started it and when it expires.
/// </summary>
public sealed class FargateTaskPlatform(IAmazonECS ecs, IOptions<FargateSandboxOptions> options) : ISandboxTaskPlatform
{
    internal const string StartedBy = "abp-sandbox";
    private readonly ConcurrentDictionary<string, string> definitions = new(StringComparer.Ordinal);

    public async Task<SandboxAvailability> CheckAsync(CancellationToken cancellationToken = default)
    {
        FargateSandboxOptions o = options.Value;
        if (string.IsNullOrEmpty(o.Cluster) || o.Subnets.Count == 0 || o.SecurityGroups.Count == 0 || string.IsNullOrEmpty(o.ExecutionRoleArn))
        {
            return SandboxAvailability.No("The Fargate sandbox isn't set up: AbpSandbox:Fargate needs its cluster, subnets, security groups and execution role (deploy/aws).");
        }

        DescribeClustersResponse found = await ecs.DescribeClustersAsync(new DescribeClustersRequest { Clusters = [o.Cluster] }, cancellationToken);
        return found.Clusters?.FirstOrDefault() is { Status: "ACTIVE" }
            ? SandboxAvailability.Yes
            : SandboxAvailability.No($"The Fargate sandbox's cluster {o.Cluster} isn't there or isn't active.");
    }

    public async Task<string> StartAsync(SandboxTaskSpec spec, CancellationToken cancellationToken = default)
    {
        FargateSandboxOptions o = options.Value;
        if (string.IsNullOrEmpty(o.Cluster) || o.Subnets.Count == 0 || o.SecurityGroups.Count == 0 || string.IsNullOrEmpty(o.ExecutionRoleArn))
        {
            throw new SandboxRefusedException("The Fargate sandbox isn't set up: AbpSandbox:Fargate needs its cluster, subnets, security groups and execution role (deploy/aws).");
        }

        (int cpu, int memory) = FargateSizes.Fit(spec.Cpus, spec.MemoryBytes);
        string definition = await DefinitionAsync(spec.WorkImage, spec.RunnerImage, spec.User, cpu, memory, cancellationToken);
        RunTaskResponse response = await ecs.RunTaskAsync(new RunTaskRequest
        {
            Cluster = o.Cluster,
            TaskDefinition = definition,
            Count = 1,
            StartedBy = StartedBy,
            LaunchType = o.Spot ? null : LaunchType.FARGATE,
            CapacityProviderStrategy = o.Spot ? [new CapacityProviderStrategyItem { CapacityProvider = "FARGATE_SPOT", Weight = 1 }] : null,
            NetworkConfiguration = new NetworkConfiguration
            {
                AwsvpcConfiguration = new AwsVpcConfiguration { Subnets = o.Subnets, SecurityGroups = o.SecurityGroups, AssignPublicIp = AssignPublicIp.DISABLED },
            },
            Overrides = new TaskOverride
            {
                ContainerOverrides =
                [
                    new ContainerOverride { Name = "work", Environment = [.. spec.Environment.Select(e => new Amazon.ECS.Model.KeyValuePair { Name = e.Key, Value = e.Value })] },
                    new ContainerOverride { Name = "courier", Environment = [new Amazon.ECS.Model.KeyValuePair { Name = "ABPSANDBOX_MANIFEST", Value = spec.ManifestUrl.ToString() }] },
                ],
            },
            Tags =
            [
                new Tag { Key = "abp-sandbox", Value = "1" },
                new Tag { Key = "abp-sandbox.kind", Value = spec.Kind },
                new Tag { Key = "abp-sandbox.node", Value = spec.Node },
                new Tag { Key = "abp-sandbox.owner", Value = spec.OwnerHash },
                new Tag { Key = "abp-sandbox.expires", Value = spec.ExpiresAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) },
            ],
            PropagateTags = PropagateTags.NONE,
        }, cancellationToken);

        if (response.Tasks is not { Count: > 0 })
        {
            string why = string.Join("; ", (response.Failures ?? []).Select(f => $"{f.Reason} {f.Detail}".Trim()));
            throw new SandboxRefusedException($"Fargate didn't start the sandbox's task: {(why.Length == 0 ? "no reason given" : why)}.");
        }

        return response.Tasks[0].TaskArn;
    }

    public async Task StopAsync(string taskId, CancellationToken cancellationToken = default)
    {
        try
        {
            await ecs.StopTaskAsync(new StopTaskRequest { Cluster = options.Value.Cluster, Task = taskId, Reason = "The sandbox is done with it." }, cancellationToken);
        }
        catch (InvalidParameterException)
        {
            // gone already
        }
    }

    public async Task<SandboxTaskState> StateAsync(string taskId, CancellationToken cancellationToken = default)
    {
        DescribeTasksResponse response = await ecs.DescribeTasksAsync(new DescribeTasksRequest { Cluster = options.Value.Cluster, Tasks = [taskId] }, cancellationToken);
        EcsTask? task = response.Tasks?.FirstOrDefault();
        return task?.LastStatus switch
        {
            null => new SandboxTaskState(SandboxTaskStatus.Stopped, "Fargate doesn't know the task."),
            "RUNNING" => new SandboxTaskState(SandboxTaskStatus.Running),
            "PROVISIONING" or "PENDING" or "ACTIVATING" => new SandboxTaskState(SandboxTaskStatus.Starting),
            _ => new SandboxTaskState(SandboxTaskStatus.Stopped, task.StoppedReason ?? Exited(task)),
        };

        static string? Exited(EcsTask task) => string.Join("; ", (task.Containers ?? []).Where(c => c.Reason is not null).Select(c => $"{c.Name}: {c.Reason}")) is { Length: > 0 } r ? r : null;
    }

    public async Task<IReadOnlyList<SandboxTaskInfo>> ListAsync(CancellationToken cancellationToken = default)
    {
        List<string> arns = [];
        string? next = null;
        do
        {
            ListTasksResponse page = await ecs.ListTasksAsync(new ListTasksRequest
            {
                Cluster = options.Value.Cluster,
                StartedBy = StartedBy,
                DesiredStatus = DesiredStatus.RUNNING,
                NextToken = next,
            }, cancellationToken);
            arns.AddRange(page.TaskArns ?? []);
            next = page.NextToken;
        }
        while (next is not null);

        List<SandboxTaskInfo> tasks = [];
        foreach (string[] batch in arns.Chunk(100))
        {
            DescribeTasksResponse described = await ecs.DescribeTasksAsync(new DescribeTasksRequest
            {
                Cluster = options.Value.Cluster,
                Tasks = [.. batch],
                Include = ["TAGS"],
            }, cancellationToken);
            foreach (EcsTask task in described.Tasks ?? [])
            {
                string? Tag(string key) => task.Tags?.FirstOrDefault(t => t.Key == key)?.Value;
                long expires = long.TryParse(Tag("abp-sandbox.expires"), NumberStyles.Integer, CultureInfo.InvariantCulture, out long e) ? e : 0;
                tasks.Add(new SandboxTaskInfo(task.TaskArn, Tag("abp-sandbox.node") ?? "", DateTimeOffset.FromUnixTimeSeconds(expires)));
            }
        }

        return tasks;
    }

    /// <summary>The task definition for an image pair, user and size: registered once, found again by its family after a restart.</summary>
    private async Task<string> DefinitionAsync(string work, string runner, string user, int cpu, int memory, CancellationToken cancellationToken)
    {
        FargateSandboxOptions o = options.Value;
        string shape = string.Join('\n', work, runner, user, cpu, memory, o.Architecture, o.ExecutionRoleArn, o.LogGroup ?? "", o.DiskGiB?.ToString(CultureInfo.InvariantCulture) ?? "", "1");
        string family = "abpsandbox-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(shape)))[..20];
        if (definitions.TryGetValue(family, out string? arn))
        {
            return arn;
        }

        try
        {
            DescribeTaskDefinitionResponse found = await ecs.DescribeTaskDefinitionAsync(new DescribeTaskDefinitionRequest { TaskDefinition = family }, cancellationToken);
            if (found.TaskDefinition?.Status == TaskDefinitionStatus.ACTIVE)
            {
                return definitions[family] = found.TaskDefinition.TaskDefinitionArn;
            }
        }
        catch (ClientException)
        {
            // not registered yet
        }

        RegisterTaskDefinitionResponse registered = await ecs.RegisterTaskDefinitionAsync(Definition(family, work, runner, user, cpu, memory), cancellationToken);
        return definitions[family] = registered.TaskDefinition.TaskDefinitionArn;
    }

    /// <summary>The three containers of docs/adr/0002, decision 2, locked down as far as Fargate allows.</summary>
    internal RegisterTaskDefinitionRequest Definition(string family, string work, string runner, string user, int cpu, int memory)
    {
        FargateSandboxOptions o = options.Value;
        string[] ids = user.Split(':');
        LogConfiguration? logs = o.LogGroup is null ? null : new LogConfiguration
        {
            LogDriver = LogDriver.Awslogs,
            Options = new Dictionary<string, string> { ["awslogs-group"] = o.LogGroup, ["awslogs-stream-prefix"] = "abp-sandbox" },
        };
        LinuxParameters locked = new() { Capabilities = new KernelCapabilities { Drop = ["ALL"] }, InitProcessEnabled = true };
        MountPoint Sandbox(bool readOnly = false) => new() { SourceVolume = "sandbox", ContainerPath = "/sandbox", ReadOnly = readOnly };
        MountPoint Private() => new() { SourceVolume = "private", ContainerPath = "/private" };
        return new RegisterTaskDefinitionRequest
        {
            Family = family,
            RequiresCompatibilities = ["FARGATE"],
            NetworkMode = NetworkMode.Awsvpc,
            Cpu = cpu.ToString(CultureInfo.InvariantCulture),
            Memory = memory.ToString(CultureInfo.InvariantCulture),
            ExecutionRoleArn = o.ExecutionRoleArn,
            RuntimePlatform = new RuntimePlatform { CpuArchitecture = o.Architecture, OperatingSystemFamily = OSFamily.LINUX },
            EphemeralStorage = o.DiskGiB is { } disk ? new EphemeralStorage { SizeInGiB = disk } : null,
            Volumes = [new Volume { Name = "sandbox" }, new Volume { Name = "private" }],
            Tags = [new Tag { Key = "abp-sandbox", Value = "1" }],
            ContainerDefinitions =
            [
                new ContainerDefinition
                {
                    Name = "init",
                    Image = runner,
                    Essential = false,
                    User = "0",
                    EntryPoint = ["sh"],
                    Command = ["/abp/init.sh"],
                    ReadonlyRootFilesystem = true,
                    Environment = [new() { Name = "ABPSANDBOX_UID", Value = ids[0] }, new() { Name = "ABPSANDBOX_GID", Value = ids.Length > 1 ? ids[1] : ids[0] }],
                    MountPoints = [Sandbox(), Private()],
                    LogConfiguration = logs,
                },
                new ContainerDefinition
                {
                    Name = "work",
                    Image = work,
                    Essential = true,
                    User = user,
                    EntryPoint = ["/sandbox/.abp/bin/busybox"],
                    Command = ["sh", "/sandbox/.abp/work.sh"],
                    ReadonlyRootFilesystem = true,
                    LinuxParameters = locked,
                    DependsOn = [new ContainerDependency { ContainerName = "init", Condition = ContainerCondition.SUCCESS }],
                    MountPoints = [Sandbox()],
                    // The work's output goes through the bucket, never to the logs.
                },
                new ContainerDefinition
                {
                    Name = "courier",
                    Image = runner,
                    Essential = false,
                    User = "65520:65520",
                    EntryPoint = ["sh"],
                    Command = ["/abp/courier.sh"],
                    ReadonlyRootFilesystem = true,
                    LinuxParameters = locked,
                    DependsOn = [new ContainerDependency { ContainerName = "init", Condition = ContainerCondition.SUCCESS }],
                    MountPoints = [Sandbox(), Private()],
                    LogConfiguration = logs,
                },
            ],
        };
    }
}

/// <summary>Fargate's CPU and memory pairs (vCPU units and MiB): the smallest that holds what a request asks for.</summary>
public static class FargateSizes
{
    private static readonly (int Cpu, int Min, int Max, int Step)[] Sizes =
    [
        (256, 512, 2048, 512),
        (512, 1024, 4096, 1024),
        (1024, 2048, 8192, 1024),
        (2048, 4096, 16384, 1024),
        (4096, 8192, 30720, 1024),
        (8192, 16384, 61440, 4096),
        (16384, 32768, 122880, 8192),
    ];

    public static (int Cpu, int MemoryMiB) Fit(double cpus, long memoryBytes)
    {
        int needCpu = (int)Math.Ceiling(cpus * 1024);
        int needMemory = (int)Math.Ceiling(memoryBytes / (1024.0 * 1024));
        foreach ((int cpu, int min, int max, int step) in Sizes)
        {
            if (cpu < needCpu || max < needMemory)
            {
                continue;
            }

            int memory = needMemory <= min ? min : min + ((needMemory - min + step - 1) / step * step);
            return (cpu, memory);
        }

        throw new SandboxRefusedException($"Fargate has no task with {cpus} CPUs and {memoryBytes / (1024 * 1024)} MiB.");
    }
}

/// <summary>Jobs and results in an S3 bucket, with presigned URLs for the courier (at most 7 days, SigV4's limit).</summary>
public sealed class S3ObjectStore(IAmazonS3 s3, IOptions<FargateSandboxOptions> options) : ISandboxObjectStore
{
    private string Bucket => string.IsNullOrEmpty(options.Value.Bucket)
        ? throw new SandboxRefusedException("The Fargate sandbox has no bucket (AbpSandbox:Fargate:Bucket).")
        : options.Value.Bucket;

    public async Task<SandboxAvailability> CheckAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(options.Value.Bucket))
        {
            return SandboxAvailability.No("The Fargate sandbox has no bucket (AbpSandbox:Fargate:Bucket).");
        }

        try
        {
            await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = options.Value.Bucket, MaxKeys = 1 }, cancellationToken);
            return SandboxAvailability.Yes;
        }
        catch (AmazonS3Exception e)
        {
            return SandboxAvailability.No($"The Fargate sandbox's bucket {options.Value.Bucket} doesn't answer: {e.Message}");
        }
    }

    public async Task PutAsync(string key, byte[] content, CancellationToken cancellationToken = default) =>
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = Bucket, Key = key, InputStream = new MemoryStream(content, writable: false) }, cancellationToken);

    public async Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            using GetObjectResponse response = await s3.GetObjectAsync(Bucket, key, cancellationToken);
            using MemoryStream copy = new();
            await response.ResponseStream.CopyToAsync(copy, cancellationToken);
            return copy.ToArray();
        }
        catch (AmazonS3Exception e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task DeletePrefixAsync(string prefix, CancellationToken cancellationToken = default)
    {
        string? next = null;
        do
        {
            ListObjectsV2Response page = await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = Bucket, Prefix = prefix, ContinuationToken = next }, cancellationToken);
            if (page.S3Objects is { Count: > 0 } objects)
            {
                await s3.DeleteObjectsAsync(new DeleteObjectsRequest { BucketName = Bucket, Objects = [.. objects.Select(o => new KeyVersion { Key = o.Key })] }, cancellationToken);
            }

            next = page.IsTruncated == true ? page.NextContinuationToken : null;
        }
        while (next is not null);
    }

    public Uri SignGet(string key, DateTimeOffset expires) => Sign(key, expires, HttpVerb.GET);

    public Uri SignPut(string key, DateTimeOffset expires) => Sign(key, expires, HttpVerb.PUT);

    private Uri Sign(string key, DateTimeOffset expires, HttpVerb verb)
    {
        DateTimeOffset most = DateTimeOffset.UtcNow.AddDays(7).AddMinutes(-5);
        return new Uri(s3.GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = Bucket,
            Key = key,
            Verb = verb,
            Expires = (expires < most ? expires : most).UtcDateTime,
            Protocol = Protocol.HTTPS,
        }));
    }
}
