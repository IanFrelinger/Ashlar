using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Ashlar.Infrastructure.Egress;

namespace Ashlar.Infrastructure.MeshLab;

/// <summary>
/// Open mesh-lab worker executor (HTTP client to commercial fleet director APIs).
/// </summary>
public static class MeshLabServiceCollectionExtensions
{
    /// <summary>
    /// Virtual mesh lab: optional background worker that completes assigned tasks via the director HTTP API.
    /// </summary>
    public static IServiceCollection AddAshlarMeshLabWorkerExecutor(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<MeshLabWorkerExecutorOptions>()
            .Bind(configuration.GetSection(MeshLabWorkerExecutorOptions.SectionPath));

        var enabled = configuration.GetValue(
            $"{MeshLabWorkerExecutorOptions.SectionPath}:Enabled",
            defaultValue: false);
        if (!enabled)
            return services;

        services.AddHttpClient(MeshLabWorkerExecutorClient.HttpClientName);
        // SPEC-007: report-only guard handler on the worker client; after the early return, so the disabled path is unchanged.
        services.AddAshlarEgressGuard();
        services.TryAddSingleton<MeshLabWorkerExecutorClient>();
        services.AddHostedService<MeshLabWorkerExecutorBackgroundService>();
        return services;
    }
}
