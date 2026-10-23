using Microsoft.Extensions.Logging;
using Ashlar.Abstractions.Agents;
using System.Collections.Concurrent;

namespace Ashlar.Orchestration.Agents;

/// <summary>
/// Monitors health and status of agents.
/// 
/// Responsibilities:
/// - Registers agents for health monitoring
/// - Periodically checks agent health status
/// - Tracks agent state and health transitions
/// - Provides health status queries
/// 
/// Used by LifecycleManager to monitor agent health.
/// Thread-safe implementation using concurrent collections.
/// </summary>
public sealed class HealthMonitor
{
    private readonly ILogger<HealthMonitor> _logger;
    // Keep every run in one collection so the timer and aggregate queries can see all live agents.
    // Only ID-based operations use the ambient run identity; an unscoped caller retains root semantics.
    private readonly ConcurrentDictionary<(object? Scope, string AgentId), AgentHealthStatus> _agentHealth = new();
    private readonly AsyncLocal<object?> _runScope = new();
    private readonly Timer? _healthCheckTimer;

    /// <summary>
    /// Initializes a new instance of the <see cref="HealthMonitor"/> class.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="healthCheckInterval">Interval between health checks (default: 30 seconds).</param>
    public HealthMonitor(ILogger<HealthMonitor> logger, TimeSpan? healthCheckInterval = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        
        var interval = healthCheckInterval ?? TimeSpan.FromSeconds(30);
        _healthCheckTimer = new Timer(PerformHealthChecks, null, interval, interval);
    }

    internal IDisposable BeginRunScope()
    {
        var previous = _runScope.Value;
        _runScope.Value = new object();
        return new RunScope(this, previous);
    }

    private sealed class RunScope : IDisposable
    {
        private readonly HealthMonitor _owner;
        private readonly object? _previous;
        private int _disposed;

        public RunScope(HealthMonitor owner, object? previous)
        {
            _owner = owner;
            _previous = previous;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _owner._runScope.Value = _previous;
        }
    }

    private (object? Scope, string AgentId) GetKey(string agentId)
    {
        ArgumentNullException.ThrowIfNull(agentId);
        return (_runScope.Value, agentId);
    }

    /// <summary>
    /// Registers an agent for health monitoring.
    /// 
    /// Adds the agent to the health monitoring dictionary and starts tracking its health status.
    /// </summary>
    /// <param name="container">The agent container to monitor.</param>
    public void RegisterAgent(AgentContainer container)
    {
        _agentHealth[GetKey(container.AgentId)] = new AgentHealthStatus
        {
            AgentId = container.AgentId,
            Container = container,
            LastHealthCheck = DateTimeOffset.UtcNow,
            Health = container.Health,
            State = container.State
        };

        _logger.LogDebug("Registered agent {AgentId} for health monitoring", container.AgentId);
    }

    /// <summary>Removes this exact container's health registration from the current run.</summary>
    /// <param name="container">The container owned by the run being cleaned up.</param>
    internal void UnregisterAgent(AgentContainer container)
    {
        var key = GetKey(container.AgentId);
        if (_agentHealth.TryGetValue(key, out var status) && ReferenceEquals(status.Container, container))
        {
            ((ICollection<KeyValuePair<(object? Scope, string AgentId), AgentHealthStatus>>)_agentHealth)
                .Remove(new(key, status));
        }
    }

    /// <summary>
    /// Unregisters an agent from health monitoring.
    /// 
    /// Removes the agent from the health monitoring dictionary.
    /// </summary>
    /// <param name="agentId">The ID of the agent to unregister.</param>
    public void UnregisterAgent(string agentId)
    {
        _agentHealth.TryRemove(GetKey(agentId), out _);
        _logger.LogDebug("Unregistered agent {AgentId} from health monitoring", agentId);
    }

    /// <summary>
    /// Gets the health status of an agent in the current lifecycle run scope, or the root outside a scope.
    /// </summary>
    /// <param name="agentId">The ID of the agent to get health status for.</param>
    /// <returns>The agent health status, or null if the agent is not registered.</returns>
    public AgentHealthStatus? GetHealthStatus(string agentId)
    {
        return _agentHealth.TryGetValue(GetKey(agentId), out var status) ? status : null;
    }

    /// <summary>
    /// Gets health status of all registered agents.
    /// </summary>
    /// <returns>A read-only list of all agent health statuses.</returns>
    public IReadOnlyList<AgentHealthStatus> GetAllHealthStatuses()
    {
        return _agentHealth.Values.ToList();
    }

    /// <summary>
    /// Gets agents with unhealthy status.
    /// 
    /// Returns all agents that have AgentHealth.Unhealthy status.
    /// </summary>
    /// <returns>A read-only list of unhealthy agent health statuses.</returns>
    public IReadOnlyList<AgentHealthStatus> GetUnhealthyAgents()
    {
        return _agentHealth.Values
            .Where(s => s.Health == AgentHealth.Unhealthy)
            .ToList();
    }

    private void PerformHealthChecks(object? state)
    {
        foreach (var healthStatus in _agentHealth.Values)
        {
            var agentId = healthStatus.AgentId;
            try
            {
                var container = healthStatus.Container;
                var currentHealth = container.Health;
                var currentState = container.State;

                // Update health status
                healthStatus.Health = currentHealth;
                healthStatus.State = currentState;
                healthStatus.LastHealthCheck = DateTimeOffset.UtcNow;

                // Check for state issues
                if (currentState == AgentState.Failed)
                {
                    _logger.LogWarning("Agent {AgentId} is in Failed state", agentId);
                }

                // Check for stuck states
                if (currentState == AgentState.Executing)
                {
                    var resourceUsage = container.GetResourceUsage();
                    if (resourceUsage.EstimatedComputeSeconds.HasValue &&
                        resourceUsage.Duration.TotalSeconds > resourceUsage.EstimatedComputeSeconds.Value * 2)
                    {
                        _logger.LogWarning("Agent {AgentId} has exceeded estimated compute time", agentId);
                        healthStatus.Health = AgentHealth.Degraded;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking health for agent {AgentId}", agentId);
            }
        }
    }

    public void Dispose()
    {
        _healthCheckTimer?.Dispose();
    }
}
