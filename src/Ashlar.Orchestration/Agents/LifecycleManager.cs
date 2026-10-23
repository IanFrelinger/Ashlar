using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Ashlar.Abstractions.Agents;

namespace Ashlar.Orchestration.Agents;

/// <summary>
/// Manages agent lifecycle: initialization, execution, and graceful shutdown.
/// 
/// Responsibilities:
/// - Registers and initializes agents
/// - Executes agents with dependency outputs
/// - Shuts down agents gracefully
/// - Supports hot-reload of agent definitions
/// - Tracks active agents
/// - Integrates with HealthMonitor for health tracking
/// 
/// Used by Orchestrator to manage the lifecycle of all spawned agents.
/// </summary>
public sealed class LifecycleManager
{
    private readonly ILogger<LifecycleManager> _logger;

    // Concurrent, because concurrent orchestrations share one manager. A plain Dictionary was mutated
    // by one orchestration while another enumerated it, and ShutdownAllAsync then handed a null agent
    // id to TryGetValue:
    //   ArgumentNullException: Value cannot be null. (Parameter 'key')
    //     at LifecycleManager.ShutdownAgentAsync -> ShutdownAllAsync -> Orchestrator.OrchestrateAsync
    // Every task submitted in that burst came back success=False. Found by UAT tier 10.
    private readonly ConcurrentDictionary<string, AgentContainer> _rootAgents = new();

    // ...and scoped, because making the map thread-safe stopped the corruption without making
    // concurrent orchestration CORRECT. Decomposition ids are not unique between runs -- the parser's
    // fallback emits "fallback-1" every time -- so two runs registered different container instances
    // under one id. The later registration won, GetAgent handed a run the OTHER run's container, and
    // execution failed with "cannot execute from state Executing"; ShutdownAllAsync then tore down
    // both runs' agents. Roughly half of every concurrent burst failed.
    //
    // A run scope flows with the async context, so the orchestrator AND anything it awaits (notably
    // IAgentTransport, which resolves agents by id from this same manager) see that run's agents
    // without a single signature changing. Outside a scope, behaviour is exactly as before.
    private readonly AsyncLocal<ConcurrentDictionary<string, AgentContainer>?> _runScope = new();

    private ConcurrentDictionary<string, AgentContainer> ActiveAgents => _runScope.Value ?? _rootAgents;
    private readonly HealthMonitor _healthMonitor;
    private readonly CancellationTokenSource _shutdownCts = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="LifecycleManager"/> class.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="healthMonitor">The health monitor for tracking agent health.</param>
    public LifecycleManager(ILogger<LifecycleManager> logger, HealthMonitor healthMonitor)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _healthMonitor = healthMonitor ?? throw new ArgumentNullException(nameof(healthMonitor));
    }

    /// <summary>
    /// Isolates every agent registered inside the returned scope from other concurrent runs.
    /// </summary>
    /// <remarks>
    /// Open one per orchestration. Registration, lookup, and <see cref="ShutdownAllAsync"/> then apply
    /// to that run alone, so two runs can use the same agent ids — which they do, because decomposition
    /// ids are not unique between runs — without overwriting each other's containers or shutting down
    /// each other's agents. The scope flows with the async context, so anything the run awaits sees it
    /// too. Disposing restores whatever was in effect before, so nesting is safe.
    /// </remarks>
    public IDisposable BeginRunScope()
    {
        var previous = _runScope.Value;
        _runScope.Value = new ConcurrentDictionary<string, AgentContainer>();
        return new RunScope(this, previous, _healthMonitor.BeginRunScope());
    }

    private sealed class RunScope : IDisposable
    {
        private readonly LifecycleManager _owner;
        private readonly ConcurrentDictionary<string, AgentContainer>? _previous;
        private readonly IDisposable _healthScope;
        private int _disposed;

        public RunScope(LifecycleManager owner, ConcurrentDictionary<string, AgentContainer>? previous, IDisposable healthScope)
        {
            _owner = owner;
            _previous = previous;
            _healthScope = healthScope;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _owner._runScope.Value = _previous;
            _healthScope.Dispose();
        }
    }

    /// <summary>
    /// Registers and initializes an agent.
    ///
    /// Registers the agent in the active agents dictionary, initializes it,
    /// and registers it with the health monitor for health tracking.
    /// </summary>
    /// <param name="handle">The agent handle to register (in-process handles wrap <see cref="AgentContainer"/>).</param>
    /// <param name="cancellationToken">A token to observe while waiting for the task to complete.</param>
    /// <returns>The registered agent container.</returns>
    /// <exception cref="ArgumentNullException">Thrown if handle is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the handle is not backed by an in-process container.</exception>
    public async Task<AgentContainer> RegisterAgentAsync(
        IAgentHandle handle,
        CancellationToken cancellationToken = default)
    {
        if (handle == null)
        {
            throw new ArgumentNullException(nameof(handle));
        }

        var container = handle.TryGetInProcessContainer()
            ?? throw new InvalidOperationException(
                "Only in-process agent handles are supported by LifecycleManager; use InProcessAgentHandle.");

        _logger.LogInformation("Registering agent {AgentId}", container.AgentId);

        try
        {
            await handle.InitializeAsync(cancellationToken);
            ActiveAgents[container.AgentId] = container;
            _healthMonitor.RegisterAgent(container);
            
            _logger.LogInformation("Agent {AgentId} registered and initialized", container.AgentId);
            return container;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to register agent {AgentId}", container.AgentId);
            throw;
        }
    }

    /// <summary>
    /// Registers an in-process agent from its <see cref="AgentContainer"/>.
    /// </summary>
    public Task<AgentContainer> RegisterAgentAsync(
        AgentContainer container,
        CancellationToken cancellationToken = default) =>
        RegisterAgentAsync(new InProcessAgentHandle(container), cancellationToken);

    /// <summary>
    /// Executes an agent with dependency outputs.
    /// 
    /// Executes the agent's ExecuteAsync method with the provided dependency outputs.
    /// The agent must be registered before execution.
    /// </summary>
    /// <param name="agentId">The ID of the agent to execute.</param>
    /// <param name="dependencyOutputs">Optional dictionary of dependency agent IDs to their outputs.</param>
    /// <param name="cancellationToken">A token to observe while waiting for the task to complete.</param>
    /// <returns>The agent's execution output.</returns>
    /// <exception cref="InvalidOperationException">Thrown if agent is not registered.</exception>
    public async Task<object> ExecuteAgentAsync(
        string agentId,
        IReadOnlyDictionary<string, object>? dependencyOutputs = null,
        CancellationToken cancellationToken = default)
    {
        if (!ActiveAgents.TryGetValue(agentId, out var container))
        {
            throw new InvalidOperationException($"Agent {agentId} is not registered");
        }

        _logger.LogInformation("Executing agent {AgentId}", agentId);

        try
        {
            var result = await container.ExecuteAsync(dependencyOutputs, cancellationToken);
            _logger.LogInformation("Agent {AgentId} completed execution", agentId);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Agent {AgentId} execution failed", agentId);
            throw;
        }
    }

    /// <summary>
    /// Shuts down an agent gracefully.
    /// 
    /// Calls the agent's ShutdownAsync method, removes it from active agents,
    /// and unregisters it from the health monitor. If graceful shutdown fails,
    /// the agent is force-terminated.
    /// </summary>
    /// <param name="agentId">The ID of the agent to shutdown.</param>
    /// <param name="cancellationToken">A token to observe while waiting for the task to complete.</param>
    /// <returns>A task that represents the asynchronous shutdown operation.</returns>
    public async Task ShutdownAgentAsync(string agentId, CancellationToken cancellationToken = default)
    {
        // A concurrently-mutated Dictionary handed this method a null id, which TryGetValue turns into
        // an ArgumentNullException that failed the whole orchestration. The map is concurrent now, so
        // this should be unreachable — it stays because "shut down nothing" is the right answer to a
        // missing id, and an orchestration must not die over one.
        if (string.IsNullOrEmpty(agentId))
        {
            _logger.LogWarning("Attempted to shutdown an agent with no id");
            return;
        }

        if (!ActiveAgents.TryGetValue(agentId, out var container))
        {
            _logger.LogWarning("Attempted to shutdown unregistered agent {AgentId}", agentId);
            return;
        }

        _logger.LogInformation("Shutting down agent {AgentId}", agentId);

        try
        {
            await container.ShutdownAsync(cancellationToken);
            ActiveAgents.TryRemove(agentId, out _);
            _healthMonitor.UnregisterAgent(agentId);
            _logger.LogInformation("Agent {AgentId} shut down successfully", agentId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error shutting down agent {AgentId}", agentId);
            // Force termination if graceful shutdown fails
            container.Terminate();
            ActiveAgents.TryRemove(agentId, out _);
            _healthMonitor.UnregisterAgent(agentId);
            throw;
        }
    }

    /// <summary>
    /// Shuts down all agents gracefully.
    /// 
    /// Shuts down all registered agents in parallel using Task.WhenAll.
    /// Used during system shutdown or cleanup operations.
    /// </summary>
    /// <param name="cancellationToken">A token to observe while waiting for the task to complete.</param>
    /// <returns>A task that represents the asynchronous shutdown operation.</returns>
    public async Task ShutdownAllAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Shutting down all agents ({Count} agents)", ActiveAgents.Count);

        // Snapshot first: another orchestration may register or remove agents while this one shuts down.
        var shutdownTasks = ActiveAgents.Keys.ToArray()
            .Select(agentId => ShutdownAgentAsync(agentId, cancellationToken))
            .ToArray();

        await Task.WhenAll(shutdownTasks);

        _logger.LogInformation("All agents shut down");
    }

    // Only the orchestrator uses this bounded ownership cleanup. Public ShutdownAllAsync retains
    // its existing semantics. A timeout stops waiting and deregisters ownership; it does not kill
    // arbitrary agent work that ignores cancellation.
    internal async Task<Exception?> CleanupRunAsync(TimeSpan? timeout = null)
    {
        var budget = timeout ?? TimeSpan.FromSeconds(5);
        if (budget <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        var agents = ActiveAgents;
        var snapshot = agents.ToArray();
        if (snapshot.Length == 0) return null;
        var cancellation = new CancellationTokenSource(budget);
        var token = cancellation.Token;
        // Task.Run also bounds an implementation that blocks synchronously before returning a Task.
        var shutdown = Task.WhenAll(snapshot.Select(pair => Task.Run(
            () => pair.Value.ShutdownAsync(token))));
        // A timeout can return before shutdown finishes. Observe any subsequent task fault.
        _ = shutdown.ContinueWith(task =>
        {
            if (task.IsFaulted) _ = task.Exception;
            cancellation.Dispose();
        }, CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        try
        {
            await shutdown.WaitAsync(budget).ConfigureAwait(false);
            return null;
        }
        catch (Exception error)
        {
            _logger.LogWarning(error, "Run cleanup did not finish gracefully within {Budget}", budget);
            return error;
        }
        finally
        {
            foreach (var pair in snapshot)
            {
                ((ICollection<KeyValuePair<string, AgentContainer>>)agents).Remove(pair);
                _healthMonitor.UnregisterAgent(pair.Value);
            }
        }
    }

    /// <summary>
    /// Gets all active agents.
    /// 
    /// Returns a snapshot of all currently registered agents.
    /// </summary>
    /// <returns>A read-only list of all active agent containers.</returns>
    public IReadOnlyList<AgentContainer> GetActiveAgents()
    {
        return ActiveAgents.Values.ToList();
    }

    /// <summary>
    /// Gets an active agent by ID.
    /// </summary>
    /// <param name="agentId">The ID of the agent to retrieve.</param>
    /// <returns>The agent container if found, null otherwise.</returns>
    public AgentContainer? GetAgent(string agentId)
    {
        return ActiveAgents.TryGetValue(agentId, out var container) ? container : null;
    }

    /// <summary>
    /// Hot-reloads an agent definition (replaces existing agent with new spec).
    /// 
    /// Shuts down the old agent (if it exists) and registers the new agent.
    /// Useful for updating agent behavior without restarting the entire system.
    /// </summary>
    /// <param name="newContainer">The new agent container to replace the old one.</param>
    /// <param name="cancellationToken">A token to observe while waiting for the task to complete.</param>
    /// <returns>The newly registered agent container.</returns>
    /// <exception cref="ArgumentNullException">Thrown if newContainer is null.</exception>
    public async Task<AgentContainer> HotReloadAgentAsync(
        AgentContainer newContainer,
        CancellationToken cancellationToken = default)
    {
        if (newContainer == null)
        {
            throw new ArgumentNullException(nameof(newContainer));
        }

        var agentId = newContainer.AgentId;

        _logger.LogInformation("Hot-reloading agent {AgentId}", agentId);

        // Shutdown old agent if it exists
        if (ActiveAgents.TryGetValue(agentId, out var oldContainer))
        {
            await ShutdownAgentAsync(agentId, cancellationToken);
        }

        // Register and initialize new agent
        return await RegisterAgentAsync(newContainer, cancellationToken);
    }
}

