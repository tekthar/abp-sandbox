namespace AbpSandbox;

/// <summary>
/// Untrusted work in containers (docs/adr/0001): a run is thrown away, a session is kept under a key, and a request's
/// preparation is saved as an image that later requests start from. What a request names is checked against the
/// operator's settings: its image is a name the settings map to a pinned reference, its limits are at most theirs.
/// </summary>
public interface ISandbox
{
    /// <summary>What the configured provider can do.</summary>
    SandboxCapabilities Capabilities { get; }

    /// <summary>Makes a container, gives it the files, runs the command within the limits, and removes it, whatever happened.</summary>
    Task<SandboxResult> RunAsync(SandboxRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens a kept container for an owner under a key, or gives back the one open under them while it lives and its
    /// image is the same (one with another image is closed first).
    /// </summary>
    Task<ISandboxSession> OpenSessionAsync(SandboxSessionRequest request, CancellationToken cancellationToken = default);

    /// <summary>The session open for an owner under a key on this server, or null.</summary>
    ISandboxSession? FindSession(string owner, string key);
}

/// <summary>A kept container. Its calls take turns; one whose command runs past its time closes the session.</summary>
public interface ISandboxSession
{
    string Id { get; }

    string Owner { get; }

    string Key { get; }

    /// <summary>The image name the session was opened with.</summary>
    string Image { get; }

    DateTimeOffset OpenedAt { get; }

    /// <summary>When it closes unless used again (its idle time), and never past its lifetime.</summary>
    DateTimeOffset ExpiresAt { get; }

    bool IsClosed { get; }

    /// <summary>Runs a command in the work folder.</summary>
    Task<SandboxExecResult> ExecAsync(IReadOnlyList<string> command, TimeSpan? timeout = null, CancellationToken cancellationToken = default);

    /// <summary>Puts files into the work folder (paths relative to it).</summary>
    Task PutFilesAsync(IReadOnlyList<SandboxFile> files, CancellationToken cancellationToken = default);

    /// <summary>Gets files or folders from the work folder (paths relative to it), within the output cap.</summary>
    Task<SandboxFiles> GetFilesAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default);

    /// <summary>Closes the session and removes its container.</summary>
    Task CloseAsync(CancellationToken cancellationToken = default);
}

/// <summary>What a provider can do; the module refuses what it can't.</summary>
public sealed record SandboxCapabilities(bool Sessions, bool Preparation);

/// <summary>A file by its path relative to the work folder, with forward slashes.</summary>
public sealed record SandboxFile(string Path, byte[] Content);

/// <summary>Files read back, and whether some were left out by the output cap.</summary>
public sealed record SandboxFiles(IReadOnlyList<SandboxFile> Files, bool Truncated)
{
    public static readonly SandboxFiles None = new([], false);
}

/// <summary>
/// Limits a request asks for; each is at most the operator's maximum (a request above it is refused), and one left
/// null is the operator's default.
/// </summary>
public sealed record SandboxLimits
{
    public TimeSpan? Time { get; init; }

    public long? MemoryBytes { get; init; }

    /// <summary>CPUs, fractions allowed (0.5 is half a CPU).</summary>
    public double? Cpus { get; init; }

    public int? Processes { get; init; }
}

/// <summary>
/// Steps run once from the base image and saved as an image that later requests with the same base, steps, files and
/// scope start from. Steps come from the host's code; content someone uploaded may be in <see cref="Files"/> only
/// when <see cref="Shared"/> is false.
/// </summary>
public sealed record SandboxPreparation
{
    /// <summary>Commands run in turn in the work folder; the first that fails fails the preparation.</summary>
    public required IReadOnlyList<IReadOnlyList<string>> Steps { get; init; }

    /// <summary>Files put in the work folder before the steps. They are part of the key, and aren't kept in the image's work folder.</summary>
    public IReadOnlyList<SandboxFile> Files { get; init; } = [];

    /// <summary>Whether owners share the prepared image. Only for steps and files that are the host's own.</summary>
    public bool Shared { get; init; }
}

/// <summary>A throwaway run.</summary>
public sealed record SandboxRequest
{
    /// <summary>Who the run is for (an agent in a scope, a package's review): limits and prepared images go by it.</summary>
    public required string Owner { get; init; }

    /// <summary>The image's name in the operator's settings (AbpSandbox:Images).</summary>
    public required string Image { get; init; }

    /// <summary>The command, as arguments (no shell unless the command is one).</summary>
    public required IReadOnlyList<string> Command { get; init; }

    /// <summary>Files put in the work folder before the command runs.</summary>
    public IReadOnlyList<SandboxFile> Files { get; init; } = [];

    /// <summary>Files or folders in the work folder read back after the command, within the output cap.</summary>
    public IReadOnlyList<string> Outputs { get; init; } = [];

    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();

    public SandboxLimits Limits { get; init; } = new();

    public SandboxPreparation? Preparation { get; init; }
}

/// <summary>A kept container.</summary>
public sealed record SandboxSessionRequest
{
    public required string Owner { get; init; }

    /// <summary>What the session is kept under for the owner (a chat, a run).</summary>
    public required string Key { get; init; }

    public required string Image { get; init; }

    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();

    /// <summary>Limits for the container (memory, CPUs, processes) and for each command (time).</summary>
    public SandboxLimits Limits { get; init; } = new();

    public SandboxPreparation? Preparation { get; init; }

    /// <summary>How long it stays open unused; at most the operator's maximum.</summary>
    public TimeSpan? IdleTime { get; init; }
}

/// <summary>What a command gave back.</summary>
public record SandboxExecResult
{
    /// <summary>The command's exit code; -1 when it was killed for its time.</summary>
    public required int ExitCode { get; init; }

    public required bool TimedOut { get; init; }

    public required string Stdout { get; init; }

    public required string Stderr { get; init; }

    /// <summary>Whether stdout or stderr was cut at the output cap.</summary>
    public required bool OutputTruncated { get; init; }

    public required TimeSpan Duration { get; init; }

    public bool Succeeded => ExitCode == 0 && !TimedOut;
}

/// <summary>What a run gave back: its command's result, the output files, and whether it started from a prepared image.</summary>
public sealed record SandboxResult : SandboxExecResult
{
    public required SandboxFiles Files { get; init; }

    /// <summary>Whether the run started from a prepared image saved before (false when it prepared one, or had none).</summary>
    public required bool PreparedFromCache { get; init; }

    /// <summary>Whether a warm container (started before the run, never used) served it.</summary>
    public bool FromWarmPool { get; init; }
}

/// <summary>A request the sandbox won't run: an unknown image, limits past the maximum, a path outside the work folder, too much input.</summary>
public sealed class SandboxRefusedException(string message) : Exception(message);

/// <summary>A preparation step that failed: its result says why.</summary>
public sealed class SandboxPreparationFailedException(int step, SandboxExecResult result)
    : Exception($"Preparation step {step + 1} failed with exit code {result.ExitCode}{(result.TimedOut ? " (timed out)" : "")}.")
{
    public int Step { get; } = step;

    public SandboxExecResult Result { get; } = result;
}
