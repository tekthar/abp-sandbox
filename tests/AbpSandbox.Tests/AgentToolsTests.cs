using System.Text.Json;
using AbpAgent;
using AbpSandbox.Agents;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AbpSandbox.Tests;

public class AgentToolsTests
{
    private static readonly Guid Agent = Guid.NewGuid();
    private static readonly Guid Scope = Guid.NewGuid();

    private static (SandboxAgentTools Tools, Sandbox Sandbox, FakeProvider Provider) Make(int maxCalls = 200)
    {
        FakeProvider provider = new();
        Sandbox sandbox = new(provider, Options.Create(new SandboxOptions { Images = new() { ["agents"] = "alpine:3.20@sha256:" + new string('a', 64) } }),
            new ManualClock(), NullLogger<Sandbox>.Instance);
        return (new SandboxAgentTools(sandbox, Options.Create(new SandboxAgentToolsOptions { MaxCallsPerAgentPerDay = maxCalls }), new ManualClock()), sandbox, provider);
    }

    private static AgentActing Acting(bool readOnly = false, Guid? run = null) =>
        new(run ?? Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), readOnly) { AgentId = Agent, ScopeId = Scope };

    private static JsonElement Input(object value) => JsonSerializer.SerializeToElement(value);

    [Fact]
    public void The_tools_are_opt_in_and_only_reading_is_read_only()
    {
        (SandboxAgentTools tools, _, _) = Make();
        Assert.All(tools.Tools, t => Assert.True(t.OptIn));
        Assert.All(tools.Tools, t => Assert.False(t.AlwaysAsks));
        Assert.Equal([SandboxAgentTools.Get], tools.Tools.Where(t => t.ReadsOnly).Select(t => t.Name));
    }

    [Fact]
    public async Task A_read_only_run_may_not_run_or_write()
    {
        (SandboxAgentTools tools, _, FakeProvider provider) = Make();
        foreach (string name in new[] { SandboxAgentTools.Run, SandboxAgentTools.Exec, SandboxAgentTools.Put })
        {
            AgentToolResult result = await tools.RunAsync(name, Input(new { script = "echo hi", files = Array.Empty<object>() }), Acting(readOnly: true), default);
            Assert.True(result.Error);
        }

        Assert.Equal(0, provider.Created);
    }

    [Fact]
    public async Task A_conversations_session_is_opened_by_a_write_and_read_after()
    {
        (SandboxAgentTools tools, Sandbox sandbox, FakeProvider provider) = Make();
        AgentActing acting = Acting();
        Assert.True((await tools.RunAsync(SandboxAgentTools.Get, Input(new { paths = new[] { "." } }), acting, default)).Error);
        Assert.False((await tools.RunAsync(SandboxAgentTools.Put, Input(new { files = new[] { new { path = "a.txt", text = "hi" } } }), acting, default)).Error);
        Assert.False((await tools.RunAsync(SandboxAgentTools.Exec, Input(new { script = "cat a.txt" }), acting, default)).Error);
        Assert.False((await tools.RunAsync(SandboxAgentTools.Get, Input(new { paths = new[] { "." } }), acting, default)).Error);
        Assert.Equal(1, provider.Created);
        Assert.NotNull(sandbox.FindSession($"agent:{Agent:N}/scope:{Scope:N}", $"run:{acting.RunId:N}"));
    }

    [Fact]
    public async Task A_chats_runs_share_its_sandbox_and_other_chats_dont()
    {
        (SandboxAgentTools tools, _, FakeProvider provider) = Make();
        Guid chat = Guid.NewGuid();
        JsonElement exec = Input(new { script = "true" });
        await tools.RunAsync(SandboxAgentTools.Exec, exec, Acting() with { ChatId = chat }, default);
        await tools.RunAsync(SandboxAgentTools.Exec, exec, Acting() with { ChatId = chat }, default);
        Assert.Equal(1, provider.Created);
        await tools.RunAsync(SandboxAgentTools.Exec, exec, Acting() with { ChatId = Guid.NewGuid() }, default);
        Assert.Equal(2, provider.Created);
    }

    [Fact]
    public async Task Bad_input_and_refusals_come_back_as_errors_for_the_model()
    {
        (SandboxAgentTools tools, _, _) = Make();
        Assert.Contains("script", (await tools.RunAsync(SandboxAgentTools.Run, Input(new { }), Acting(), default)).Content);
        Assert.Contains("base64", (await tools.RunAsync(SandboxAgentTools.Put, Input(new { files = new[] { new { path = "a", base64 = "%%" } } }), Acting(), default)).Content);
        Assert.Contains("work folder", (await tools.RunAsync(SandboxAgentTools.Put, Input(new { files = new[] { new { path = "../a", text = "x" } } }), Acting(), default)).Content);
    }

    [Fact]
    public async Task An_agent_has_its_runs_for_the_day()
    {
        (SandboxAgentTools tools, _, _) = Make(maxCalls: 2);
        JsonElement run = Input(new { script = "true" });
        Assert.False((await tools.RunAsync(SandboxAgentTools.Run, run, Acting(), default)).Error);
        Assert.False((await tools.RunAsync(SandboxAgentTools.Run, run, Acting(), default)).Error);
        AgentToolResult third = await tools.RunAsync(SandboxAgentTools.Run, run, Acting(), default);
        Assert.True(third.Error);
        Assert.Contains("today", third.Content);
    }

    [Fact]
    public async Task The_host_keeps_its_own_tools_and_gets_the_sandboxs()
    {
        (SandboxAgentTools tools, _, _) = Make();
        AgentToolsWithSandbox both = new(new HostTools(), tools);
        Assert.Equal(["host_read", .. tools.Tools.Select(t => t.Name)], (await both.ListAsync(default)).Select(t => t.Name));
        Assert.Equal("host", (await both.RunAsync("host_read", Input(new { }), Acting(), default)).Content);
        Assert.StartsWith("Exit code 0", (await both.RunAsync(SandboxAgentTools.Run, Input(new { script = "true" }), Acting(), default)).Content);
    }

    private sealed class HostTools : IAgentTools
    {
        public Task<IReadOnlyList<AgentTool>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AgentTool>>([new("host_read", "Read", "Reads.", JsonSerializer.SerializeToElement(new { type = "object" }), true, false)]);

        public Task<AgentToolResult> RunAsync(string name, JsonElement input, AgentActing acting, CancellationToken cancellationToken) =>
            Task.FromResult(new AgentToolResult("host", false));
    }
}
