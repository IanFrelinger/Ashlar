using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Ashlar.Orchestration.Agents;
using Ashlar.Abstractions.Agents;

namespace Ashlar.Orchestration.Coordination;

/// <summary>
/// Tracks progress of agent execution and detects issues like thrashing and stalling.
/// 
/// Responsibilities:
/// - Monitors agent state transitions
/// - Detects stalling (agents stuck in same state too long)
/// - Detects thrashing (excessive state transitions)
/// - Provides progress summaries for reporting
/// - Tracks agent health and execution metrics
/// 
/// Thread-safe implementation using concurrent collections.
/// </summary>
public sealed class ProgressTracker
{
    private readonly ILogger<ProgressTracker> _logger;
    private readonly RunState _rootState = new();
    private readonly AsyncLocal<RunState?> _runState = new();

    private RunState State => _runState.Value ?? _rootState;
    private readonly TimeSpan _stallThreshold = TimeSpan.FromMinutes(5);
    private readonly int _thrashingThreshold = 5;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProgressTracker"/> class.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    public ProgressTracker(ILogger<ProgressTracker> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Starts isolated progress tracking for an orchestration run.
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
        internal readonly ConcurrentDictionary<string, AgentProgress> AgentProgress = new();
        internal readonly ConcurrentDictionary<string, List<AgentState>> StateHistory = new();
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
    /// Records progress for an agent.
    /// 
    /// Updates agent state history and checks for stalling and thrashing issues.
    /// Thread-safe operation using concurrent collections.
    /// </summary>
    /// <param name="container">The agent container to record progress for.</param>
    /// <exception cref="ArgumentNullException">Thrown if container is null.</exception>
    public void RecordProgress(AgentContainer container)
    {
        if (container == null)
        {
            throw new ArgumentNullException(nameof(container));
        }

        var agentId = container.AgentId;
        var currentState = container.State;

        // Track state history (thread-safe)
        var history = State.StateHistory.GetOrAdd(agentId, _ => new List<AgentState>());
        if (history.Count == 0 || history.Last() != currentState)
        {
            history.Add(currentState);
        }

        // Update progress (thread-safe)
        var progress = State.AgentProgress.AddOrUpdate(
            agentId,
            new AgentProgress
            {
                AgentId = agentId,
                FirstSeen = DateTimeOffset.UtcNow,
                LastUpdate = DateTimeOffset.UtcNow,
                CurrentState = currentState,
                Health = container.Health
            },
            (key, existing) =>
            {
                existing.LastUpdate = DateTimeOffset.UtcNow;
                existing.CurrentState = currentState;
                existing.Health = container.Health;
                return existing;
            });

        // Check for issues (thread-safe access to history)
        lock (history)
        {
            if (history.Count == 0 || history.Last() != currentState)
            {
                history.Add(currentState);
            }
            CheckThrashing(agentId, history);
        }
        CheckStalling(agentId, progress);
    }

    /// <summary>
    /// Checks if the system has converged (all agents completed or failed).
    /// </summary>
    /// <param name="agents">The list of agent containers to check.</param>
    /// <returns>True if all agents are in Completed, Failed, or Terminated state, false otherwise.</returns>
    public bool HasConverged(IReadOnlyList<AgentContainer> agents)
    {
        return agents.All(container =>
            container.State == AgentState.Completed ||
            container.State == AgentState.Failed ||
            container.State == AgentState.Terminated);
    }

    /// <summary>
    /// Gets progress summary for all agents.
    /// 
    /// Calculates statistics including:
    /// - Counts by state (completed, failed, executing, waiting, ready)
    /// - Progress percentage
    /// - Stalling and thrashing agent counts
    /// - Average execution time
    /// </summary>
    /// <param name="agents">The list of agent containers to summarize.</param>
    /// <returns>A <see cref="ProgressSummary"/> object containing aggregated progress statistics.</returns>
    public ProgressSummary GetSummary(IReadOnlyList<AgentContainer> agents)
    {
        var completed = agents.Count(a => a.State == AgentState.Completed);
        var failed = agents.Count(a => a.State == AgentState.Failed);
        var executing = agents.Count(a => a.State == AgentState.Executing);
        var waiting = agents.Count(a => a.State == AgentState.WaitingForDependencies);
        var ready = agents.Count(a => a.State == AgentState.Ready);
        var agentIds = new HashSet<string>(agents.Select(agent => agent.AgentId), StringComparer.Ordinal);
        var stalling = GetStallingAgents().Count(agentIds.Contains);
        var thrashing = GetThrashingAgents().Count(agentIds.Contains);

        return new ProgressSummary
        {
            TotalAgents = agents.Count,
            Completed = completed,
            Failed = failed,
            Executing = executing,
            WaitingForDependencies = waiting,
            Ready = ready,
            ProgressPercentage = agents.Count > 0 ? (double)completed / agents.Count : 0.0,
            StallingAgents = stalling,
            ThrashingAgents = thrashing,
            AverageExecutionTime = CalculateAverageExecutionTime(agents)
        };
    }

    /// <summary>
    /// Calculates the average execution time for completed agents.
    /// </summary>
    /// <param name="agents">The list of agent containers to calculate from.</param>
    /// <returns>The average execution time, or null if no agents have completed.</returns>
    private TimeSpan? CalculateAverageExecutionTime(IReadOnlyList<AgentContainer> agents)
    {
        var completedAgents = State.AgentProgress.Values
            .Where(p => agents.Any(a => a.AgentId == p.AgentId && a.State == AgentState.Completed))
            .ToList();

        if (completedAgents.Count == 0)
        {
            return null;
        }

        var totalDuration = completedAgents
            .Select(p => (p.LastUpdate - p.FirstSeen).TotalMilliseconds)
            .Sum();

        return TimeSpan.FromMilliseconds(totalDuration / completedAgents.Count);
    }

    /// <summary>
    /// Gets agents that are stalling (no progress for threshold time).
    /// 
    /// An agent is considered stalling if it's in Executing state and hasn't updated
    /// for longer than the stall threshold (default: 5 minutes).
    /// </summary>
    /// <returns>A read-only list of agent IDs that are stalling.</returns>
    public IReadOnlyList<string> GetStallingAgents()
    {
        var now = DateTimeOffset.UtcNow;
        return State.AgentProgress.Values
            .Where(p => p.CurrentState == AgentState.Executing &&
                       (now - p.LastUpdate) > _stallThreshold)
            .Select(p => p.AgentId)
            .ToList();
    }

    /// <summary>
    /// Gets agents that are thrashing (repeated state oscillation).
    /// 
    /// An agent is considered thrashing if it has oscillated between states
    /// more than the thrashing threshold (default: 5) times.
    /// </summary>
    /// <returns>A read-only list of agent IDs that are thrashing.</returns>
    public IReadOnlyList<string> GetThrashingAgents()
    {
        return State.StateHistory
            .Where(kvp => IsThrashing(kvp.Value))
            .Select(kvp => kvp.Key)
            .ToList();
    }

    /// <summary>
    /// Checks if an agent is thrashing and logs a warning if detected.
    /// </summary>
    /// <param name="agentId">The ID of the agent to check.</param>
    /// <param name="history">The state history for the agent.</param>
    private void CheckThrashing(string agentId, List<AgentState> history)
    {
        if (IsThrashing(history))
        {
            _logger.LogWarning("Agent {AgentId} is thrashing (repeated state oscillation)", agentId);
        }
    }

    /// <summary>
    /// Determines if a state history indicates thrashing behavior.
    /// 
    /// Thrashing is detected when the same state appears repeatedly
    /// (more than the threshold) in recent history.
    /// </summary>
    /// <param name="history">The state history to analyze.</param>
    /// <returns>True if thrashing is detected, false otherwise.</returns>
    private bool IsThrashing(List<AgentState> history)
    {
        if (history.Count < _thrashingThreshold * 2)
        {
            return false;
        }

        // Check for oscillation pattern (e.g., Ready -> Executing -> Ready -> Executing...)
        var recent = history.TakeLast(_thrashingThreshold * 2).ToList();
        var stateCounts = recent.GroupBy(s => s).ToDictionary(g => g.Key, g => g.Count());

        // If we see the same state repeated many times, it might be thrashing
        return stateCounts.Values.Any(count => count >= _thrashingThreshold);
    }

    /// <summary>
    /// Checks if an agent is stalling and logs a warning if detected.
    /// </summary>
    /// <param name="agentId">The ID of the agent to check.</param>
    /// <param name="progress">The progress information for the agent.</param>
    private void CheckStalling(string agentId, AgentProgress progress)
    {
        if (progress.CurrentState == AgentState.Executing)
        {
            var stallDuration = DateTimeOffset.UtcNow - progress.LastUpdate;
            if (stallDuration > _stallThreshold)
            {
                _logger.LogWarning("Agent {AgentId} appears to be stalling (no progress for {Duration})",
                    agentId, stallDuration);
            }
        }
    }
}
