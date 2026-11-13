namespace AbpSandbox.Remote;

/// <summary>
/// Object storage for a cloud provider (docs/adr/0002, decision 1): S3, Azure Blob Storage. Keys are the provider's;
/// a signed link lets the task's courier read or write one object until it expires, with no credentials of its own.
/// </summary>
public interface ISandboxObjectStore
{
    /// <summary>Whether the store answers and its settings are there.</summary>
    Task<SandboxAvailability> CheckAsync(CancellationToken cancellationToken = default) => Task.FromResult(SandboxAvailability.Yes);

    Task PutAsync(string key, byte[] content, CancellationToken cancellationToken = default);

    /// <summary>The object, or null when it isn't there (yet).</summary>
    Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Removes every object under a prefix.</summary>
    Task DeletePrefixAsync(string prefix, CancellationToken cancellationToken = default);

    /// <summary>A link that reads one object with a plain GET until it expires (a store may make it expire sooner).</summary>
    Uri SignGet(string key, DateTimeOffset expires);

    /// <summary>A link that writes one object with a plain PUT of its bytes until it expires.</summary>
    Uri SignPut(string key, DateTimeOffset expires);
}

/// <summary>
/// A cloud's container tasks (docs/adr/0002, decision 2): Fargate (ECS), Azure Container Instances. A task is the
/// runner's init, then the operator's image running the work loop and the runner's courier, on a shared volume, in a
/// network the deployment closed.
/// </summary>
public interface ISandboxTaskPlatform
{
    /// <summary>Whether the platform answers and its settings are there.</summary>
    Task<SandboxAvailability> CheckAsync(CancellationToken cancellationToken = default) => Task.FromResult(SandboxAvailability.Yes);

    /// <summary>Starts a task and gives its ID; it may still be starting when this returns.</summary>
    Task<string> StartAsync(SandboxTaskSpec spec, CancellationToken cancellationToken = default);

    /// <summary>Stops a task; one that's gone already is fine.</summary>
    Task StopAsync(string taskId, CancellationToken cancellationToken = default);

    Task<SandboxTaskState> StateAsync(string taskId, CancellationToken cancellationToken = default);

    /// <summary>The sandbox's tasks there are, with the server that started them and when they expire.</summary>
    Task<IReadOnlyList<SandboxTaskInfo>> ListAsync(CancellationToken cancellationToken = default);
}

/// <summary>What a task is started with, checked by the module and the provider.</summary>
public sealed record SandboxTaskSpec
{
    /// <summary>A name for the cloud's console: abpsandbox-{kind}-{id}.</summary>
    public required string Name { get; init; }

    /// <summary>The operator's image, pinned, for the work container.</summary>
    public required string WorkImage { get; init; }

    /// <summary>The runner's image, pinned, for the init and courier containers.</summary>
    public required string RunnerImage { get; init; }

    /// <summary>The signed link to the task's manifest, given only to the courier.</summary>
    public required Uri ManifestUrl { get; init; }

    /// <summary>The user ("uid:gid") the work runs as; never root.</summary>
    public required string User { get; init; }

    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();

    public required long MemoryBytes { get; init; }

    public required double Cpus { get; init; }

    /// <summary>A process limit, where the platform has one (Fargate has none; the time limit bounds a fork bomb there).</summary>
    public required int Processes { get; init; }

    public required string Kind { get; init; }

    public required string Node { get; init; }

    public required DateTimeOffset ExpiresAt { get; init; }

    public required string OwnerHash { get; init; }
}

public enum SandboxTaskStatus
{
    Starting,
    Running,
    Stopped,
}

/// <summary>A task's state, and why it stopped when the platform says.</summary>
public sealed record SandboxTaskState(SandboxTaskStatus Status, string? Reason = null);

public sealed record SandboxTaskInfo(string Id, string Node, DateTimeOffset ExpiresAt);

/// <summary>The cloud provider's settings, from "AbpSandbox:Remote".</summary>
public class RemoteSandboxOptions
{
    public const string Section = "AbpSandbox:Remote";

    /// <summary>The runner's image (runner/Dockerfile), pinned by digest, in the cloud's registry.</summary>
    public string RunnerImage { get; set; } = "";

    /// <summary>Where a task's objects go in the store.</summary>
    public string Prefix { get; set; } = "abp-sandbox/";

    /// <summary>How often the provider looks for a job's result.</summary>
    public TimeSpan PollEvery { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>How long a task may take to start (pull its images, get its network) before its first job fails.</summary>
    public TimeSpan StartTime { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>The jobs a session may run (its manifest signs a link for each).</summary>
    public int MaxSessionJobs { get; set; } = 1000;

    /// <summary>The time a job's result may take past its own time (the courier's poll and the uploads).</summary>
    public TimeSpan JobGrace { get; set; } = TimeSpan.FromSeconds(30);
}
