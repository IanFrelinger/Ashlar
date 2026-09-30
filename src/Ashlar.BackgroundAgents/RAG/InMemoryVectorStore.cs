using System.Collections.Concurrent;
using Ashlar.BackgroundAgents.DataSensitivity;

namespace Ashlar.BackgroundAgents.RAG;

/// <summary>
/// In-memory vector store. Stores embeddings and text; supports sensitivity-filtered search.
/// </summary>
/// <remarks>
/// <para><b>Search always filters, and fails closed.</b> A null, blank or unknown clearance is the
/// registry's FLOOR (Public for the primitives), and an unmarked or unknown document label is its
/// MOST RESTRICTIVE level -- see <see cref="DataSensitivityFallbacks"/>. This store used to apply
/// no filter at all unless BOTH a registry was supplied AND the clearance resolved, and it returned
/// every unmarked document to every caller.</para>
/// <para><b>Re-indexing never lowers a label.</b> <see cref="IndexAsync"/> refuses to replace an
/// existing id with a lower-labelled document; remove it first if the lower label is right.</para>
/// </remarks>
public sealed class InMemoryVectorStore : IVectorStore
{
    private readonly ConcurrentDictionary<string, (string Text, float[] Embedding, string? SensitivityLevelName)> _documents = new();
    private readonly IDataSensitivityRegistry _sensitivityRegistry;

    /// <summary>
    /// Initializes a new instance of the <see cref="InMemoryVectorStore"/> class.
    /// </summary>
    /// <param name="sensitivityRegistry">Registry that orders sensitivity levels. When null, a
    /// registry of the five primitive levels is used, so filtering is never switched off by
    /// omission.</param>
    public InMemoryVectorStore(IDataSensitivityRegistry? sensitivityRegistry = null)
    {
        _sensitivityRegistry = sensitivityRegistry ?? new DataSensitivityRegistry();
    }

    /// <inheritdoc />
    public Task IndexAsync(
        string id,
        string text,
        float[] embedding,
        string? sensitivityLevelName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(id))
            throw new ArgumentNullException(nameof(id));
        var incoming = (text ?? string.Empty, (float[])embedding.Clone(), sensitivityLevelName);
        // AddOrUpdate, not an indexer write, so the downgrade check and the replacement see the same
        // existing document even under a concurrent writer.
        _documents.AddOrUpdate(
            id,
            incoming,
            (key, existing) =>
            {
                RagSensitivity.ThrowIfDowngrade(_sensitivityRegistry, key, existing.SensitivityLevelName, sensitivityLevelName);
                return incoming;
            });
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<VectorSearchResult>> SearchAsync(
        float[] embedding,
        int maxResults,
        double minScore,
        string? maxSensitivityLevelName,
        CancellationToken cancellationToken = default)
    {
        // Refuse an unrankable query before touching the corpus. A zero-magnitude query has no
        // angle to any document, so it scores 0.0 against all of them and -- at the common
        // minScore of 0.0 -- comes back holding the first maxResults documents in the store as
        // "hits". See VectorMath.UnrankableQuery for why this refuses instead of returning an
        // empty list, and VectorMath.TryCosineSimilarity for why the test is on the accumulated
        // norm rather than on the components.
        if (!VectorMath.IsRankable(embedding))
            throw VectorMath.UnrankableQuery(nameof(embedding));

        var clearance = _sensitivityRegistry.ResolveClearance(maxSensitivityLevelName);

        var querySpan = embedding.AsSpan();
        var results = new List<VectorSearchResult>();

        foreach (var (docId, doc) in _documents)
        {
            if (!RagSensitivity.CanRead(_sensitivityRegistry, clearance, doc.SensitivityLevelName))
                continue;

            // Skip a document that cannot be ranked against this query rather than scoring it --
            // a different dimension, or a zero-magnitude embedding. Both used to arrive as the
            // value 0.0 out of CosineSimilarity, and the filter below admits anything at or above
            // minScore, so at the common minScore of 0.0 they came back as score-0.0 hits instead
            // of being ignored. A zero-magnitude ROW needs no malformed input: an empty .log in a
            // knowledge-source directory is enough, because TokenEmbeddingGenerator returns an
            // all-zero vector for text it finds no tokens in.
            //
            // Skip, not refuse, in this direction: one unrankable row must not take a good query
            // down with it. The opposite choice is made for the QUERY above, and
            // VectorMath.UnrankableQuery says why the two directions differ.
            //
            // TryCosineSimilarity is now the single authority on which pairs are comparable, so
            // the inline dimension check that used to sit here is its first clause. The test
            // SearchAsync_DocumentOfDifferentDimension_IsSkippedNotReturnedAtScoreZero still pins
            // the same invariant; it now fails when that clause is reverted rather than when a
            // line here is deleted.
            if (!VectorMath.TryCosineSimilarity(querySpan, doc.Embedding.AsSpan(), out var score))
                continue;

            if (score >= minScore)
                results.Add(new VectorSearchResult(docId, doc.Text, score, doc.SensitivityLevelName));
        }

        var ordered = results.OrderByDescending(r => r.Score).Take(maxResults).ToList();
        return Task.FromResult<IReadOnlyList<VectorSearchResult>>(ordered);
    }

    /// <inheritdoc />
    public Task RemoveAsync(string id, CancellationToken cancellationToken = default)
    {
        _documents.TryRemove(id, out _);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        _documents.Clear();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<int> GetDocumentCountAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(_documents.Count);
}
