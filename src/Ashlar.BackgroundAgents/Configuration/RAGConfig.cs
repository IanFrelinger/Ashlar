namespace Ashlar.BackgroundAgents.Configuration;

/// <summary>
/// RAG (Retrieval Augmented Generation) configuration.
/// </summary>
public class RAGConfig
{
    /// <summary>
    /// Whether RAG is enabled for this agent.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Vector store provider. Only <c>in-memory</c> is supported by agent configuration.
    /// Names are compared without case sensitivity and surrounding whitespace.
    /// </summary>
    /// <remarks>
    /// The built-in compositions use a host-wide, in-process store. This per-agent setting
    /// does not select a persistent store; sqlite, postgres, qdrant and unknown names are
    /// refused when enabled. A custom host can register its own store directly, but that
    /// does not add configuration-driven provider selection.
    /// </remarks>
    public string? VectorStoreProvider { get; set; }

    /// <summary>
    /// Reserved for future persistent providers. The in-memory provider does not use a path
    /// or connection string, and its indexed documents are lost when the process exits.
    /// </summary>
    public string? VectorStorePath { get; set; }

    /// <summary>
    /// Maximum number of results to retrieve.
    /// </summary>
    public int MaxRetrievalResults { get; set; } = 5;

    /// <summary>
    /// Minimum similarity score (0.0-1.0).
    /// </summary>
    public double SimilarityThreshold { get; set; } = 0.7;

    /// <summary>
    /// Paths to knowledge sources (directories or files).
    /// </summary>
    public List<string>? KnowledgeSources { get; set; }

    /// <summary>
    /// Maximum sensitivity level name of sources to index.
    /// </summary>
    public string MaxSourceSensitivity { get; set; } = "Internal";

    internal void ValidateProvider(string agentId)
    {
        if (!Enabled)
            return;

        if (string.IsNullOrWhiteSpace(VectorStoreProvider))
            throw new InvalidOperationException($"Agent {agentId} RAG enabled but no provider specified");

        if (!string.Equals(VectorStoreProvider.Trim(), "in-memory", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Agent {agentId} RAG vector store provider '{VectorStoreProvider}' is unsupported. "
                + "Only 'in-memory' is supported by agent configuration; persistent provider selection "
                + "is not implemented and VectorStorePath does not create a persistent store.");
        }
    }
}
