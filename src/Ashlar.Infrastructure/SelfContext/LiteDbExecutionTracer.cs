using System.Text.Json;
using LiteDB;
using Ashlar.Core.Application.SelfContext.Models;
using Ashlar.Core.Application.SelfContext.Ports;

namespace Ashlar.Infrastructure.SelfContext;

/// <summary>
/// LiteDB-backed execution tracer.
/// </summary>
public sealed class LiteDbExecutionTracer : IExecutionTracer
{
    private const string CollectionName = "execution_traces";
    private readonly string _connectionString;
    private readonly object _indexGate = new();
    private bool _indexesReady;

    /// <summary>Initializes a new lite db execution tracer.</summary>
    public LiteDbExecutionTracer(string pathOrConnectionString)
    {
        if (string.IsNullOrWhiteSpace(pathOrConnectionString))
            throw new ArgumentNullException(nameof(pathOrConnectionString));
        var trimmed = pathOrConnectionString.Trim();
        _connectionString = trimmed.StartsWith("Filename=", StringComparison.OrdinalIgnoreCase) ? trimmed : $"Filename={trimmed}";
    }

    /// <inheritdoc />
    public Task TraceAsync(string operation, IReadOnlyDictionary<string, object>? context = null, string? path = null, string? outcome = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entry = new ExecutionTraceEntry
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = DateTimeOffset.UtcNow,
            Operation = operation,
            Path = path,
            Outcome = outcome,
            Context = context,
        };
        return LogAsync(entry, cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ExecutionTraceEntry>> QueryAsync(DateTimeOffset? since = null, DateTimeOffset? until = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var db = new LiteDatabase(_connectionString);
        var col = db.GetCollection<TraceDoc>(CollectionName);
        // BsonExpression, not LINQ, for the same reason EnsureIndexes uses the string overload:
        // LiteDB resolves a LINQ predicate through a BsonMapper that is not safe to drive from
        // several threads at once, and a read concurrent with a write can throw
        // NotSupportedException out of LinqExpressionVisitor.ResolveMember. Reads and writes here
        // ARE concurrent -- QueryAsync reads the trace while TraceAsync, which is called from
        // wherever work happens, writes to it. Parameters are bound rather than interpolated, so
        // a caller-supplied value cannot alter the filter.
        var query = col.Query();
        if (since.HasValue)
            // Serialize through the same mapper that wrote the documents, so the comparison is against
            // the representation actually stored rather than whatever a DateTimeOffset converts to.
            query = query.Where("$.Timestamp >= @0", BsonMapper.Global.Serialize(since.Value));
        if (until.HasValue)
            query = query.Where("$.Timestamp <= @0", BsonMapper.Global.Serialize(until.Value));
        var docs = query.OrderByDescending("$.Timestamp").Limit(500).ToList();
        var entries = docs.Select(ToEntry).ToList();
        return Task.FromResult<IReadOnlyList<ExecutionTraceEntry>>(entries);
    }

    private Task LogAsync(ExecutionTraceEntry entry, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var db = new LiteDatabase(_connectionString);
        var col = db.GetCollection<TraceDoc>(CollectionName);
        EnsureIndexes(col);
        col.Insert(ToDoc(entry));
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
    private void EnsureIndexes(ILiteCollection<TraceDoc> col)
    {
        if (Volatile.Read(ref _indexesReady)) return;

        lock (_indexGate)
        {
            if (_indexesReady) return;
            col.EnsureIndex(nameof(TraceDoc.Timestamp));
            Volatile.Write(ref _indexesReady, true);
        }
    }

    private static TraceDoc ToDoc(ExecutionTraceEntry e)
    {
        string? contextJson = null;
        if (e.Context != null && e.Context.Count > 0)
        {
            try
            {
                var dict = e.Context.ToDictionary(kv => kv.Key, kv => kv.Value ?? "");
                contextJson = System.Text.Json.JsonSerializer.Serialize(dict);
            }
            catch
            {
                // Skip context if serialization fails
            }
        }
        return new TraceDoc
        {
            Id = e.Id,
            Timestamp = e.Timestamp,
            Operation = e.Operation,
            Path = e.Path,
            Outcome = e.Outcome,
            ContextJson = contextJson,
        };
    }

    private static ExecutionTraceEntry ToEntry(TraceDoc d)
    {
        IReadOnlyDictionary<string, object>? context = null;
        if (!string.IsNullOrEmpty(d.ContextJson))
        {
            try
            {
                var dict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(d.ContextJson);
                if (dict != null)
                    context = new Dictionary<string, object>(dict.ToDictionary(kv => kv.Key, kv => (object)kv.Value));
            }
            catch
            {
                // Ignore invalid JSON
            }
        }
        return new ExecutionTraceEntry
        {
            Id = d.Id,
            Timestamp = d.Timestamp,
            Operation = d.Operation,
            Path = d.Path,
            Outcome = d.Outcome,
            Context = context,
        };
    }

    private sealed class TraceDoc
    {
        /// <summary>Id.</summary>
        [BsonId]
        public string Id { get; set; } = string.Empty;
        /// <summary>Timestamp.</summary>
        public DateTimeOffset Timestamp { get; set; }
        /// <summary>Operation.</summary>
        public string Operation { get; set; } = string.Empty;
        /// <summary>Path.</summary>
        public string? Path { get; set; }
        /// <summary>Outcome.</summary>
        public string? Outcome { get; set; }
        /// <summary>Context json.</summary>
        public string? ContextJson { get; set; }
    }
}
