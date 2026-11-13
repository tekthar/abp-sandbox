using Amazon;
using Amazon.ECS;
using Amazon.S3;
using AbpSandbox.Remote;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Volo.Abp.Modularity;

namespace AbpSandbox.Fargate;

/// <summary>
/// AbpSandbox on AWS Fargate (docs/adr/0002). Settings: "AbpSandbox:Fargate" (what deploy/aws made) and
/// "AbpSandbox:Remote" (the runner's image). Credentials are the SDK's own chain: the server's role in AWS.
/// </summary>
[DependsOn(typeof(AbpSandboxModule))]
public class AbpSandboxFargateModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.Configure<FargateSandboxOptions>(context.Services.GetConfiguration().GetSection(FargateSandboxOptions.Section));
        context.Services.TryAddSingleton<IAmazonECS>(sp => Region(sp) is { } region ? new AmazonECSClient(region) : new AmazonECSClient());
        context.Services.TryAddSingleton<IAmazonS3>(sp => Region(sp) is { } region ? new AmazonS3Client(region) : new AmazonS3Client());
        context.Services.AddRemoteSandbox<FargateTaskPlatform, S3ObjectStore>(context.Services.GetConfiguration());
    }

    private static RegionEndpoint? Region(IServiceProvider services) =>
        services.GetRequiredService<IOptions<FargateSandboxOptions>>().Value.Region is { Length: > 0 } name ? RegionEndpoint.GetBySystemName(name) : null;
}
