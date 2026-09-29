using Ashlar.Core.Application.Scaling.Models;
using Ashlar.Core.Application.Scaling.Ports;

namespace Ashlar.Infrastructure.Scaling;

/// <summary>Default no-op scaler — safe when no cluster/compose control plane is configured.</summary>
internal sealed class NullWorkloadScaler : IWorkloadScaler
{
    /// <inheritdoc />
    public string ProviderName => "null";

    /// <inheritdoc />
    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(true);

    /// <inheritdoc />
    public Task<IReadOnlyList<WorkloadDescriptor>> ListWorkloadsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<WorkloadDescriptor>>(Array.Empty<WorkloadDescriptor>());

    /// <inheritdoc />
    public Task<WorkloadReplicaSnapshot> GetReplicasAsync(
        string workloadId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new WorkloadReplicaSnapshot(
            workloadId,
            DesiredReplicas: 0,
            CurrentReplicas: 0,
            ReadyReplicas: 0,
            ObservedAtUtc: DateTimeOffset.UtcNow,
            ProviderDetail: "null provider — configure Ashlar:WorkloadScaling:Provider=kubernetes|compose"));

    /// <inheritdoc />
    public Task<WorkloadScaleResult> ScaleAsync(
        WorkloadScaleRequest request,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new WorkloadScaleResult(
            Success: false,
            request.WorkloadId,
            PreviousDesiredReplicas: 0,
            DesiredReplicas: request.DesiredReplicas,
            Message: "NullWorkloadScaler does not mutate capacity. Set Ashlar:WorkloadScaling:Provider to kubernetes or compose."));
}
