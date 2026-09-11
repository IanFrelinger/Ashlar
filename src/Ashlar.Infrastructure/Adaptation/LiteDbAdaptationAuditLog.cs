using LiteDB;
using Ashlar.Core.Application.Adaptation.Models;
using Ashlar.Core.Application.Adaptation.Ports;
using Ashlar.Core.Application.Persistence;
using Ashlar.Infrastructure.Persistence;

namespace Ashlar.Infrastructure.Adaptation;

/// <summary>
/// LiteDB-backed adaptation audit log.
/// </summary>
public sealed class LiteDbAdaptationAuditLog : IAdaptationAuditLog
{
    private const string CollectionName = "adaptation_audit";
    private readonly string _connectionString;
    private readonly object _indexGate = new();
    private bool _indexesReady;

    /// <summary>Initializes a new lite db adaptation audit log.</summary>
    public LiteDbAdaptationAuditLog(string pathOrConnectionString)
    {
        if (string.IsNullOrWhiteSpace(pathOrConnectionString))
            throw new ArgumentNullException(nameof(pathOrConnectionString));
        _connectionString = LiteDbConnectionString.ForSharedAccess(pathOrConnectionString, nameof(pathOrConnectionString));
        LiteDbDocumentMapper.EnsureMapped<AuditDoc>();
    }

    /// <inheritdoc />
    public Task LogAsync(AdaptationAuditEntry entry, CancellationToken cancellationToken = default)
    {
        LiteDbDocumentMapper.EnsureMapped<AuditDoc>();
        cancellationToken.ThrowIfCancellationRequested();
        using var db = new LiteDatabase(_connectionString);
        var col = db.GetCollection<AuditDoc>(CollectionName);
        EnsureIndexes(col);
        col.Insert(ToDoc(entry));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<AdaptationAuditEntry>> QueryAsync(DateTimeOffset? since = null, DateTimeOffset? until = null, CancellationToken cancellationToken = default)
    {
        LiteDbDocumentMapper.EnsureMapped<AuditDoc>();
        cancellationToken.ThrowIfCancellationRequested();
        using var db = new LiteDatabase(_connectionString);
        var col = db.GetCollection<AuditDoc>(CollectionName);
        // BsonExpression, not LINQ, for the same reason EnsureIndexes uses the string overload:
        // LiteDB resolves a LINQ predicate through a BsonMapper that is not safe to drive from
        // several threads at once, and a read concurrent with a write can throw
        // NotSupportedException out of LinqExpressionVisitor.ResolveMember. Reads and writes here
        // ARE concurrent -- RollbackManager, AdaptationPromoter and NewBrickGenerator write to
        // the same singleton this reads. Parameters are bound rather than interpolated, so a
        // caller-supplied value cannot alter the filter.
        var query = col.Query();
        if (since.HasValue)
            // Serialize through the same mapper that wrote the documents, so the comparison is against
            // the representation actually stored rather than whatever a DateTimeOffset converts to.
            query = query.Where("$.Timestamp >= @0", BsonMapper.Global.Serialize(since.Value));
        if (until.HasValue)
            query = query.Where("$.Timestamp <= @0", BsonMapper.Global.Serialize(until.Value));
        var docs = query.OrderByDescending("$.Timestamp").ToList();
        var entries = docs.Select(ToEntry).ToList();
        return Task.FromResult<IReadOnlyList<AdaptationAuditEntry>>(entries);
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
    private void EnsureIndexes(ILiteCollection<AuditDoc> col)
    {
        if (Volatile.Read(ref _indexesReady)) return;

        lock (_indexGate)
        {
            if (_indexesReady) return;
            col.EnsureIndex(nameof(AuditDoc.Timestamp));
            Volatile.Write(ref _indexesReady, true);
        }
    }

    private static AuditDoc ToDoc(AdaptationAuditEntry e)
    {
        return new AuditDoc
        {
            Id = e.Id,
            Timestamp = e.Timestamp,
            AutonomyLevel = e.AutonomyLevel,
            Outcome = e.Outcome,
            BrickId = e.BrickId,
            FailureType = e.FailureType,
            FilePath = e.FilePath,
            RegressionPassed = e.RegressionPassed,
            Promoted = e.Promoted,
            Message = e.Message,
        };
    }

    private static AdaptationAuditEntry ToEntry(AuditDoc d)
    {
        return new AdaptationAuditEntry
        {
            Id = d.Id,
            Timestamp = d.Timestamp,
            AutonomyLevel = d.AutonomyLevel,
            Outcome = d.Outcome,
            BrickId = d.BrickId,
            FailureType = d.FailureType,
            FilePath = d.FilePath,
            RegressionPassed = d.RegressionPassed,
            Promoted = d.Promoted,
            Message = d.Message,
        };
    }

    private sealed class AuditDoc
    {
        /// <summary>Id.</summary>
        [BsonId]
        public string Id { get; set; } = string.Empty;
        /// <summary>Timestamp.</summary>
        public DateTimeOffset Timestamp { get; set; }
        /// <summary>Autonomy level.</summary>
        public string AutonomyLevel { get; set; } = string.Empty;
        /// <summary>Outcome.</summary>
        public string Outcome { get; set; } = string.Empty;
        /// <summary>Brick id.</summary>
        public string? BrickId { get; set; }
        /// <summary>Failure type.</summary>
        public string? FailureType { get; set; }
        /// <summary>File path.</summary>
        public string? FilePath { get; set; }
        /// <summary>Regression passed.</summary>
        public bool RegressionPassed { get; set; }
        /// <summary>Promoted.</summary>
        public bool Promoted { get; set; }
        /// <summary>Message.</summary>
        public string? Message { get; set; }
    }
}
