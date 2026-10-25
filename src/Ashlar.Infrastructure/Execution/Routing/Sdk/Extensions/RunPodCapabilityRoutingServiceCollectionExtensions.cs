using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ashlar.Core.Application.Execution.Routing;
using Ashlar.Core.Application.Mesh;
using Ashlar.Core.Application.Mesh.Models;
using Ashlar.Core.Application.Mesh.Ports;
using Ashlar.Core.Application.NodeCapabilityRuntime.Ports;
using Ashlar.Infrastructure.Adaptation;
using Ashlar.Infrastructure.Egress;
using Ashlar.Infrastructure.Mesh;

namespace Ashlar.Infrastructure.Execution.Routing.Sdk.Extensions;
/// <summary>
/// Registers RunPod + NCR capability-routing execution components.
/// </summary>
public static class RunPodCapabilityRoutingServiceCollectionExtensions
{
    /// <summary>Adds run pod capability routing.</summary>
    /// <remarks>
    /// Calls <see cref="EgressServiceCollectionExtensions.AddAshlarEgressGuard"/> after the typed RunPod client
    /// (SPEC-007, report-only), so the collection also gets <c>AddLogging</c>, an <c>IEgressGuard</c> (TryAdd),
    /// <see cref="EgressDecisionLoggerSubscription"/> and the <c>EgressDecisionLoggerActivator</c> hosted service,
    /// unless an earlier <c>AddAshlarEgressGuard</c> call on this collection already added them.
    /// </remarks>
    public static IServiceCollection AddRunPodCapabilityRouting(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        if (services is null) throw new ArgumentNullException(nameof(services));
        if (configuration is null) throw new ArgumentNullException(nameof(configuration));

        services.AddOptions<RunPodBrickConfig>()
            .Bind(configuration.GetSection(RunPodBrickConfig.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<RunPodBrickConfig>, ValidateAirGappedRunPodOptions>());

        services.TryAddSingleton<IInstanceDiscovery>(sp =>
        {
            var instancesPath = Environment.GetEnvironmentVariable("ASHLAR_MESH_INSTANCES_PATH");
            var trustedPeerIdsCsv = Environment.GetEnvironmentVariable("ASHLAR_TRUSTED_PEER_IDS");
            var untrustedPeerIdsCsv = Environment.GetEnvironmentVariable("ASHLAR_UNTRUSTED_PEER_IDS");
            return new FileBasedInstanceDiscovery(
                string.IsNullOrWhiteSpace(instancesPath) ? null : instancesPath.Trim(),
                trustedPeerIdsCsv,
                untrustedPeerIdsCsv,
                MeshTrustPolicyConfiguration.ResolveDiscoveryPolicy());
        });

        services.AddHttpClient<IRunPodClient, RunPodHttpClient>((sp, client) =>
        {
            var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<RunPodBrickConfig>>().Value;
            var baseUrl = string.IsNullOrWhiteSpace(options.BaseUrl)
                ? "https://api.runpod.io"
                : options.BaseUrl.TrimEnd('/');
            client.BaseAddress = new Uri(baseUrl + "/", UriKind.Absolute);
        });
        // SPEC-007: report-only guard handler on the RunPod client and the default client below (idempotent).
        services.AddAshlarEgressGuard();

        services.TryAddSingleton<ILocalQueueDepthProvider, EnvironmentQueueDepthProvider>();
        // Each poller is one singleton reached by both its snapshot interface and IHostedService.
        // The snapshots must not be found by enumerating IHostedService: that constructs EVERY hosted
        // service, and one that depends on the brick registry (the MCP server does) re-enters this
        // composition through CapabilityRoutingBrick -> INCRCapabilitySnapshot, a cycle factories
        // hide from the container, so startup recursed without ever throwing.
        services.TryAddSingleton<NCRCapabilityPoller>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, NCRCapabilityPoller>(
            sp => sp.GetRequiredService<NCRCapabilityPoller>()));
        services.TryAddSingleton<INCRCapabilitySnapshot>(sp => sp.GetRequiredService<NCRCapabilityPoller>());
        services.TryAddSingleton<PeerCapabilitySnapshotPoller>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, PeerCapabilitySnapshotPoller>(
            sp => sp.GetRequiredService<PeerCapabilitySnapshotPoller>()));
        services.TryAddSingleton<IPeerCapabilitySnapshot>(sp => sp.GetRequiredService<PeerCapabilitySnapshotPoller>());
        services.TryAddSingleton<AshlarPeerBrickExecutor>();
        services.TryAddSingleton<IPeerExecutor>(sp => sp.GetRequiredService<AshlarPeerBrickExecutor>());
        services.TryAddSingleton(sp =>
        {
            var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<RunPodBrickConfig>>().Value;
            return new PeerTrustPolicyResolver(options.PeerTrustPolicy, options.TrustedPeerIdsCsv, options.UntrustedPeerIdsCsv);
        });

        services.TryAddSingleton<ILocalExecutor, ProviderFactoryLocalExecutor>();
        services.TryAddSingleton<RunPodBrick>();
        services.TryAddSingleton<ICapabilityRouter, NcrCapabilityRouter>();
        services.TryAddSingleton<CapabilityRoutingBrick>();

        // Default executor entrypoint is capability-routing brick.
        services.TryAddSingleton<IBrickExecutor>(sp => sp.GetRequiredService<CapabilityRoutingBrick>());

        // Register both routing bricks into adaptation/composite brick registries.
        services.Configure<AdaptationBrickOptions>(options =>
        {
            if (!options.AdditionalBrickTypes.Contains(typeof(RunPodBrick)))
            {
                options.AdditionalBrickTypes.Add(typeof(RunPodBrick));
            }

            if (!options.AdditionalBrickTypes.Contains(typeof(CapabilityRoutingBrick)))
            {
                options.AdditionalBrickTypes.Add(typeof(CapabilityRoutingBrick));
            }
        });

        services.TryAddSingleton<CompositeBrickRegistry>(sp =>
        {
            var local = sp.GetRequiredService<Ashlar.Core.Domain.Execution.IBrickRegistry>();
            var remoteCatalogs = sp.GetService<IReadOnlyList<IRemoteBrickCatalog>>() ?? [];
            var clientFactory = sp.GetRequiredService<IHttpClientFactory>();
            var logger = sp.GetService<ILogger<CompositeBrickRegistry>>();
            return new CompositeBrickRegistry(local, remoteCatalogs, clientFactory.CreateClient(), logger);
        });

        return services;
    }
}
