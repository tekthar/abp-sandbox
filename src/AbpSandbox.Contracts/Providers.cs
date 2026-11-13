namespace AbpSandbox;

/// <summary>
/// What runs containers: Docker, AWS Fargate, Kubernetes. A provider does the mechanics with values the module has
/// already checked (a pinned image reference, limits within the operator's maxima, paths inside the work folder); the
/// rules are the module's (docs/adr/0001, decision 6). Every container it makes is locked down (no network, no
/// capabilities, a user that isn't root, the root read-only unless the spec says it's a preparation), and carries
/// the spec's labels so a sweep finds it.
/// </summary>
public interface ISandboxProvider
{
    /// <summary>A short name for logs: "docker", "fargate".</summary>
    string Name { get; }

    SandboxCapabilities Capabilities { get; }

    /// <summary>Whether it can run containers now (its engine or service answers, its settings are there); quick, and never throws.</summary>
    Task<SandboxAvailability> CheckAsync(CancellationToken cancellationToken = default) => Task.FromResult(SandboxAvailability.Yes);

    /// <summary>Makes a container, not yet started, with its work folder empty.</summary>
    Task<ISandboxContainer> CreateAsync(SandboxContainerSpec spec, CancellationToken cancellationToken = default);

    /// <summary>The prepared image saved under a key, or null.</summary>
    Task<string?> FindPreparedAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Saves a preparation's container (its root, not its work folder) as an image under a key, and gives its reference.</summary>
    Task<string> SavePreparedAsync(ISandboxContainer container, string key, CancellationToken cancellationToken = default);

    /// <summary>The prepared images there are.</summary>
    Task<IReadOnlyList<SandboxPreparedImage>> ListPreparedAsync(CancellationToken cancellationToken = default);

    Task RemovePreparedAsync(string reference, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes containers past their expiry and, when <paramref name="node"/> is given, every container that node made
    /// (it restarted: what it made is no one's now). Gives how many it removed.
    /// </summary>
    Task<int> SweepAsync(DateTimeOffset now, string? node, CancellationToken cancellationToken = default);
}

/// <summary>A container a provider made. Removing it is safe to call more than once.</summary>
public interface ISandboxContainer
{
    string Id { get; }

    /// <summary>Puts files into the work folder; the container may be started or not.</summary>
    Task PutFilesAsync(IReadOnlyList<SandboxFile> files, CancellationToken cancellationToken = default);

    /// <summary>Starts the container's command and waits for it within the time, killing it past it.</summary>
    Task<SandboxExecResult> RunAsync(TimeSpan time, long maxOutputBytes, CancellationToken cancellationToken = default);

    /// <summary>Starts a container made to be kept (its command keeps it up).</summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs a command in a started container's work folder, as the spec's user. Past its time the command's output so
    /// far is given back with <see cref="SandboxExecResult.TimedOut"/>; the caller then removes the container, since a
    /// command can't be stopped alone.
    /// </summary>
    Task<SandboxExecResult> ExecAsync(IReadOnlyList<string> command, TimeSpan time, long maxOutputBytes, CancellationToken cancellationToken = default);

    /// <summary>Reads files or folders (paths relative to the work folder) up to a total; ones that aren't there are skipped.</summary>
    Task<SandboxFiles> GetFilesAsync(IReadOnlyList<string> paths, long maxBytes, CancellationToken cancellationToken = default);

    Task RemoveAsync(CancellationToken cancellationToken = default);
}

/// <summary>What a container is made with, checked by the module.</summary>
public sealed record SandboxContainerSpec
{
    /// <summary>The pinned image reference (or a prepared image's).</summary>
    public required string Image { get; init; }

    /// <summary>The command; null for a container kept up (a session's, a preparation's).</summary>
    public IReadOnlyList<string>? Command { get; init; }

    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();

    public required long MemoryBytes { get; init; }

    public required double Cpus { get; init; }

    public required int Processes { get; init; }

    /// <summary>The user ("uid:gid") the command and execs run as; null for the image's own (preparation only).</summary>
    public string? User { get; init; }

    /// <summary>A preparation: the root is writable and the image's default capabilities stay, less raw sockets and mknod.</summary>
    public bool Preparation { get; init; }

    /// <summary>"run", "session" or "prepare".</summary>
    public required string Kind { get; init; }

    /// <summary>The server that made it.</summary>
    public required string Node { get; init; }

    /// <summary>When a sweep may remove it, whatever its state.</summary>
    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>The owner, as a label for operators (hashed: owners may be names).</summary>
    public required string OwnerHash { get; init; }
}

/// <summary>A prepared image: its reference, key and when it was saved.</summary>
public sealed record SandboxPreparedImage(string Reference, string Key, DateTimeOffset CreatedAt);
