using System.Text.Json;

namespace Ashlar.Mcp.Server;

/// <summary>
/// Contribution port for the MCP server catalog. The built-in
/// <see cref="ToolboxMcpToolContributor"/> bridges DI-registered <see cref="Ashlar.Abstractions.ITool"/>
/// implementations; hosts add further contributors (e.g. a brick-backed contributor living in the
/// host layer, which may reference <c>Ashlar.Core.Domain</c> without dragging that dependency into
/// this adapter project).
///
/// Contributors only describe and execute. Allowlisting, argument overrides, policy gating,
/// auditing, and concurrency limits are applied centrally by <see cref="AshlarMcpToolBridge"/> —
/// a contributor must never be reachable except through the bridge.
/// </summary>
public interface IMcpToolContributor
{
    /// <summary>
    /// Describes the tools this contributor currently offers. Called at startup validation and
    /// on each MCP <c>tools/list</c>. Implementations should throw on invalid definitions
    /// (bad JSON schema, duplicate ids) so misconfiguration fails fast rather than surfacing
    /// as a confusing client-side error.
    /// </summary>
    Task<IReadOnlyList<AshlarMcpToolDescriptor>> DescribeAsync(CancellationToken ct);

    /// <summary>
    /// Executes a tool previously described by this contributor, addressed by its canonical
    /// Ashlar id. Business-logic failures are reported via <see cref="AshlarMcpToolOutcome.IsError"/>,
    /// never as exceptions (exceptions are treated as internal errors by the bridge).
    /// </summary>
    Task<AshlarMcpToolOutcome> InvokeAsync(string canonicalId, JsonElement arguments, CancellationToken ct);
}

/// <summary>
/// A tool as advertised over MCP.
/// </summary>
/// <param name="McpName">MCP-facing tool name (sanitized to the conservative <c>[A-Za-z0-9_-]</c> charset).</param>
/// <param name="CanonicalId">The canonical Ashlar tool id (e.g. <c>repo.fs.read</c>) used for invocation, policy checks, and audit.</param>
/// <param name="Description">Human/model-readable description.</param>
/// <param name="InputSchema">JSON schema object (root <c>type</c> must be <c>object</c>).</param>
public sealed record AshlarMcpToolDescriptor(
    string McpName,
    string CanonicalId,
    string Description,
    JsonElement InputSchema);

/// <summary>
/// Result of a contributor invocation, pre-shaped for MCP:
/// <paramref name="Text"/> becomes the text content block, <paramref name="Payload"/> (when not a
/// plain string) is additionally serialized as structured content.
/// </summary>
/// <param name="IsError">True when the tool executed but failed (maps to <c>isError</c> on the MCP result).</param>
/// <param name="Text">Text representation of the outcome.</param>
/// <param name="Payload">Optional structured payload.</param>
public sealed record AshlarMcpToolOutcome(bool IsError, string Text, object? Payload = null)
{
    /// <summary>
    /// Requests the bridge to produce the text representation from the structured payload after response access
    /// is authorized. Contributors must defer payload serialization to the bridge when using this option.
    /// </summary>
    public bool SerializePayloadAsText { get; init; }
}
