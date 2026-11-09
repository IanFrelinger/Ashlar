namespace Ashlar.BackgroundAgents.Trust;

/// <summary>
/// Log of sanitization events (what was stripped, why).
/// </summary>
public interface ISanitizationAuditLog
{
    /// <summary>
    /// Logs a redaction event.
    /// </summary>
    void LogRedaction(DateTimeOffset timestamp, string ruleVersion, string fieldOrType, string disposition, string? reason);

    /// <summary>
    /// Gets at most <paramref name="maxCount"/> audit entries, newest first.
    /// Returns an empty list when <paramref name="maxCount"/> is zero or negative.
    /// When supplied, <paramref name="since"/> is an inclusive lower timestamp bound.
    /// </summary>
    IReadOnlyList<SanitizationAuditEntry> GetRecent(int maxCount, DateTimeOffset? since = null);
}
