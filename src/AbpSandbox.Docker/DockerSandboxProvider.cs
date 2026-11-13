using System.Diagnostics;
using System.Formats.Tar;
using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AbpSandbox.Docker;

/// <summary>The Docker provider's settings, from "AbpSandbox:Docker".</summary>
public class DockerSandboxOptions
{
    public const string Section = "AbpSandbox:Docker";

    /// <summary>The Docker Engine: unix:///var/run/docker.sock, npipe://./pipe/docker_engine or http://host:2375; the platform's own by default.</summary>
    public string? Endpoint { get; set; }

    /// <summary>Whether an image that isn't on the engine is pulled; off, the operator pulls the images the settings name.</summary>
    public bool Pull { get; set; } = true;

    /// <summary>The <c>/tmp</c> each container gets, in memory, without exec.</summary>
    public long TmpBytes { get; set; } = 64L * 1024 * 1024;

    /// <summary>What keeps a session's or a preparation's container up; the image needs it (Docker's init runs it).</summary>
    public List<string> KeepAliveCommand { get; set; } = ["sleep", "infinity"];
}

/// <summary>
/// Containers through the Docker Engine (docs/adr/0001, decision 7): no network, a read-only root, every capability
/// dropped, <c>no-new-privileges</c>, a user that isn't root, memory without swap, CPUs, processes and a small log.
/// The work folder is the container's own volume at <c>/sandbox</c>, which Docker writes to on a read-only root and
/// removes with the container. Docker's containers share the server's kernel: for development and hosts' own checks.
/// </summary>
public sealed class DockerSandboxProvider(IOptions<DockerSandboxOptions> options, ILogger<DockerSandboxProvider> logger) : ISandboxProvider, IDisposable
{
    internal const string Root = "/sandbox";
    internal const string Work = Root + "/work";
    private const string PreparedRepo = "abpsandbox-prepared";

    private readonly DockerEngine engine = new(options.Value.Endpoint ?? DockerEngine.DefaultEndpoint);
    private readonly SemaphoreSlim pulling = new(1, 1);

    public string Name => "docker";

    public SandboxCapabilities Capabilities { get; } = new(Sessions: true, Preparation: true);

    public async Task<SandboxAvailability> CheckAsync(CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource quick = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        quick.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            return await engine.PingAsync(quick.Token) ? SandboxAvailability.Yes : SandboxAvailability.No("The Docker engine doesn't answer its ping.");
        }
        catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException or TimeoutException or System.Net.Sockets.SocketException)
        {
            return SandboxAvailability.No($"The Docker engine at {options.Value.Endpoint ?? DockerEngine.DefaultEndpoint} doesn't answer.");
        }
    }

    public async Task<ISandboxContainer> CreateAsync(SandboxContainerSpec spec, CancellationToken cancellationToken = default)
    {
        await EnsureImageAsync(spec.Image, cancellationToken);
        (int uid, int gid) = Owner(spec.User);
        JsonObject host = new()
        {
            ["NetworkMode"] = "none",
            ["ReadonlyRootfs"] = !spec.Preparation,
            ["CapDrop"] = spec.Preparation ? new JsonArray("NET_RAW", "MKNOD", "SYS_CHROOT", "AUDIT_WRITE", "SETFCAP") : new JsonArray("ALL"),
            ["SecurityOpt"] = new JsonArray("no-new-privileges"),
            ["Memory"] = spec.MemoryBytes,
            ["MemorySwap"] = spec.MemoryBytes,
            ["NanoCpus"] = (long)(spec.Cpus * 1_000_000_000),
            ["PidsLimit"] = spec.Processes,
            ["Tmpfs"] = new JsonObject { ["/tmp"] = $"rw,noexec,nosuid,nodev,size={options.Value.TmpBytes}" },
            ["LogConfig"] = new JsonObject { ["Type"] = "json-file", ["Config"] = new JsonObject { ["max-size"] = "2m", ["max-file"] = "1" } },
            ["Init"] = true,
        };
        JsonObject body = new()
        {
            ["Image"] = spec.Image,
            ["Cmd"] = new JsonArray([.. (spec.Command ?? options.Value.KeepAliveCommand).Select(a => (JsonNode)JsonValue.Create(a)!)]),
            ["Env"] = new JsonArray([.. spec.Environment.Select(e => (JsonNode)JsonValue.Create($"{e.Key}={e.Value}")!)]),
            ["WorkingDir"] = Work,
            ["NetworkDisabled"] = true,
            ["AttachStdout"] = false,
            ["AttachStderr"] = false,
            ["Tty"] = false,
            ["OpenStdin"] = false,
            ["Volumes"] = new JsonObject { [Root] = new JsonObject() },
            ["Labels"] = new JsonObject
            {
                ["abp-sandbox"] = "1",
                ["abp-sandbox.kind"] = spec.Kind,
                ["abp-sandbox.node"] = spec.Node,
                ["abp-sandbox.owner"] = spec.OwnerHash,
                ["abp-sandbox.expires"] = spec.ExpiresAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            },
            ["HostConfig"] = host,
        };
        if (spec.User is not null)
        {
            body["User"] = spec.User;
        }

        string id = await engine.CreateContainerAsync($"abpsandbox-{spec.Kind}-{Guid.NewGuid():N}"[..40], body, cancellationToken);
        DockerSandboxContainer container = new(engine, id, spec.User, uid, gid);
        try
        {
            // The work folder, owned by the user that writes to it (the volume's own root is root's).
            await container.PutFilesAsync([], cancellationToken);
        }
        catch
        {
            await container.RemoveAsync(CancellationToken.None);
            throw;
        }

        return container;
    }

    public async Task<string?> FindPreparedAsync(string key, CancellationToken cancellationToken = default)
    {
        JsonArray images = await engine.ListImagesAsync($"abp-sandbox.key={key}", cancellationToken);
        return images.FirstOrDefault()?["Id"]?.GetValue<string>();
    }

    public Task<string> SavePreparedAsync(ISandboxContainer container, string key, CancellationToken cancellationToken = default) =>
        engine.CommitAsync(container.Id, PreparedRepo, key[..32], new Dictionary<string, string>
        {
            ["abp-sandbox.prepared"] = "1",
            ["abp-sandbox.key"] = key,
        }, cancellationToken);

    public async Task<IReadOnlyList<SandboxPreparedImage>> ListPreparedAsync(CancellationToken cancellationToken = default)
    {
        JsonArray images = await engine.ListImagesAsync("abp-sandbox.prepared=1", cancellationToken);
        return [.. images.Select(i => new SandboxPreparedImage(
            i!["Id"]!.GetValue<string>(),
            i["Labels"]?["abp-sandbox.key"]?.GetValue<string>() ?? "",
            DateTimeOffset.FromUnixTimeSeconds(i["Created"]?.GetValue<long>() ?? 0)))];
    }

    public Task RemovePreparedAsync(string reference, CancellationToken cancellationToken = default) => engine.RemoveImageAsync(reference, cancellationToken);

    public async Task<int> SweepAsync(DateTimeOffset now, string? node, CancellationToken cancellationToken = default)
    {
        int removed = 0;
        foreach (JsonNode? c in await engine.ListContainersAsync("abp-sandbox=1", cancellationToken))
        {
            JsonNode? labels = c?["Labels"];
            bool expired = long.TryParse(labels?["abp-sandbox.expires"]?.GetValue<string>(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long expires)
                && DateTimeOffset.FromUnixTimeSeconds(expires) <= now;
            bool ours = node is not null && labels?["abp-sandbox.node"]?.GetValue<string>() == node;
            if (expired || ours)
            {
                await engine.RemoveContainerAsync(c!["Id"]!.GetValue<string>(), cancellationToken);
                removed++;
            }
        }

        return removed;
    }

    public void Dispose()
    {
        engine.Dispose();
        pulling.Dispose();
    }

    private async Task EnsureImageAsync(string reference, CancellationToken ct)
    {
        if (await engine.ImageExistsAsync(reference, ct))
        {
            return;
        }

        if (!options.Value.Pull)
        {
            throw new SandboxRefusedException($"The sandbox's image {reference} isn't on the Docker engine, and pulling is off.");
        }

        await pulling.WaitAsync(ct);
        try
        {
            if (!await engine.ImageExistsAsync(reference, ct))
            {
                logger.LogInformation("Pulling the sandbox image {Image}.", reference);
                await engine.PullAsync(reference, ct);
            }
        }
        finally
        {
            pulling.Release();
        }
    }

    /// <summary>The owner of the work folder: the spec's numeric user, or the default when the image's own user runs (a preparation).</summary>
    private static (int Uid, int Gid) Owner(string? user)
    {
        if (user is null)
        {
            return (65534, 65534);
        }

        string[] parts = user.Split(':');
        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int uid) || uid == 0
            || (parts.Length > 1 && !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out _)) || parts.Length > 2)
        {
            throw new SandboxRefusedException($"The sandbox's user is a uid that isn't 0, with an optional gid (uid:gid), not '{user}'.");
        }

        return (uid, parts.Length > 1 ? int.Parse(parts[1], CultureInfo.InvariantCulture) : uid);
    }
}

/// <summary>A Docker container of the sandbox's.</summary>
internal sealed class DockerSandboxContainer(DockerEngine engine, string id, string? user, int uid, int gid) : ISandboxContainer
{
    private int removed;

    public string Id => id;

    public Task PutFilesAsync(IReadOnlyList<SandboxFile> files, CancellationToken cancellationToken = default) =>
        engine.PutArchiveAsync(id, DockerSandboxProvider.Root, Tar(files), cancellationToken);

    public async Task<SandboxExecResult> RunAsync(TimeSpan time, long maxOutputBytes, CancellationToken cancellationToken = default)
    {
        Stopwatch watch = Stopwatch.StartNew();
        await engine.StartAsync(id, cancellationToken);
        int exitCode;
        bool timedOut = false;
        using (CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            limit.CancelAfter(time);
            try
            {
                exitCode = await engine.WaitAsync(id, limit.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                timedOut = true;
                exitCode = -1;
                await engine.KillAsync(id, CancellationToken.None);
                await engine.WaitAsync(id, cancellationToken);
            }
        }

        watch.Stop();
        (string stdout, string stderr, bool truncated) = await engine.LogsAsync(id, maxOutputBytes, cancellationToken);
        return new SandboxExecResult { ExitCode = exitCode, TimedOut = timedOut, Stdout = stdout, Stderr = stderr, OutputTruncated = truncated, Duration = watch.Elapsed };
    }

    public Task StartAsync(CancellationToken cancellationToken = default) => engine.StartAsync(id, cancellationToken);

    public async Task<SandboxExecResult> ExecAsync(IReadOnlyList<string> command, TimeSpan time, long maxOutputBytes, CancellationToken cancellationToken = default)
    {
        JsonObject body = new()
        {
            ["AttachStdout"] = true,
            ["AttachStderr"] = true,
            ["Tty"] = false,
            ["Cmd"] = new JsonArray([.. command.Select(a => (JsonNode)JsonValue.Create(a)!)]),
            ["WorkingDir"] = DockerSandboxProvider.Work,
        };
        if (user is not null)
        {
            body["User"] = user;
        }

        Stopwatch watch = Stopwatch.StartNew();
        string exec = await engine.ExecCreateAsync(id, body, cancellationToken);
        using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(time);
        (string stdout, string stderr, bool truncated, bool cancelled) = await engine.ExecStartAsync(exec, maxOutputBytes, limit.Token);
        cancellationToken.ThrowIfCancellationRequested();
        int? exitCode = cancelled ? null : await engine.ExecExitCodeAsync(exec, cancellationToken);
        watch.Stop();
        return new SandboxExecResult
        {
            ExitCode = exitCode ?? -1,
            TimedOut = cancelled,
            Stdout = stdout,
            Stderr = stderr,
            OutputTruncated = truncated,
            Duration = watch.Elapsed,
        };
    }

    public async Task<SandboxFiles> GetFilesAsync(IReadOnlyList<string> paths, long maxBytes, CancellationToken cancellationToken = default)
    {
        List<SandboxFile> files = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        long total = 0;
        bool truncated = false;
        foreach (string path in paths)
        {
            await using Stream? tar = await engine.GetArchiveAsync(id, path == "." ? DockerSandboxProvider.Work : $"{DockerSandboxProvider.Work}/{path}", cancellationToken);
            if (tar is null)
            {
                continue;
            }

            // The archive's names start at the path's last part ("work" for the whole folder): they're put back under the path's folder.
            string under = path == "." ? "" : path.Contains('/') ? path[..(path.LastIndexOf('/') + 1)] : "";
            using TarReader reader = new(tar);
            while (await reader.GetNextEntryAsync(copyData: false, cancellationToken) is { } entry)
            {
                if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                {
                    continue; // folders, links and devices: links aren't followed out of the work folder
                }

                string name = entry.Name.TrimStart('.', '/');
                string relative = path == "." ? (name.StartsWith("work/", StringComparison.Ordinal) ? name[5..] : name) : under + name;
                if (relative.Length == 0 || !seen.Add(relative))
                {
                    continue;
                }

                if (total + entry.Length > maxBytes || files.Count >= 5000)
                {
                    truncated = true;
                    continue;
                }

                using MemoryStream copy = new();
                if (entry.DataStream is not null)
                {
                    await entry.DataStream.CopyToAsync(copy, cancellationToken); // an empty file has none
                }
                total += copy.Length;
                files.Add(new SandboxFile(relative, copy.ToArray()));
            }
        }

        return new SandboxFiles(files, truncated);
    }

    public async Task RemoveAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref removed, 1) == 0)
        {
            await engine.RemoveContainerAsync(id, cancellationToken);
        }
    }

    /// <summary>A tar of the work folder and the files under it, all owned by the user that works there.</summary>
    private byte[] Tar(IReadOnlyList<SandboxFile> files)
    {
        using MemoryStream output = new();
        using (TarWriter writer = new(output, TarEntryFormat.Pax, leaveOpen: true))
        {
            HashSet<string> folders = new(StringComparer.Ordinal);
            void Folder(string name)
            {
                if (folders.Add(name))
                {
                    writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, name + "/") { Uid = uid, Gid = gid, Mode = Folders });
                }
            }

            Folder("work");
            foreach (SandboxFile file in files)
            {
                string[] parts = file.Path.Split('/');
                for (int i = 1; i < parts.Length; i++)
                {
                    Folder("work/" + string.Join('/', parts[..i]));
                }

                writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "work/" + file.Path)
                {
                    Uid = uid,
                    Gid = gid,
                    Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
                    DataStream = new MemoryStream(file.Content, writable: false),
                });
            }
        }

        return output.ToArray();
    }

    private const UnixFileMode Folders = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
}
