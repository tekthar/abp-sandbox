using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Volo.Abp.Modularity;

namespace AbpSandbox;

/// <summary>
/// AbpSandbox: untrusted work in containers (docs/adr/0001). Depend on a provider's module too (AbpSandbox.Docker);
/// without one, every request is refused. Settings are the host's "AbpSandbox" section (<see cref="SandboxOptions"/>).
/// </summary>
public class AbpSandboxModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.Configure<SandboxOptions>(context.Services.GetConfiguration().GetSection(SandboxOptions.Section));
        context.Services.TryAddSingleton(TimeProvider.System);
        context.Services.TryAddSingleton<ISandboxProvider, NoSandboxProvider>();
        context.Services.TryAddSingleton<Sandbox>();
        context.Services.TryAddSingleton<ISandbox>(sp => sp.GetRequiredService<Sandbox>());
        context.Services.AddHostedService<SandboxSweeper>();
    }
}

/// <summary>The provider when the host configured none: it refuses, so nothing runs on the host's own server by accident.</summary>
public sealed class NoSandboxProvider : ISandboxProvider
{
    private static SandboxRefusedException None() => new("No sandbox provider is configured: depend on AbpSandbox.Docker or another provider's module.");

    public string Name => "none";

    public SandboxCapabilities Capabilities => new(false, false);

    public Task<ISandboxContainer> CreateAsync(SandboxContainerSpec spec, CancellationToken cancellationToken = default) => throw None();

    public Task<string?> FindPreparedAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);

    public Task<string> SavePreparedAsync(ISandboxContainer container, string key, CancellationToken cancellationToken = default) => throw None();

    public Task<IReadOnlyList<SandboxPreparedImage>> ListPreparedAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<SandboxPreparedImage>>([]);

    public Task RemovePreparedAsync(string reference, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<int> SweepAsync(DateTimeOffset now, string? node, CancellationToken cancellationToken = default) => Task.FromResult(0);
}

/// <summary>
/// The sweep (docs/adr/0001, decision 6): at start, what this server made before it restarted; then, every
/// <see cref="SandboxOptions.SweepEvery"/>, sessions past their time, containers past their expiry on any server, and
/// prepared images past the count; and the warm pool is filled. When the host stops, its sessions close and its warm
/// containers go.
/// </summary>
public sealed class SandboxSweeper(Sandbox sandbox, IOptions<SandboxOptions> options, ILogger<SandboxSweeper> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Sweep || sandbox.Provider is NoSandboxProvider)
        {
            return;
        }

        await SweepAsync(sandbox.Node, stoppingToken);
        using PeriodicTimer timer = new(options.Value.SweepEvery > TimeSpan.Zero ? options.Value.SweepEvery : TimeSpan.FromMinutes(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await SweepAsync(null, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        await sandbox.CloseAllAsync();
    }

    private async Task SweepAsync(string? node, CancellationToken cancellationToken)
    {
        try
        {
            int sessions = await sandbox.CloseExpiredAsync(cancellationToken);
            int containers = await sandbox.Provider.SweepAsync(sandbox.Clock.GetUtcNow(), node, cancellationToken);
            int images = await sandbox.EvictPreparedAsync(cancellationToken);
            int warm = await sandbox.TopUpWarmAsync(cancellationToken);
            if (sessions + containers + images + warm > 0)
            {
                logger.LogInformation("Sandbox sweep: {Sessions} sessions closed, {Containers} containers and {Images} prepared images removed, {Warm} warm containers started.",
                    sessions, containers, images, warm);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "The sandbox sweep failed; it tries again at the next.");
        }
    }
}
