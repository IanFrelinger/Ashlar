namespace Ashlar.Core.Application.Pipelines.Models;

/// <summary>Declares whether repeating a stage can repeat external effects.</summary>
public enum PipelineStageEffects
{
    /// <summary>No safety declaration. An uncertain invocation requires reconciliation.</summary>
    Unspecified = 0,

    /// <summary>The stage performs no externally observable writes and may be replayed.</summary>
    None = 1,

    /// <summary>The connector atomically deduplicates effects using the supplied idempotency key.</summary>
    Idempotent = 2,

    /// <summary>The stage may perform effects that cannot safely be repeated.</summary>
    NonIdempotent = 3
}
