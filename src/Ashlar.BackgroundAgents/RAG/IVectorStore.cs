namespace Ashlar.BackgroundAgents.RAG;

/// <summary>
/// Interface for vector stores used by RAG.
/// Supports indexing text with optional sensitivity level and similarity search.
/// </summary>
public interface IVectorStore
{
    /// <summary>
    /// Index a document (or chunk) with an embedding and optional sensitivity level.
    /// </summary>
    /// <param name="id">Unique document/chunk id.</param>
    /// <param name="text">Raw text content.</param>
    /// <param name="embedding">Embedding vector (same dimension as store).</param>
    /// <param name="sensitivityLevelName">Sensitivity level name. Omitted means UNMARKED, which search
    /// treats as the most restrictive level. Replacing an existing id with a LOWER level throws
    /// <see cref="InvalidOperationException"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task IndexAsync(
        string id,
        string text,
        float[] embedding,
        string? sensitivityLevelName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Search for similar documents by embedding, optionally filtered by max sensitivity level.
    /// </summary>
    /// <param name="embedding">Query embedding.</param>
    /// <param name="maxResults">Maximum number of results.</param>
    /// <param name="minScore">Minimum similarity score (0.0-1.0).</param>
    /// <param name="maxSensitivityLevelName">The caller's clearance: only documents at or below it are
    /// returned. Omitted or unrecognised means the FLOOR (Public), never everything.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Results ordered by score descending.</returns>
    Task<IReadOnlyList<VectorSearchResult>> SearchAsync(
        float[] embedding,
        int maxResults,
        double minScore,
        string? maxSensitivityLevelName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Remove a document by id.
    /// </summary>
    Task RemoveAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Clear all documents (optional; not all stores may support).
    /// </summary>
    Task ClearAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Get the number of documents in the store.
    /// </summary>
    Task<int> GetDocumentCountAsync(CancellationToken cancellationToken = default);
}
