using Ashlar.Core.Application.Pipelines.Models;
using Ashlar.Core.Application.Pipelines.Ports;

namespace Ashlar.Tests.Infrastructure.Helpers;

/// <summary>
/// The unconditional whole-run write <c>IPipelineRunStore</c> used to expose, kept here as a
/// TEST-ONLY arrange helper.
/// </summary>
/// <remarks>
/// <para><c>IPipelineRunStore.SaveAsync(PipelineRun)</c> was removed from the port because it is the
/// lost update: <c>PipelineOrchestrator</c> held one run across an entire execution loop and wrote
/// every field of that snapshot back seven times, reverting anything another writer had committed in
/// between. Product code can no longer spell it; the port takes a merge the store applies to the
/// document it reads inside its own write transaction. This mirrors
/// <c>RegistryArrangeExtensions</c> in the fleet test project, for the same reason and with the same
/// restriction.</para>
///
/// <para>Arrange code is the one place where the old shape is still the right thing to write,
/// because the test owns the file, there is no concurrent writer, and "put this exact document
/// there" is the whole intent.</para>
///
/// <para><b>Do not use this in a race test, and do not use it as the competing writer in an
/// interleaving test.</b> A concurrency fact that writes through this is exercising the very shape
/// the fix removed, and would pass whether or not the merge exists. A competing writer must go
/// through <c>MergeAsync</c> with a real merge, so that what it proves is that the STORE reconciles
/// two writers rather than that this helper overwrites.</para>
/// </remarks>
public static class PipelineRunStoreArrangeExtensions
{
    /// <summary>Writes <paramref name="run"/> verbatim, for arrange code only.</summary>
    /// <param name="store">Store under test.</param>
    /// <param name="run">The exact document to store.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The run that was written.</returns>
    public static Task<PipelineRun> PutAsync(
        this IPipelineRunStore store,
        PipelineRun run,
        CancellationToken cancellationToken = default)
    {
        if (store == null) throw new ArgumentNullException(nameof(store));
        if (run == null) throw new ArgumentNullException(nameof(run));

        return store.MergeAsync(run.RunId, _ => run, cancellationToken);
    }
}
