using Ashlar.Core.Domain;

namespace Ashlar.BackgroundAgents.Configuration;

/// <summary>
/// Configuration for a background agent.
/// 
/// Defines all aspects of a background agent including:
/// - Identity and role
/// - Model provider and configuration
/// - Commands and parameters
/// - Schedule (when to run)
/// - Data sensitivity restrictions
/// - RAG and web search capabilities
/// - Exfiltration prevention policies
/// </summary>
public class BackgroundAgentConfig
{
    /// <summary>
    /// Unique agent identifier.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable agent name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Agent role (e.g., "monitor", "analyzer", "optimizer", "auditor").
    /// </summary>
    public string Role { get; set; } = string.Empty;

    /// <summary>
    /// Parent agent ID (for hierarchies).
    /// </summary>
    public string? ParentId { get; set; }

    /// <summary>
    /// Model provider ("openai", "azure", "ollama", "deterministic").
    ///
    /// <para>Defaults to <see cref="AshlarDefaults.DeterministicProviderName"/> — the offline,
    /// no-LLM route — and is spelled from that constant rather than a literal, because
    /// <c>ProviderFactory.KnownProviders</c> must contain whatever this default is. When the two
    /// drifted apart, a scaffold that <c>ashlar verify</c> had just certified refused to run on
    /// the same directory.</para>
    /// </summary>
    public string ModelProvider { get; set; } = AshlarDefaults.DeterministicProviderName;

    /// <summary>
    /// Specific model name (optional, e.g., "gpt-4", "llama2").
    /// </summary>
    public string? ModelName { get; set; }

    /// <summary>
    /// Commands this agent can execute.
    /// </summary>
    public List<string> Commands { get; set; } = new();

    /// <summary>
    /// Agent-specific parameters.
    /// </summary>
    public Dictionary<string, object>? Parameters { get; set; }

    /// <summary>
    /// Schedule configuration (when to run).
    /// </summary>
    public BackgroundAgentSchedule Schedule { get; set; } = new();

    /// <summary>
    /// Whether this agent is enabled.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Maximum data sensitivity level this agent can access (by name).
    /// </summary>
    public string MaxDataSensitivity { get; set; } = "Public";

    /// <summary>
    /// Specific sensitivity level names allowed (optional, for fine-grained control).
    /// </summary>
    public List<string>? AllowedDataSensitivityLevels { get; set; }

    /// <summary>
    /// Custom sensitivity levels defined for this agent (optional).
    /// </summary>
    public Dictionary<string, DataSensitivity.CustomSensitivityLevel>? CustomSensitivityLevels { get; set; }

    /// <summary>
    /// RAG (Retrieval Augmented Generation) configuration (optional).
    /// </summary>
    public RAGConfig? RAG { get; set; }

    /// <summary>
    /// Web search configuration (optional).
    /// </summary>
    public WebSearchConfig? WebSearch { get; set; }

    /// <summary>
    /// Exfiltration prevention policy.
    /// </summary>
    public ExfiltrationPolicy ExfiltrationPolicy { get; set; } = new();
}
