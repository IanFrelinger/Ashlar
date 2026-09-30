namespace Ashlar.BackgroundAgents.Trust;

/// <summary>
/// Sanitizes outgoing LLM/vision context before delegation to IProviderFactory.
/// Blocks if classification uncertain -- and an implementation that cannot inspect content at all
/// (no content filter) must block, never allow. Logs all redactions.
/// </summary>
public interface ICloudSanitizationProxy
{
    /// <summary>
    /// Sanitizes the outgoing context for cloud dispatch.
    /// </summary>
    /// <param name="context">Context to sanitize.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result: allowed with sanitized context, or blocked with reason.</returns>
    SanitizationResult SanitizeForCloud(OutgoingContext context, CancellationToken cancellationToken = default);
}
