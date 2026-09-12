using Ashlar.Core.Application.Pipelines.Models;
using Ashlar.Core.Application.Pipelines.Ports;

namespace Ashlar.Infrastructure.Pipelines;

/// <summary>
/// Simple in-memory run store for pipeline execution tests and local runtime.
/// </summary>
/// <remarks>
/// <para>This is the DEFAULT provider — <c>PipelinePersistenceOptions.Provider</c> is
/// <c>"InMemory"</c> and only <c>ASHLAR_PIPELINE_STORE_PROVIDER=LiteDb</c> swaps it — and
/// <c>IPipelineRunStore</c> is registered as a singleton, so two concurrent
/// <c>IPipelineOrchestrator.RunAsync</c> calls in one host share one of these. Its old
/// <c>SaveAsync</c> was <c>_runs[run.RunId] = run</c>: the same whole-document replace, and the same
/// lost update, with no LiteDB involved. Parity with the durable store is therefore not a
/// convenience for tests; it is where the default deployment's version of this defect lived.</para>
///
/// <para>A plain dictionary under a lock rather than a <c>ConcurrentDictionary</c>:
/// <c>AddOrUpdate</c> may invoke its factory more than once under contention, and while the merge is
/// required to be side-effect free, one lock held across read-merge-write is both simpler to reason
/// about and the same guarantee the LiteDB store's transaction gives. The merge is synchronous pure
/// computation, so holding the lock across it cannot block on anything.</para>
/// </remarks>
public sealed class InMemoryPipelineRunStore : IPipelineRunStore
{
    private readonly Dictionary<string, PipelineRun> _runs = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    /// <summary>Merge asynchronously.</summary>
    /// <param name="runId">The run to write; the entry is addressed by this id.</param>
    /// <param name="merge">Applied to the stored run while the gate is held.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The run this store holds after the write.</returns>
    public Task<PipelineRun> MergeAsync(
        string runId,
        Func<PipelineRun?, PipelineRun> merge,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(runId))
            throw new ArgumentException("Run id is required.", nameof(runId));
        if (merge == null) throw new ArgumentNullException(nameof(merge));

        lock (_gate)
        {
            _runs.TryGetValue(runId, out var existing);
            var next = merge(existing);
            if (next == null)
                throw new InvalidOperationException(
                    $"a pipeline run merge returned null for run '{runId}'. This port has no "
                    + "decline outcome: return the document the merge was handed to leave the "
                    + "stored run unchanged.");

            // Same rule as the durable store: the write is addressed by the id the read used.
            if (!string.Equals(next.RunId, runId, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"a pipeline run merge may not change RunId ('{runId}' -> '{next.RunId}'). The "
                    + "write is addressed by the id the read used; changing it would leave two "
                    + "entries where the caller expected one.");

            _runs[runId] = next;
            return Task.FromResult(next);
        }
    }

    /// <summary>Get asynchronously.</summary>
    public Task<PipelineRun?> GetAsync(string runId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _runs.TryGetValue(runId, out var run);
            return Task.FromResult(run);
        }
    }
}
