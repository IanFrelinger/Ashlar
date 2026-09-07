using Ashlar.Abstractions;

namespace Ashlar.Core.Application.Orchestration.Ports;

/// <summary>
/// Port for creating agents from specifications.
/// 
/// Defines the contract for agent instantiation:
/// - Creates agents from spawn specifications
/// - Returns concrete agent instances
/// 
/// Implementations (typically in Orchestration layer) handle:
/// - Domain-specific agent type selection
/// - Dependency injection and wiring
/// - Model resolution and wrapping
/// 
/// Used by BackgroundAgents to create agent instances without
/// depending on concrete orchestration implementations.
/// </summary>
public interface IAgentCreator
{
    /// <summary>
    /// Creates an agent instance from the given spawn specification.
    /// </summary>
    /// <param name="spec">Agent spawn specification containing domain, goal, and constraints.</param>
    /// <returns>A fully configured agent instance.</returns>
    /// <exception cref="ArgumentNullException">Thrown if spec is null.</exception>
    IAgent CreateAgent(AgentSpawnSpecDto spec);
}
