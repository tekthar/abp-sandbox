using Microsoft.Extensions.DependencyInjection;
using Volo.Abp.Modularity;

namespace AbpSandbox.Docker;

/// <summary>AbpSandbox with Docker as its provider (docs/adr/0001, decision 7). Settings: "AbpSandbox:Docker" (<see cref="DockerSandboxOptions"/>).</summary>
[DependsOn(typeof(AbpSandboxModule))]
public class AbpSandboxDockerModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.Configure<DockerSandboxOptions>(context.Services.GetConfiguration().GetSection(DockerSandboxOptions.Section));
        context.Services.AddSingleton<ISandboxProvider, DockerSandboxProvider>();
    }
}
