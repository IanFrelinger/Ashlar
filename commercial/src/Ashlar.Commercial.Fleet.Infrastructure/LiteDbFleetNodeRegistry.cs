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

    /// <summary>Register or update async operation.</summary>
    public async Task RegisterOrUpdateAsync(MeshFleetNodeState node, CancellationToken cancellationToken = default)
    {
        LiteDbDocumentMapper.EnsureMapped<MeshFleetNodeDoc>();
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var db = new LiteDatabase(_connectionString);
            var col = db.GetCollection<MeshFleetNodeDoc>(LiteDbMeshDirectorConnection.FleetCollection);
            col.Upsert(MeshFleetNodeDoc.FromState(node));
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
