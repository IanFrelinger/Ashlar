using System.Text.Json;

namespace Ashlar.Mcp.Server;

/// <summary>
/// Records the <c>tools/call</c> requests <see cref="AshlarMcpToolBridge"/> refuses before any contributor sees them:
/// a tool name the catalog does not list, and a call the <see cref="IMcpInvocationGate"/> denies. Without one, those
/// refusals reach only the host log. Hosts that promise a record per call register one (the brick contributor in
/// <c>Ashlar.Mcp.Bricks</c> does).
/// </summary>
public interface IMcpRefusalAuditor
{
    /// <summary>
    /// Writes one record of the refusal and returns the text the caller is sent: <paramref name="message"/> with the
    /// record's id appended, so the record can be matched to the exchange. Throws when the record cannot be written;
    /// the bridge refuses the call either way.
    /// </summary>
    /// <param name="requestedName">The tool name exactly as the caller sent it. Caller-controlled: any length, any characters.</param>
    /// <param name="canonicalId">The canonical id when the name resolved to a catalog entry (a gate denial); otherwise <see langword="null"/>.</param>
    /// <param name="arguments">The arguments as received (an empty object when none were sent).</param>
    /// <param name="message">What the caller is told, without the record id.</param>
    /// <param name="ct">Cancellation.</param>
    Task<string> RecordRefusalAsync(string requestedName, string? canonicalId, JsonElement arguments, string message, CancellationToken ct);
}
