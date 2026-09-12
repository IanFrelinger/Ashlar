using Ashlar.Commercial.Fleet.Contracts.Models;

namespace Ashlar.Commercial.Fleet.Contracts.Ports;

/// <summary>
/// In-process registry of mesh worker nodes (Phase 1 control plane).
/// </summary>
public interface IFleetNodeRegistry
{
    /// <summary>
    /// Reads the node, applies <paramref name="merge"/> to it and writes the result, as ONE
    /// operation on the store.
    /// </summary>
    /// <param name="peerId">Worker peer id.</param>
    /// <param name="merge">
    /// Builds the document to store. Its argument is the document the store just read INSIDE the
    /// write transaction, or <c>null</c> when the peer is new, so "keep whatever is stored for a
    /// field this request did not carry" is expressible without a separate read.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The state that was written.</returns>
    /// <remarks>
    /// <para><b>Why this replaced <c>RegisterOrUpdateAsync(MeshFleetNodeState)</c>.</b> That method
    /// was an unconditional whole-document upsert with no read of its own, so a caller that wanted to
    /// preserve a field the request omitted HAD to read first — and
    /// <c>CommercialFleetEndpoints.RegisterFleetNodeAsync</c> did exactly that, deriving
    /// <c>Admitted</c> from a snapshot taken on a database the store had already closed. A
    /// <c>POST /fleet/nodes/{peerId}/revoke</c> committing in that window was written straight back
    /// to <c>true</c>, and placement selects on <c>Admitted &amp;&amp; !Drained</c>, so the peer an
    /// operator had just revoked went back into the eligible set. Revocation is the fleet's
    /// containment action; it is not something a reconnecting node's own heartbeat may undo. The
    /// merge now runs inside the store's transaction, so there is no window and no way to express the
    /// old shape.</para>
    ///
    /// <para><b>What the merge may not do.</b> Synchronous, no I/O, no call back into this registry:
    /// it runs inside the LiteDB transaction while the store holds a non-reentrant
    /// <c>SemaphoreSlim(1,1)</c>, and <c>LiteDbAtomic.Mutate</c> refuses a nested transaction. Do the
    /// validating and the awaiting before the call and close over the result.</para>
    /// </remarks>
    Task<MeshFleetNodeState> RegisterOrMergeAsync(
        string peerId,
        Func<MeshFleetNodeState?, MeshFleetNodeState> merge,
        CancellationToken cancellationToken = default);

    /// <summary>Removes a node.</summary>
    /// <param name="peerId">Worker peer id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True when a document was deleted.</returns>
    Task<bool> RemoveAsync(string peerId, CancellationToken cancellationToken = default);

    /// <summary>Reads every node.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>All nodes, ordered by peer id.</returns>
    Task<IReadOnlyList<MeshFleetNodeState>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Reads one node.</summary>
    /// <param name="peerId">Worker peer id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The node, or null.</returns>
    Task<MeshFleetNodeState?> GetAsync(string peerId, CancellationToken cancellationToken = default);

    /// <summary>Sets the drain flag.</summary>
    /// <param name="peerId">Worker peer id.</param>
    /// <param name="drained">Whether the node is being taken out of service.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True when the node existed.</returns>
    Task<bool> SetDrainedAsync(string peerId, bool drained, CancellationToken cancellationToken = default);

    /// <summary>Sets the admission flag.</summary>
    /// <param name="peerId">Worker peer id.</param>
    /// <param name="admitted">Whether the node may receive work.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True when the node existed.</returns>
    Task<bool> SetAdmittedAsync(string peerId, bool admitted, CancellationToken cancellationToken = default);

    /// <param name="peerId">Worker peer id.</param>
    /// <param name="reportedQueueDepth">Optional worker-reported queue depth for elastic placement (Phase 5).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task HeartbeatAsync(string peerId, int? reportedQueueDepth = null, CancellationToken cancellationToken = default);
}
