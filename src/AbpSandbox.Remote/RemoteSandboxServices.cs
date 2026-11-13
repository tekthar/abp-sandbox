using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AbpSandbox.Remote;

/// <summary>What a cloud provider's module registers: the remote provider over its own task platform and object store.</summary>
public static class RemoteSandboxServices
{
    /// <param name="name">The name AbpSandbox:Provider picks it with (fargate, azure).</param>
    public static IServiceCollection AddRemoteSandbox<TPlatform, TStore>(this IServiceCollection services, IConfiguration configuration, string name)
        where TPlatform : class, ISandboxTaskPlatform
        where TStore : class, ISandboxObjectStore
    {
        services.Configure<RemoteSandboxOptions>(configuration.GetSection(RemoteSandboxOptions.Section));
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ISandboxTaskPlatform, TPlatform>();
        services.AddSingleton<ISandboxObjectStore, TStore>();
        services.AddSandboxProvider<RemoteSandboxProvider>(name);
        return services;
    }
}
