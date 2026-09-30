using Ashlar.BackgroundAgents.DataSensitivity;
using Ashlar.BackgroundAgents.WebSearch;

namespace Ashlar.BackgroundAgents.Trust;

/// <summary>
/// Default implementation of ICloudSanitizationProxy.
/// Uses ISensitiveContentFilter for PII and IDataTaxonomy for classification.
/// </summary>
/// <remarks>
/// <para><b>No filter means no cloud.</b> Without an <see cref="ISensitiveContentFilter"/> this proxy
/// cannot tell a clean prompt from one carrying PII, so every prompt bound for a cloud provider is
/// BLOCKED (and the block is audited) rather than passed through. It used to pass everything
/// through unaudited, and that was the state <c>ashlar improve</c> ran in with trust "enabled":
/// its DI registration built this class with every optional dependency null, so the trust switch
/// wrapped the provider in a sanitizer that sanitized nothing. Air-gapped contexts are still
/// allowed unfiltered, because they leave the machine for no cloud at all.</para>
/// <para>The taxonomy is accepted but not yet consulted; classification-driven blocking is a later
/// step. It is the content filter, not the taxonomy, whose absence blocks.</para>
/// </remarks>
public sealed class CloudSanitizationProxy : ICloudSanitizationProxy
{
    private const string RuleVersion = "trust-v1";
    private readonly ISensitiveContentFilter? _contentFilter;
    private readonly IDataTaxonomy? _taxonomy;
    private readonly ISanitizationAuditLog? _auditLog;

    /// <summary>
    /// Creates a new cloud sanitization proxy.
    /// </summary>
    /// <param name="contentFilter">PII filter. If null, every non-air-gapped prompt is BLOCKED: a
    /// sanitizer that cannot inspect content must not wave it through.</param>
    /// <param name="taxonomy">Optional taxonomy. If null, no data-type classification.</param>
    /// <param name="auditLog">Optional audit log. If null, redactions and blocks are not persisted.</param>
    public CloudSanitizationProxy(
        ISensitiveContentFilter? contentFilter = null,
        IDataTaxonomy? taxonomy = null,
        ISanitizationAuditLog? auditLog = null)
    {
        _contentFilter = contentFilter;
        _taxonomy = taxonomy;
        _auditLog = auditLog;
    }

    /// <inheritdoc />
    public SanitizationResult SanitizeForCloud(OutgoingContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (context.IsAirGapped)
            return SanitizationResult.AllowedWith(context);

        if (_contentFilter == null)
        {
            Audit("blocked", "No sensitive-content filter configured");
            return SanitizationResult.Blocked(
                "No sensitive-content filter is configured, so the prompt cannot be checked for PII; "
                + "refusing to send it to a cloud provider unfiltered. Register an ISensitiveContentFilter "
                + "(AddTrustServices registers the default one).");
        }

        var redactions = new List<SanitizationAuditEntry>();
        var systemPrompt = context.SystemPrompt ?? string.Empty;
        var userPrompt = context.UserPrompt ?? string.Empty;

        // PII check: block if query contains PII
        var combined = systemPrompt + "\n" + userPrompt;
        if (_contentFilter.ShouldBlockQuery(combined))
        {
            Audit("blocked", "PII detected in prompt");
            return SanitizationResult.Blocked("Prompt contains PII; blocked per policy.");
        }

        var filteredSystem = _contentFilter.FilterQuery(systemPrompt);
        var filteredUser = _contentFilter.FilterQuery(userPrompt);
        if (filteredSystem != systemPrompt || filteredUser != userPrompt)
        {
            // Logged once, here. A second loop over `redactions` used to log every entry again, so
            // the audit trail showed two redactions for every one that happened.
            redactions.Add(Audit("redacted", "PII redacted"));
            systemPrompt = filteredSystem;
            userPrompt = filteredUser;
        }

        var sanitized = new OutgoingContext
        {
            SystemPrompt = systemPrompt,
            UserPrompt = userPrompt,
            Variables = context.Variables,
            IsAirGapped = context.IsAirGapped,
            Provider = context.Provider,
        };

        return SanitizationResult.AllowedWith(sanitized, redactions);
    }

    private SanitizationAuditEntry Audit(string disposition, string reason)
    {
        var entry = new SanitizationAuditEntry
        {
            Timestamp = DateTimeOffset.UtcNow,
            RuleVersion = RuleVersion,
            FieldOrType = "prompt",
            Disposition = disposition,
            Reason = reason,
        };
        _auditLog?.LogRedaction(entry.Timestamp, entry.RuleVersion, entry.FieldOrType, entry.Disposition, entry.Reason);
        return entry;
    }
}
