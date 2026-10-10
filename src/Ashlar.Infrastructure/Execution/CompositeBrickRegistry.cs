using Ashlar.Abstractions.Security.Egress;
using Microsoft.Extensions.Logging;
using Ashlar.Brick.Contracts;
using Ashlar.Core.Domain.Bricks;
using Ashlar.Core.Domain.Execution;

namespace Ashlar.Infrastructure.Execution;

/// <summary>
/// DomainBrick registry that merges local bricks with bricks from one or more remote catalogs.
/// Remote bricks are returned as <see cref="RemoteBrick"/> instances.
/// </summary>
public sealed class CompositeBrickRegistry : IBrickRegistry, IDisposable
{
    private readonly IBrickRegistry _local;
    private readonly IReadOnlyList<IRemoteBrickCatalog> _remoteCatalogs;
    private readonly HttpClient _httpClient;
    private readonly EgressRefusalWarnings _refusalWarnings;
    private readonly ILogger<CompositeBrickRegistry>? _logger;

    /// <summary>Initializes a new composite brick registry.</summary>
    public CompositeBrickRegistry(
        IBrickRegistry local,
        IReadOnlyList<IRemoteBrickCatalog> remoteCatalogs,
        HttpClient httpClient,
        ILogger<CompositeBrickRegistry>? logger = null)
    {
        _local = local ?? throw new ArgumentNullException(nameof(local));
        _remoteCatalogs = remoteCatalogs ?? new List<IRemoteBrickCatalog>();
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger;
        _refusalWarnings = new EgressRefusalWarnings((refusal, suppressed, summary) =>
            _logger?.LogWarning(new EventId(7307, "EgressRefusalHandled"),
                "Egress operation refused. Site={Site} Reason={Reason} Ref={Ref} Suppressed={Suppressed} Summary={Summary}",
                refusal.Site, refusal.Reason, refusal.Ref, suppressed, summary));
    }

    /// <summary>Gets brick.</summary>
    public DomainBrick? GetBrick(string id)
    {
        var local = _local.GetBrick(id);
        if (local != null) return local;

        foreach (var catalog in _remoteCatalogs)
        {
            try
            {
                var entry = catalog.GetByIdAsync(id).GetAwaiter().GetResult();
                if (entry != null)
                {
                    var executeBaseUrl = entry.HostBaseUrl ?? catalog.BaseUrl.TrimEnd('/');
                    var capabilityFetch = catalog.GetCapabilitiesWithStalenessAsync().GetAwaiter().GetResult();
                    entry.HostCapabilities ??= capabilityFetch.Capabilities;
                    if (capabilityFetch.IsStale)
                    {
                        _logger?.LogWarning(
                            "Remote brick {BrickId} from {BaseUrl} is using stale host capability data.",
                            entry.Id,
                            catalog.BaseUrl);
                    }
                    return new RemoteBrick(entry, _httpClient, executeBaseUrl, null);
                }
            }
            catch (Exception ex) when (EgressRefusal.Find(ex) is { } refusal)
            {
                _refusalWarnings.Report(refusal);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to get brick {BrickId} from catalog {BaseUrl}", id, catalog.BaseUrl);
            }
        }

        return null;
    }

    /// <summary>Gets all bricks.</summary>
    public IReadOnlyList<DomainBrick> GetAllBricks()
    {
        var set = new Dictionary<string, DomainBrick>(StringComparer.OrdinalIgnoreCase);

        foreach (var b in _local.GetAllBricks())
            set[b.Id] = b;

        foreach (var catalog in _remoteCatalogs)
        {
            try
            {
                var entries = catalog.GetAllAsync().GetAwaiter().GetResult();
                var baseUrl = catalog.BaseUrl.TrimEnd('/');
                var capabilityFetch = catalog.GetCapabilitiesWithStalenessAsync().GetAwaiter().GetResult();
                var hostCapabilities = capabilityFetch.Capabilities;
                if (capabilityFetch.IsStale)
                {
                    _logger?.LogWarning(
                        "Remote catalog {BaseUrl} returned stale host capability data for brick list.",
                        catalog.BaseUrl);
                }
                foreach (var entry in entries)
                {
                    if (set.ContainsKey(entry.Id)) continue;
                    entry.HostCapabilities ??= hostCapabilities;
                    set[entry.Id] = new RemoteBrick(entry, _httpClient, entry.HostBaseUrl ?? baseUrl, null);
                }
            }
            catch (Exception ex) when (EgressRefusal.Find(ex) is { } refusal)
            {
                _refusalWarnings.Report(refusal);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to get bricks from catalog {BaseUrl}", catalog.BaseUrl);
            }
        }

        return set.Values.ToList();
    }
    /// <summary>Flushes suppressed refusal warnings and releases their timers.</summary>
    public void Dispose()
    {
        _refusalWarnings.Dispose();
    }

}
