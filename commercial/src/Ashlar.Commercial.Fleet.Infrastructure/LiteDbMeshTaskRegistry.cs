using LiteDB;
using Ashlar.Commercial.Fleet.Contracts.Models;
using Ashlar.Commercial.Fleet.Contracts.Ports;
using Ashlar.Core.Application.Persistence;

namespace Ashlar.Commercial.Fleet.Infrastructure;

/// <summary>
/// LiteDB-backed mesh task registry (director persistence).
/// </summary>
public sealed class LiteDbMeshTaskRegistry : IMeshTaskRegistry
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly object _indexGate = new();
    private bool _indexesReady;

    public LiteDbMeshTaskRegistry(string pathOrConnectionString)
    {
        if (string.IsNullOrWhiteSpace(pathOrConnectionString))
            throw new ArgumentNullException(nameof(pathOrConnectionString));
        _connectionString = LiteDbConnectionString.ForSharedAccess(pathOrConnectionString, nameof(pathOrConnectionString));
        LiteDbDocumentMapper.EnsureMapped<MeshTaskDoc>();
    }

    /// <summary>Creates async.</summary>
    public async Task<MeshTaskState> CreateAsync(MeshTaskCreateSpec spec, CancellationToken cancellationToken = default)
    {
        LiteDbDocumentMapper.EnsureMapped<MeshTaskDoc>();
        var id = $"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}";
        var correlation = string.IsNullOrWhiteSpace(spec.CorrelationId) ? null : spec.CorrelationId.Trim();
        var idem = string.IsNullOrWhiteSpace(spec.IdempotencyKey) ? null : spec.IdempotencyKey.Trim();

        var task = new MeshTaskState(
            TaskId: id,
            Name: spec.Name,
            Steps: Math.Max(1, spec.Steps),
            RequiredBrickIds: spec.RequiredBrickIds ?? Array.Empty<string>(),
            Affinity: spec.Affinity ?? new Dictionary<string, string>(),
            Priority: spec.Priority,
            DeadlineUtc: spec.DeadlineUtc,
            Status: MeshTaskStatus.Pending,
            AssignedPeerId: null,
            AssignedApiBaseUrl: null,
            PlacementReason: null,
            AttemptCount: 0,
            CreatedAtUtc: DateTimeOffset.UtcNow,
            LastScheduledAtUtc: null,
            CorrelationId: correlation,
            IdempotencyKey: idem,
            LastScheduleIdempotencyKey: null,
            ResultSummary: null,
            ResultHandle: null,
            LeaseToken: null,
            LeaseOwnerPeerId: null,
            LeaseExpiresUtc: null,
            CheckpointHandle: null);

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var db = new LiteDatabase(_connectionString);
            var col = db.GetCollection<MeshTaskDoc>(LiteDbMeshDirectorConnection.TasksCollection);
            EnsureIndexes(col);

            // Check-then-insert, and the client retry that Idempotency-Key exists to absorb IS the
            // concurrent case. The index on IdempotencyKey is not unique, so nothing at the database
            // level would catch two racing submissions of one key -- they would both insert, both get
            // placed and both execute. The transaction makes the probe and the insert one operation.
            return LiteDbAtomic.Mutate(db, () =>
            {
                if (idem is not null)
                {
                    // BsonExpression, not LINQ. _lock does not cover this: TryGetByIdempotencyKeyAsync is
                    // the one method on this class that does not take it, and it reads the same collection
                    // from an HTTP request thread while this runs. The key is bound rather than
                    // interpolated, so a request body cannot alter the filter.
                    var existing = col.FindOne("$.IdempotencyKey = @0", idem);
                    if (existing is not null)
                        return existing.ToState();
                }

                col.Insert(MeshTaskDoc.FromState(task));
                return task;
            });
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Attempts to get by idempotency key async.</summary>
    public Task<MeshTaskState?> TryGetByIdempotencyKeyAsync(string idempotencyKey, CancellationToken cancellationToken = default)
    {
        LiteDbDocumentMapper.EnsureMapped<MeshTaskDoc>();
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            return Task.FromResult<MeshTaskState?>(null);

        var key = idempotencyKey.Trim();
        using var db = new LiteDatabase(_connectionString);
        var col = db.GetCollection<MeshTaskDoc>(LiteDbMeshDirectorConnection.TasksCollection);
        // See CreateAsync: this method takes no lock, so it runs freely against CreateAsync's index
        // declaration and insert. Bound parameter -- the key arrives in an HTTP request body.
        var doc = col.FindOne("$.IdempotencyKey = @0", key);
        return Task.FromResult(doc?.ToState());
    }

    /// <summary>
    /// Declares the indexes once per store, by field name.
    /// </summary>
    /// <remarks>
    /// Two problems with declaring them on every write. LiteDB resolves an <c>EnsureIndex(x =&gt; x.Field)</c>
    /// expression through a BsonMapper that is not safe to drive from several threads at once —
    /// concurrent writers threw <c>NotSupportedException</c> out of <c>LinqExpressionVisitor.ResolveMember</c>
    /// (green on Windows, red in CI on Linux, which is the timing difference doing what timing
    /// differences do). And re-declaring an index that already exists is work no write needs to repeat.
    /// The string overload skips the expression visitor entirely; the flag skips the call after the
    /// first success.
    /// </remarks>
    private void EnsureIndexes(ILiteCollection<MeshTaskDoc> col)
    {
        if (Volatile.Read(ref _indexesReady)) return;

        lock (_indexGate)
        {
            if (_indexesReady) return;
            col.EnsureIndex(nameof(MeshTaskDoc.IdempotencyKey));
            Volatile.Write(ref _indexesReady, true);
        }
    }

    /// <summary>Gets async.</summary>
    public async Task<MeshTaskState?> GetAsync(string taskId, CancellationToken cancellationToken = default)
    {
        LiteDbDocumentMapper.EnsureMapped<MeshTaskDoc>();
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var db = new LiteDatabase(_connectionString);
            var col = db.GetCollection<MeshTaskDoc>(LiteDbMeshDirectorConnection.TasksCollection);
            var doc = col.FindById(taskId);
            return doc?.ToState();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>List async operation.</summary>
    public async Task<IReadOnlyList<MeshTaskState>> ListAsync(CancellationToken cancellationToken = default)
    {
        LiteDbDocumentMapper.EnsureMapped<MeshTaskDoc>();
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var db = new LiteDatabase(_connectionString);
            var col = db.GetCollection<MeshTaskDoc>(LiteDbMeshDirectorConnection.TasksCollection);
            return col.FindAll()
                .Select(d => d.ToState())
                .OrderByDescending(t => t.Priority)
                .ThenByDescending(t => t.CreatedAtUtc)
                .ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Update async operation.</summary>
    /// <param name="taskId">Task id.</param>
    /// <param name="transform">Applied to the document read inside the transaction; null declines.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What happened and the state the store holds.</returns>
    /// <remarks>
    /// The read, the transform and the write are one transaction, so the precondition the transform
    /// evaluates is evaluated against the document that is about to be overwritten rather than
    /// against a snapshot from a database this store has since closed. That outer pair was the lost
    /// update the port comment describes. Measured at the call sites with this transaction removed:
    /// 24 of 24 rounds accepted BOTH a migrate and a completion on one task, 20 of 20 rounds ended
    /// with a revoked fleet node re-admitted, and nothing threw in either.
    /// </remarks>
    public async Task<MeshTaskUpdateResult> UpdateAsync(
        string taskId,
        Func<MeshTaskState, MeshTaskState?> transform,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(taskId))
            throw new ArgumentException("taskId is required.", nameof(taskId));
        if (transform is null)
            throw new ArgumentNullException(nameof(transform));

        LiteDbDocumentMapper.EnsureMapped<MeshTaskDoc>();
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var db = new LiteDatabase(_connectionString);
            var col = db.GetCollection<MeshTaskDoc>(LiteDbMeshDirectorConnection.TasksCollection);
            return LiteDbAtomic.Mutate(db, () =>
            {
                var current = col.FindById(taskId);
                if (current is null)
                    return new MeshTaskUpdateResult(MeshTaskUpdateOutcome.NotFound, null);

                var currentState = current.ToState();
                var next = transform(currentState);
                if (next is null)
                    return new MeshTaskUpdateResult(MeshTaskUpdateOutcome.PreconditionFailed, currentState);

                // The transform may not move the document: the id it was read under is the id it is
                // written back under, and a changed TaskId would insert a second document under the
                // old one's contents.
                if (!string.Equals(next.TaskId, taskId, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"a mesh task transform may not change TaskId ('{taskId}' -> '{next.TaskId}'). "
                        + "The write is addressed by the id the read used; changing it would leave two "
                        + "documents where the caller expected one.");
                }

                col.Update(MeshTaskDoc.FromState(next));
                return new MeshTaskUpdateResult(MeshTaskUpdateOutcome.Applied, next);
            });
        }
        finally
        {
            _lock.Release();
        }
    }
}
