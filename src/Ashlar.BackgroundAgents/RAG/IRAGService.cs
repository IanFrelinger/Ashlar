namespace Ashlar.BackgroundAgents.RAG;

/// <summary>
/// RAG (Retrieval Augmented Generation) service: indexes text with embeddings and runs sensitivity-aware similarity search.
/// </summary>
public interface IRAGService
{
    /// <summary>
    /// Search for documents similar to the query text.
    /// </summary>
    /// <param name="query">Query text.</param>
    /// <param name="maxResults">Maximum number of results.</param>
    /// <param name="minScore">Minimum similarity score (0.0-1.0).</param>
    /// <param name="maxSensitivityLevelName">The caller's clearance: only documents at or below it are
    /// returned. Omitted or unrecognised means the FLOOR (Public), never everything; an unmarked
    /// document is treated as the most restrictive level.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Search results ordered by score descending.</returns>
    Task<IReadOnlyList<VectorSearchResult>> SearchAsync(
        string query,
        int maxResults,
        double minScore,
        string? maxSensitivityLevelName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Index a document (or chunk) with optional sensitivity level.
    /// </summary>
    /// <remarks>Re-indexing an existing <paramref name="id"/> at a LOWER level than it is stored at
    /// is refused with <see cref="InvalidOperationException"/>; remove it first.</remarks>
    /// <param name="id">Document id.</param>
    /// <param name="text">Document text.</param>
    /// <param name="sensitivityLevelName">Sensitivity level name. Omitted means UNMARKED, which every
    /// search treats as the most restrictive level -- never Public.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task IndexAsync(
        string id,
        string text,
        string? sensitivityLevelName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Remove a document by id.
    /// </summary>
    Task RemoveAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Clear all documents.
    /// </summary>
    Task ClearAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Get the number of documents in the RAG store.
    /// </summary>
    Task<int> GetDocumentCountAsync(CancellationToken cancellationToken = default);
}
