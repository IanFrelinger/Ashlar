using Ashlar.Commercial.Fleet.Contracts.Models;

namespace Ashlar.Commercial.Fleet.Contracts.Ports;

/// <summary>
/// In-process store for mesh tasks (Phase 1).
/// </summary>
public interface IMeshTaskRegistry
{
    /// <summary>Creates a task, absorbing a repeated <see cref="MeshTaskCreateSpec.IdempotencyKey"/>.</summary>
    /// <param name="spec">What to create.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The created task, or the one an earlier submission of the same key created.</returns>
    Task<MeshTaskState> CreateAsync(MeshTaskCreateSpec spec, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns an existing task created with the same <see cref="MeshTaskCreateSpec.IdempotencyKey"/> (Phase 3), or null.
    /// </summary>
    /// <param name="idempotencyKey">The submission key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The task, or null.</returns>
    Task<MeshTaskState?> TryGetByIdempotencyKeyAsync(string idempotencyKey, CancellationToken cancellationToken = default);

    /// <summary>Reads one task.</summary>
    /// <param name="taskId">Task id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The task, or null.</returns>
    Task<MeshTaskState?> GetAsync(string taskId, CancellationToken cancellationToken = default);

    /// <summary>Reads every task.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>All tasks, highest priority first.</returns>
    Task<IReadOnlyList<MeshTaskState>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the task, applies <paramref name="transform"/> to it and writes the result, as ONE
    /// operation on the store.
    /// </summary>
    /// <param name="taskId">Task id.</param>
    /// <param name="transform">
    /// The change to make. It is handed the document the store just read INSIDE the write
    /// transaction, so any precondition — the lease token, the status, the attempt count, all three —
    /// is a predicate over that argument. Return <c>null</c> to decline, which surfaces as
    /// <see cref="MeshTaskUpdateOutcome.PreconditionFailed"/> and writes nothing.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What happened, and the state the store holds.</returns>
    /// <remarks>
    /// <para><b>Why a transform and not <c>UpdateAsync(MeshTaskState)</c>.</b> The old shape took a
    /// whole document the caller had read through an EARLIER <c>GetAsync</c>/<c>ListAsync</c>, on a
    /// database the store had since closed and reopened. Every field of that snapshot was written
    /// back, so anything another writer committed in the window was silently reverted — and nothing
    /// threw, on any platform. Measured on a standalone probe during this change's scoping pass, in
    /// the Linux devtest container, two registry instances over one file, 8 threads x 50
    /// read-modify-writes to one task: 400 updates attempted, 400 reported successful, <b>87
    /// survived</b>; with the transform applied inside the store's transaction, 400 of 400. The
    /// costs at the actual call sites were re-measured through the fleet race tests and are in the
    /// CHANGELOG. An <c>expectedLeaseToken</c> parameter was the other candidate and is strictly
    /// weaker: it also stops the loss (86 applied / 314 refused / 0 lost on the same probe) but
    /// refuses 78% of operations, forces a retry loop into four of the five callers, and cannot
    /// express the precondition the placement path actually needs, because a Pending task's lease
    /// token is null on both sides of the race and two placements both satisfy "expect null".</para>
    ///
    /// <para><b>What the transform may not do.</b> It runs inside the LiteDB write transaction while
    /// the store also holds a non-reentrant <c>SemaphoreSlim(1,1)</c>. It must be synchronous and do
    /// no I/O. It must not call back into THIS registry: that deadlocks on the semaphore. And it must
    /// not call into ANY other LiteDB store - which an earlier revision of this paragraph said was
    /// covered by the two mechanisms above, and is not. The fleet registries are constructed with the
    /// SAME FILE PATH and <c>MeshTaskPlacementService</c> holds both, so re-checking a node inside a
    /// task transform is the natural edit; the sibling store's semaphore is not this one's, and the
    /// .NET named mutex LiteDB's <c>SharedEngine</c> queues on is thread-reentrant. Measured in the
    /// Linux devtest container: the inner <c>BeginTrans</c> returned TRUE, both <c>Commit</c>s
    /// returned TRUE, and one of the two writes was simply not on disk afterwards. That case is now
    /// refused by a thread-static depth count in <c>LiteDbAtomic.Mutate</c>, so it throws rather than
    /// losing a write - but the rule is the same either way: compute anything that needs an
    /// <c>await</c>, another store, or a clock-and-policy decision BEFORE the call and close over the
    /// result.</para>
    ///
    /// <para>It may also be invoked more than once in principle; keep it free of side effects.</para>
    /// </remarks>
    Task<MeshTaskUpdateResult> UpdateAsync(
        string taskId,
        Func<MeshTaskState, MeshTaskState?> transform,
        CancellationToken cancellationToken = default);
}
