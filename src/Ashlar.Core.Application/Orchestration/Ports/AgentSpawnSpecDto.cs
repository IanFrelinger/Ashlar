namespace Ashlar.Core.Application.Orchestration.Ports;

/// <summary>
/// Data transfer object for agent spawn specifications.
/// 
/// Contains:
/// - Agent identification (ID, domain, goal, description)
/// - Dependencies on other agents
/// - Optional Ollama model name
/// 
/// This is a simplified port-level representation used by application layer components
/// (such as BackgroundAgents) to request agent creation without depending on the full
/// orchestration layer models.
/// </summary>
public sealed record AgentSpawnSpecDto
{
    /// <summary>
    /// Unique identifier for this agent instance.
    /// </summary>
    public required string AgentId { get; init; }

    /// <summary>
    /// Friendly agent name for policy and diagnostics. Defaults to <see cref="AgentId"/>.
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Domain or category this agent specializes in (e.g., "Infrastructure", "Security", "Generic").
    /// </summary>
    public required string Domain { get; init; }

    /// <summary>
    /// Primary goal or objective for this agent.
    /// </summary>
    public required string Goal { get; init; }

    /// <summary>
    /// Detailed description of what this agent should accomplish.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// List of agent IDs that must complete before this agent can start.
    /// </summary>
    public IReadOnlyList<string> Dependencies { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Optional Ollama model name to use for this specific agent.
    /// </summary>
    public string? OllamaModel { get; init; }
}
