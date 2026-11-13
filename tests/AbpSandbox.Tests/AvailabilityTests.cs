using System.Text.Json;
using AbpAgent;
using AbpSandbox.Agents;
using AbpSandbox.Docker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AbpSandbox.Tests;

/// <summary>Which provider runs (AbpSandbox:Provider), and whether the sandbox can run now (ISandbox.CheckAsync).</summary>
public class AvailabilityTests
{
    private static readonly string Pinned = "alpine:3.20@sha256:" + new string('a', 64);

    private static ISandboxProvider Picked(string? name)
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.Configure<SandboxOptions>(o => o.Provider = name);
        services.Configure<DockerSandboxOptions>(_ => { });
        services.AddSandboxProvider<DockerSandboxProvider>("docker");
        services.AddSandboxProvider<FakeProvider>("fake");
        return SandboxProviders.Pick(services.BuildServiceProvider());
    }

    [Fact]
    public async Task No_provider_runs_until_the_settings_name_one()
    {
        NoSandboxProvider none = Assert.IsType<NoSandboxProvider>(Picked(null));
        SandboxAvailability why = await none.CheckAsync();
        Assert.False(why.Available);
        Assert.Contains("AbpSandbox:Provider", why.Why);
        Assert.Contains("docker, fake", why.Why);
        Assert.IsType<NoSandboxProvider>(Picked("none"));
    }

    [Fact]
    public async Task The_named_provider_runs_and_an_unknown_name_says_so()
    {
        Assert.IsType<DockerSandboxProvider>(Picked("docker"));
        Assert.IsType<FakeProvider>(Picked("FAKE"));
        SandboxAvailability unknown = await Picked("azure").CheckAsync();
        Assert.False(unknown.Available);
        Assert.Contains("'azure'", unknown.Why);
    }

    private static (Sandbox Sandbox, FakeProvider Provider, ManualClock Clock) Make()
    {
        FakeProvider provider = new();
        ManualClock clock = new();
        Sandbox sandbox = new(provider, Options.Create(new SandboxOptions { Images = new() { ["agents"] = Pinned, ["loose"] = "alpine:3.20" } }), clock, NullLogger<Sandbox>.Instance);
        return (sandbox, provider, clock);
    }

    [Fact]
    public async Task The_providers_answer_is_kept_for_a_minute_and_an_image_is_checked_every_time()
    {
        (Sandbox sandbox, FakeProvider provider, ManualClock clock) = Make();
        Assert.True((await sandbox.CheckAsync("agents")).Available);
        provider.Availability = SandboxAvailability.No("Docker is down.");
        Assert.True((await sandbox.CheckAsync()).Available); // the kept answer
        Assert.False((await sandbox.CheckAsync("nowhere")).Available);
        Assert.Contains("isn't pinned", (await sandbox.CheckAsync("loose")).Why);
        clock.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal("Docker is down.", (await sandbox.CheckAsync("agents")).Why);
        Assert.Equal(2, provider.Checks);
    }

    [Fact]
    public async Task A_provider_that_throws_is_not_available()
    {
        (Sandbox sandbox, FakeProvider provider, _) = Make();
        provider.CheckThrows = true;
        SandboxAvailability answer = await sandbox.CheckAsync();
        Assert.False(answer.Available);
        Assert.Contains("doesn't answer", answer.Why);
    }

    [Fact]
    public async Task Agents_are_offered_the_tools_only_while_the_sandbox_is_there()
    {
        (Sandbox sandbox, FakeProvider provider, ManualClock clock) = Make();
        SandboxAgentTools tools = new(sandbox, Options.Create(new SandboxAgentToolsOptions()), clock);
        AgentToolsWithSandbox both = new(new HostTools(), tools);
        Assert.Equal(5, (await both.ListAsync(default)).Count);
        Assert.Equal(4, (await tools.AvailableToolsAsync()).Count);

        provider.Availability = SandboxAvailability.No("Docker is down.");
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(["host_read"], (await both.ListAsync(default)).Select(t => t.Name));
        Assert.Empty(await tools.AvailableToolsAsync());
        Assert.Equal("Docker is down.", (await tools.CheckAsync()).Why);

        AgentActing acting = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), false) { AgentId = Guid.NewGuid(), ScopeId = Guid.NewGuid() };
        AgentToolResult refused = await tools.RunAsync(SandboxAgentTools.Run, JsonSerializer.SerializeToElement(new { script = "true" }), acting, default);
        Assert.True(refused.Error);
        Assert.Equal("The sandbox isn't available: Docker is down.", refused.Content);
        Assert.Equal(0, provider.Created);
    }

    [Fact]
    public async Task Agents_image_not_in_the_settings_keeps_the_tools_away()
    {
        (Sandbox sandbox, _, ManualClock clock) = Make();
        SandboxAgentTools tools = new(sandbox, Options.Create(new SandboxAgentToolsOptions { Image = "missing" }), clock);
        Assert.Empty(await tools.AvailableToolsAsync());
        Assert.Contains("'missing'", (await tools.CheckAsync()).Why);
    }

    [Fact]
    public async Task A_docker_engine_that_isnt_there_is_not_available()
    {
        using DockerSandboxProvider provider = new(Options.Create(new DockerSandboxOptions { Endpoint = "http://127.0.0.1:1" }), NullLogger<DockerSandboxProvider>.Instance);
        SandboxAvailability answer = await provider.CheckAsync();
        Assert.False(answer.Available);
        Assert.Contains("doesn't answer", answer.Why);
    }

    [DockerFact]
    public async Task A_docker_engine_that_answers_is_available()
    {
        using DockerSandboxProvider provider = new(Options.Create(new DockerSandboxOptions { Endpoint = Environment.GetEnvironmentVariable("ABP_SANDBOX_DOCKER_ENDPOINT") }), NullLogger<DockerSandboxProvider>.Instance);
        Assert.True((await provider.CheckAsync()).Available);
    }

    private sealed class HostTools : IAgentTools
    {
        public Task<IReadOnlyList<AgentTool>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AgentTool>>([new("host_read", "Read", "Reads.", JsonSerializer.SerializeToElement(new { type = "object" }), true, false)]);

        public Task<AgentToolResult> RunAsync(string name, JsonElement input, AgentActing acting, CancellationToken cancellationToken) =>
            Task.FromResult(new AgentToolResult("host", false));
    }
}
