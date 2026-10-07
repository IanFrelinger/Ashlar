using Ashlar.AI.Pipeline.Rag;
using Ashlar.BackgroundAgents.RAG;

namespace Ashlar.Hosting.Meai;

/// <summary>
/// Adapts <see cref="VectorDataRagService"/> to the legacy <see cref="IRAGService"/> surface
/// so tools/CLI keep working after Phase 6 cutover.
/// </summary>
/// <remarks>
/// <para>This adapter no longer substitutes a tier for one the caller did not give, in either
/// direction, and both substitutions it used to make failed open. An omitted search clearance
/// became "TopSecret", so <c>rag search</c> without <c>--max-sensitivity</c> returned the whole
/// corpus, and the audit then recorded <c>caller_tier=TopSecret</c> as if the caller had been
/// cleared for it. An omitted index label became "Public", so an unclassified file was served to
/// everyone.</para>
/// <para>Both blanks now pass through to <see cref="VectorDataRagService"/>, which applies the
/// FLOOR to an omitted clearance and ranks an unlabelled record at the TOP (see
/// <see cref="TrustTierOrder"/>), and whose audit names the clearance it actually applied and
/// why.</para>
/// </remarks>
public sealed class MeaiVectorDataRagAdapter : IRAGService
{
    private readonly VectorDataRagService _rag;

    /// <summary>Creates the adapter.</summary>
    public MeaiVectorDataRagAdapter(VectorDataRagService rag) =>
        _rag = rag ?? throw new ArgumentNullException(nameof(rag));

    /// <inheritdoc />
    public async Task<IReadOnlyList<VectorSearchResult>> SearchAsync(
        string query,
        int maxResults,
        double minScore,
        string? maxSensitivityLevelName,
        CancellationToken cancellationToken = default)
    {
        var hits = await _rag.SearchAsync(
                query ?? string.Empty,
                callerMaxTrustTier: maxSensitivityLevelName,
                top: maxResults,
                minScore: minScore,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return hits
            .Select(h => new VectorSearchResult(
                h.Record.Key,
                h.Record.Text,
                h.Score ?? 0d,
                // IRAGService says null means "unmarked"; the store keeps an unlabelled chunk as "".
                string.IsNullOrWhiteSpace(h.Record.TrustTier) ? null : h.Record.TrustTier))
            .ToList();
    }

    /// <inheritdoc />
    public Task IndexAsync(
        string id,
        string text,
        string? sensitivityLevelName,
        CancellationToken cancellationToken = default) =>
        _rag.IndexAsync(
            id,
            text ?? string.Empty,
            sourceUri: null,
            trustTier: sensitivityLevelName,
            cancellationToken: cancellationToken);

    /// <inheritdoc />
    public Task RemoveAsync(string id, CancellationToken cancellationToken = default) =>
        _rag.RemoveAsync(id, cancellationToken);

    /// <inheritdoc />
    public Task ClearAsync(CancellationToken cancellationToken = default) =>
        _rag.ClearAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<int> GetDocumentCountAsync(CancellationToken cancellationToken = default)
    {
        var count = await _rag.GetDocumentCountAsync(cancellationToken).ConfigureAwait(false);
        return count < 0 ? 0 : count;
    }
}
