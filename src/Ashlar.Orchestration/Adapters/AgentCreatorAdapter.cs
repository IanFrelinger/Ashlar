using Microsoft.Extensions.Logging;
using Ashlar.Abstractions;
using Ashlar.Core.Application.Orchestration.Ports;
using Ashlar.Orchestration.Agents;
using Ashlar.Orchestration.Architect.Models;

namespace Ashlar.Orchestration.Adapters;

/// <summary>
/// Adapter implementing IAgentCreator port using AgentFactory.
/// 
/// Bridges the Application layer port with the concrete Orchestration implementation:
/// - Converts AgentSpawnSpecDto to AgentSpawnSpec
/// - Delegates agent creation to AgentFactory
/// - Returns IAgent instances
/// 
/// This adapter allows BackgroundAgents and other application services
/// to create agents without depending directly on the Orchestration layer.
/// </summary>
public sealed class AgentCreatorAdapter : IAgentCreator
{
    private readonly AgentFactory _agentFactory;
    private readonly ILogger<AgentCreatorAdapter> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AgentCreatorAdapter"/> class.
    /// </summary>
    /// <param name="agentFactory">The underlying agent factory.</param>
    /// <param name="logger">Logger for adapter operations.</param>
    public AgentCreatorAdapter(
        AgentFactory agentFactory,
        ILogger<AgentCreatorAdapter> logger)
    {
        _agentFactory = agentFactory ?? throw new ArgumentNullException(nameof(agentFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public IAgent CreateAgent(AgentSpawnSpecDto spec)
    {
        if (spec == null)
        {
            throw new ArgumentNullException(nameof(spec));
        }

        _logger.LogDebug(
            "Creating agent {AgentId} (domain: {Domain}) via adapter",
            spec.AgentId,
            spec.Domain);

        // Convert DTO to full AgentSpawnSpec
        var fullSpec = new AgentSpawnSpec
        {
            AgentId = spec.AgentId,
            Name = spec.Name,
            Domain = spec.Domain,
            Goal = spec.Goal,
            Description = spec.Description,
            Dependencies = spec.Dependencies,
            OllamaModel = spec.OllamaModel
        };

        // Delegate to AgentFactory which returns BaseAgent (implements IAgent)
        return _agentFactory.CreateAgent(fullSpec);
    }
}
