using System.Collections.Concurrent;
using Ashlar.Commercial.Fleet.Contracts.Models;
using Ashlar.Commercial.Fleet.Contracts.Ports;

namespace Ashlar.Commercial.Fleet.Infrastructure;

/// <summary>
/// Thread-safe in-memory mesh task store (Phase 1); Phase 3 adds idempotency key index.
/// </summary>
public sealed class InMemoryMeshTaskRegistry : IMeshTaskRegistry
{
    private readonly ConcurrentDictionary<string, MeshTaskState> _tasks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _idempotencyToTaskId = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>Creates async.</summary>
    public async Task<MeshTaskState> CreateAsync(MeshTaskCreateSpec spec, CancellationToken cancellationToken = default)
    {
        var id = $"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}";
        var correlation = string.IsNullOrWhiteSpace(spec.CorrelationId) ? null : spec.CorrelationId.Trim();
        var idem = string.IsNullOrWhiteSpace(spec.IdempotencyKey) ? null : spec.IdempotencyKey.Trim();

        var task = new MeshTaskState(
            TaskId: id,
            Name: spec.Name,
            Steps: Math.Max(1, spec.Steps),
            RequiredBrickIds: spec.RequiredBrickIds ?? Array.Empty<string>(),
            Affinity: spec.Affinity ?? new Dictionary<string, string>(),
            Priority: spec.Priority,
            DeadlineUtc: spec.DeadlineUtc,
            Status: MeshTaskStatus.Pending,
            AssignedPeerId: null,
            AssignedApiBaseUrl: null,
            PlacementReason: null,
            AttemptCount: 0,
            CreatedAtUtc: DateTimeOffset.UtcNow,
            LastScheduledAtUtc: null,
            CorrelationId: correlation,
            IdempotencyKey: idem,
            LastScheduleIdempotencyKey: null,
            ResultSummary: null,
            ResultHandle: null,
            LeaseToken: null,
            LeaseOwnerPeerId: null,
            LeaseExpiresUtc: null,
            CheckpointHandle: null);

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (idem is not null)
            {
                if (_idempotencyToTaskId.TryGetValue(idem, out var existingId) &&
                    _tasks.TryGetValue(existingId, out var existingTask))
                {
                    return existingTask;
                }

                _idempotencyToTaskId[idem] = id;
            }

            _tasks[id] = task;
            return task;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Attempts to get by idempotency key async.</summary>
    public Task<MeshTaskState?> TryGetByIdempotencyKeyAsync(string idempotencyKey, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            return Task.FromResult<MeshTaskState?>(null);
        var key = idempotencyKey.Trim();
        return Task.FromResult(
            _idempotencyToTaskId.TryGetValue(key, out var taskId) && _tasks.TryGetValue(taskId, out var t)
                ? t
                : null);
    }

    /// <summary>Gets async.</summary>
    public async Task<MeshTaskState?> GetAsync(string taskId, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _tasks.TryGetValue(taskId, out var t) ? t : null;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>List async operation.</summary>
    public async Task<IReadOnlyList<MeshTaskState>> ListAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _tasks.Values
                .OrderByDescending(t => t.Priority)
                .ThenByDescending(t => t.CreatedAtUtc)
                .ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Update async operation.</summary>
    /// <param name="taskId">Task id.</param>
    /// <param name="transform">Applied to the stored state under this registry's lock; null declines.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What happened and the state the store holds.</returns>
    public async Task<MeshTaskUpdateResult> UpdateAsync(
        string taskId,
        Func<MeshTaskState, MeshTaskState?> transform,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(taskId))
            throw new ArgumentException("taskId is required.", nameof(taskId));
        if (transform is null)
            throw new ArgumentNullException(nameof(transform));

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_tasks.TryGetValue(taskId, out var current))
                return new MeshTaskUpdateResult(MeshTaskUpdateOutcome.NotFound, null);

            var next = transform(current);
            if (next is null)
                return new MeshTaskUpdateResult(MeshTaskUpdateOutcome.PreconditionFailed, current);

            if (!string.Equals(next.TaskId, current.TaskId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"a mesh task transform may not change TaskId ('{taskId}' -> '{next.TaskId}'). "
                    + "The LiteDB registry addresses the write by the id the read used and would "
                    + "otherwise leave two documents; this store refuses the same shape so the two "
                    + "implementations cannot diverge.");
            }

            _tasks[next.TaskId] = next;
            return new MeshTaskUpdateResult(MeshTaskUpdateOutcome.Applied, next);
        }
        finally
        {
            _lock.Release();
        }
    }
}
