namespace AbpSandbox;

/// <summary>
/// The operator's settings, from the host's "AbpSandbox" section: the images requests may name, and the defaults and
/// maxima of every limit (docs/adr/0001, decisions 3 and 5).
/// </summary>
public class SandboxOptions
{
    public const string Section = "AbpSandbox";

    /// <summary>Image names requests use, each mapped to a pinned reference (name:tag@sha256:...).</summary>
    public Dictionary<string, string> Images { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Whether a reference without a digest is taken; off, so an image can't change under the settings. Development only.</summary>
    public bool AllowUnpinnedImages { get; set; }

    /// <summary>
    /// This server's name on the containers it makes, so that after a restart it removes what it made before. Unless set,
    /// the machine's name and the process's ID: what a crashed process left then goes when it expires.
    /// </summary>
    public string? Node { get; set; }

    /// <summary>The user ("uid:gid") commands run as; never root.</summary>
    public string User { get; set; } = "65534:65534";

    public TimeSpan DefaultTime { get; set; } = TimeSpan.FromMinutes(1);

    public TimeSpan MaxTime { get; set; } = TimeSpan.FromMinutes(10);

    public long DefaultMemoryBytes { get; set; } = 512L * 1024 * 1024;

    public long MaxMemoryBytes { get; set; } = 2048L * 1024 * 1024;

    public double DefaultCpus { get; set; } = 1;

    public double MaxCpus { get; set; } = 2;

    public int DefaultProcesses { get; set; } = 128;

    public int MaxProcesses { get; set; } = 512;

    /// <summary>The most a request may put in, all files together.</summary>
    public long MaxInputBytes { get; set; } = 100L * 1024 * 1024;

    /// <summary>The most files a request may put in.</summary>
    public int MaxInputFiles { get; set; } = 5000;

    /// <summary>The most read back as files, all together.</summary>
    public long MaxOutputBytes { get; set; } = 10L * 1024 * 1024;

    /// <summary>The most kept of stdout and of stderr, each.</summary>
    public long MaxConsoleBytes { get; set; } = 1024L * 1024;

    /// <summary>Runs and preparations at once on this server; more wait their turn.</summary>
    public int MaxRunsAtOnce { get; set; } = 4;

    /// <summary>Sessions open at once on this server, and for one owner.</summary>
    public int MaxSessions { get; set; } = 20;

    public int MaxSessionsPerOwner { get; set; } = 2;

    public TimeSpan DefaultIdleTime { get; set; } = TimeSpan.FromMinutes(10);

    public TimeSpan MaxIdleTime { get; set; } = TimeSpan.FromHours(1);

    /// <summary>How long a session may live however much it's used.</summary>
    public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromHours(4);

    /// <summary>The time a whole preparation may take.</summary>
    public TimeSpan PreparationTime { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Prepared images kept; past it, the ones used longest ago go.</summary>
    public int MaxPreparedImages { get; set; } = 20;

    /// <summary>
    /// Containers kept started for each image name, with the default limits, so a run doesn't wait for one to start
    /// (a Fargate task takes tens of seconds). Each serves one run, one that asks for no other limits, environment or
    /// preparation, and is removed after; another starts in its place. Empty: none (the default).
    /// </summary>
    public Dictionary<string, int> WarmPool { get; set; } = new(StringComparer.Ordinal);

    /// <summary>How long a warm container waits before it's replaced by a new one (an image's update reaches the pool by then).</summary>
    public TimeSpan WarmLifetime { get; set; } = TimeSpan.FromHours(1);

    public TimeSpan SweepEvery { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Whether the sweep runs on this server (a host with a job system of its own may run it there).</summary>
    public bool Sweep { get; set; } = true;
}
