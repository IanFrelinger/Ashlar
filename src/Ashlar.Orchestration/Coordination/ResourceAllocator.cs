using Microsoft.Extensions.Logging;
using Ashlar.Orchestration.Agents;
using Ashlar.Orchestration.Architect;
using Ashlar.Orchestration.Architect.Models;

namespace Ashlar.Orchestration.Coordination;

/// <summary>
/// Allocates resources (compute, context, memory) to agents based on priority and availability.
/// 
/// Responsibilities:
/// - Manages global resource budget (compute seconds, context tokens, memory)
/// - Allocates resources to agents based on priority
/// - Preempts lower-priority agents when resources are insufficient
/// - Tracks resource usage and availability
/// - Prevents resource exhaustion
/// 
/// Thread-safe implementation using one lock for the global allocation ledger and counters.
/// </summary>
public sealed class ResourceAllocator
{
    private readonly ILogger<ResourceAllocator> _logger;
    private readonly ResourceBudget _budget;
    // One global budget and ledger; run identity only determines ownership of each allocation.
    // Every ledger/counter access is protected by _allocationLock.
    private readonly Dictionary<(RunScope? Scope, string AgentId), ResourceAllocation> _allocations = new();
    private readonly AsyncLocal<RunScope?> _runScope = new();
    private int _allocatedComputeSeconds;
    private int _allocatedContextTokens;
    private int _allocatedMemoryMB;
    private readonly object _allocationLock = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="ResourceAllocator"/> class.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="budget">Optional resource budget. If not provided, uses default values (3600s compute, 200k tokens, 16GB memory).</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if a budget maximum is negative.</exception>
    public ResourceAllocator(ILogger<ResourceAllocator> logger, ResourceBudget? budget = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _budget = budget ?? new ResourceBudget
        {
            MaxComputeSeconds = 3600,
            MaxContextTokens = 200000,
            MaxMemoryMB = 16384
        };
        if (_budget.MaxComputeSeconds is < 0 || _budget.MaxContextTokens is < 0 || _budget.MaxMemoryMB is < 0)
            throw new ArgumentOutOfRangeException(nameof(budget), "Resource budget maxima must be nonnegative or null.");
    }

    internal IDisposable BeginRunScope()
    {
        var scope = new RunScope(this, _runScope.Value);
        _runScope.Value = scope;
        return scope;
    }

    private sealed class RunScope : IDisposable
    {
        private readonly ResourceAllocator _owner;
        private readonly RunScope? _previous;
        // Read and written only under the owner's allocation lock.
        internal bool IsDisposed { get; private set; }

        public RunScope(ResourceAllocator owner, RunScope? previous)
        {
            _owner = owner;
            _previous = previous;
        }

        public void Dispose()
        {
            lock (_owner._allocationLock)
            {
                if (IsDisposed) return;
                IsDisposed = true;
                foreach (var key in _owner._allocations.Keys.Where(key => ReferenceEquals(key.Scope, this)).ToArray())
                {
                    _owner.RemoveAllocation(key);
                }
            }
            _owner._runScope.Value = _previous;
        }
    }

    // The caller holds _allocationLock, so removal and all three counter updates are atomic.
    private ResourceAllocation? RemoveAllocation((RunScope? Scope, string AgentId) key)
    {
        if (!_allocations.Remove(key, out var allocation)) return null;
        _allocatedComputeSeconds -= allocation.AllocatedComputeSeconds;
        _allocatedContextTokens -= allocation.AllocatedContextTokens;
        _allocatedMemoryMB -= allocation.AllocatedMemoryMB;
        return allocation;
    }

    /// <summary>
    /// Allocates resources to an agent based on priority.
    /// Higher priority agents get resources first. If resources are insufficient,
    /// lower priority agents may be preempted. Rejected requests preserve existing reservations.
    /// Negative resource requirements are rejected without changing the allocation ledger.
    /// </summary>
    /// <param name="container">The agent container requesting resources.</param>
    /// <param name="allocation">Output parameter: the resource allocation if successful, null otherwise.</param>
    /// <returns>True if resources were allocated, false if allocation failed.</returns>
    /// <exception cref="ArgumentNullException">Thrown if container is null.</exception>
    public bool TryAllocate(AgentContainer container, out ResourceAllocation? allocation)
    {
        allocation = null;

        if (container == null)
        {
            throw new ArgumentNullException(nameof(container));
        }

        var spec = container.Agent.Spec;
        var requirements = spec.ResourceRequirements;

        var requestedCompute = requirements?.EstimatedComputeSeconds ?? 0;
        var requestedContext = requirements?.RequiredContextTokens ?? 0;
        var requestedMemory = requirements?.RequiredMemoryMB ?? 0;
        ArgumentNullException.ThrowIfNull(container.AgentId);
        if (requestedCompute < 0 || requestedContext < 0 || requestedMemory < 0)
            return false;

        // Check ownership and allocate under the same global lock, including zero-resource requests.
        lock (_allocationLock)
        {
            var scope = _runScope.Value;
            // A child task must not recreate allocations after its owning scope has been cleaned up.
            if (scope?.IsDisposed == true) return false;
            var key = (scope, container.AgentId);
            // Repeated requests for an ID within one run reuse its reservation, never charge twice.
            // Changing requirements requires an explicit release followed by a new allocation.
            if (_allocations.TryGetValue(key, out allocation)) return true;

            if (requirements == null)
            {
                allocation = new ResourceAllocation
                {
                    AgentId = container.AgentId,
                    AllocatedComputeSeconds = 0,
                    AllocatedContextTokens = 0,
                    AllocatedMemoryMB = 0,
                    Priority = spec.Priority
                };
                _allocations[key] = allocation;
                return true;
            }

            var availableCompute = (_budget.MaxComputeSeconds ?? int.MaxValue) - _allocatedComputeSeconds;
            var availableContext = (_budget.MaxContextTokens ?? int.MaxValue) - _allocatedContextTokens;
            var availableMemory = (_budget.MaxMemoryMB ?? int.MaxValue) - _allocatedMemoryMB;

            // If resources are insufficient, try to preempt lower priority agents
            if (requestedCompute > availableCompute ||
                requestedContext > availableContext ||
                requestedMemory > availableMemory)
            {
                // Try to free resources by preempting lower priority agents
                var freed = TryPreemptLowerPriority(spec.Priority,
                    Math.Max(0, requestedCompute - availableCompute),
                    Math.Max(0, requestedContext - availableContext),
                    Math.Max(0, requestedMemory - availableMemory));
                if (!freed)
                {
                    _logger.LogWarning(
                        "Cannot allocate resources for agent {AgentId} (priority {Priority}): requested ({Compute}s, {Context} tokens, {Memory}MB) exceeds available ({AvailableCompute}s, {AvailableContext} tokens, {AvailableMemory}MB)",
                        container.AgentId, spec.Priority, requestedCompute, requestedContext, requestedMemory,
                        availableCompute, availableContext, availableMemory);
                    return false;
                }

                // Recalculate available after preemption
                availableCompute = (_budget.MaxComputeSeconds ?? int.MaxValue) - _allocatedComputeSeconds;
                availableContext = (_budget.MaxContextTokens ?? int.MaxValue) - _allocatedContextTokens;
                availableMemory = (_budget.MaxMemoryMB ?? int.MaxValue) - _allocatedMemoryMB;
            }

            // Allocate resources
            allocation = new ResourceAllocation
            {
                AgentId = container.AgentId,
                AllocatedComputeSeconds = requestedCompute,
                AllocatedContextTokens = requestedContext,
                AllocatedMemoryMB = requestedMemory,
                Priority = spec.Priority
            };

            _allocations[key] = allocation;
            _allocatedComputeSeconds += requestedCompute;
            _allocatedContextTokens += requestedContext;
            _allocatedMemoryMB += requestedMemory;
        }

        _logger.LogDebug("Allocated resources to agent {AgentId} (priority {Priority}): {Compute}s, {Context} tokens, {Memory}MB",
            container.AgentId, spec.Priority, requestedCompute, requestedContext, requestedMemory);

        return true;
    }

    /// <summary>
    /// Attempts to free resources by preempting lower priority agents.
    /// </summary>
    /// <param name="requestingPriority">The priority of the agent requesting resources.</param>
    /// <param name="neededCompute">The compute seconds needed.</param>
    /// <param name="neededContext">The context tokens needed.</param>
    /// <param name="neededMemory">The memory in MB needed.</param>
    /// <returns>True if enough resources were freed, false otherwise.</returns>
    private bool TryPreemptLowerPriority(
        int requestingPriority,
        int neededCompute,
        int neededContext,
        int neededMemory)
    {
        // Get lower priority allocations sorted by priority (lowest first)
        var lowerPriorityAllocations = _allocations
            .Where(entry => entry.Value.Priority < requestingPriority)
            .OrderBy(entry => entry.Value.Priority)
            .ToList();

        var freedCompute = 0;
        var freedContext = 0;
        var freedMemory = 0;
        var victims = new List<(RunScope? Scope, string AgentId)>();

        // Plan under the allocation lock. An infeasible request must leave every reservation intact.
        foreach (var entry in lowerPriorityAllocations)
        {
            if (freedCompute >= neededCompute &&
                freedContext >= neededContext &&
                freedMemory >= neededMemory)
            {
                break;
            }

            victims.Add(entry.Key);
            freedCompute += entry.Value.AllocatedComputeSeconds;
            freedContext += entry.Value.AllocatedContextTokens;
            freedMemory += entry.Value.AllocatedMemoryMB;
        }

        if (freedCompute < neededCompute || freedContext < neededContext || freedMemory < neededMemory)
            return false;

        // The complete plan fits all dimensions: remove only the selected exact owner keys.
        foreach (var key in victims)
        {
            var allocation = RemoveAllocation(key);
            if (allocation is null) continue;
            _logger.LogInformation(
                "Preempted agent {AgentId} (priority {Priority}) to free resources for higher priority agent",
                allocation.AgentId, allocation.Priority);
        }

        return true;
    }

    /// <summary>
    /// Releases resources when an agent completes (frees up resources).
    /// </summary>
    /// <param name="agentId">The ID of the agent whose resources should be released.</param>
    public void ReleaseResources(string agentId)
    {
        ArgumentNullException.ThrowIfNull(agentId);
        ResourceAllocation? allocation;
        lock (_allocationLock)
        {
            allocation = RemoveAllocation((_runScope.Value, agentId));
            if (allocation is null) return;
        }

        _logger.LogDebug("Released resources from agent {AgentId}: {Compute}s, {Context} tokens, {Memory}MB",
            agentId, allocation.AllocatedComputeSeconds, allocation.AllocatedContextTokens, allocation.AllocatedMemoryMB);
    }

    // Metrics for an agent must use the caller's reservation, not another run with the same ID.
    internal ResourceAllocation? GetAllocation(string agentId)
    {
        ArgumentNullException.ThrowIfNull(agentId);
        lock (_allocationLock)
        {
            return _allocations.TryGetValue((_runScope.Value, agentId), out var allocation)
                ? allocation
                : null;
        }
    }

    /// <summary>
    /// Gets current resource usage statistics.
    /// </summary>
    /// <returns>A <see cref="ResourceUsageReport"/> containing current resource usage and utilization metrics.</returns>
    public ResourceUsageReport GetUsageReport()
    {
        lock (_allocationLock)
        {
            return new ResourceUsageReport
            {
                TotalAllocatedComputeSeconds = _allocatedComputeSeconds,
                TotalAllocatedContextTokens = _allocatedContextTokens,
                TotalAllocatedMemoryMB = _allocatedMemoryMB,
                MaxComputeSeconds = _budget.MaxComputeSeconds,
                MaxContextTokens = _budget.MaxContextTokens,
                MaxMemoryMB = _budget.MaxMemoryMB,
                UtilizationCompute = _budget.MaxComputeSeconds is > 0
                    ? (double)_allocatedComputeSeconds / _budget.MaxComputeSeconds.Value
                    : 0.0,
                UtilizationContext = _budget.MaxContextTokens is > 0
                    ? (double)_allocatedContextTokens / _budget.MaxContextTokens.Value
                    : 0.0,
                UtilizationMemory = _budget.MaxMemoryMB is > 0
                    ? (double)_allocatedMemoryMB / _budget.MaxMemoryMB.Value
                    : 0.0
            };
        }
    }

    /// <summary>
    /// Gets all current resource allocations.
    /// </summary>
    /// <returns>A read-only list of all current <see cref="ResourceAllocation"/> instances.</returns>
    public IReadOnlyList<ResourceAllocation> GetAllocations()
    {
        lock (_allocationLock)
        {
            return _allocations.Values.ToList();
        }
    }
}
