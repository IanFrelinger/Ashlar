namespace Ashlar.Mcp.Bricks;

/// <summary>
/// The audit configuration the MCP brick host (<c>Ashlar.Mcp.Server.Host</c>) starts with, as lowest-priority
/// configuration values: appsettings, environment variables and arguments all override them.
/// </summary>
public static class AshlarMcpBrickHostDefaults
{
    /// <summary>
    /// One File-sink record per brick call, flushed per event, in <c>&lt;localApplicationData&gt;/ashlar/mcp-audit</c>
    /// (outside the working directory an IDE spawns the host in), one <c>mcp-brick-calls-*.ndjson</c> file per server
    /// start, and no file ever pruned: an IDE starts the host once per session, so a retention count would delete
    /// the records of old sessions.
    /// </summary>
    /// <param name="localApplicationData">The user's local application data directory.</param>
    public static IReadOnlyDictionary<string, string?> Audit(string localApplicationData) => new Dictionary<string, string?>
    {
        ["Ashlar:Audit:Sinks:0"] = "File",
        ["Ashlar:Audit:File:Directory"] = Path.Combine(localApplicationData, "ashlar", "mcp-audit"),
        ["Ashlar:Audit:File:FilePrefix"] = "mcp-brick-calls",
        ["Ashlar:Audit:File:FlushEveryEvent"] = "true",
        ["Ashlar:Audit:File:MaxRotatedFiles"] = "0",
    };
}
