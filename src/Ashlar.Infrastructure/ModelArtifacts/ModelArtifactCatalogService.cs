using Ashlar.Abstractions.Security.Egress;
using Microsoft.Extensions.Logging;
using Ashlar.Core.Application.ModelArtifacts;
using Ashlar.Core.Application.ModelArtifacts.Ports;

namespace Ashlar.Infrastructure.ModelArtifacts;

/// <summary>
/// Merges all registered <see cref="IModelArtifactCatalogSource"/> implementations.
/// Sources that are unavailable or throw are skipped.
/// </summary>
public sealed class ModelArtifactCatalogService : IModelArtifactCatalogService, IDisposable
{
    private readonly IEnumerable<IModelArtifactCatalogSource> _sources;
    private readonly EgressRefusalWarnings _refusalWarnings;
    private readonly ILogger<ModelArtifactCatalogService> _logger;

    /// <summary>Initializes a new model artifact catalog service.</summary>
    public ModelArtifactCatalogService(
        IEnumerable<IModelArtifactCatalogSource> sources,
        ILogger<ModelArtifactCatalogService> logger)
    {
        _sources = sources ?? throw new ArgumentNullException(nameof(sources));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _refusalWarnings = new EgressRefusalWarnings((refusal, suppressed, summary) =>
            _logger?.LogWarning(new EventId(7307, "EgressRefusalHandled"),
                "Egress operation refused. Site={Site} Reason={Reason} Ref={Ref} Suppressed={Suppressed} Summary={Summary}",
                refusal.Site, refusal.Reason, refusal.Ref, suppressed, summary));
    }

    /// <summary>List all asynchronously.</summary>
    public Task<IReadOnlyList<ModelArtifactRecord>> ListAllAsync(CancellationToken cancellationToken = default) =>
        ListFromSourcesAsync(static s => s is not IRemoteInstallableModelArtifactSource, cancellationToken);

    /// <summary>List installable asynchronously.</summary>
    public Task<IReadOnlyList<ModelArtifactRecord>> ListInstallableAsync(CancellationToken cancellationToken = default) =>
        ListFromSourcesAsync(static s => s is IRemoteInstallableModelArtifactSource, cancellationToken);

    private async Task<IReadOnlyList<ModelArtifactRecord>> ListFromSourcesAsync(
        Func<IModelArtifactCatalogSource, bool> predicate,
        CancellationToken cancellationToken)
    {
        var list = new List<ModelArtifactRecord>();
        foreach (var source in _sources)
        {
            if (!predicate(source))
            {
                continue;
            }

            try
            {
                if (!await source.IsAvailableAsync(cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }

                var chunk = await source.ListAsync(cancellationToken).ConfigureAwait(false);
                if (chunk.Count > 0)
                {
                    list.AddRange(chunk);
                }
            }
            catch (Exception ex) when (EgressRefusal.Find(ex) is { } refusal)
            {
                _refusalWarnings.Report(refusal);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Model artifact catalog source {SourceId} failed; continuing with other sources", source.SourceId);
            }
        }

        return list;
    }
    /// <summary>Flushes suppressed refusal warnings and releases their timers.</summary>
    public void Dispose()
    {
        _refusalWarnings.Dispose();
    }

}
