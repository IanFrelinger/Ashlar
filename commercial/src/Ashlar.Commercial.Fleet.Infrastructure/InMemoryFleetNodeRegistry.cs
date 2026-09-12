using System.Collections.Concurrent;
using Ashlar.Commercial.Fleet.Contracts.Models;
using Ashlar.Commercial.Fleet.Contracts.Ports;

namespace Ashlar.Commercial.Fleet.Infrastructure;

/// <summary>
/// Thread-safe in-memory fleet registry (Phase 1). Replaced by external store in later phases.
/// </summary>
public sealed class InMemoryFleetNodeRegistry : IFleetNodeRegistry
{
    private readonly ConcurrentDictionary<string, MeshFleetNodeState> _nodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>Register or merge async operation.</summary>
    /// <param name="peerId">Worker peer id.</param>
    /// <param name="merge">Builds the document from the stored one (null when new), under this registry's lock.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The state that was written.</returns>
    public async Task<MeshFleetNodeState> RegisterOrMergeAsync(
        string peerId,
        Func<MeshFleetNodeState?, MeshFleetNodeState> merge,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(peerId))
            throw new ArgumentException("peerId is required.", nameof(peerId));
        if (merge is null)
            throw new ArgumentNullException(nameof(merge));

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _nodes.TryGetValue(peerId, out var current);
            var next = merge(current);
            if (next is null)
                throw new InvalidOperationException("the merge returned null; it must return the document to store.");
            if (!string.Equals(next.PeerId, peerId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"a fleet node merge may not change PeerId ('{peerId}' -> '{next.PeerId}'). "
                    + "The LiteDB registry addresses the write by the id the read used; this store "
                    + "refuses the same shape so the two implementations cannot diverge.");
            }

            _nodes[next.PeerId] = next;
            return next;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Remove async operation.</summary>
    public async Task<bool> RemoveAsync(string peerId, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _nodes.TryRemove(peerId, out _);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>List async operation.</summary>
    public async Task<IReadOnlyList<MeshFleetNodeState>> ListAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _nodes.Values.OrderBy(n => n.PeerId, StringComparer.OrdinalIgnoreCase).ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Gets async.</summary>
    public async Task<MeshFleetNodeState?> GetAsync(string peerId, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _nodes.TryGetValue(peerId, out var n) ? n : null;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Sets drained async.</summary>
    public async Task<bool> SetDrainedAsync(string peerId, bool drained, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_nodes.TryGetValue(peerId, out var existing))
                return false;
            _nodes[peerId] = existing with { Drained = drained };
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Sets admitted async.</summary>
    public async Task<bool> SetAdmittedAsync(string peerId, bool admitted, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_nodes.TryGetValue(peerId, out var existing))
                return false;
            _nodes[peerId] = existing with { Admitted = admitted };
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Heartbeat async operation.</summary>
    public async Task HeartbeatAsync(string peerId, int? reportedQueueDepth = null, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_nodes.TryGetValue(peerId, out var existing))
            {
                var depth = reportedQueueDepth ?? existing.ReportedQueueDepth;
                if (depth < 0) depth = 0;
                _nodes[peerId] = existing with
                {
                    LastHeartbeatUtc = DateTimeOffset.UtcNow,
                    ReportedQueueDepth = depth
                };
            }
        }
        finally
        {
            _lock.Release();
        }
    }
}
