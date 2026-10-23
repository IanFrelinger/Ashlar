using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Ashlar.Orchestration.Agents;
using Ashlar.Abstractions.Agents;
using Ashlar.Orchestration.Architect.Models;

namespace Ashlar.Orchestration.Coordination;

/// <summary>
/// Resolves dependencies between agents and manages execution order.
/// 
/// Responsibilities:
/// - Maintains dependency graph (forward and reverse)
/// - Tracks agent outputs for dependency resolution
/// - Determines which agents are ready to execute
/// - Notifies agents when dependencies are satisfied
/// - Handles circular dependency detection
/// 
/// Uses concurrent collections for thread-safe operations in parallel execution scenarios.
/// </summary>
public sealed class DependencyResolver
{
    private readonly ILogger<DependencyResolver> _logger;
    private readonly RunState _rootState = new();
    private readonly AsyncLocal<RunState?> _runState = new();

    private RunState State => _runState.Value ?? _rootState;

    /// <summary>
    /// Initializes a new instance of the <see cref="DependencyResolver"/> class.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    public DependencyResolver(ILogger<DependencyResolver> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Starts an isolated orchestration run while preserving standalone resolver state.
    /// Call synchronously in the run's execution context before starting child tasks.
    /// </summary>
    internal IDisposable BeginRunScope()
    {
        var previous = _runState.Value;
        _runState.Value = new RunState();
        return new RunScope(_runState, previous);
    }

    private sealed class RunState
    {
        internal readonly ConcurrentDictionary<string, AgentContainer> Agents = new();
        internal readonly ConcurrentDictionary<string, object> Outputs = new();
        internal readonly ConcurrentDictionary<string, HashSet<string>> DependencyGraph = new();
        internal readonly ConcurrentDictionary<string, HashSet<string>> ReverseDependencyGraph = new();
    }

    private sealed class RunScope : IDisposable
    {
        private readonly AsyncLocal<RunState?> _slot;
        private readonly RunState? _previous;
        private bool _disposed;

        internal RunScope(AsyncLocal<RunState?> slot, RunState? previous)
        {
            _slot = slot;
            _previous = previous;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _slot.Value = _previous;
            _disposed = true;
        }
    }

    /// <summary>
    /// Registers an agent and builds the dependency graph.
    /// 
    /// Updates both forward dependency graph (agent → dependencies) and
    /// reverse dependency graph (dependency → dependents) for efficient lookups.
    /// </summary>
    /// <param name="container">Agent container to register</param>
    /// <exception cref="ArgumentNullException">Thrown if container is null</exception>
    public void RegisterAgent(AgentContainer container)
    {
        if (container == null)
        {
            throw new ArgumentNullException(nameof(container));
        }

        var agentId = container.AgentId;
        State.Agents[agentId] = container;

        // Build effective dependency graph from explicit dependencies + chain-of-command supervisor.
        var effectiveDependencies = BuildEffectiveDependencies(container.Agent.Spec);
        State.DependencyGraph[agentId] = effectiveDependencies;
        
        // Build reverse dependency graph (who depends on this agent) - thread-safe
        foreach (var dep in effectiveDependencies)
        {
            State.ReverseDependencyGraph.AddOrUpdate(
                dep,
                new HashSet<string> { agentId },
                (key, existing) =>
                {
                    lock (existing)
                    {
                        existing.Add(agentId);
                    }
                    return existing;
                });
        }

        _logger.LogDebug("Registered agent {AgentId} with {DependencyCount} dependencies", 
            agentId, effectiveDependencies.Count);
    }

    /// <summary>
    /// Gets agents that are ready to execute (all dependencies resolved).
    /// 
    /// An agent is ready if:
    /// - All its dependencies have outputs recorded
    /// - The agent is in Ready or Executing state (not Failed/Completed/Terminated)
    /// </summary>
    /// <returns>A read-only list of agent containers that are ready to execute.</returns>
    public IReadOnlyList<AgentContainer> GetReadyAgents()
    {
        return State.Agents.Values
            .Where(container =>
            {
                var agentId = container.AgentId;
                var dependencies = GetDependenciesForAgent(agentId);

                // Agent is ready if:
                // 1. All dependencies have outputs
                // 2. Agent is in Ready or Executing state (not Failed/Completed/Terminated)
                return dependencies.All(dep => State.Outputs.ContainsKey(dep)) &&
                       (container.State == AgentState.Ready || container.State == AgentState.Executing);
            })
            .ToList();
    }

    /// <summary>
    /// Gets agents that are blocked (waiting for dependencies).
    /// Includes both direct and transitive dependencies.
    /// </summary>
    /// <returns>A read-only list of agent containers that are blocked waiting for dependencies.</returns>
    public IReadOnlyList<AgentContainer> GetBlockedAgents()
    {
        return State.Agents.Values
            .Where(container =>
            {
                // Check if all dependencies (including transitive) are resolved
                var allResolved = AreAllDependenciesResolved(container.AgentId, new HashSet<string>());
                return !allResolved && container.State != AgentState.Completed;
            })
            .ToList();
    }

    /// <summary>
    /// Gets the blocking chain for an agent (which dependencies are missing).
    /// </summary>
    /// <param name="agentId">The ID of the agent to check.</param>
    /// <returns>A read-only list of agent IDs that are blocking this agent's execution.</returns>
    public IReadOnlyList<string> GetBlockingDependencies(string agentId)
    {
        if (!State.Agents.TryGetValue(agentId, out var container))
        {
            return Array.Empty<string>();
        }

        var blocking = new HashSet<string>();
        CollectBlockingDependencies(agentId, GetDependenciesForAgent(agentId), blocking, new HashSet<string>());
        return blocking.ToList();
    }

    /// <summary>
    /// Recursively collects blocking dependencies for an agent.
    /// </summary>
    /// <param name="agentId">The agent ID to check.</param>
    /// <param name="dependencies">The list of dependencies to check.</param>
    /// <param name="blocking">The set to populate with blocking dependency IDs.</param>
    /// <param name="visited">The set of already visited agent IDs to prevent cycles.</param>
    private void CollectBlockingDependencies(
        string agentId,
        IReadOnlyCollection<string> dependencies,
        HashSet<string> blocking,
        HashSet<string> visited)
    {
        if (visited.Contains(agentId))
        {
            return;
        }
        visited.Add(agentId);

        foreach (var dep in dependencies)
        {
            if (!State.Outputs.ContainsKey(dep))
            {
                blocking.Add(dep);
            }
            else if (State.Agents.TryGetValue(dep, out var depContainer))
            {
                // Check transitive dependencies
                CollectBlockingDependencies(dep, GetDependenciesForAgent(dep), blocking, visited);
            }
        }
    }

    /// <summary>
    /// Records an agent's output and unblocks dependent agents.
    /// Handles transitive dependencies by recursively checking if dependents are now ready.
    /// </summary>
    /// <param name="agentId">The ID of the agent that produced the output.</param>
    /// <param name="output">The output object produced by the agent.</param>
    /// <exception cref="ArgumentException">Thrown if agentId is null or empty.</exception>
    /// <exception cref="ArgumentNullException">Thrown if output is null.</exception>
    public void RecordOutput(string agentId, object output)
    {
        if (string.IsNullOrWhiteSpace(agentId))
        {
            throw new ArgumentException("Agent ID cannot be null or empty", nameof(agentId));
        }

        State.Outputs[agentId] = output ?? throw new ArgumentNullException(nameof(output));

        _logger.LogDebug("Recorded output for agent {AgentId}, unblocking dependent agents", agentId);

        // Notify direct dependents
        if (State.ReverseDependencyGraph.TryGetValue(agentId, out var dependents))
        {
            var newlyUnblocked = new HashSet<string>();
            
            foreach (var dependentId in dependents)
            {
                if (State.Agents.TryGetValue(dependentId, out var dependent))
                {
                    // Check if all dependencies are now resolved (including transitive)
                    if (AreAllDependenciesResolved(dependentId, new HashSet<string>()))
                    {
                        var dependencyOutputs = GetDependencyOutputs(GetDependenciesForAgent(dependentId));
                        _ = dependent.Agent.WaitForDependenciesAsync(dependencyOutputs);
                        newlyUnblocked.Add(dependentId);
                    }
                }
            }

            // Handle transitive dependencies - if a dependent became unblocked, check its dependents
            foreach (var unblockedId in newlyUnblocked)
            {
                NotifyTransitiveDependents(unblockedId, new HashSet<string>());
            }
        }
    }

    /// <summary>
    /// Recursively notifies transitive dependents when an agent becomes unblocked.
    /// </summary>
    /// <param name="agentId">The agent ID that became unblocked.</param>
    /// <param name="visited">The set of already visited agent IDs to prevent infinite recursion.</param>
    private void NotifyTransitiveDependents(string agentId, HashSet<string> visited)
    {
        if (visited.Contains(agentId))
        {
            return; // Prevent infinite recursion
        }
        visited.Add(agentId);

        if (!State.ReverseDependencyGraph.TryGetValue(agentId, out var dependents))
        {
            return;
        }

        foreach (var dependentId in dependents)
        {
            if (State.Agents.TryGetValue(dependentId, out var dependent))
            {
                if (AreAllDependenciesResolved(dependentId, new HashSet<string>()))
                {
                    var dependencyOutputs = GetDependencyOutputs(GetDependenciesForAgent(dependentId));
                    _ = dependent.Agent.WaitForDependenciesAsync(dependencyOutputs);
                    NotifyTransitiveDependents(dependentId, visited);
                }
            }
        }
    }

    /// <summary>
    /// Checks if all dependencies (including transitive) are resolved for an agent.
    /// </summary>
    /// <param name="agentId">The agent ID to check.</param>
    /// <param name="visited">The set of already visited agent IDs to prevent cycles.</param>
    /// <returns>True if all dependencies are resolved, false otherwise.</returns>
    private bool AreAllDependenciesResolved(string agentId, HashSet<string> visited)
    {
        if (visited.Contains(agentId))
        {
            return true; // Circular dependency or already checked
        }
        visited.Add(agentId);

        if (!State.Agents.TryGetValue(agentId, out var container))
        {
            return false;
        }

        var dependencies = GetDependenciesForAgent(agentId);
        foreach (var dep in dependencies)
        {
            // Direct dependency must have output
            if (!State.Outputs.ContainsKey(dep))
            {
                return false;
            }

            // Recursively check transitive dependencies
            if (!AreAllDependenciesResolved(dep, visited))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Gets outputs for all dependencies of an agent.
    /// </summary>
    /// <param name="dependencies">The list of dependency agent IDs.</param>
    /// <returns>A dictionary mapping dependency agent IDs to their outputs.</returns>
    public IReadOnlyDictionary<string, object> GetDependencyOutputs(IReadOnlyList<string> dependencies)
    {
        var result = new Dictionary<string, object>();
        foreach (var dep in dependencies)
        {
            if (State.Outputs.TryGetValue(dep, out var output))
            {
                result[dep] = output;
            }
        }
        return result;
    }

    /// <summary>
    /// Gets effective dependencies for an agent after command-hierarchy enrichment.
    /// </summary>
    /// <param name="agentId">Agent id.</param>
    /// <returns>Effective dependency list for scheduling and resolution.</returns>
    public IReadOnlyList<string> GetDependenciesForAgent(string agentId)
    {
        if (string.IsNullOrWhiteSpace(agentId))
        {
            return Array.Empty<string>();
        }

        if (State.DependencyGraph.TryGetValue(agentId, out var dependencies))
        {
            return dependencies.ToList();
        }

        return Array.Empty<string>();
    }

    /// <summary>
    /// Gets outputs for dependency ids.
    /// </summary>
    /// <param name="dependencies">Dependency ids.</param>
    /// <returns>Resolved dependency outputs map.</returns>
    public IReadOnlyDictionary<string, object> GetDependencyOutputs(IEnumerable<string> dependencies)
    {
        var result = new Dictionary<string, object>();
        foreach (var dep in dependencies)
        {
            if (string.IsNullOrWhiteSpace(dep))
            {
                continue;
            }

            if (State.Outputs.TryGetValue(dep, out var output))
            {
                result[dep] = output;
            }
        }

        return result;
    }

    /// <summary>
    /// Gets the execution order (topological sort) for all agents.
    /// 
    /// Performs a topological sort of the dependency graph to determine
    /// the order in which agents should be executed. Detects and logs circular dependencies.
    /// </summary>
    /// <returns>A read-only list of agent IDs in execution order.</returns>
    public IReadOnlyList<string> GetExecutionOrder()
    {
        var result = new List<string>();
        var visited = new HashSet<string>();
        var inProgress = new HashSet<string>();

        void Visit(string agentId)
        {
            if (visited.Contains(agentId))
            {
                return;
            }

            if (inProgress.Contains(agentId))
            {
                _logger.LogWarning("Circular dependency detected involving agent {AgentId}", agentId);
                return;
            }

            inProgress.Add(agentId);

            if (State.DependencyGraph.TryGetValue(agentId, out var dependencies))
            {
                foreach (var dep in dependencies)
                {
                    if (State.Agents.ContainsKey(dep))
                    {
                        Visit(dep);
                    }
                }
            }

            inProgress.Remove(agentId);
            visited.Add(agentId);
            result.Add(agentId);
        }

        foreach (var agentId in State.Agents.Keys)
        {
            Visit(agentId);
        }

        return result;
    }

    /// <summary>
    /// Checks if all dependencies for an agent are resolved.
    /// </summary>
    /// <param name="agentId">The ID of the agent to check.</param>
    /// <returns>True if all dependencies are resolved, false otherwise.</returns>
    public bool AreDependenciesResolved(string agentId)
    {
        if (!State.Agents.TryGetValue(agentId, out var container))
        {
            return false;
        }

        return GetDependenciesForAgent(agentId).All(dep => State.Outputs.ContainsKey(dep));
    }

    /// <summary>
    /// Gets all resolved outputs from all agents.
    /// </summary>
    /// <returns>A dictionary mapping agent IDs to their outputs.</returns>
    public IReadOnlyDictionary<string, object> GetAllOutputs()
    {
        return State.Outputs;
    }

    /// <summary>
    /// Clears all registered agents and outputs.
    /// </summary>
    public void Clear()
    {
        State.Agents.Clear();
        State.Outputs.Clear();
        State.DependencyGraph.Clear();
        State.ReverseDependencyGraph.Clear();
    }

    private static HashSet<string> BuildEffectiveDependencies(AgentSpawnSpec spec)
    {
        var dependencies = new HashSet<string>(
            spec.Dependencies.Where(dep => !string.IsNullOrWhiteSpace(dep)),
            StringComparer.OrdinalIgnoreCase);

        var supervisor = ResolveSupervisor(spec);
        if (!string.IsNullOrWhiteSpace(supervisor) &&
            !string.Equals(spec.AgentId, supervisor, StringComparison.OrdinalIgnoreCase))
        {
            dependencies.Add(supervisor);
        }

        return dependencies;
    }

    private static string? ResolveSupervisor(AgentSpawnSpec spec)
    {
        if (!string.IsNullOrWhiteSpace(spec.ReportsToAgentId))
        {
            return spec.ReportsToAgentId;
        }

        return spec.CommandChain.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));
    }
}

