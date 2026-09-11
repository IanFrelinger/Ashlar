using LiteDB;
using Ashlar.Core.Application.Adaptation.Models;
using Ashlar.Core.Application.Adaptation.Ports;
using Ashlar.Infrastructure.Persistence;

namespace Ashlar.Infrastructure.Adaptation;

/// <summary>
/// LiteDB-backed adaptation log. Persistent, queryable.
/// </summary>
public sealed class LiteDbAdaptationLog : IAdaptationLog
{
    private const string CollectionName = "adaptation_records";
    private readonly string _connectionString;
    private readonly object _indexGate = new();
    private bool _indexesReady;

    /// <summary>Initializes a new lite db adaptation log.</summary>
    public LiteDbAdaptationLog(string pathOrConnectionString)
    {
        if (string.IsNullOrWhiteSpace(pathOrConnectionString))
            throw new ArgumentNullException(nameof(pathOrConnectionString));
        var trimmed = pathOrConnectionString.Trim();
        _connectionString = trimmed.StartsWith("Filename=", StringComparison.OrdinalIgnoreCase) ? trimmed : $"Filename={trimmed}";
        LiteDbDocumentMapper.EnsureMapped<AdaptationDoc>();
    }

    /// <inheritdoc />
    public Task LogAsync(AdaptationRecord record, CancellationToken cancellationToken = default)
    {
        LiteDbDocumentMapper.EnsureMapped<AdaptationDoc>();
        cancellationToken.ThrowIfCancellationRequested();
        using var db = new LiteDatabase(_connectionString);
        var col = db.GetCollection<AdaptationDoc>(CollectionName);
        EnsureIndexes(col);
        col.Insert(ToDoc(record));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<AdaptationRecord>> QueryAsync(DateTimeOffset? since = null, DateTimeOffset? until = null, string? brickId = null, CancellationToken cancellationToken = default)
    {
        LiteDbDocumentMapper.EnsureMapped<AdaptationDoc>();
        cancellationToken.ThrowIfCancellationRequested();
        using var db = new LiteDatabase(_connectionString);
        var col = db.GetCollection<AdaptationDoc>(CollectionName);
        // BsonExpression, not LINQ, for the same reason EnsureIndexes uses the string overload:
        // LiteDB resolves a LINQ predicate through a BsonMapper that is not safe to drive from
        // several threads at once, and a read concurrent with a write can throw
        // NotSupportedException out of LinqExpressionVisitor.ResolveMember. Reads and writes here
        // ARE concurrent -- KnowledgeQueryService reads this behind GET /knowledge/query while
        // the adaptation writers log. Parameters are bound rather than interpolated, so a
        // caller-supplied value cannot alter the filter.
        var query = col.Query();
        if (since.HasValue)
            // Serialize through the same mapper that wrote the documents, so the comparison is against
            // the representation actually stored rather than whatever a DateTimeOffset converts to.
            query = query.Where("$.Timestamp >= @0", BsonMapper.Global.Serialize(since.Value));
        if (until.HasValue)
            query = query.Where("$.Timestamp <= @0", BsonMapper.Global.Serialize(until.Value));
        if (!string.IsNullOrWhiteSpace(brickId))
            query = query.Where("$.BrickId = @0", brickId);
        var docs = query.OrderByDescending("$.Timestamp").ToList();
        var records = docs.Select(ToRecord).ToList();
        return Task.FromResult<IReadOnlyList<AdaptationRecord>>(records);
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
    private void EnsureIndexes(ILiteCollection<AdaptationDoc> col)
    {
        if (Volatile.Read(ref _indexesReady)) return;

        lock (_indexGate)
        {
            if (_indexesReady) return;
            col.EnsureIndex(nameof(AdaptationDoc.Timestamp));
            col.EnsureIndex(nameof(AdaptationDoc.BrickId));
            Volatile.Write(ref _indexesReady, true);
        }
    }

    private static AdaptationDoc ToDoc(AdaptationRecord r)
    {
        return new AdaptationDoc
        {
            Id = r.Id,
            Timestamp = r.Timestamp,
            BrickId = r.BrickId,
            FailureType = r.FailureType,
            FixApplied = (int)r.FixApplied,
            FilePath = r.FilePath,
            RegressionPassed = r.RegressionPassed,
            Promoted = r.Promoted,
            Message = r.Message,
        };
    }

    private static AdaptationRecord ToRecord(AdaptationDoc d)
    {
        return new AdaptationRecord
        {
            Id = d.Id,
            Timestamp = d.Timestamp,
            BrickId = d.BrickId,
            FailureType = d.FailureType,
            FixApplied = (AdaptationFixType)d.FixApplied,
            FilePath = d.FilePath,
            RegressionPassed = d.RegressionPassed,
            Promoted = d.Promoted,
            Message = d.Message,
        };
    }

    private sealed class AdaptationDoc
    {
        /// <summary>Id.</summary>
        [BsonId]
        public string Id { get; set; } = string.Empty;
        /// <summary>Timestamp.</summary>
        public DateTimeOffset Timestamp { get; set; }
        /// <summary>Brick id.</summary>
        public string? BrickId { get; set; }
        /// <summary>Failure type.</summary>
        public string FailureType { get; set; } = string.Empty;
        /// <summary>Fix applied.</summary>
        public int FixApplied { get; set; }
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
