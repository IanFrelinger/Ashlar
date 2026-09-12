using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ashlar.Commercial.Fleet.Contracts.Models;
using Ashlar.Commercial.Fleet.Contracts.Ports;

namespace Ashlar.Commercial.Fleet.Infrastructure;

/// <summary>
/// Clears expired execution leases by moving <see cref="MeshTaskStatus.Assigned"/> or <see cref="MeshTaskStatus.Running"/> tasks back to <see cref="MeshTaskStatus.Pending"/>.
/// </summary>
public sealed class MeshLeaseSweepBackgroundService : BackgroundService
{
    private readonly IMeshTaskRegistry _tasks;
    private readonly IOptionsMonitor<MeshCheckpointOptions> _options;
    private readonly ILogger<MeshLeaseSweepBackgroundService>? _logger;

    public MeshLeaseSweepBackgroundService(
        IMeshTaskRegistry tasks,
        IOptionsMonitor<MeshCheckpointOptions> options,
        ILogger<MeshLeaseSweepBackgroundService>? logger = null)
    {
        _tasks = tasks ?? throw new ArgumentNullException(nameof(tasks));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;
    }

    /// <summary>
    /// Whether the document the transaction just read is STILL an expired lease.
    /// </summary>
    /// <param name="current">The document read inside the write transaction.</param>
    /// <returns>True when the sweep may clear it.</returns>
    /// <remarks>
    /// This is the precondition the old code evaluated against a whole-collection snapshot. A worker
    /// that extended its lease, a placement that re-issued it, or a PATCH that finished the task
    /// after the list was taken all fail this test now, where before they were reverted -- a
    /// completed task went back to Pending carrying the snapshot's <c>ResultSummary</c> and was
    /// executed again, and an extended lease was reclaimed mid-execution so two peers ran one task.
    /// </remarks>
    private static bool IsStillAnExpiredLease(MeshTaskState current)
        => current.Status is MeshTaskStatus.Assigned or MeshTaskStatus.Running
           && current.LeaseExpiresUtc is { } expiry
           && expiry <= DateTimeOffset.UtcNow;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var opts = _options.CurrentValue;
            var interval = TimeSpan.FromMinutes(Math.Max(1, opts.SweepIntervalMinutes));

            try
            {
                if (opts.SweepEnabled)
                {
                    var list = await _tasks.ListAsync(stoppingToken).ConfigureAwait(false);
                    foreach (var t in list)
                    {
                        // The list is a candidate filter and nothing more. The snapshot of the LAST
                        // element is as old as the whole preceding round -- every write below is a
                        // separate database open -- so the real decision is re-taken inside the
                        // transform, against the document the transaction is about to overwrite, with
                        // a clock read at that moment rather than before the list.
                        if (t.Status is not (MeshTaskStatus.Assigned or MeshTaskStatus.Running))
                            continue;
                        if (t.LeaseExpiresUtc is null)
                            continue;
                        if (t.LeaseExpiresUtc > DateTimeOffset.UtcNow)
                            continue;

                        var result = await _tasks.UpdateAsync(
                            t.TaskId,
                            current => IsStillAnExpiredLease(current)
                                ? current with
                                {
                                    Status = MeshTaskStatus.Pending,
                                    AssignedPeerId = null,
                                    AssignedApiBaseUrl = null,
                                    LeaseToken = null,
                                    LeaseOwnerPeerId = null,
                                    LeaseExpiresUtc = null,
                                    PlacementReason = "lease.expired"
                                }
                                : null,
                            stoppingToken).ConfigureAwait(false);

                        if (result.Applied)
                            _logger?.LogWarning("mesh-lease-sweep reclaimed task={TaskId}", t.TaskId);
                        else
                            _logger?.LogDebug("mesh-lease-sweep skipped task={TaskId} outcome={Outcome}", t.TaskId, result.Outcome);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "mesh-lease-sweep round failed");
            }

            try
            {
                await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
