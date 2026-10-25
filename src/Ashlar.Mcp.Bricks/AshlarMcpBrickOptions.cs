namespace Ashlar.Mcp.Bricks;

/// <summary>
/// Which host-registered bricks the MCP server exposes. Bound from <see cref="SectionPath"/>.
/// </summary>
/// <remarks>
/// A separate allowlist from <c>Ashlar:Mcp:Server:ExposedToolIds</c>: that list names
/// <see cref="Ashlar.Abstractions.ITool"/> ids, and the toolbox contributor fails startup for any id
/// it cannot resolve to an <c>ITool</c>, so a brick id there would refuse to boot.
/// </remarks>
public sealed class AshlarMcpBrickOptions
{
    /// <summary>Configuration section path.</summary>
    public const string SectionPath = "Ashlar:Mcp:Bricks";

    /// <summary>
    /// Brick ids (as registered in <see cref="Ashlar.Core.Domain.Execution.IBrickRegistry"/>) to expose.
    /// Empty exposes no bricks. An id with no registered brick fails catalog validation at startup.
    /// </summary>
    public IList<string> ExposedBrickIds { get; } = new List<string>();
}
