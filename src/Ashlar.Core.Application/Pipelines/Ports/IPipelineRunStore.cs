using Ashlar.Core.Application.Pipelines.Models;

namespace Ashlar.Core.Application.Pipelines.Ports;

/// <summary>Persists pipeline runs for diagnostics and recovery.</summary>
public interface IPipelineRunStore
{
    /// <summary>Reads, transforms and writes one run in a single store transaction.</summary>
    /// <param name="runId">The identifier used for both the read and the write.</param>
    /// <param name="merge">
    /// A synchronous, side-effect-free transform of the currently stored run (null if absent).
    /// Throwing aborts the write. The transform must not perform I/O or access another store.
    /// </param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>The persisted run. A null result or changed identifier is rejected.</returns>
    /// <remarks>
    /// The orchestrator uses this transaction to create a destination run only if its ID is
    /// unused, before invoking any executor. Later writes advance that one execution. Resume
    /// reads an existing source into a fresh destination; results from separate executions must
    /// never be combined merely because their run IDs or stage names coincide.
    /// </remarks>
    Task<PipelineRun> MergeAsync(
        string runId,
        Func<PipelineRun?, PipelineRun> merge,
        CancellationToken cancellationToken = default);

    /// <summary>Retrieves a previously stored run by identifier.</summary>
    /// <param name="runId">Unique run identifier.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>The stored run, or null if absent.</returns>
    Task<PipelineRun?> GetAsync(string runId, CancellationToken cancellationToken = default);
}
