using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AbpSandbox;

/// <summary>The checks the module makes before a provider sees a request (docs/adr/0001, decisions 3 to 5).</summary>
public sealed class SandboxPolicy(SandboxOptions options)
{
    /// <summary>The limits a request gets: its own where they are within the maxima, the defaults where it gave none.</summary>
    public sealed record Resolved(TimeSpan Time, long MemoryBytes, double Cpus, int Processes);

    public SandboxOptions Options => options;

    /// <summary>The pinned reference an image name maps to.</summary>
    public string Image(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || !options.Images.TryGetValue(name, out string? reference) || string.IsNullOrWhiteSpace(reference))
        {
            throw new SandboxRefusedException($"'{name}' isn't one of the sandbox's images.");
        }

        if (!options.AllowUnpinnedImages && !reference.Contains("@sha256:", StringComparison.Ordinal))
        {
            throw new SandboxRefusedException($"The sandbox's image '{name}' isn't pinned to a digest (name:tag@sha256:...).");
        }

        return reference;
    }

    public Resolved Limits(SandboxLimits asked)
    {
        TimeSpan time = asked.Time ?? options.DefaultTime;
        long memory = asked.MemoryBytes ?? options.DefaultMemoryBytes;
        double cpus = asked.Cpus ?? options.DefaultCpus;
        int processes = asked.Processes ?? options.DefaultProcesses;
        Within("time", time > TimeSpan.Zero && time <= options.MaxTime, $"{options.MaxTime.TotalSeconds:0} seconds");
        Within("memory", memory >= 16L * 1024 * 1024 && memory <= options.MaxMemoryBytes, $"{options.MaxMemoryBytes / (1024 * 1024)} MB");
        Within("CPUs", cpus > 0 && cpus <= options.MaxCpus, options.MaxCpus.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Within("processes", processes >= 1 && processes <= options.MaxProcesses, options.MaxProcesses.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return new Resolved(time, memory, cpus, processes);

        static void Within(string what, bool ok, string most)
        {
            if (!ok)
            {
                throw new SandboxRefusedException($"The sandbox's {what} is at most {most}.");
            }
        }
    }

    public TimeSpan IdleTime(TimeSpan? asked)
    {
        TimeSpan idle = asked ?? options.DefaultIdleTime;
        if (idle <= TimeSpan.Zero || idle > options.MaxIdleTime)
        {
            throw new SandboxRefusedException($"A sandbox session's idle time is at most {options.MaxIdleTime.TotalMinutes:0} minutes.");
        }

        return idle;
    }

    /// <summary>Files going in: paths inside the work folder, each once, within the count and size.</summary>
    public void Input(IReadOnlyList<SandboxFile> files)
    {
        if (files.Count > options.MaxInputFiles)
        {
            throw new SandboxRefusedException($"A sandbox takes at most {options.MaxInputFiles} files.");
        }

        HashSet<string> seen = new(StringComparer.Ordinal);
        long total = 0;
        foreach (SandboxFile file in files)
        {
            Path(file.Path);
            if (!seen.Add(file.Path))
            {
                throw new SandboxRefusedException($"'{file.Path}' is given twice.");
            }

            total += file.Content.LongLength;
            if (total > options.MaxInputBytes)
            {
                throw new SandboxRefusedException($"A sandbox takes at most {options.MaxInputBytes / (1024 * 1024)} MB of files.");
            }
        }
    }

    /// <summary>Paths to read back: inside the work folder ("." is all of it).</summary>
    public void Outputs(IReadOnlyList<string> paths)
    {
        if (paths.Count > 100)
        {
            throw new SandboxRefusedException("A sandbox reads back at most 100 paths.");
        }

        foreach (string path in paths)
        {
            if (path != ".")
            {
                Path(path);
            }
        }
    }

    public static void Command(IReadOnlyList<string> command)
    {
        if (command.Count == 0 || string.IsNullOrEmpty(command[0]) || command.Any(a => a is null || a.Contains('\0')))
        {
            throw new SandboxRefusedException("A sandbox command needs a program and arguments without NUL characters.");
        }
    }

    public static void Environment(IReadOnlyDictionary<string, string> environment)
    {
        foreach ((string name, string value) in environment)
        {
            if (string.IsNullOrEmpty(name) || name.Contains('=') || name.Contains('\0') || value.Contains('\0'))
            {
                throw new SandboxRefusedException($"'{name}' isn't an environment variable a sandbox takes.");
            }
        }
    }

    /// <summary>A path relative to the work folder, with forward slashes, that can't leave it.</summary>
    public static void Path(string path)
    {
        string[] parts = path.Split('/');
        if (string.IsNullOrEmpty(path) || path.Length > 1024 || path.StartsWith('/') || path.Contains('\\') || path.Contains('\0')
            || parts.Any(p => p is "" or "." or ".."))
        {
            throw new SandboxRefusedException($"'{path}' isn't a path inside the sandbox's work folder.");
        }
    }

    /// <summary>
    /// A preparation's key: the base image's reference, its scope (the owner's or shared), its steps and its files'
    /// contents (docs/adr/0001, decision 2). The same request gives the same key on every server.
    /// </summary>
    public static string PreparationKey(string reference, string owner, SandboxPreparation preparation)
    {
        StringBuilder text = new("abp-sandbox/preparation/1\n");
        text.Append(reference).Append('\n');
        text.Append(preparation.Shared ? "shared" : "owner:" + owner).Append('\n');
        text.Append(JsonSerializer.Serialize(preparation.Steps)).Append('\n');
        foreach (SandboxFile file in preparation.Files.OrderBy(f => f.Path, StringComparer.Ordinal))
        {
            text.Append(file.Path).Append(' ').Append(Convert.ToHexStringLower(SHA256.HashData(file.Content))).Append('\n');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    /// <summary>An owner as a label: hashed, since owners may be names.</summary>
    public static string OwnerHash(string owner) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(owner)))[..16];
}
