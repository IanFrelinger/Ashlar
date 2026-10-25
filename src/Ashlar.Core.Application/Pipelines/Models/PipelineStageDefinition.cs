namespace Ashlar.Core.Application.Pipelines.Models;

/// <summary>
/// Declares one stage in a template.
/// </summary>
public sealed record PipelineStageDefinition
{
    /// <summary>Unique stage identifier within the template.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Human-readable stage name.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Logical graph role of this stage (standard, fan-out, or fan-in).</summary>
    public PipelineStageType StageType { get; init; } = PipelineStageType.Standard;

    /// <summary>Execution mode governing worker selection for this stage.</summary>
    public PipelineExecutionMode Mode { get; init; } = PipelineExecutionMode.Deterministic;

    /// <summary>Replay safety declaration. Unspecified stages require reconciliation after uncertain execution.</summary>
    public PipelineStageEffects Effects { get; init; } = PipelineStageEffects.Unspecified;

    /// <summary>Fan-in join strategy when multiple predecessors feed this stage.</summary>
    public PipelineJoinStrategy? JoinStrategy { get; init; }

    /// <summary>Minimum successful predecessors required when join strategy is quorum.</summary>
    public int? QuorumCount { get; init; }

    /// <summary>Ordered worker types to try when primary dispatch fails.</summary>
    public IReadOnlyList<PipelineWorkerType> FallbackChain { get; init; } = Array.Empty<PipelineWorkerType>();

    /// <summary>Parallelism and validation constraints for this stage.</summary>
    public PipelineStageConstraints Constraints { get; init; } = new();

    /// <summary>Optional routing hints for deterministic and agentic worker pools.</summary>
    public PipelineWorkerBinding WorkerBinding { get; init; } = new();
}
