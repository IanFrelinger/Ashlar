namespace Ashlar.Core.Application.Pipelines.Models;

/// <summary>
/// Stage run state for persistence and diagnostics.
/// </summary>
public sealed record PipelineStageRun
{
    /// <summary>Stage identifier within the pipeline template.</summary>
    public string StageId { get; init; } = string.Empty;

    /// <summary>Current execution state of this stage attempt.</summary>
    public PipelineStageRunState State { get; init; } = PipelineStageRunState.Pending;

    /// <summary>Attempt number for this stage (incremented on retry).</summary>
    public int Attempt { get; init; }

    /// <summary>Stable operation identity allocated before dispatch and preserved on resume.</summary>
    public string? IdempotencyKey { get; init; }

    /// <summary>Hash binding the operation identity to its stage, topology, and inputs.</summary>
    public string? ExecutionFingerprint { get; init; }

    /// <summary>Replay safety declaration at creation; null denotes a legacy checkpoint.</summary>
    public PipelineStageEffects? Effects { get; init; }

    /// <summary>Whether an uncertain external effect requires reconciliation before further execution.</summary>
    public bool ReconciliationRequired { get; init; }

    /// <summary>Identifier of the worker that executed this stage, if assigned.</summary>
    public string? WorkerId { get; init; }

    /// <summary>Worker pool type that handled this stage, if dispatched.</summary>
    public PipelineWorkerType? WorkerType { get; init; }

    /// <summary>Serialized stage output on success.</summary>
    public string? Output { get; init; }

    /// <summary>Error message when the stage failed.</summary>
    public string? Error { get; init; }
}
