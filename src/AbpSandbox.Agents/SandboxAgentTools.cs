using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using AbpAgent;
using Microsoft.Extensions.Options;

namespace AbpSandbox.Agents;

/// <summary>The agents' sandbox settings, from "AbpSandbox:Agents".</summary>
public class SandboxAgentToolsOptions
{
    public const string Section = "AbpSandbox:Agents";

    /// <summary>The image (a name in AbpSandbox:Images) agents' commands run in; it needs a shell (sh).</summary>
    public string Image { get; set; } = "agents";

    /// <summary>Runs and commands one agent may start in a day, on this server.</summary>
    public int MaxCallsPerAgentPerDay { get; set; } = 200;

    /// <summary>The most characters of a tool's answer the model reads; past it the answer says it was cut.</summary>
    public int MaxAnswerChars { get; set; } = 24_000;
}

/// <summary>
/// Sandbox tools for AbpAgent (docs/adr/0001; agreed with AbpAgent's maintainers on 13 Nov 2026): a computer with no
/// network. Each is opt-in (off until the agent's manager turns it on); the ones that run or write aren't read-only,
/// so they ask first and are refused in a read-only run. They take only what the model gives them: a record's files
/// come through the host's own tools, which check rights, never from the sandbox reading the host's data. A session
/// is the chat's, kept between its turns (the run's when it isn't a chat's). Prepared images and limits go by the agent in its
/// scope, so one project's never serve another's.
/// </summary>
public sealed class SandboxAgentTools(ISandbox sandbox, IOptions<SandboxAgentToolsOptions> options, TimeProvider clock)
{
    public const string Run = "sandbox_run";
    public const string Exec = "sandbox_exec";
    public const string Put = "sandbox_put";
    public const string Get = "sandbox_get";

    private static readonly JsonElement FilesSchema = JsonSerializer.SerializeToElement(new
    {
        type = "array",
        description = "Files to put in the work folder.",
        items = new
        {
            type = "object",
            properties = new
            {
                path = new { type = "string", description = "Relative to the work folder, with forward slashes." },
                text = new { type = "string", description = "The file's text (UTF-8)." },
                base64 = new { type = "string", description = "The file's bytes in base64, for a file that isn't text." },
            },
            required = new[] { "path" },
        },
    });

    private readonly ConcurrentDictionary<(Guid Agent, DateOnly Day), int> calls = new();

    public IReadOnlyList<AgentTool> Tools { get; } =
    [
        new(Run, "Run in a sandbox",
            "Runs a shell script in a new, empty Linux container with no network and throws the container away after. "
            + "Give it the files it needs; it gives back the exit code, stdout, stderr and the files asked for. "
            + "For work that needs more than one step, use sandbox_exec instead.",
            Schema(new
            {
                script = new { type = "string", description = "The sh script to run, in the work folder." },
                files = FilesSchema,
                outputs = new { type = "array", items = new { type = "string" }, description = "Files or folders in the work folder to give back." },
            }, "script"), ReadsOnly: false, FromPlugin: false) { OptIn = true },
        new(Exec, "Run in this conversation's sandbox",
            "Runs a shell script in this conversation's Linux container (made the first time, with no network), which keeps "
            + "its files between calls until it's idle for a while. Gives back the exit code, stdout and stderr.",
            Schema(new { script = new { type = "string", description = "The sh script to run, in the work folder." } }, "script"),
            ReadsOnly: false, FromPlugin: false) { OptIn = true },
        new(Put, "Put files in this conversation's sandbox",
            "Puts files in this conversation's container, in its work folder.",
            Schema(new { files = FilesSchema }, "files"), ReadsOnly: false, FromPlugin: false) { OptIn = true },
        new(Get, "Read files from this conversation's sandbox",
            "Reads files or folders from this conversation's container: text as text, other files in base64.",
            Schema(new { paths = new { type = "array", items = new { type = "string" }, description = "Relative to the work folder; \".\" is all of it." } }, "paths"),
            ReadsOnly: true, FromPlugin: false) { OptIn = true },
    ];

    public bool Owns(string name) => name is Run or Exec or Put or Get;

    /// <summary>Whether agents' sandbox can run now: a provider is set and answers, and AbpSandbox:Agents:Image is pinned in AbpSandbox:Images.</summary>
    public Task<SandboxAvailability> CheckAsync(CancellationToken cancellationToken = default) => sandbox.CheckAsync(options.Value.Image, cancellationToken);

    /// <summary>
    /// The tools while the sandbox is available, none otherwise: what a run is offered, and what a host's own pages
    /// (an agent's tool settings) should list, so a manager never turns on a tool no run is offered.
    /// </summary>
    public async Task<IReadOnlyList<AgentTool>> AvailableToolsAsync(CancellationToken cancellationToken = default) =>
        (await CheckAsync(cancellationToken)).Available ? Tools : [];

    public async Task<AgentToolResult> RunAsync(string name, JsonElement input, AgentActing acting, CancellationToken cancellationToken)
    {
        if (!Owns(name))
        {
            return Refuse($"{name} isn't a sandbox tool.");
        }

        if (acting.ReadOnly && name != Get)
        {
            return Refuse("This run may only read: the sandbox's tools that run or write aren't allowed in it.");
        }

        if (acting.AgentId is not { } agent)
        {
            return Refuse("The sandbox's tools are an agent's: this run names none.");
        }

        if (await CheckAsync(cancellationToken) is { Available: false } unavailable)
        {
            return Refuse($"The sandbox isn't available: {unavailable.Why}");
        }

        string owner = $"agent:{agent:N}/scope:{acting.ScopeId?.ToString("N") ?? "none"}";
        try
        {
            switch (name)
            {
                case Run:
                {
                    if (!Count(agent))
                    {
                        return Refuse($"The agent has used its {options.Value.MaxCallsPerAgentPerDay} sandbox runs for today.");
                    }

                    SandboxResult result = await sandbox.RunAsync(new SandboxRequest
                    {
                        Owner = owner,
                        Image = options.Value.Image,
                        Command = ["sh", "-c", Text(input, "script")],
                        Files = Files(input),
                        Outputs = Strings(input, "outputs"),
                    }, cancellationToken);
                    return Answer(Describe(result) + DescribeFiles(result.Files), !result.Succeeded);
                }

                case Exec:
                {
                    if (!Count(agent))
                    {
                        return Refuse($"The agent has used its {options.Value.MaxCallsPerAgentPerDay} sandbox runs for today.");
                    }

                    string script = Text(input, "script");
                    ISandboxSession session = await SessionAsync(owner, acting, cancellationToken);
                    SandboxExecResult result = await session.ExecAsync(["sh", "-c", script], cancellationToken: cancellationToken);
                    return Answer(Describe(result) + (result.TimedOut ? "\nThe script ran past its time: the container was closed, and its files are gone.\n" : ""), !result.Succeeded);
                }

                case Put:
                {
                    IReadOnlyList<SandboxFile> files = Files(input);
                    ISandboxSession session = await SessionAsync(owner, acting, cancellationToken);
                    await session.PutFilesAsync(files, cancellationToken);
                    return Answer($"Put {files.Count} file(s) in the work folder.", false);
                }

                default:
                {
                    ISandboxSession? session = sandbox.FindSession(owner, Key(acting));
                    if (session is null)
                    {
                        return Refuse("This conversation has no sandbox open: sandbox_exec or sandbox_put opens one.");
                    }

                    return Answer(DescribeFiles(await session.GetFilesAsync(Strings(input, "paths"), cancellationToken)), false);
                }
            }
        }
        catch (SandboxRefusedException e)
        {
            return Refuse(e.Message);
        }
        catch (SandboxPreparationFailedException e)
        {
            return Refuse(e.Message);
        }
        catch (InputException e)
        {
            return Refuse(e.Message);
        }
    }

    /// <summary>A session is the chat's, kept between its turns, or the run's when the run isn't a chat's.</summary>
    private static string Key(AgentActing acting) => acting.ChatId is { } chat ? $"chat:{chat:N}" : $"run:{acting.RunId:N}";

    private Task<ISandboxSession> SessionAsync(string owner, AgentActing acting, CancellationToken cancellationToken) =>
        sandbox.OpenSessionAsync(new SandboxSessionRequest { Owner = owner, Key = Key(acting), Image = options.Value.Image }, cancellationToken);

    private bool Count(Guid agent)
    {
        DateOnly today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        foreach ((Guid, DateOnly) old in calls.Keys.Where(k => k.Day < today))
        {
            calls.TryRemove(old, out _);
        }

        return calls.AddOrUpdate((agent, today), 1, (_, n) => n + 1) <= options.Value.MaxCallsPerAgentPerDay;
    }

    private AgentToolResult Answer(string text, bool error)
    {
        int max = options.Value.MaxAnswerChars;
        return new AgentToolResult(text.Length <= max ? text : text[..max] + $"\n[The answer was cut at {max} characters.]", error);
    }

    private static AgentToolResult Refuse(string message) => new(message, true);

    private static string Describe(SandboxExecResult result)
    {
        StringBuilder text = new();
        text.Append(result.TimedOut ? "Timed out" : $"Exit code {result.ExitCode}").Append($" after {result.Duration.TotalSeconds:0.0} s.\n");
        if (result.Stdout.Length > 0)
        {
            text.Append("stdout:\n").Append(result.Stdout).Append(result.Stdout.EndsWith('\n') ? "" : "\n");
        }

        if (result.Stderr.Length > 0)
        {
            text.Append("stderr:\n").Append(result.Stderr).Append(result.Stderr.EndsWith('\n') ? "" : "\n");
        }

        if (result.OutputTruncated)
        {
            text.Append("[Output was cut at the sandbox's cap.]\n");
        }

        return text.ToString();
    }

    private static string DescribeFiles(SandboxFiles files)
    {
        if (files.Files.Count == 0)
        {
            return files.Truncated ? "No files fit in the sandbox's output cap.\n" : "";
        }

        StringBuilder text = new();
        foreach (SandboxFile file in files.Files)
        {
            text.Append($"File {file.Path} ({file.Content.Length} bytes)");
            try
            {
                string content = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(file.Content);
                text.Append(":\n").Append(content).Append(content.EndsWith('\n') ? "" : "\n");
            }
            catch (DecoderFallbackException)
            {
                text.Append(", base64:\n").Append(Convert.ToBase64String(file.Content)).Append('\n');
            }
        }

        if (files.Truncated)
        {
            text.Append("[Some files were left out at the sandbox's output cap.]\n");
        }

        return text.ToString();
    }

    private static JsonElement Schema(object properties, params string[] required) =>
        JsonSerializer.SerializeToElement(new { type = "object", properties, required });

    private static string Text(JsonElement input, string name) =>
        input.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
            ? text
            : throw new InputException($"Give '{name}' as text.");

    private static IReadOnlyList<string> Strings(JsonElement input, string name)
    {
        if (!input.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
        {
            return [];
        }

        return value.ValueKind == JsonValueKind.Array && value.EnumerateArray().All(v => v.ValueKind == JsonValueKind.String)
            ? [.. value.EnumerateArray().Select(v => v.GetString()!)]
            : throw new InputException($"Give '{name}' as a list of paths.");
    }

    private static IReadOnlyList<SandboxFile> Files(JsonElement input)
    {
        if (!input.TryGetProperty("files", out JsonElement value) || value.ValueKind == JsonValueKind.Null)
        {
            return [];
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new InputException("Give 'files' as a list of {path, text} or {path, base64}.");
        }

        List<SandboxFile> files = [];
        foreach (JsonElement file in value.EnumerateArray())
        {
            string path = file.ValueKind == JsonValueKind.Object && file.TryGetProperty("path", out JsonElement p) && p.ValueKind == JsonValueKind.String
                ? p.GetString()!
                : throw new InputException("Each file needs a 'path'.");
            if (file.TryGetProperty("text", out JsonElement text) && text.ValueKind == JsonValueKind.String)
            {
                files.Add(new SandboxFile(path, Encoding.UTF8.GetBytes(text.GetString()!)));
            }
            else if (file.TryGetProperty("base64", out JsonElement b64) && b64.ValueKind == JsonValueKind.String)
            {
                try
                {
                    files.Add(new SandboxFile(path, Convert.FromBase64String(b64.GetString()!)));
                }
                catch (FormatException)
                {
                    throw new InputException($"'{path}' isn't valid base64.");
                }
            }
            else
            {
                throw new InputException($"'{path}' needs its 'text' or its 'base64'.");
            }
        }

        return files;
    }

    private sealed class InputException(string message) : Exception(message);
}

/// <summary>
/// A host's tools with the sandbox's added while it's available: register it as the host's IAgentTools, over the host's
/// own catalog.
/// </summary>
public sealed class AgentToolsWithSandbox(IAgentTools host, SandboxAgentTools sandbox) : IAgentTools
{
    public async Task<IReadOnlyList<AgentTool>> ListAsync(CancellationToken cancellationToken) =>
        [.. await host.ListAsync(cancellationToken), .. await sandbox.AvailableToolsAsync(cancellationToken)];

    public async Task<IReadOnlyList<AgentTool>> ListAsync(AgentToolsFor run, CancellationToken cancellationToken) =>
        [.. await host.ListAsync(run, cancellationToken), .. await sandbox.AvailableToolsAsync(cancellationToken)];

    public Task<AgentToolResult> RunAsync(string name, JsonElement input, AgentActing acting, CancellationToken cancellationToken) =>
        sandbox.Owns(name) ? sandbox.RunAsync(name, input, acting, cancellationToken) : host.RunAsync(name, input, acting, cancellationToken);
}
