using Ashlar.Core.Application.Scaling.Models;
using Ashlar.Core.Application.Scaling.Ports;

namespace Ashlar.Infrastructure.Scaling;

/// <summary>Default demand signal with zero queue pressure.</summary>
internal sealed class NullWorkloadDemandSignal : IWorkloadDemandSignal
{
    /// <inheritdoc />
    public Task<WorkloadDemandSnapshot> GetDemandAsync(
        string workloadId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new WorkloadDemandSnapshot(
            workloadId,
            QueueDepth: 0,
            PendingTasks: 0,
            ObservedAtUtc: DateTimeOffset.UtcNow));
}
