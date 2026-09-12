using LiteDB;
using Ashlar.Commercial.Fleet.Contracts.Models;
using Ashlar.Commercial.Fleet.Contracts.Ports;
using Ashlar.Core.Application.Persistence;

namespace Ashlar.Commercial.Fleet.Infrastructure;

/// <summary>
/// LiteDB-backed fleet node registry (director persistence).
/// </summary>
public sealed class LiteDbFleetNodeRegistry : IFleetNodeRegistry
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public LiteDbFleetNodeRegistry(string pathOrConnectionString)
    {
        if (string.IsNullOrWhiteSpace(pathOrConnectionString))
            throw new ArgumentNullException(nameof(pathOrConnectionString));
        _connectionString = LiteDbConnectionString.ForSharedAccess(pathOrConnectionString, nameof(pathOrConnectionString));
        LiteDbDocumentMapper.EnsureMapped<MeshFleetNodeDoc>();
    }

    /// <summary>Register or merge async operation.</summary>
    /// <param name="peerId">Worker peer id.</param>
    /// <param name="merge">Builds the document from the one read inside the transaction (null when new).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The state that was written.</returns>
    /// <remarks>
    /// The read the merge needs happens here, inside the transaction, instead of in the endpoint on
    /// a database this store had already closed. That is the whole fix: a revoke committing between
    /// a registering node's read and its write used to be overwritten with the <c>Admitted = true</c>
    /// the read had seen.
    /// </remarks>
    public async Task<MeshFleetNodeState> RegisterOrMergeAsync(
        string peerId,
        Func<MeshFleetNodeState?, MeshFleetNodeState> merge,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(peerId))
            throw new ArgumentException("peerId is required.", nameof(peerId));
        if (merge is null)
            throw new ArgumentNullException(nameof(merge));

        LiteDbDocumentMapper.EnsureMapped<MeshFleetNodeDoc>();
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var db = new LiteDatabase(_connectionString);
            var col = db.GetCollection<MeshFleetNodeDoc>(LiteDbMeshDirectorConnection.FleetCollection);
            return LiteDbAtomic.Mutate(db, () =>
            {
                var current = col.FindById(peerId);
                var next = merge(current?.ToState());
                if (next is null)
                    throw new InvalidOperationException("the merge returned null; it must return the document to store.");
                if (!string.Equals(next.PeerId, peerId, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"a fleet node merge may not change PeerId ('{peerId}' -> '{next.PeerId}'). "
                        + "The write is addressed by the id the read used.");
                }

                col.Upsert(MeshFleetNodeDoc.FromState(next));
                return next;
            });
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Remove async operation.</summary>
    public async Task<bool> RemoveAsync(string peerId, CancellationToken cancellationToken = default)
    {
        LiteDbDocumentMapper.EnsureMapped<MeshFleetNodeDoc>();
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var db = new LiteDatabase(_connectionString);
            var col = db.GetCollection<MeshFleetNodeDoc>(LiteDbMeshDirectorConnection.FleetCollection);
            return col.Delete(peerId);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>List async operation.</summary>
    public async Task<IReadOnlyList<MeshFleetNodeState>> ListAsync(CancellationToken cancellationToken = default)
    {
        LiteDbDocumentMapper.EnsureMapped<MeshFleetNodeDoc>();
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var db = new LiteDatabase(_connectionString);
            var col = db.GetCollection<MeshFleetNodeDoc>(LiteDbMeshDirectorConnection.FleetCollection);
            return col.FindAll()
                .Select(d => d.ToState())
                .OrderBy(n => n.PeerId, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Gets async.</summary>
    public async Task<MeshFleetNodeState?> GetAsync(string peerId, CancellationToken cancellationToken = default)
    {
        LiteDbDocumentMapper.EnsureMapped<MeshFleetNodeDoc>();
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var db = new LiteDatabase(_connectionString);
            var col = db.GetCollection<MeshFleetNodeDoc>(LiteDbMeshDirectorConnection.FleetCollection);
            var doc = col.FindById(peerId);
            return doc?.ToState();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Sets drained async.</summary>
    public async Task<bool> SetDrainedAsync(string peerId, bool drained, CancellationToken cancellationToken = default)
    {
        LiteDbDocumentMapper.EnsureMapped<MeshFleetNodeDoc>();
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var db = new LiteDatabase(_connectionString);
            var col = db.GetCollection<MeshFleetNodeDoc>(LiteDbMeshDirectorConnection.FleetCollection);
            // Whole-document write-back off a snapshot: without the transaction a heartbeat landing
            // between the read and the write reverts Drained to what it read, and placement selects on
            // Admitted && !Drained -- so the node keeps taking work it was being drained of.
            return LiteDbAtomic.Mutate(db, () =>
            {
                var doc = col.FindById(peerId);
                if (doc is null)
                    return false;
                doc.Drained = drained;
                return col.Update(doc);
            });
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Sets admitted async.</summary>
    public async Task<bool> SetAdmittedAsync(string peerId, bool admitted, CancellationToken cancellationToken = default)
    {
        LiteDbDocumentMapper.EnsureMapped<MeshFleetNodeDoc>();
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var db = new LiteDatabase(_connectionString);
            var col = db.GetCollection<MeshFleetNodeDoc>(LiteDbMeshDirectorConnection.FleetCollection);
            // Same shape as SetDrainedAsync, and the same loss in the other direction: un-admitting a
            // peer is precisely the operation a stale heartbeat must not be able to undo.
            return LiteDbAtomic.Mutate(db, () =>
            {
                var doc = col.FindById(peerId);
                if (doc is null)
                    return false;
                doc.Admitted = admitted;
                return col.Update(doc);
            });
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Heartbeat async operation.</summary>
    public async Task HeartbeatAsync(string peerId, int? reportedQueueDepth = null, CancellationToken cancellationToken = default)
    {
        LiteDbDocumentMapper.EnsureMapped<MeshFleetNodeDoc>();
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var db = new LiteDatabase(_connectionString);
            var col = db.GetCollection<MeshFleetNodeDoc>(LiteDbMeshDirectorConnection.FleetCollection);
            // The highest-frequency writer on this file -- every fleet node on a timer -- and the one
            // whose stale snapshot silently re-admits and un-drains a peer an operator just took out.
            LiteDbAtomic.Mutate(db, () =>
            {
                var doc = col.FindById(peerId);
                if (doc is null)
                    return;

                var depth = reportedQueueDepth ?? doc.ReportedQueueDepth;
                if (depth < 0) depth = 0;
                doc.LastHeartbeatUtc = DateTimeOffset.UtcNow;
                doc.ReportedQueueDepth = depth;
                col.Update(doc);
            });
        }
        finally
        {
            _lock.Release();
        }
    }
}
