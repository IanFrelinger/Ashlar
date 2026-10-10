namespace Ashlar.AI.Pipeline.Governance;

/// <summary>
/// Audit record for a single model invocation (no raw prompt/response content).
/// </summary>
public sealed class ChatInvocationAuditRecord
{
    /// <summary>UTC timestamp.</summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Target key (e.g. <c>local:ollama</c>).</summary>
    public required string TargetKey { get; init; }

    /// <summary>Model id when known.</summary>
    public string? ModelId { get; init; }

    /// <summary>Outcome: success, fault, cancelled, denied.</summary>
    public required string Outcome { get; init; }

    /// <summary>Policy decisions applied (e.g. allowed, sanitize=redact).</summary>
    public IReadOnlyList<string> PolicyDecisions { get; init; } = Array.Empty<string>();

    /// <summary>Redaction count (content never included).</summary>
    public int RedactionCount { get; init; }

    /// <summary>Redaction categories.</summary>
    public IReadOnlyList<string> RedactionCategories { get; init; } = Array.Empty<string>();

    /// <summary>Input token count when available.</summary>
    public long? InputTokenCount { get; init; }

    /// <summary>Output token count when available.</summary>
    public long? OutputTokenCount { get; init; }

    /// <summary>Latency in milliseconds.</summary>
    public long LatencyMs { get; init; }

    /// <summary>Optional correlation / barrier identity.</summary>
    public string? CorrelationId { get; init; }

    /// <summary>Fault or deny reason code (no secrets).</summary>
    public string? ReasonCode { get; init; }

    /// <summary>The full operator-only egress record when policy refused a call. Never return this to the subject.</summary>
    public Ashlar.Abstractions.Security.Egress.EgressDecision? EgressDecision { get; init; }
}

/// <summary>
/// Receives model-invocation audit records.
/// </summary>
public interface IChatInvocationAuditor
{
    /// <summary>Persists or fans out an audit record.</summary>
    void Record(ChatInvocationAuditRecord record);
}
