using LiteDB;
using Ashlar.Core.Application.Trust.Models;
using Ashlar.Core.Application.Trust.Ports;
using Ashlar.Core.Application.Persistence;
using Ashlar.Infrastructure.Persistence;

namespace Ashlar.Infrastructure.Trust;

/// <summary>
/// LiteDB-backed user knowledge log. Pure managed C#, runs on all platforms (macOS, Linux, Windows, etc.).
/// Persists entries with provenance and versioning.
/// </summary>
public sealed class LiteDbUserKnowledgeLogStore : IUserKnowledgeLogStore
{
    private readonly string _connectionString;
    private readonly object _indexGate = new();
    private bool _indexesReady;

    private const string CollectionName = "user_knowledge_log";

    /// <summary>
    /// Creates a new LiteDB-backed user knowledge log store.
    /// </summary>
    /// <param name="pathOrConnectionString">File path (e.g. knowledge.db) or LiteDB connection string (e.g. "Filename=knowledge.db").</param>
    public LiteDbUserKnowledgeLogStore(string pathOrConnectionString)
    {
        if (string.IsNullOrWhiteSpace(pathOrConnectionString))
            throw new ArgumentNullException(nameof(pathOrConnectionString));

        _connectionString = LiteDbConnectionString.ForSharedAccess(pathOrConnectionString, nameof(pathOrConnectionString));
        LiteDbDocumentMapper.EnsureMapped<KnowledgeLogDoc>();
    }

    /// <inheritdoc />
    public Task UpsertAsync(UserKnowledgeLogEntry entry, CancellationToken cancellationToken = default)
    {
        LiteDbDocumentMapper.EnsureMapped<KnowledgeLogDoc>();
        using var db = new LiteDatabase(_connectionString);
        var col = db.GetCollection<KnowledgeLogDoc>(CollectionName);
        EnsureIndexes(col);

        // The read decides what the write stores -- Version is derived from the document on disk --
        // so the pair has to be one operation. Shared mode releases its named mutex between two engine
        // calls, so without this two overlapping upserts of one id both read Version=N and both write
        // N+1: one increment and one caller's Content are gone, and nothing is thrown. Measured in the
        // Linux container at 4 threads x 100 updates to one id: 358 of 400 survived before, 400 after.
        LiteDbAtomic.Mutate(db, () =>
        {
            var existing = col.FindById(entry.Id);
            var version = existing != null ? existing.Version + 1 : entry.Version;
            var createdAt = existing?.CreatedAt ?? entry.CreatedAt;

            var doc = new KnowledgeLogDoc
            {
                Id = entry.Id,
                DataType = entry.DataType ?? string.Empty,
                Content = entry.Content ?? string.Empty,
                SourceObservationIds = entry.SourceObservationIds?.ToArray() ?? Array.Empty<string>(),
                Version = version,
                CreatedAt = createdAt,
                UpdatedAt = DateTimeOffset.UtcNow,
                DeletedAt = null,
            };
            col.Upsert(doc);
        });

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        LiteDbDocumentMapper.EnsureMapped<KnowledgeLogDoc>();
        using var db = new LiteDatabase(_connectionString);
        var col = db.GetCollection<KnowledgeLogDoc>(CollectionName);
        // Update writes the WHOLE document back, including the Content and Version this read
        // snapshotted, so an upsert committing in between is reverted while the tombstone is set. One
        // operation, or the delete and the upsert each undo half of the other.
        LiteDbAtomic.Mutate(db, () =>
        {
            var doc = col.FindById(id);
            if (doc == null) return;
            doc.DeletedAt = DateTimeOffset.UtcNow;
            doc.UpdatedAt = DateTimeOffset.UtcNow;
            col.Update(doc);
        });

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<UserKnowledgeLogEntry?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        LiteDbDocumentMapper.EnsureMapped<KnowledgeLogDoc>();
        using var db = new LiteDatabase(_connectionString);
        var col = db.GetCollection<KnowledgeLogDoc>(CollectionName);
        // $._id, not $.Id: Id carries [BsonId], so that is the field name LiteDB actually stored and
        // the one the LINQ form resolved to. "$.Id = @0" would compile, run, throw nothing, and match
        // no document at all. BsonExpression rather than LINQ for the usual reason -- this is the
        // HTTP read path behind GET /knowledge/query, concurrent with UpsertAsync and DeleteAsync.
        var doc = col.FindOne("$._id = @0 AND $.DeletedAt = null", id);
        if (doc == null) return Task.FromResult<UserKnowledgeLogEntry?>(null);
        return Task.FromResult<UserKnowledgeLogEntry?>(ToEntry(doc));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<UserKnowledgeLogEntry>> GetAsync(string? dataType = null, int maxCount = 100, CancellationToken cancellationToken = default)
    {
        LiteDbDocumentMapper.EnsureMapped<KnowledgeLogDoc>();
        using var db = new LiteDatabase(_connectionString);
        var col = db.GetCollection<KnowledgeLogDoc>(CollectionName);
        // BsonExpression, not LINQ, for the same reason EnsureIndexes uses the string overload:
        // LiteDB resolves a LINQ predicate through a BsonMapper that is not safe to drive from
        // several threads at once, and a read concurrent with a write can throw
        // NotSupportedException out of LinqExpressionVisitor.ResolveMember. Reads and writes here
        // ARE concurrent -- this is the HTTP read path behind GET /knowledge/query, running
        // against UpsertAsync and DeleteAsync. Parameters are bound rather than interpolated, so
        // a caller-supplied value cannot alter the filter.
        // "$.DeletedAt = null" matches an explicit null AND a document written before the field
        // existed, which is exactly what the LINQ form matched.
        var query = col.Query().Where("$.DeletedAt = null");
        if (!string.IsNullOrEmpty(dataType))
            query = query.Where("$.DataType = @0", dataType);
        var docs = query.OrderByDescending("$.UpdatedAt").Limit(maxCount).ToList();
        var entries = docs.Select(ToEntry).ToList();
        return Task.FromResult<IReadOnlyList<UserKnowledgeLogEntry>>(entries);
    }

    /// <inheritdoc />
    public async Task<string> ExportToJsonAsync(int maxCount = 1000, CancellationToken cancellationToken = default)
    {
        var entries = await GetAsync(null, maxCount, cancellationToken).ConfigureAwait(false);
        return UserKnowledgeLogExportHelper.ToJson(entries);
    }

    /// <inheritdoc />
    public async Task<string> ExportToMarkdownAsync(int maxCount = 1000, CancellationToken cancellationToken = default)
    {
        var entries = await GetAsync(null, maxCount, cancellationToken).ConfigureAwait(false);
        return UserKnowledgeLogExportHelper.ToMarkdown(entries);
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
    private void EnsureIndexes(ILiteCollection<KnowledgeLogDoc> col)
    {
        if (Volatile.Read(ref _indexesReady)) return;

        lock (_indexGate)
        {
            if (_indexesReady) return;
            col.EnsureIndex(nameof(KnowledgeLogDoc.UpdatedAt));
            col.EnsureIndex(nameof(KnowledgeLogDoc.DataType));
            Volatile.Write(ref _indexesReady, true);
        }
    }

    private static UserKnowledgeLogEntry ToEntry(KnowledgeLogDoc doc)
    {
        return new UserKnowledgeLogEntry
        {
            Id = doc.Id,
            DataType = doc.DataType,
            Content = doc.Content,
            SourceObservationIds = doc.SourceObservationIds ?? Array.Empty<string>(),
            Version = doc.Version,
            CreatedAt = doc.CreatedAt,
            UpdatedAt = doc.UpdatedAt,
            DeletedAt = doc.DeletedAt,
        };
    }

    private sealed class KnowledgeLogDoc
    {
        /// <summary>Id.</summary>
        [BsonId]
        public string Id { get; set; } = string.Empty;
        /// <summary>Data type.</summary>
        public string DataType { get; set; } = string.Empty;
        /// <summary>Generated content text.</summary>
        public string Content { get; set; } = string.Empty;
        /// <summary>Source observation ids.</summary>
        public string[]? SourceObservationIds { get; set; }
        /// <summary>Version.</summary>
        public int Version { get; set; }
        /// <summary>Created at.</summary>
        public DateTimeOffset CreatedAt { get; set; }
        /// <summary>Updated at.</summary>
        public DateTimeOffset UpdatedAt { get; set; }
        /// <summary>Deleted at.</summary>
        public DateTimeOffset? DeletedAt { get; set; }
    }
}
