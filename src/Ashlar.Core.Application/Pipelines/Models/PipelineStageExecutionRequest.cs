namespace Ashlar.Core.Application.Pipelines.Models;

/// <summary>
/// Context payload passed to a stage executor.
/// </summary>
public sealed record PipelineStageExecutionRequest
{
    /// <summary>Parent pipeline run identifier.</summary>
    public required string RunId { get; init; }

    /// <summary>Stage identifier being executed.</summary>
    public required string StageId { get; init; }

    /// <summary>Attempt number for this stage execution.</summary>
    public required int Attempt { get; init; }

    /// <summary>Stable operation identity across retries, fallback workers, and resumed runs. Idempotent connectors must deduplicate against this key.</summary>
    public string? IdempotencyKey { get; init; }

    /// <summary>Worker pool type selected for this attempt.</summary>
    public required PipelineWorkerType WorkerType { get; init; }

    /// <summary>Template stage definition for this execution.</summary>
    public required PipelineStageDefinition Stage { get; init; }

    /// <summary>Materialized execution node with graph topology.</summary>
    public required PipelineExecutionNode Node { get; init; }

    /// <summary>Named input values from predecessors or run-level inputs.</summary>
    public IReadOnlyDictionary<string, string> Inputs { get; init; } = new Dictionary<string, string>();
}
