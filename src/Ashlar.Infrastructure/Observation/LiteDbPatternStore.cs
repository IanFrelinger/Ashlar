using LiteDB;
using Ashlar.Core.Application.Observation.Models;
using Ashlar.Core.Application.Observation.Ports;
using Ashlar.Infrastructure.Persistence;

namespace Ashlar.Infrastructure.Observation;
/// <summary>
/// LiteDB-backed pattern store. Persistent, queryable, survives restarts.
/// </summary>
public sealed class LiteDbPatternStore : IPatternStore
{
    private const string CollectionName = "observed_patterns";
    private readonly string _connectionString;
    private readonly object _indexGate = new();
    private bool _indexesReady;
    /// <summary>
    /// Creates a new LiteDB-backed pattern store.
    /// </summary>
    /// <param name = "pathOrConnectionString">File path (e.g. patterns.db) or LiteDB connection string.</param>
    public LiteDbPatternStore(string pathOrConnectionString)
    {
        if (string.IsNullOrWhiteSpace(pathOrConnectionString))
            throw new ArgumentNullException(nameof(pathOrConnectionString));
        var trimmed = pathOrConnectionString.Trim();
        _connectionString = trimmed.StartsWith("Filename=", StringComparison.OrdinalIgnoreCase) ? trimmed : $"Filename={trimmed}";
        LiteDbDocumentMapper.EnsureMapped<PatternDoc>();
    }

    /// <inheritdoc/>
    public Task AddAsync(ObservedPattern pattern, CancellationToken cancellationToken = default)
    {
        LiteDbDocumentMapper.EnsureMapped<PatternDoc>();
        cancellationToken.ThrowIfCancellationRequested();
        using var db = new LiteDatabase(_connectionString);
        var col = db.GetCollection<PatternDoc>(CollectionName);
        EnsureIndexes(col);
        var doc = ToDoc(pattern);
        col.Insert(doc);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<ObservedPattern>> QueryAsync(PatternStoreQueryParams query, CancellationToken cancellationToken = default)
    {
        LiteDbDocumentMapper.EnsureMapped<PatternDoc>();
        cancellationToken.ThrowIfCancellationRequested();
        using var db = new LiteDatabase(_connectionString);
        var col = db.GetCollection<PatternDoc>(CollectionName);
        // BsonExpression, not LINQ, for the same reason EnsureIndexes uses the string overload:
        // LiteDB resolves a LINQ predicate through a BsonMapper that is not safe to drive from
        // several threads at once, and a read concurrent with a write can throw
        // NotSupportedException out of LinqExpressionVisitor.ResolveMember. Reads and writes here
        // ARE concurrent -- ObservationPipelineService writes here as a hosted service while
        // KnowledgeQueryService reads it behind GET /knowledge/query. Parameters are bound rather
        // than interpolated, so a caller-supplied value cannot alter the filter.
        var bsonQuery = col.Query();
        if (query.Since.HasValue)
            // Serialize through the same mapper that wrote the documents, so the comparison is against
            // the representation actually stored rather than whatever a DateTimeOffset converts to.
            bsonQuery = bsonQuery.Where("$.LastSeen >= @0", BsonMapper.Global.Serialize(query.Since.Value));
        if (query.Until.HasValue)
            bsonQuery = bsonQuery.Where("$.FirstSeen <= @0", BsonMapper.Global.Serialize(query.Until.Value));
        if (!string.IsNullOrWhiteSpace(query.ProjectPath))
            bsonQuery = bsonQuery.Where("$.ProjectPath = @0", query.ProjectPath);
        if (!string.IsNullOrWhiteSpace(query.EventType))
            bsonQuery = bsonQuery.Where("$.EventType = @0", query.EventType);
        var docs = bsonQuery.OrderByDescending("$.LastSeen").Limit(query.MaxCount).ToList();
        var patterns = docs.Select(ToPattern).ToList();
        return Task.FromResult<IReadOnlyList<ObservedPattern>>(patterns);
    }

    /// <inheritdoc/>
    public Task PersistAsync(CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
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
    private void EnsureIndexes(ILiteCollection<PatternDoc> col)
    {
        if (Volatile.Read(ref _indexesReady)) return;

        lock (_indexGate)
        {
            if (_indexesReady) return;
            col.EnsureIndex(nameof(PatternDoc.LastSeen));
            col.EnsureIndex(nameof(PatternDoc.FirstSeen));
            col.EnsureIndex(nameof(PatternDoc.EventType));
            col.EnsureIndex(nameof(PatternDoc.ProjectPath));
            Volatile.Write(ref _indexesReady, true);
        }
    }

    private static PatternDoc ToDoc(ObservedPattern p)
    {
        return new PatternDoc
        {
            PatternId = p.PatternId,
            EventType = p.EventType,
            Frequency = p.Frequency,
            FirstSeen = p.FirstSeen,
            LastSeen = p.LastSeen,
            ProjectPath = p.ProjectPath,
            RelatedEventIds = p.RelatedEventIds?.ToArray() ?? Array.Empty<string>(),
            MetadataJson = p.Metadata.HasValue ? p.Metadata.Value.GetRawText() : null,
        };
    }

    private static ObservedPattern ToPattern(PatternDoc d)
    {
        System.Text.Json.JsonElement? metadata = null;
        if (!string.IsNullOrEmpty(d.MetadataJson))
        {
            try
            {
                metadata = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(d.MetadataJson);
            }
            catch
            {
                // Ignore invalid JSON
            }
        }

        return new ObservedPattern
        {
            PatternId = d.PatternId,
            EventType = d.EventType,
            Frequency = d.Frequency,
            FirstSeen = d.FirstSeen,
            LastSeen = d.LastSeen,
            ProjectPath = d.ProjectPath,
            RelatedEventIds = d.RelatedEventIds ?? Array.Empty<string>(),
            Metadata = metadata,
        };
    }

    private sealed class PatternDoc
    {
        /// <summary>Pattern id.</summary>
        [BsonId]
        public string PatternId { get; set; } = string.Empty;
        /// <summary>Event type.</summary>
        public string EventType { get; set; } = string.Empty;
        /// <summary>Frequency.</summary>
        public int Frequency { get; set; }
        /// <summary>First seen.</summary>
        public DateTimeOffset FirstSeen { get; set; }
        /// <summary>Last seen.</summary>
        public DateTimeOffset LastSeen { get; set; }
        /// <summary>Related event ids.</summary>
        public string[]? RelatedEventIds { get; set; }
        /// <summary>Metadata json.</summary>
        public string? MetadataJson { get; set; }
        /// <summary>Path to the generated brick project.</summary>
        public string? ProjectPath { get; set; }
    }
}