using LiteDB;
using Ashlar.Core.Application.Observation.Ports;
using Ashlar.Infrastructure.Persistence;

namespace Ashlar.Infrastructure.Observation;

/// <summary>
/// LiteDB-backed store for processed pattern IDs. Uses same DB file as pattern store.
/// </summary>
public sealed class LiteDbPatternProcessedStore : IPatternProcessedStore
{
    private const string CollectionName = "processed_patterns";
    private readonly string _connectionString;
    private readonly object _indexGate = new();
    private bool _indexesReady;

    /// <summary>Initializes a new lite db pattern processed store.</summary>
    public LiteDbPatternProcessedStore(string pathOrConnectionString)
    {
        if (string.IsNullOrWhiteSpace(pathOrConnectionString))
            throw new ArgumentNullException(nameof(pathOrConnectionString));
        var trimmed = pathOrConnectionString.Trim();
        _connectionString = trimmed.StartsWith("Filename=", StringComparison.OrdinalIgnoreCase) ? trimmed : $"Filename={trimmed}";
        LiteDbDocumentMapper.EnsureMapped<ProcessedDoc>();
    }

    /// <summary>Mark processed asynchronously.</summary>
    public Task MarkProcessedAsync(string patternId, CancellationToken cancellationToken = default)
    {
        LiteDbDocumentMapper.EnsureMapped<ProcessedDoc>();
        cancellationToken.ThrowIfCancellationRequested();
        using var db = new LiteDatabase(_connectionString);
        var col = db.GetCollection<ProcessedDoc>(CollectionName);
        EnsureIndexes(col);
        col.Insert(new ProcessedDoc { PatternId = patternId, ProcessedAt = DateTimeOffset.UtcNow });
        return Task.CompletedTask;
    }

    /// <summary>Is processed asynchronously.</summary>
    public Task<bool> IsProcessedAsync(string patternId, CancellationToken cancellationToken = default)
    {
        LiteDbDocumentMapper.EnsureMapped<ProcessedDoc>();
        cancellationToken.ThrowIfCancellationRequested();
        using var db = new LiteDatabase(_connectionString);
        var col = db.GetCollection<ProcessedDoc>(CollectionName);
        // The index is declared by the write path, not here. A read has no business declaring one,
        // and this call was the LINQ-expression form -- the one that races on the BsonMapper.
        // Query.EQ below is already the string-based API and never reaches the expression visitor.
        var doc = col.FindOne(Query.EQ(nameof(ProcessedDoc.PatternId), patternId));
        return Task.FromResult(doc != null);
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
    private void EnsureIndexes(ILiteCollection<ProcessedDoc> col)
    {
        if (Volatile.Read(ref _indexesReady)) return;

        lock (_indexGate)
        {
            if (_indexesReady) return;
            col.EnsureIndex(nameof(ProcessedDoc.PatternId));
            Volatile.Write(ref _indexesReady, true);
        }
    }

    private sealed class ProcessedDoc
    {
        /// <summary>Id.</summary>
        [BsonId]
        public ObjectId Id { get; set; } = ObjectId.NewObjectId();
        /// <summary>Pattern id.</summary>
        public string PatternId { get; set; } = string.Empty;
        /// <summary>Processed at.</summary>
        public DateTimeOffset ProcessedAt { get; set; }
    }
}
