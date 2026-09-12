using Ashlar.Core.Application.Pipelines.Models;

namespace Ashlar.Infrastructure.Pipelines;

/// <summary>Creates one execution per destination ID and advances only that execution.</summary>
public static class PipelineRunPersistence
{
    /// <summary>Claims an unused destination before any stage executes.</summary>
    /// <param name="stored">The run read inside the write transaction, or null.</param>
    /// <param name="proposed">The new execution.</param>
    /// <returns>The new execution when its ID is unused.</returns>
    public static PipelineRun Create(PipelineRun? stored, PipelineRun proposed)
    {
        ArgumentNullException.ThrowIfNull(proposed);
        if (stored != null)
            throw new InvalidOperationException(
                $"Pipeline run '{proposed.RunId}' already exists. Use a fresh --run-id or omit it; "
                + "use --resume-run-id to read the prior run into that new destination.");
        return proposed;
    }

    /// <summary>Persists progress from the execution that created the destination.</summary>
    /// <param name="stored">The run read inside the write transaction.</param>
    /// <param name="proposed">That execution's next state.</param>
    /// <returns>The advanced run, preserving its identity.</returns>
    /// <remarks>
    /// Creation is exclusive and run IDs cannot be reused. No lease or schema migration is needed:
    /// interrupted executions remain readable and resume writes to a fresh destination. The
    /// identity check also refuses a missing/replaced record rather than silently recreating it.
    /// </remarks>
    public static PipelineRun Advance(PipelineRun? stored, PipelineRun proposed)
    {
        ArgumentNullException.ThrowIfNull(proposed);
        if (stored == null || stored.RunId != proposed.RunId
            || stored.TemplateId != proposed.TemplateId || stored.StartedAt != proposed.StartedAt)
            throw new InvalidOperationException(
                $"Pipeline run '{proposed.RunId}' no longer matches the execution that created it.");

        return stored with
        {
            State = proposed.State,
            CompletedAt = proposed.CompletedAt,
            StageRuns = proposed.StageRuns,
        };
    }
}
