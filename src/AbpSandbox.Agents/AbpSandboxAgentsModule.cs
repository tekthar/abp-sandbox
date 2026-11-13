using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Volo.Abp.Modularity;

namespace AbpSandbox.Agents;

/// <summary>
/// The sandbox's tools for AbpAgent (<see cref="SandboxAgentTools"/>). A host adds them to its own catalog: register
/// <see cref="AgentToolsWithSandbox"/> over its IAgentTools. Settings: "AbpSandbox:Agents".
/// </summary>
[DependsOn(typeof(AbpSandboxModule))]
public class AbpSandboxAgentsModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.Configure<SandboxAgentToolsOptions>(context.Services.GetConfiguration().GetSection(SandboxAgentToolsOptions.Section));
        context.Services.TryAddSingleton<SandboxAgentTools>();
    }
}
