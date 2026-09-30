using Ashlar.BackgroundAgents.DataSensitivity;
using Ashlar.Core.Application.Trust.Ports;

namespace Ashlar.BackgroundAgents.RAG;

/// <summary>
/// Indexes documents from file paths into the RAG vector store with optional sensitivity level.
/// </summary>
public interface IKnowledgeBaseIndexer
{
    /// <summary>
    /// Index documents from the given paths (files and/or directories).
    /// </summary>
    /// <param name="paths">File or directory paths to index.</param>
    /// <param name="defaultSensitivityLevelName">Sensitivity level to assign to indexed documents. Omitted
    /// means UNMARKED, which search treats as the most restrictive level, never Public. The CLI's
    /// <c>rag index</c> refuses to run without one so the choice is always a deliberate one.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Number of documents indexed.</returns>
    Task<int> IndexDocumentsAsync(
        IEnumerable<string> paths,
        string? defaultSensitivityLevelName,
        CancellationToken cancellationToken = default);
}
