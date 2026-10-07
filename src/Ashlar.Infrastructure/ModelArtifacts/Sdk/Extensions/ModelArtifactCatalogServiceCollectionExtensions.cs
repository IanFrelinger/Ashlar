using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Ashlar.Abstractions;
using Ashlar.Core.Application.ModelArtifacts.Ports;
using Ashlar.Infrastructure.Egress;
using Ashlar.Infrastructure.ModelArtifacts;
using Ashlar.Infrastructure.NodeCapabilityRuntime.Backends;
using Ashlar.Infrastructure.NodeCapabilityRuntime.Sdk.Extensions;

namespace Ashlar.Infrastructure.ModelArtifacts.Sdk.Extensions;
/// <summary>DI registration extensions for model artifact catalog.</summary>
public static class ModelArtifactCatalogServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IModelArtifactCatalogService"/> and the default Ollama <c>/api/tags</c> source.
    /// Call <see cref="AddDockerOllamaModelArtifactCatalogSource"/> from desktop host registrations when Docker discovery is desired.
    /// </summary>
    /// <remarks>
    /// Calls <see cref="EgressServiceCollectionExtensions.AddAshlarEgressGuard"/> after the three catalog clients
    /// (SPEC-007, report-only), so the collection also gets <c>AddLogging</c>, an <c>IEgressGuard</c> (TryAdd),
    /// <see cref="EgressDecisionLoggerSubscription"/> and the <c>EgressDecisionLoggerActivator</c> hosted service,
    /// unless an earlier <c>AddAshlarEgressGuard</c> call on this collection already added them.
    /// </remarks>
    public static IServiceCollection AddModelArtifactCatalog(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        if (services is null) throw new ArgumentNullException(nameof(services));
        if (configuration is null) throw new ArgumentNullException(nameof(configuration));

        services.AddOllamaBackendOptions(configuration);
        services.AddOptions<DockerOllamaModelArtifactCatalogOptions>()
            .Bind(configuration.GetSection(DockerOllamaModelArtifactCatalogOptions.SectionName));
        services.AddOptions<OllamaRemoteLibraryCatalogOptions>()
            .Bind(configuration.GetSection(OllamaRemoteLibraryCatalogOptions.SectionName))
            .Configure<IOptions<AshlarResolvedDeploymentProfileOptions>>((opts, profile) =>
            {
                // Property initializer stays true, so Full is unchanged. On AirGapped, an absent Enabled key
                // defaults to false. An explicit Enabled value, and any later Configure, still win.
                var enabled = configuration.GetSection(OllamaRemoteLibraryCatalogOptions.SectionName)["Enabled"];
                if (enabled is null && profile.Value.IsAirGapped)
                    opts.Enabled = false;
            });

        services.AddHttpClient(OllamaTagsModelArtifactCatalogSource.HttpClientName, (sp, client) =>
        {
            var ollama = sp.GetRequiredService<IOptions<OllamaBackendOptions>>().Value;
            client.BaseAddress = new Uri(ollama.BaseUrl.TrimEnd('/') + "/", UriKind.Absolute);
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        services.AddHttpClient(DockerOllamaModelArtifactCatalogSource.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(12);
        });

        services.AddHttpClient(OllamaRemoteLibraryModelArtifactCatalogSource.HttpClientName, (sp, client) =>
        {
            var opts = sp.GetRequiredService<IOptionsMonitor<OllamaRemoteLibraryCatalogOptions>>().CurrentValue;
            var baseUrl = string.IsNullOrWhiteSpace(opts.BaseUrl) ? "https://ollama.com" : opts.BaseUrl.Trim();
            client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/", global::System.UriKind.Absolute);
            client.Timeout = opts.RequestTimeout <= TimeSpan.Zero ? TimeSpan.FromSeconds(60) : opts.RequestTimeout;
        });
        // SPEC-007: report-only guard handler on the three catalog clients above (idempotent).
        services.AddAshlarEgressGuard();

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IModelArtifactCatalogSource, OllamaTagsModelArtifactCatalogSource>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IModelArtifactCatalogSource, OllamaRemoteLibraryModelArtifactCatalogSource>());
        services.TryAddSingleton<IModelArtifactCatalogService, ModelArtifactCatalogService>();
        return services;
    }

    /// <summary>
    /// Adds Docker engine probing for Ollama containers (same HTTP <c>/api/tags</c> per published port).
    /// </summary>
    public static IServiceCollection AddDockerOllamaModelArtifactCatalogSource(this IServiceCollection services)
    {
        if (services is null) throw new ArgumentNullException(nameof(services));

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IModelArtifactCatalogSource, DockerOllamaModelArtifactCatalogSource>());
        return services;
    }
}
