using Ashlar.Core.Application.Execution.Ports;
using Ashlar.Infrastructure.Scaling;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Ashlar.Infrastructure.Execution.Sandbox;

/// <summary>Explicit host opt-in for temporary isolated code tasks.</summary>
public static class IsolatedTaskServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Docker task runner and the deadline reaper. Does not launch
    /// anything or change agent/model routing. Hosts may schedule the reaper's SweepAsync
    /// for cleanup after crashes; registering it alone does not schedule background work.
    /// Existing host-provided runners and provenance sinks are preserved.
    /// </summary>
    public static IServiceCollection AddAshlarIsolatedTasks(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddLogging();
        services.TryAddSingleton<IProcessCommandRunner, ProcessCommandRunner>();
        services.TryAddSingleton<ISessionProvenanceSink, LoggingSessionProvenanceSink>();
        services.TryAddSingleton(sp => new DockerSandboxSessionReaper(
            sp.GetRequiredService<IProcessCommandRunner>(),
            logger: sp.GetService<ILogger<DockerSandboxSessionReaper>>(),
            provenance: sp.GetRequiredService<ISessionProvenanceSink>()));
        // Do not install an unpinned shared session runner: doing so before AddAshlarAutonomy
        // would suppress its digest-pinned registration. Resolve any host session at provider
        // construction time, or keep this feature's default private to this task runner.
        services.TryAddSingleton<IIsolatedTaskRunner>(sp => new DockerIsolatedTaskRunner(
            sp.GetService<ISandboxedSessionRunner>() ?? new DockerSandboxedSessionRunner(
                sp.GetRequiredService<IProcessCommandRunner>(),
                logger: sp.GetService<ILogger<DockerSandboxedSessionRunner>>(),
                provenance: sp.GetRequiredService<ISessionProvenanceSink>())));
        return services;
    }
}
