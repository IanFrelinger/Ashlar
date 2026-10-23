using Ashlar.Abstractions;
using Ashlar.BackgroundAgents.Configuration;

namespace Ashlar.BackgroundAgents.Registry;

/// <summary>
/// Represents a background agent instance with its configuration and state.
/// </summary>
public class BackgroundAgentInstance
{
    // Set only by registry registration. Public Config (including Id) remains editable.
    internal string RegisteredId { get; init; } = string.Empty;

    /// <summary>
    /// The agent instance.
    /// </summary>
    public IAgent Agent { get; set; } = null!;

    /// <summary>
    /// The agent configuration.
    /// </summary>
    public BackgroundAgentConfig Config { get; set; } = null!;

    /// <summary>
    /// Current state of the agent.
    /// </summary>
    public BackgroundAgentState State { get; set; } = BackgroundAgentState.Idle;

    /// <summary>
    /// When the agent was last started.
    /// </summary>
    public DateTimeOffset? LastStartedAt { get; set; }

    /// <summary>
    /// When the agent last completed execution.
    /// </summary>
    public DateTimeOffset? LastCompletedAt { get; set; }

    /// <summary>
    /// Number of times the agent has executed.
    /// </summary>
    public int ExecutionCount { get; set; }

    /// <summary>
    /// Number of successful executions.
    /// </summary>
    public int SuccessCount { get; set; }

    /// <summary>
    /// Number of failed executions.
    /// </summary>
    public int FailureCount { get; set; }

    /// <summary>
    /// Last error message (if any).
    /// </summary>
    public string? LastError { get; set; }
}
