using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
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
            .Bind(configuration.GetSection(OllamaRemoteLibraryCatalogOptions.SectionName));

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
