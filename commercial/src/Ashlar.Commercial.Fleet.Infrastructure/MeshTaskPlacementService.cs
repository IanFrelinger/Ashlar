using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ashlar.Commercial.Fleet.Contracts.Models;
using Ashlar.Commercial.Fleet.Contracts.Ports;

namespace Ashlar.Commercial.Fleet.Infrastructure;

/// <summary>
/// Greedy placement: eligible non-drained nodes that advertise required bricks and match affinity labels.
/// </summary>
public sealed class MeshTaskPlacementService : IMeshTaskPlacementService
{
    private readonly IFleetNodeRegistry _nodes;
    private readonly IMeshTaskRegistry _tasks;
    private readonly MeshCheckpointOptions _checkpointOptions;
    private readonly string _placementTrustPolicy;
    private readonly ILogger<MeshTaskPlacementService>? _logger;

    public MeshTaskPlacementService(
        IFleetNodeRegistry nodes,
        IMeshTaskRegistry tasks,
        IOptions<MeshCheckpointOptions> checkpointOptions,
        IOptions<MeshPlacementTrustOptions> placementTrustOptions,
        ILogger<MeshTaskPlacementService>? logger = null)
    {
        _nodes = nodes ?? throw new ArgumentNullException(nameof(nodes));
        _tasks = tasks ?? throw new ArgumentNullException(nameof(tasks));
        _checkpointOptions = checkpointOptions?.Value ?? throw new ArgumentNullException(nameof(checkpointOptions));
        _placementTrustPolicy = MeshFleetTrustPolicy.NormalizePolicy(placementTrustOptions?.Value.PeerTrustPolicy);
        _logger = logger;
    }

    public Task<(bool Ok, MeshTaskState? Task, string? Error)> TryScheduleAsync(
        string taskId,
        string? scheduleIdempotencyKey = null,
        string? correlationId = null,
        int? leaseSecondsOverride = null,
        CancellationToken cancellationToken = default)
        => TryPlaceAsync(taskId, skipPreviousPeerId: null, scheduleIdempotencyKey, correlationId, leaseSecondsOverride, cancellationToken);

    public async Task<(bool Ok, MeshTaskState? Task, string? Error)> TryRetryAsync(
        string taskId,
        string? scheduleIdempotencyKey = null,
        string? correlationId = null,
        int? leaseSecondsOverride = null,
        CancellationToken cancellationToken = default)
    {
        var current = await _tasks.GetAsync(taskId, cancellationToken).ConfigureAwait(false);
        var skip = current?.AssignedPeerId;
        return await TryPlaceAsync(taskId, skipPreviousPeerId: skip, scheduleIdempotencyKey, correlationId, leaseSecondsOverride, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Whether the stored document is still the one the placement decision was taken on.
    /// </summary>
    /// <param name="current">The document read inside the write transaction.</param>
    /// <param name="snapshot">What this call last read outside it.</param>
    /// <returns>True when no competing placement has moved the task in between.</returns>
    /// <remarks>
    /// <para>A lease-token comparison on its own is NOT enough here, which is why this is a
    /// four-field test. Three of this file's write sites operate on a task whose <c>LeaseToken</c> is
    /// null on both sides of the race: two concurrent placements of one Pending task both read null,
    /// both satisfy an expect-null precondition, and the second silently overwrites the first's
    /// assignment - so both peers hold a token, both execute, and <c>AttemptCount</c> increments
    /// once instead of twice. The task carries no revision field and adding one would need a
    /// migration for every document already on disk; the four fields a competing placement
    /// necessarily changes are a revision in everything but name.</para>
    ///
    /// <para><c>AttemptCount</c> is the member that makes two concurrent RETRIES distinguishable: a
    /// retry clears the lease on purpose, so the token alone cannot tell the second retry that the
    /// first has already run.</para>
    /// </remarks>
    private static bool PlacementSnapshotStillStands(MeshTaskState current, MeshTaskState snapshot)
        => string.Equals(current.LeaseToken, snapshot.LeaseToken, StringComparison.Ordinal)
           && current.Status == snapshot.Status
           && current.AttemptCount == snapshot.AttemptCount
           && string.Equals(current.AssignedPeerId, snapshot.AssignedPeerId, StringComparison.Ordinal);

    private async Task<(bool Ok, MeshTaskState? Task, string? Error)> TryPlaceAsync(
        string taskId,
        string? skipPreviousPeerId,
        string? scheduleIdempotencyKey,
        string? correlationId,
        int? leaseSecondsOverride,
        CancellationToken cancellationToken)
    {
        var task = await _tasks.GetAsync(taskId, cancellationToken).ConfigureAwait(false);
        if (task is null)
            return (false, null, "task.not_found");

        if (task.Status == MeshTaskStatus.Succeeded)
            return (false, task, "task.already_succeeded");

        if (task.DeadlineUtc is { } d && DateTimeOffset.UtcNow > d)
        {
            var deadlineSnapshot = task;
            var expiredResult = await _tasks.UpdateAsync(
                taskId,
                current => PlacementSnapshotStillStands(current, deadlineSnapshot)
                           && current.DeadlineUtc is { } stillDue
                           && DateTimeOffset.UtcNow > stillDue
                    ? current with { Status = MeshTaskStatus.Failed, PlacementReason = "deadline_exceeded" }
                    : null,
                cancellationToken).ConfigureAwait(false);

            if (expiredResult.Outcome == MeshTaskUpdateOutcome.NotFound)
                return (false, null, "task.not_found");

            // A refusal here means something else moved the task while this call was deciding it was
            // too late. Report the state the store holds, never the one this method composed.
            return (false, expiredResult.State, "task.deadline_exceeded");
        }

        var isRetry = !string.IsNullOrEmpty(skipPreviousPeerId);
        var scheduleKey = string.IsNullOrWhiteSpace(scheduleIdempotencyKey) ? null : scheduleIdempotencyKey.Trim();
        var corr = string.IsNullOrWhiteSpace(correlationId)
            ? task.CorrelationId
            : correlationId.Trim();

        // Active lease + idempotent schedule (Phase 3) or reclaim expired lease (Phase 6).
        if ((task.Status == MeshTaskStatus.Assigned || task.Status == MeshTaskStatus.Running) &&
            !string.IsNullOrEmpty(task.AssignedPeerId))
        {
            var nowUtc = DateTimeOffset.UtcNow;
            if (task.LeaseExpiresUtc is { } exp && nowUtc > exp)
            {
                var reclaimSnapshot = task;
                var reclaimResult = await _tasks.UpdateAsync(
                    taskId,
                    current => PlacementSnapshotStillStands(current, reclaimSnapshot)
                               && current.LeaseExpiresUtc is { } stillExpired
                               && DateTimeOffset.UtcNow > stillExpired
                        ? current with
                        {
                            Status = MeshTaskStatus.Pending,
                            AssignedPeerId = null,
                            AssignedApiBaseUrl = null,
                            LeaseToken = null,
                            LeaseOwnerPeerId = null,
                            LeaseExpiresUtc = null,
                            PlacementReason = "lease.expired_before_reschedule"
                        }
                        : null,
                    cancellationToken).ConfigureAwait(false);

                if (reclaimResult.Outcome == MeshTaskUpdateOutcome.NotFound)
                    return (false, null, "task.not_found");

                // A REFUSAL here is this call losing the race, and it may not be laundered into a
                // fresh premise. Adopting the winner's document as the new placementSnapshot made
                // PlacementSnapshotStillStands pass by construction a few lines below, and Shape()
                // forces Status = Pending from ANY status, so the Succeeded guard at the top of
                // this method was never re-applied: a task that finished in this window was
                // re-placed, reported Ok, and handed a fresh lease. Measured through a decorator
                // that commits the completion in exactly this window: placementOk=True with the
                // stored row left Assigned, attempt 2, and the completion's ResultSummary dangling
                // on it. The other reachable refusal is a lease that is no longer expired because
                // its worker renewed it -- an extension changes only LeaseExpiresUtc, which
                // PlacementSnapshotStillStands does not compare -- and adopting that one re-placed
                // a task with a LIVE lease. Both are the same answer: the premise is void.
                if (reclaimResult.Outcome == MeshTaskUpdateOutcome.PreconditionFailed)
                {
                    _logger?.LogInformation(
                        "mesh placement task={TaskId} lost the reclaim race; the task moved before "
                        + "the expired lease could be cleared",
                        taskId);
                    return (false, reclaimResult.State, "schedule.conflict");
                }

                task = reclaimResult.State ?? task;
            }
            else if (!isRetry && task.Status == MeshTaskStatus.Assigned)
            {
                if (scheduleKey is null)
                    return (true, task with { CorrelationId = corr ?? task.CorrelationId }, null);
                if (string.Equals(task.LastScheduleIdempotencyKey, scheduleKey, StringComparison.Ordinal))
                    return (true, task with { CorrelationId = corr ?? task.CorrelationId }, null);
                return (false, task with { CorrelationId = corr ?? task.CorrelationId }, "schedule.idempotency_conflict");
            }
            else if (!isRetry && task.Status == MeshTaskStatus.Running)
            {
                if (scheduleKey is null)
                    return (true, task with { CorrelationId = corr ?? task.CorrelationId }, null);
                if (string.Equals(task.LastScheduleIdempotencyKey, scheduleKey, StringComparison.Ordinal))
                    return (true, task with { CorrelationId = corr ?? task.CorrelationId }, null);
                return (false, task with { CorrelationId = corr ?? task.CorrelationId }, "schedule.idempotency_conflict");
            }
        }

        // The shaping that used to build `work` off the snapshot, expressed as a function of the
        // document the TRANSACTION reads. It carries no field out of the snapshot except the
        // caller's own correlation id, so nothing a competing writer committed can ride back in.
        MeshTaskState Shape(MeshTaskState cur)
        {
            var shaped = isRetry
                ? cur with
                {
                    Status = MeshTaskStatus.Pending,
                    AssignedPeerId = null,
                    AssignedApiBaseUrl = null,
                    PlacementReason = null,
                    CorrelationId = corr ?? cur.CorrelationId,
                    LeaseToken = null,
                    LeaseOwnerPeerId = null,
                    LeaseExpiresUtc = null
                }
                : cur with { CorrelationId = corr ?? cur.CorrelationId };

            // Unchanged by this fix, deliberately: a Failed task IS placed again by schedule and by
            // retry. That is a semantics question about TryPlaceAsync, not a lost update - it
            // happens on a single fresh read with no concurrency at all - so a concurrency change is
            // the wrong pull request to decide it in, and it is left exactly as it was.
            return shaped.Status == MeshTaskStatus.Pending ? shaped : shaped with { Status = MeshTaskStatus.Pending };
        }

        var placementSnapshot = task;
        var work = Shape(task);

        var nodes = await _nodes.ListAsync(cancellationToken).ConfigureAwait(false);
        var eligible = nodes
            .Where(n => n.Admitted && !n.Drained && !string.IsNullOrWhiteSpace(n.ApiBaseUrl))
            .Where(n => MeshFleetTrustPolicy.IsEligible(n.TrustTier, _placementTrustPolicy))
            .Where(n => MatchesAffinity(n, work.Affinity))
            .Where(n => HasBricks(n, work.RequiredBrickIds))
            .OrderBy(n => n.ReportedQueueDepth)
            .ThenByDescending(n => n.LastHeartbeatUtc ?? DateTimeOffset.MinValue)
            .ThenBy(n => n.PeerId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (eligible.Count == 0)
        {
            var reason = nodes.Any(n => n.Admitted && !n.Drained && !MeshFleetTrustPolicy.IsEligible(n.TrustTier, _placementTrustPolicy))
                ? "placement.trust_policy_blocked"
                : nodes.Any(n => !n.Admitted && !n.Drained)
                    ? "placement.peer_not_admitted"
                    : "placement.no_eligible_nodes";
            _logger?.LogWarning("mesh placement task={TaskId} reason={Reason}", taskId, reason);

            var pendingResult = await _tasks.UpdateAsync(
                taskId,
                current => PlacementSnapshotStillStands(current, placementSnapshot)
                    ? Shape(current) with { Status = MeshTaskStatus.Pending, PlacementReason = reason }
                    : null,
                cancellationToken).ConfigureAwait(false);

            if (pendingResult.Outcome == MeshTaskUpdateOutcome.NotFound)
                return (false, null, "task.not_found");
            if (pendingResult.Outcome == MeshTaskUpdateOutcome.PreconditionFailed)
            {
                // The reason is still the true reason this attempt found nowhere to put the task. It
                // is simply not what is on disk, because another writer got there first.
                _logger?.LogDebug(
                    "mesh placement task={TaskId} reason={Reason} not recorded: lost the write race",
                    taskId,
                    reason);
            }

            return (false, pendingResult.State, reason);
        }

        var candidates = string.IsNullOrEmpty(skipPreviousPeerId)
            ? eligible
            : eligible.Where(n => !string.Equals(n.PeerId, skipPreviousPeerId, StringComparison.OrdinalIgnoreCase)).ToList();
        if (candidates.Count == 0)
            candidates = eligible;

        var pick = candidates[0];
        var leaseSeconds = leaseSecondsOverride.HasValue
            ? Math.Clamp(leaseSecondsOverride.Value, 60, 86_400)
            : Math.Max(60, _checkpointOptions.LeaseSeconds);
        var leaseToken = Guid.NewGuid().ToString("N");
        var leaseUntil = DateTimeOffset.UtcNow.AddSeconds(leaseSeconds);
        var apiBaseUrl = NormalizeBaseUrl(pick.ApiBaseUrl);

        // THE write this change exists for. The node pick stays outside the transform because it
        // awaits the node registry; the precondition does not, so two placements racing on one task
        // can no longer both mint a lease token and leave the loser's peer executing a task the
        // document no longer says it owns.
        var assignResult = await _tasks.UpdateAsync(
            taskId,
            current => PlacementSnapshotStillStands(current, placementSnapshot)
                ? Shape(current) with
                {
                    Status = MeshTaskStatus.Assigned,
                    AssignedPeerId = pick.PeerId,
                    AssignedApiBaseUrl = apiBaseUrl,
                    PlacementReason = "placement.ok",
                    AttemptCount = current.AttemptCount + 1,
                    LastScheduledAtUtc = DateTimeOffset.UtcNow,
                    LastScheduleIdempotencyKey = scheduleKey,
                    LeaseToken = leaseToken,
                    LeaseOwnerPeerId = pick.PeerId,
                    LeaseExpiresUtc = leaseUntil,
                    CheckpointHandle = null
                }
                : null,
            cancellationToken).ConfigureAwait(false);

        switch (assignResult.Outcome)
        {
            case MeshTaskUpdateOutcome.NotFound:
                return (false, null, "task.not_found");
            case MeshTaskUpdateOutcome.PreconditionFailed:
                _logger?.LogInformation(
                    "mesh placement task={TaskId} lost the write race; peer={PeerId} was NOT given the lease",
                    taskId,
                    pick.PeerId);
                return (false, assignResult.State, "schedule.conflict");
            default:
                var assigned = assignResult.State!;
                _logger?.LogInformation(
                    "mesh placement task={TaskId} peer={PeerId} attempt={Attempt} retry={Retry} correlationId={CorrelationId}",
                    taskId,
                    pick.PeerId,
                    assigned.AttemptCount,
                    isRetry,
                    assigned.CorrelationId ?? "");
                return (true, assigned, null);
        }
    }

    private static string NormalizeBaseUrl(string url)
    {
        var t = url.Trim();
        return t.EndsWith('/') ? t : t + "/";
    }

    private static bool MatchesAffinity(MeshFleetNodeState node, IReadOnlyDictionary<string, string> affinity)
    {
        if (affinity.Count == 0) return true;
        foreach (var kv in affinity)
        {
            if (!node.Labels.TryGetValue(kv.Key, out var v) ||
                !string.Equals(v, kv.Value, StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    private static bool HasBricks(MeshFleetNodeState node, IReadOnlyList<string> required)
    {
        if (required.Count == 0) return true;
        var caps = new HashSet<string>(node.AdvertisedBrickIds, StringComparer.OrdinalIgnoreCase);
        foreach (var label in node.Labels)
        {
            if (label.Value.StartsWith("brick:", StringComparison.OrdinalIgnoreCase))
                caps.Add(label.Value["brick:".Length..].Trim());
        }

        foreach (var r in required)
        {
            if (string.IsNullOrWhiteSpace(r)) continue;
            if (!caps.Contains(r.Trim())) return false;
        }

        return true;
    }
}
