using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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
    public static IServiceCollection AddRunPodCapabilityRouting(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        if (services is null) throw new ArgumentNullException(nameof(services));
        if (configuration is null) throw new ArgumentNullException(nameof(configuration));

        services.AddOptions<RunPodBrickConfig>()
            .Bind(configuration.GetSection(RunPodBrickConfig.SectionName));

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
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, NCRCapabilityPoller>());
        services.TryAddSingleton<INCRCapabilitySnapshot>(sp =>
            sp.GetServices<IHostedService>().OfType<NCRCapabilityPoller>().First());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, PeerCapabilitySnapshotPoller>());
        services.TryAddSingleton<IPeerCapabilitySnapshot>(sp =>
            sp.GetServices<IHostedService>().OfType<PeerCapabilitySnapshotPoller>().First());
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
