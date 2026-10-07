using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ashlar.Core.Domain.Execution;

namespace Ashlar.Infrastructure.Execution.Sdk.Extensions;
/// <summary>
/// Optional wiring so each Ashlar host resolves bricks from peers over HTTP
/// (<see cref="HttpRemoteBrickCatalog"/> + <see cref="CompositeBrickRegistry"/>).
/// Dynamic compute scaling for a single task is handled separately by
/// <see cref="Routing.NcrCapabilityRouter"/> and peer HTTP execution when RunPod capability routing is enabled;
/// this extension only federates brick catalogs and execution delegation via <see cref="RemoteBrick"/>.
/// </summary>
public static class AshlarFederatedBrickMeshServiceCollectionExtensions
{
    /// <summary>
    /// When <see cref="BrickHostOptions.RemoteCatalogBaseUrls"/> is non-empty, replaces
    /// <see cref="IBrickRegistry"/> with <see cref="CompositeBrickRegistry"/> backed by the
    /// local <see cref="BrickRegistry"/> plus remote catalogs. Requires adaptation to be
    /// registered (concrete <see cref="BrickRegistry"/>).
    /// </summary>
    public static IServiceCollection AddAshlarFederatedBrickMesh(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        if (services is null) throw new ArgumentNullException(nameof(services));
        if (configuration is null) throw new ArgumentNullException(nameof(configuration));

        services.AddOptions<BrickHostOptions>()
            .Bind(configuration.GetSection(BrickHostOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<BrickHostOptions>, ValidateAirGappedBrickHostOptions>());

        services.AddSingleton<IBrickRegistry>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<BrickHostOptions>>().Value;
            var urls = (opts.RemoteCatalogBaseUrls ?? Array.Empty<string>())
                .Select(u => u?.Trim() ?? "")
                .Where(u => u.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Only the CONCRETE BrickRegistry may be resolved here. Asking for
            // IBrickRegistry as a fallback re-enters this very factory: this
            // registration is the last IBrickRegistry descriptor, so it always wins
            // last-wins and resolves to itself. That fallback therefore never returned
            // an alternative registry — it recursed until the process stopped making
            // progress, with no exception and no stack overflow, which is precisely the
            // case the guard below was written to report cleanly.
            var local = sp.GetService<BrickRegistry>() as IBrickRegistry;
            if (local is null)
                throw new InvalidOperationException(
                    "Federated brick mesh requires a local BrickRegistry (enable adaptation in the deployment profile).");

            if (urls.Count == 0)
                return local;

            foreach (var baseUrl in urls)
            {
                if (!Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri) ||
                    (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                {
                    throw new InvalidOperationException(
                        $"BrickHost.RemoteCatalogBaseUrls contains invalid URL: '{baseUrl}'.");
                }
            }

            var factory = sp.GetRequiredService<IHttpClientFactory>();
            var logger = sp.GetService<ILogger<CompositeBrickRegistry>>();
            var catalogs = new List<IRemoteBrickCatalog>(urls.Count);
            foreach (var baseUrl in urls)
            {
                var normalized = baseUrl.TrimEnd('/') + "/";
                var client = factory.CreateClient();
                client.BaseAddress = new Uri(normalized, UriKind.Absolute);
                if (opts.UseAuth && !string.IsNullOrEmpty(opts.ApiKeyValue) && !string.IsNullOrEmpty(opts.ApiKeyHeader))
                    client.DefaultRequestHeaders.TryAddWithoutValidation(opts.ApiKeyHeader, opts.ApiKeyValue);
                catalogs.Add(new HttpRemoteBrickCatalog(client, sp.GetService<ILogger<HttpRemoteBrickCatalog>>()));
            }

            return new CompositeBrickRegistry(local, catalogs, factory.CreateClient(), logger);
        });

        return services;
    }
}
