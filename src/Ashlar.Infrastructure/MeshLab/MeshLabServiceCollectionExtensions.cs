using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
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
    /// <remarks>
    /// When the worker is enabled, calls <see cref="EgressServiceCollectionExtensions.AddAshlarEgressGuard"/> after
    /// the worker client (SPEC-007, report-only), so the collection also gets <c>AddLogging</c>, an
    /// <c>IEgressGuard</c> (TryAdd), <see cref="EgressDecisionLoggerSubscription"/> and the
    /// <c>EgressDecisionLoggerActivator</c> hosted service, unless an earlier <c>AddAshlarEgressGuard</c> call on this
    /// collection already added them. When it is disabled, none of them is added.
    /// </remarks>
    public static IServiceCollection AddAshlarMeshLabWorkerExecutor(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<MeshLabWorkerExecutorOptions>()
            .Bind(configuration.GetSection(MeshLabWorkerExecutorOptions.SectionPath))
            .ValidateOnStart();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<MeshLabWorkerExecutorOptions>, ValidateAirGappedMeshLabWorkerOptions>());

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
