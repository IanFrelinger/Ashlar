using Microsoft.Extensions.Logging;
using Ashlar.Abstractions;
using Ashlar.Orchestration.Agents;
using Ashlar.Orchestration.Architect.Models;

namespace Ashlar.Tests.BackgroundAgents.Agents;

/// <summary>
/// Meta-agent that manages other background agents (dog-fooding).
/// Extends BaseDomainAgent; when run with a toolbox containing AgentManagementToolbox tools,
/// can start, stop, restart, and update background agents via tool calls.
/// </summary>
public sealed class BackgroundAgentManagerAgent : BaseDomainAgent
{
    /// <summary>
    /// Initializes a new instance of the <see cref="BackgroundAgentManagerAgent"/> class.
    /// </summary>
    /// <param name="spec">Agent spawn specification.</param>
    /// <param name="logger">Logger (use ILogger&lt;BaseAgent&gt; or wrap ILogger&lt;BackgroundAgentManagerAgent&gt;).</param>
    /// <param name="model">Optional model for LLM-driven decisions.</param>
    public BackgroundAgentManagerAgent(
        AgentSpawnSpec spec,
        ILogger<BaseAgent> logger,
        IModel? model = null)
        : base(spec, logger, model)
    {
    }

    /// <inheritdoc />
    protected override Task OnInitializeAsync(CancellationToken cancellationToken)
        => Task.CompletedTask;

    /// <inheritdoc />
    protected override Task OnDependenciesResolvedAsync(
        IReadOnlyDictionary<string, object> dependencyOutputs,
        CancellationToken cancellationToken)
        => Task.CompletedTask;

    /// <inheritdoc />
    protected override string GetSystemPrompt()
    {
        return """
You are a background agent manager. You manage other background agents by starting, stopping, restarting, or updating their configuration.
Available tools: enable_agent (start an agent by ID), disable_agent (stop an agent), restart_agent (stop then start), update_agent_config (reload config and re-register).
Use these tools to keep agents healthy and apply configuration changes. Prefer restart_agent when config has changed and the agent should run with new settings.
""";
    }

    /// <inheritdoc />
    protected override object GetMockOutput()
    {
        return new
        {
            AgentId = Spec.AgentId,
            Domain = Spec.Domain,
            Goal = Spec.Goal,
            Output = $"Background agent manager {Spec.AgentId} ready. Use toolbox tools enable_agent, disable_agent, restart_agent, update_agent_config to manage agents.",
            GeneratedAt = DateTimeOffset.UtcNow
        };
    }

    /// <inheritdoc />
    protected override Task OnShutdownAsync(CancellationToken cancellationToken)
        => Task.CompletedTask;
}
