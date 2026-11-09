using System.Collections.Concurrent;
using System.Text.Json;
using Ashlar.Core.Application.Trust.Models;
using Ashlar.Core.Application.Trust.Ports;
using Ashlar.Core.Domain;

namespace Ashlar.BackgroundAgents.Trust;

/// <summary>
/// Unified in-memory audit log for sanitization, boundary changes, and classification.
/// Implements both IDataDecisionAuditLog and ISanitizationAuditLog.
/// </summary>
public sealed class DataDecisionAuditLog : IDataDecisionAuditLog, ISanitizationAuditLog
{
    private readonly ConcurrentQueue<DataDecisionAuditEntry> _entries = new();
    private const int MaxEntries = AshlarDefaults.DataDecisionAuditMaxEntries;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    /// <inheritdoc />
    public void LogSanitization(SanitizationAuditEntryDto entry)
    {
        Append(new DataDecisionAuditEntry
        {
            EventType = "Sanitization",
            Timestamp = entry.Timestamp,
            RuleVersion = entry.RuleVersion,
            FieldOrType = entry.FieldOrType,
            Disposition = entry.Disposition,
            Reason = entry.Reason,
        });
    }

    /// <inheritdoc />
    public void LogRedaction(DateTimeOffset timestamp, string ruleVersion, string fieldOrType, string disposition, string? reason)
    {
        LogSanitization(new SanitizationAuditEntryDto(timestamp, ruleVersion, fieldOrType, disposition, reason));
    }

    /// <inheritdoc />
    IReadOnlyList<SanitizationAuditEntry> ISanitizationAuditLog.GetRecent(int maxCount, DateTimeOffset? since)
    {
        var all = GetRecentInternal(maxCount, since, null, "Sanitization");
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
    public IReadOnlyList<DataDecisionAuditEntry> GetRecent(int maxCount, DateTimeOffset? since = null, DateTimeOffset? until = null, string? eventType = null) =>
        GetRecentInternal(maxCount, since, until, eventType);

    /// <inheritdoc />
    public string ExportToJson(int maxCount = 1000, DateTimeOffset? since = null, DateTimeOffset? until = null, string? eventType = null)
    {
        var entries = GetRecentInternal(maxCount, since, until, eventType);
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
        return JsonSerializer.Serialize(export, JsonOptions);
    }

    /// <inheritdoc />
    public string ExportToMarkdown(int maxCount = 1000, DateTimeOffset? since = null, DateTimeOffset? until = null, string? eventType = null)
    {
        var entries = GetRecentInternal(maxCount, since, until, eventType);
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
        var entries = GetRecentInternal(maxCount, since, until, eventType);
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
        _entries.Enqueue(entry);
        while (_entries.Count > MaxEntries && _entries.TryDequeue(out _)) { }
    }

    private IReadOnlyList<DataDecisionAuditEntry> GetRecentInternal(int maxCount, DateTimeOffset? since, DateTimeOffset? until, string? eventType)
    {
        var list = _entries.ToArray();
        var filtered = list.AsEnumerable();
        if (since.HasValue)
            filtered = filtered.Where(e => e.Timestamp >= since.Value);
        if (until.HasValue)
            filtered = filtered.Where(e => e.Timestamp <= until.Value);
        if (!string.IsNullOrEmpty(eventType))
            filtered = filtered.Where(e => string.Equals(e.EventType, eventType, StringComparison.OrdinalIgnoreCase));
        return filtered.OrderByDescending(e => e.Timestamp).Take(maxCount).ToList();
    }
}
