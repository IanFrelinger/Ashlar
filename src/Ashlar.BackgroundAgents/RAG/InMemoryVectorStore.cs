using System.Collections.Concurrent;
using Ashlar.BackgroundAgents.DataSensitivity;

namespace Ashlar.BackgroundAgents.RAG;

/// <summary>
/// In-memory vector store. Stores embeddings and text; supports sensitivity-filtered search.
/// </summary>
public sealed class InMemoryVectorStore : IVectorStore
{
    private readonly ConcurrentDictionary<string, (string Text, float[] Embedding, string? SensitivityLevelName)> _documents = new();
    private readonly IDataSensitivityRegistry? _sensitivityRegistry;

    /// <summary>
    /// Initializes a new instance of the <see cref="InMemoryVectorStore"/> class.
    /// </summary>
    /// <param name="sensitivityRegistry">Optional registry for sensitivity-level filtering in search.</param>
    public InMemoryVectorStore(IDataSensitivityRegistry? sensitivityRegistry = null)
    {
        _sensitivityRegistry = sensitivityRegistry;
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
        _documents[id] = (text ?? string.Empty, (float[])embedding.Clone(), sensitivityLevelName);
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
        IDataSensitivityLevel? maxLevel = null;
        if (!string.IsNullOrWhiteSpace(maxSensitivityLevelName) && _sensitivityRegistry != null)
            maxLevel = _sensitivityRegistry.GetByName(maxSensitivityLevelName);

        var querySpan = embedding.AsSpan();
        var results = new List<VectorSearchResult>();

        foreach (var (docId, doc) in _documents)
        {
            if (maxLevel != null && !string.IsNullOrEmpty(doc.SensitivityLevelName))
            {
                var docLevel = _sensitivityRegistry!.GetByName(doc.SensitivityLevelName);
                if (docLevel == null || !_sensitivityRegistry.CanAccess(maxLevel, docLevel))
                    continue;
            }

            // Skip documents of a different dimension rather than scoring them. This is the
            // same guard SqliteVectorStore.SearchAsync applies, for the same reason:
            // CosineSimilarity returns 0 for a length mismatch and the filter below admits
            // anything at or above minScore, so at the common minScore of 0.0 a document
            // written by a differently-dimensioned generator would come back as a score-0.0
            // hit instead of being ignored.
            if (doc.Embedding.Length != embedding.Length)
                continue;

            var score = VectorMath.CosineSimilarity(querySpan, doc.Embedding.AsSpan());
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
