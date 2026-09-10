using LiteDB;
using Ashlar.Core.Application.SelfContext.Models;
using Ashlar.Core.Application.SelfContext.Ports;

namespace Ashlar.Infrastructure.SelfContext;

/// <summary>
/// LiteDB-backed test failure store. Phase F: wire test failures into adaptation trigger.
/// </summary>
public sealed class LiteDbTestFailureStore : ITestFailureStore
{
    private const string CollectionName = "test_failures";
    private readonly string _connectionString;
    private readonly object _indexGate = new();
    private bool _indexesReady;

    /// <summary>Initializes a new lite db test failure store.</summary>
    public LiteDbTestFailureStore(string pathOrConnectionString)
    {
        if (string.IsNullOrWhiteSpace(pathOrConnectionString))
            throw new ArgumentNullException(nameof(pathOrConnectionString));
        var trimmed = pathOrConnectionString.Trim();
        _connectionString = trimmed.StartsWith("Filename=", StringComparison.OrdinalIgnoreCase) ? trimmed : $"Filename={trimmed}";
    }

    /// <inheritdoc />
    public Task RecordAsync(TestFailureRecord record, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var db = new LiteDatabase(_connectionString);
        var col = db.GetCollection<TestFailureDoc>(CollectionName);
        EnsureIndexes(col);
        col.Insert(new TestFailureDoc
        {
            Id = record.Id,
            Timestamp = record.Timestamp,
            TestName = record.TestName,
            FilePath = record.FilePath,
            ErrorMessage = record.ErrorMessage,
            StackTrace = record.StackTrace,
        });
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<TestFailureRecord>> QueryAsync(DateTimeOffset? since = null, DateTimeOffset? until = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var db = new LiteDatabase(_connectionString);
        var col = db.GetCollection<TestFailureDoc>(CollectionName);
        // BsonExpression, not LINQ, for the same reason EnsureIndexes uses the string overload:
        // LiteDB resolves a LINQ predicate through a BsonMapper that is not safe to drive from
        // several threads at once, and a read concurrent with a write can throw
        // NotSupportedException out of LinqExpressionVisitor.ResolveMember. Reads and writes here
        // ARE concurrent -- SelfImprovementLoop reads the history while the ingestion bridge and
        // the CLI ingest-failures command record into it. Parameters are bound rather than
        // interpolated, so a caller-supplied value cannot alter the filter.
        var query = col.Query();
        if (since.HasValue)
            // Serialize through the same mapper that wrote the documents, so the comparison is against
            // the representation actually stored rather than whatever a DateTimeOffset converts to.
            query = query.Where("$.Timestamp >= @0", BsonMapper.Global.Serialize(since.Value));
        if (until.HasValue)
            query = query.Where("$.Timestamp <= @0", BsonMapper.Global.Serialize(until.Value));
        var docs = query.OrderByDescending("$.Timestamp").Limit(100).ToList();
        var records = docs.Select(d => new TestFailureRecord
        {
            Id = d.Id,
            Timestamp = d.Timestamp,
            TestName = d.TestName,
            FilePath = d.FilePath,
            ErrorMessage = d.ErrorMessage,
            StackTrace = d.StackTrace,
        }).ToList();
        return Task.FromResult<IReadOnlyList<TestFailureRecord>>(records);
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
    private void EnsureIndexes(ILiteCollection<TestFailureDoc> col)
    {
        if (Volatile.Read(ref _indexesReady)) return;

        lock (_indexGate)
        {
            if (_indexesReady) return;
            col.EnsureIndex(nameof(TestFailureDoc.Timestamp));
            Volatile.Write(ref _indexesReady, true);
        }
    }

    private sealed class TestFailureDoc
    {
        /// <summary>Id.</summary>
        [BsonId]
        public string Id { get; set; } = string.Empty;
        /// <summary>Timestamp.</summary>
        public DateTimeOffset Timestamp { get; set; }
        /// <summary>Test name.</summary>
        public string TestName { get; set; } = string.Empty;
        /// <summary>File path.</summary>
        public string? FilePath { get; set; }
        /// <summary>Error message.</summary>
        public string? ErrorMessage { get; set; }
        /// <summary>Stack trace.</summary>
        public string? StackTrace { get; set; }
    }
}
