using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ashlar.Commercial.Fleet.Contracts.Models;
using Ashlar.Commercial.Fleet.Contracts.Ports;

namespace Ashlar.Commercial.Fleet.Infrastructure;

/// <summary>
/// Phase 6: lease extension, migrate-for-checkpoint and worker status reporting.
/// </summary>
/// <remarks>
/// Every method here states its precondition INSIDE the transform the registry applies within its
/// write transaction, never against a snapshot read beforehand. Read
/// <see cref="IMeshTaskRegistry.UpdateAsync"/> for why: the old shape compared the lease token on a
/// database the store had already closed, so a sweep, a re-placement or another worker's report
/// committing in the window was overwritten wholesale by the snapshot, and the method then returned
/// success for a write whose result it had never looked at.
/// </remarks>
public sealed class MeshTaskExecutionService
{
    private readonly IMeshTaskRegistry _tasks;
    private readonly IOptions<MeshCheckpointOptions> _checkpointOptions;
    private readonly ILogger<MeshTaskExecutionService>? _logger;

    /// <summary>Initializes a new mesh task execution service.</summary>
    /// <param name="tasks">Task registry.</param>
    /// <param name="checkpointOptions">Lease and checkpoint options.</param>
    /// <param name="logger">Optional logger.</param>
    public MeshTaskExecutionService(
        IMeshTaskRegistry tasks,
        IOptions<MeshCheckpointOptions> checkpointOptions,
        ILogger<MeshTaskExecutionService>? logger = null)
    {
        _tasks = tasks ?? throw new ArgumentNullException(nameof(tasks));
        _checkpointOptions = checkpointOptions ?? throw new ArgumentNullException(nameof(checkpointOptions));
        _logger = logger;
    }

    /// <summary>Extends the execution lease a worker holds on a task.</summary>
    /// <param name="taskId">Task id.</param>
    /// <param name="leaseToken">The token the worker believes it holds.</param>
    /// <param name="extendSeconds">Optional override, clamped to 60..86400.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Ok plus the stored state, or an error code.</returns>
    /// <remarks>
    /// The token is compared against the document inside the write transaction, so a lease the sweep
    /// cleared - or one a placement has since re-issued to another peer - cannot be extended. Before
    /// that, the comparison happened on a snapshot and the write put the whole pre-sweep document
    /// back with a FRESH expiry, so the task was leased to a peer the director had already reclaimed
    /// it from and the sweep would not look at it again for another interval.
    /// </remarks>
    public async Task<(bool Ok, MeshTaskState? Task, string? Error)> ExtendLeaseAsync(
        string taskId,
        string leaseToken,
        int? extendSeconds,
        CancellationToken cancellationToken = default)
    {
        var baseSeconds = Math.Max(60, _checkpointOptions.Value.LeaseSeconds);
        var add = extendSeconds.HasValue ? Math.Clamp(extendSeconds.Value, 60, 86_400) : baseSeconds;
        var newExpiry = DateTimeOffset.UtcNow.AddSeconds(add);

        var result = await _tasks.UpdateAsync(
            taskId,
            current => HoldsLease(current, leaseToken)
                ? current with { LeaseExpiresUtc = newExpiry }
                : null,
            cancellationToken).ConfigureAwait(false);

        switch (result.Outcome)
        {
            case MeshTaskUpdateOutcome.NotFound:
                return (false, null, "task.not_found");
            case MeshTaskUpdateOutcome.PreconditionFailed:
                return (false, result.State, "lease.invalid_token");
            default:
                _logger?.LogDebug("mesh-lease extended task={TaskId} until={Until}", taskId, newExpiry);
                return (true, result.State, null);
        }
    }

    /// <summary>Records a checkpoint handle and hands the task back for re-placement.</summary>
    /// <param name="taskId">Task id.</param>
    /// <param name="leaseToken">The token the worker believes it holds.</param>
    /// <param name="checkpointHandle">Opaque handle the next peer resumes from.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Ok plus the stored state, or an error code.</returns>
    /// <remarks>
    /// Both the token and the Assigned/Running guard are re-evaluated inside the transaction. Either
    /// one evaluated on a snapshot lets a finished task be dragged back to Pending carrying the
    /// snapshot's result fields, and lets two workers holding the same stale token each write their
    /// own checkpoint handle over the other's.
    /// </remarks>
    public async Task<(bool Ok, MeshTaskState? Task, string? Error)> MigrateForCheckpointAsync(
        string taskId,
        string leaseToken,
        string checkpointHandle,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(checkpointHandle))
            return (false, null, "checkpoint.required");

        var handle = checkpointHandle.Trim();
        var declinedBecauseOfStatus = false;

        var result = await _tasks.UpdateAsync(
            taskId,
            current =>
            {
                if (!HoldsLease(current, leaseToken))
                    return null;
                if (current.Status is not (MeshTaskStatus.Assigned or MeshTaskStatus.Running))
                {
                    declinedBecauseOfStatus = true;
                    return null;
                }

                return current with
                {
                    Status = MeshTaskStatus.Pending,
                    AssignedPeerId = null,
                    AssignedApiBaseUrl = null,
                    CheckpointHandle = handle,
                    LeaseToken = null,
                    LeaseOwnerPeerId = null,
                    LeaseExpiresUtc = null,
                    PlacementReason = "migrate.checkpoint_ready"
                };
            },
            cancellationToken).ConfigureAwait(false);

        switch (result.Outcome)
        {
            case MeshTaskUpdateOutcome.NotFound:
                return (false, null, "task.not_found");
            case MeshTaskUpdateOutcome.PreconditionFailed:
                return (false, result.State, declinedBecauseOfStatus ? "migrate.invalid_status" : "lease.invalid_token");
            default:
                _logger?.LogInformation("mesh-migrate task={TaskId} checkpoint recorded", taskId);
                return (true, result.State, null);
        }
    }

    /// <summary>Applies a worker- or operator-reported status transition.</summary>
    /// <param name="taskId">Task id.</param>
    /// <param name="status">Requested status.</param>
    /// <param name="leaseToken">The reporter's lease token, if it holds one.</param>
    /// <param name="reason">Optional placement reason to record.</param>
    /// <param name="correlationId">Optional correlation id, already resolved from the request.</param>
    /// <param name="resultSummary">Optional result summary for a terminal transition.</param>
    /// <param name="resultHandle">Optional result handle for a terminal transition.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Ok plus the stored state, or an error code.</returns>
    /// <remarks>
    /// <para>This moved out of <c>CommercialFleetEndpoints.PatchMeshTaskStatusAsync</c> so the
    /// derivation happens where the transform can hold it, and so a race test can reach it without
    /// the HTTP host - the only project that could exercise that endpoint,
    /// <c>Ashlar.Commercial.Tests.Fleet.Host</c>, is in no solution and no lane.</para>
    ///
    /// <para><b>One behaviour change, and one that was claimed and is NOT made.</b> The change: a
    /// caller that OFFERS a lease token is now checked against the stored one for every status,
    /// where the check used to be gated on <c>Status is Running or Assigned</c> - and Succeeded,
    /// Failed and Pending are exactly the three arms that null the lease fields, so a worker whose
    /// lease had been swept or re-placed could report Succeeded, clear the NEW owner's lease and
    /// overwrite its result. Offering a token that is not the stored one is refused even when the
    /// stored task is unleased, which is what stops a migrate that cleared the lease being undone
    /// by the same stale worker (measured: without it, 24 of 24 rounds accepted both writers).</para>
    ///
    /// <para>The change NOT made, having been claimed in this change's first revision and in its
    /// CHANGELOG entry: a caller that supplies NO token is treated exactly as before. That is the
    /// OPERATOR path - <c>PATCH /api/mesh/tasks/{id}/status</c> with no <c>leaseToken</c> - and it
    /// is the only lever an operator has to fail or requeue a task whose worker died holding a live
    /// lease. <c>MeshCheckpointOptions.SweepEnabled</c> is <c>false</c> by default and
    /// <c>LeaseSeconds</c> is 1800, so nothing else reclaims that lease, and there is no cancel or
    /// abandon route; <c>/retry</c> re-places the task rather than failing it. Refusing the
    /// no-token caller on any leased task - which an earlier revision of this method did for every
    /// status - removed that lever, was not needed by any of the measured races (a worker always
    /// sends its token, so the first branch is what refuses it), and was described in the commit
    /// message as leaving the operator path unchanged. The gate below is therefore the one the
    /// endpoint shipped with, deliberately: no token plus a live lease is refused for Running and
    /// Assigned, and accepted for the terminal and requeue arms.</para>
    ///
    /// <para>And the state reported back is the one the store wrote, not the one this method
    /// composed.</para>
    /// </remarks>
    public async Task<(bool Ok, MeshTaskState? Task, string? Error)> ApplyStatusAsync(
        string taskId,
        MeshTaskStatus status,
        string? leaseToken,
        string? reason,
        string? correlationId,
        string? resultSummary,
        string? resultHandle,
        CancellationToken cancellationToken = default)
    {
        var result = await _tasks.UpdateAsync(
            taskId,
            current =>
            {
                // OFFERING a token is itself a claim of ownership, so a token that is not the
                // stored one is refused whatever the status and even when the stored task is
                // unleased. Both halves were measured to matter. Without the first, a worker whose
                // lease had been swept or re-placed cleared the NEW owner's lease and overwrote its
                // result. Without the second, a migrate that cleared the lease first was then
                // overwritten by the same stale worker reporting Succeeded, and the run counted six
                // rounds out of twenty-four where BOTH writers were accepted.
                //
                // Supplying NO token is the operator path and keeps the gate the endpoint shipped
                // with: refused for Running and Assigned on a leased task, accepted for Succeeded,
                // Failed and Pending. An operator forcing a stuck task terminal while an
                // unreachable peer still holds a live lease is the only lever there is -- the sweep
                // is off by default, the lease is 1800 s, and there is no cancel route -- and no
                // measured race needs it closed, because a worker always sends its token and is
                // refused by the branch above. See the remarks: the wider refusal was claimed and
                // is deliberately not made.
                if (!string.IsNullOrWhiteSpace(leaseToken))
                {
                    if (!HoldsLease(current, leaseToken))
                        return null;
                }
                else if (status is MeshTaskStatus.Running or MeshTaskStatus.Assigned
                         && !string.IsNullOrWhiteSpace(current.LeaseToken))
                {
                    return null;
                }

                var corr = correlationId ?? current.CorrelationId;
                return status switch
                {
                    MeshTaskStatus.Running => current with
                    {
                        Status = MeshTaskStatus.Running,
                        PlacementReason = reason ?? current.PlacementReason,
                        CorrelationId = corr
                    },
                    MeshTaskStatus.Succeeded => current with
                    {
                        Status = MeshTaskStatus.Succeeded,
                        PlacementReason = reason ?? current.PlacementReason,
                        CorrelationId = corr,
                        ResultSummary = resultSummary ?? current.ResultSummary,
                        ResultHandle = resultHandle ?? current.ResultHandle,
                        AssignedPeerId = null,
                        AssignedApiBaseUrl = null,
                        LeaseToken = null,
                        LeaseOwnerPeerId = null,
                        LeaseExpiresUtc = null
                    },
                    MeshTaskStatus.Failed => current with
                    {
                        Status = MeshTaskStatus.Failed,
                        PlacementReason = reason ?? current.PlacementReason,
                        CorrelationId = corr,
                        ResultSummary = resultSummary ?? current.ResultSummary,
                        ResultHandle = resultHandle ?? current.ResultHandle,
                        AssignedPeerId = null,
                        AssignedApiBaseUrl = null,
                        LeaseToken = null,
                        LeaseOwnerPeerId = null,
                        LeaseExpiresUtc = null
                    },
                    MeshTaskStatus.Pending => current with
                    {
                        Status = MeshTaskStatus.Pending,
                        AssignedPeerId = null,
                        AssignedApiBaseUrl = null,
                        PlacementReason = reason,
                        CorrelationId = corr,
                        LeaseToken = null,
                        LeaseOwnerPeerId = null,
                        LeaseExpiresUtc = null
                    },
                    MeshTaskStatus.Assigned => current with
                    {
                        Status = MeshTaskStatus.Assigned,
                        PlacementReason = reason ?? current.PlacementReason,
                        CorrelationId = corr
                    },
                    _ => current
                };
            },
            cancellationToken).ConfigureAwait(false);

        return result.Outcome switch
        {
            MeshTaskUpdateOutcome.NotFound => (false, null, "task.not_found"),
            MeshTaskUpdateOutcome.PreconditionFailed => (false, result.State, "lease.token_mismatch_or_missing"),
            _ => (true, result.State, null),
        };
    }

    /// <summary>Whether <paramref name="offered"/> is the lease token the stored task carries.</summary>
    /// <param name="current">The document read inside the write transaction.</param>
    /// <param name="offered">The token the caller supplied.</param>
    /// <returns>True when the task is leased and the tokens match.</returns>
    internal static bool HoldsLease(MeshTaskState current, string? offered)
        => !string.IsNullOrWhiteSpace(current.LeaseToken)
           && !string.IsNullOrWhiteSpace(offered)
           && SecureEquals(current.LeaseToken, offered);

    private static bool SecureEquals(string a, string b)
    {
        var aa = Encoding.UTF8.GetBytes(a.Trim());
        var bb = Encoding.UTF8.GetBytes(b.Trim());
        return aa.Length == bb.Length && CryptographicOperations.FixedTimeEquals(aa, bb);
    }
}
