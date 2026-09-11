using System.Collections.Concurrent;
using System.Text.Json;
using LiteDB;
using Ashlar.Core.Application.Trust.Models;
using Ashlar.Core.Application.Trust.Ports;
using Ashlar.Infrastructure.Persistence;

namespace Ashlar.BackgroundAgents.Trust;

/// <summary>
/// LiteDB-backed audit log for data decisions. Persists across restarts for compliance.
/// </summary>
public sealed class LiteDbDataDecisionAuditLog : IDataDecisionAuditLog, ISanitizationAuditLog
{
    private const string CollectionName = "data_decision_audit";
    private readonly string _connectionString;
    private readonly ConcurrentQueue<DataDecisionAuditEntry> _buffer = new();
    private const int BufferFlushThreshold = 10;
    private readonly object _indexGate = new();
    private bool _indexesReady;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public LiteDbDataDecisionAuditLog(string pathOrConnectionString)
    {
        if (string.IsNullOrWhiteSpace(pathOrConnectionString))
            throw new ArgumentNullException(nameof(pathOrConnectionString));
        var trimmed = pathOrConnectionString.Trim();
        _connectionString = trimmed.StartsWith("Filename=", StringComparison.OrdinalIgnoreCase) ? trimmed : $"Filename={trimmed}";

        // This store is the one built EAGERLY rather than in a factory lambda
        // (ServiceCollectionExtensions.AddTrustServices), and the singleton it produces stands behind
        // both IDataDecisionAuditLog and ISanitizationAuditLog, so its type is first touched by
        // whichever caller wins. Building it here puts that build on the single thread doing the
        // registration.
        LiteDbDocumentMapper.EnsureMapped<AuditDoc>();
    }

    /// <inheritdoc />
    public void LogSanitization(SanitizationAuditEntryDto entry)
    {
        var e = new DataDecisionAuditEntry
        {
            EventType = "Sanitization",
            Timestamp = entry.Timestamp,
            RuleVersion = entry.RuleVersion,
            FieldOrType = entry.FieldOrType,
            Disposition = entry.Disposition,
            Reason = entry.Reason,
        };
        Append(e);
    }

    /// <inheritdoc />
    public void LogRedaction(DateTimeOffset timestamp, string ruleVersion, string fieldOrType, string disposition, string? reason)
    {
        LogSanitization(new SanitizationAuditEntryDto(timestamp, ruleVersion, fieldOrType, disposition, reason));
    }

    /// <inheritdoc />
    public void LogBoundaryChange(BoundaryChangeEvent evt)
    {
        Append(new DataDecisionAuditEntry
        {
            EventType = "BoundaryChange",
            Timestamp = evt.Timestamp,
            ChangeType = evt.ChangeType,
            Category = evt.Category,
            SourceId = evt.SourceId,
            ProjectPath = evt.ProjectPath,
            PreviousState = evt.PreviousState,
            NewState = evt.NewState,
        });
    }

    /// <inheritdoc />
    public void LogClassification(string dataType, string levelName, string? reason)
    {
        Append(new DataDecisionAuditEntry
        {
            EventType = "Classification",
            Timestamp = DateTimeOffset.UtcNow,
            DataType = dataType,
            LevelName = levelName,
            Reason = reason,
        });
    }

    /// <inheritdoc />
    public void LogScopeChainRejection(IReadOnlyList<string> chainIds, int rejectedStep, string? resolvedPath, string reason)
    {
        Append(new DataDecisionAuditEntry
        {
            EventType = "ScopeChainRejected",
            Timestamp = DateTimeOffset.UtcNow,
            ChangeType = rejectedStep.ToString(),
            SourceId = string.Join(";", chainIds),
            ProjectPath = resolvedPath,
            Reason = reason,
        });
    }

    /// <inheritdoc />
    public void LogAmbientAction(string agentId, string summary, int toolCallsExecuted)
    {
        Append(new DataDecisionAuditEntry
        {
            EventType = "AmbientAction",
            Timestamp = DateTimeOffset.UtcNow,
            SourceId = agentId,
            Reason = summary,
            ChangeType = toolCallsExecuted.ToString(),
        });
    }

    /// <inheritdoc />
    public void LogCopilotTask(string tenantId, string taskId, bool success)
    {
        Append(new DataDecisionAuditEntry
        {
            EventType = "CopilotTask",
            Timestamp = DateTimeOffset.UtcNow,
            TenantId = tenantId,
            SourceId = taskId,
            Disposition = success ? "Success" : "Failure",
        });
    }

    /// <inheritdoc />
    IReadOnlyList<SanitizationAuditEntry> ISanitizationAuditLog.GetRecent(int maxCount, DateTimeOffset? since)
    {
        var all = GetRecent(maxCount * 3, since, null, "Sanitization");
        return all
            .Select(e => new SanitizationAuditEntry
            {
                Timestamp = e.Timestamp,
                RuleVersion = e.RuleVersion ?? "",
                FieldOrType = e.FieldOrType ?? "",
                Disposition = e.Disposition ?? "",
                Reason = e.Reason,
            })
            .ToList();
    }

    /// <inheritdoc />
    public IReadOnlyList<DataDecisionAuditEntry> GetRecent(int maxCount, DateTimeOffset? since = null, DateTimeOffset? until = null, string? eventType = null)
    {
        // The read surface: ExportToJson/Markdown/Csv and the explicit ISanitizationAuditLog.GetRecent
        // all come through here. DeserializeObject off a half-built mapper is the failure this closes
        // on this side -- it outnumbered the write-side throws roughly four to one when measured.
        LiteDbDocumentMapper.EnsureMapped<AuditDoc>();
        FlushBuffer();
        using var db = new LiteDatabase(_connectionString);
        var col = db.GetCollection<AuditDoc>(CollectionName);
        // The index is declared by FlushBuffer, the write path. A read has no business declaring one.
        // BsonExpression, not LINQ, for the same reason EnsureIndexes uses the string overload:
        // LiteDB resolves a LINQ predicate through a BsonMapper that is not safe to drive from
        // several threads at once, and a read concurrent with a write can throw
        // NotSupportedException out of LinqExpressionVisitor.ResolveMember. Reads and writes here
        // ARE concurrent -- this log is one shared instance behind both IDataDecisionAuditLog and
        // ISanitizationAuditLog, buffering writes from any caller. Parameters are bound rather
        // than interpolated, so a caller-supplied value cannot alter the filter.
        var query = col.Query();
        if (since.HasValue)
            // Serialize through the same mapper that wrote the documents, so the comparison is against
            // the representation actually stored rather than whatever a DateTimeOffset converts to.
            query = query.Where("$.Timestamp >= @0", BsonMapper.Global.Serialize(since.Value));
        if (until.HasValue)
            query = query.Where("$.Timestamp <= @0", BsonMapper.Global.Serialize(until.Value));
        if (!string.IsNullOrEmpty(eventType))
            query = query.Where("$.EventType = @0", eventType);
        var docs = query.OrderByDescending("$.Timestamp").Limit(maxCount).ToList();
        return docs.Select(ToEntry).ToList();
    }

    /// <inheritdoc />
    public string ExportToJson(int maxCount = 1000, DateTimeOffset? since = null, DateTimeOffset? until = null, string? eventType = null)
    {
        var entries = GetRecent(maxCount, since, until, eventType);
        var export = new
        {
            exportedAt = DateTimeOffset.UtcNow,
            count = entries.Count,
            since = since?.ToString("o"),
            until = until?.ToString("o"),
            eventType = eventType,
            entries = entries.Select(e => new
            {
                e.EventType,
                e.Timestamp,
                e.TenantId,
                e.RuleVersion,
                e.FieldOrType,
                e.Disposition,
                e.Reason,
                e.ChangeType,
                e.Category,
                e.SourceId,
                e.ProjectPath,
                e.PreviousState,
                e.NewState,
                e.DataType,
                e.LevelName,
            }).ToList(),
        };
        return System.Text.Json.JsonSerializer.Serialize(export, JsonOptions);
    }

    /// <inheritdoc />
    public string ExportToMarkdown(int maxCount = 1000, DateTimeOffset? since = null, DateTimeOffset? until = null, string? eventType = null)
    {
        var entries = GetRecent(maxCount, since, until, eventType);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# Data Decision Audit Log");
        sb.AppendLine();
        sb.AppendLine($"Exported: {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine($"Entries: {entries.Count}");
        if (since.HasValue)
            sb.AppendLine($"Since: {since.Value:yyyy-MM-dd HH:mm:ss} UTC");
        if (until.HasValue)
            sb.AppendLine($"Until: {until.Value:yyyy-MM-dd HH:mm:ss} UTC");
        if (!string.IsNullOrEmpty(eventType))
            sb.AppendLine($"EventType: {eventType}");
        sb.AppendLine();

        foreach (var e in entries)
        {
            sb.AppendLine($"## {e.EventType} @ {e.Timestamp:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine();
            if (e.EventType == "Sanitization")
            {
                sb.AppendLine($"- **Rule:** {e.RuleVersion} | **Field:** {e.FieldOrType} | **Disposition:** {e.Disposition}");
                if (!string.IsNullOrEmpty(e.Reason))
                    sb.AppendLine($"- **Reason:** {e.Reason}");
            }
            else if (e.EventType == "BoundaryChange")
            {
                sb.AppendLine($"- **Change:** {e.ChangeType} | {e.PreviousState} → {e.NewState}");
                if (!string.IsNullOrEmpty(e.Category))
                    sb.AppendLine($"- **Category:** {e.Category}");
                if (!string.IsNullOrEmpty(e.SourceId))
                    sb.AppendLine($"- **Source:** {e.SourceId}");
                if (!string.IsNullOrEmpty(e.ProjectPath))
                    sb.AppendLine($"- **Project:** {e.ProjectPath}");
            }
            else if (e.EventType == "Classification")
            {
                sb.AppendLine($"- **DataType:** {e.DataType} | **Level:** {e.LevelName}");
                if (!string.IsNullOrEmpty(e.Reason))
                    sb.AppendLine($"- **Reason:** {e.Reason}");
            }
            else if (e.EventType == "ScopeChainRejected")
            {
                sb.AppendLine($"- **RejectedStep:** {e.ChangeType} | **Path:** {e.ProjectPath}");
                if (!string.IsNullOrEmpty(e.SourceId))
                    sb.AppendLine($"- **Chain:** {e.SourceId}");
                if (!string.IsNullOrEmpty(e.Reason))
                    sb.AppendLine($"- **Reason:** {e.Reason}");
            }
            else if (e.EventType == "AmbientAction")
            {
                sb.AppendLine($"- **Agent:** {e.SourceId} | **ToolCalls:** {e.ChangeType}");
                if (!string.IsNullOrEmpty(e.Reason))
                    sb.AppendLine($"- **Summary:** {e.Reason}");
            }
            else if (e.EventType == "CopilotTask")
            {
                sb.AppendLine($"- **Tenant:** {e.TenantId} | **Task:** {e.SourceId} | **Outcome:** {e.Disposition}");
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <inheritdoc />
    public string ExportToCsv(int maxCount = 1000, DateTimeOffset? since = null, DateTimeOffset? until = null, string? eventType = null)
    {
        var entries = GetRecent(maxCount, since, until, eventType);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Timestamp,EventType,TenantId,RuleVersion,FieldOrType,Disposition,Reason,ChangeType,Category,SourceId,ProjectPath,PreviousState,NewState,DataType,LevelName");
        foreach (var e in entries)
        {
            var line = string.Join(",",
                EscapeCsv(e.Timestamp.ToString("o")),
                EscapeCsv(e.EventType),
                EscapeCsv(e.TenantId),
                EscapeCsv(e.RuleVersion),
                EscapeCsv(e.FieldOrType),
                EscapeCsv(e.Disposition),
                EscapeCsv(e.Reason),
                EscapeCsv(e.ChangeType),
                EscapeCsv(e.Category),
                EscapeCsv(e.SourceId),
                EscapeCsv(e.ProjectPath),
                EscapeCsv(e.PreviousState),
                EscapeCsv(e.NewState),
                EscapeCsv(e.DataType),
                EscapeCsv(e.LevelName));
            sb.AppendLine(line);
        }
        return sb.ToString();
    }

    private static string EscapeCsv(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "\"\"";
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        return "\"" + value + "\"";
    }

    private void Append(DataDecisionAuditEntry entry)
    {
        // Every Log* method funnels through here, so one call covers the whole write surface -- and it
        // is taken BEFORE the flush gate below, never inside it.
        LiteDbDocumentMapper.EnsureMapped<AuditDoc>();
        _buffer.Enqueue(entry);
        if (_buffer.Count >= BufferFlushThreshold)
            FlushBuffer();
    }

    private void FlushBuffer()
    {
        var toFlush = new List<DataDecisionAuditEntry>();
        while (_buffer.TryDequeue(out var entry))
            toFlush.Add(entry);
        if (toFlush.Count == 0) return;
        using var db = new LiteDatabase(_connectionString);
        var col = db.GetCollection<AuditDoc>(CollectionName);
        EnsureIndexes(col);
        foreach (var entry in toFlush)
            col.Insert(ToDoc(entry));
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

    private static AuditDoc ToDoc(DataDecisionAuditEntry e)
    {
        return new AuditDoc
        {
            Id = ObjectId.NewObjectId().ToString(),
            EventType = e.EventType,
            Timestamp = e.Timestamp,
            TenantId = e.TenantId,
            RuleVersion = e.RuleVersion,
            FieldOrType = e.FieldOrType,
            Disposition = e.Disposition,
            Reason = e.Reason,
            ChangeType = e.ChangeType,
            Category = e.Category,
            SourceId = e.SourceId,
            ProjectPath = e.ProjectPath,
            PreviousState = e.PreviousState,
            NewState = e.NewState,
            DataType = e.DataType,
            LevelName = e.LevelName,
        };
    }

    private static DataDecisionAuditEntry ToEntry(AuditDoc d)
    {
        return new DataDecisionAuditEntry
        {
            EventType = d.EventType,
            Timestamp = d.Timestamp,
            TenantId = d.TenantId,
            RuleVersion = d.RuleVersion,
            FieldOrType = d.FieldOrType,
            Disposition = d.Disposition,
            Reason = d.Reason,
            ChangeType = d.ChangeType,
            Category = d.Category,
            SourceId = d.SourceId,
            ProjectPath = d.ProjectPath,
            PreviousState = d.PreviousState,
            NewState = d.NewState,
            DataType = d.DataType,
            LevelName = d.LevelName,
        };
    }

    private sealed class AuditDoc
    {
        [BsonId]
        public string Id { get; set; } = string.Empty;
        public string EventType { get; set; } = string.Empty;
        public DateTimeOffset Timestamp { get; set; }
        public string? TenantId { get; set; }
        public string? RuleVersion { get; set; }
        public string? FieldOrType { get; set; }
        public string? Disposition { get; set; }
        public string? Reason { get; set; }
        public string? ChangeType { get; set; }
        public string? Category { get; set; }
        public string? SourceId { get; set; }
        public string? ProjectPath { get; set; }
        public string? PreviousState { get; set; }
        public string? NewState { get; set; }
        public string? DataType { get; set; }
        public string? LevelName { get; set; }
    }
}
