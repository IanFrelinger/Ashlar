using System.Text;
using Ashlar.Abstractions;
using Ashlar.Tools.Dev.Deltas;

namespace Ashlar.Tools.Dev;

/// <summary>
/// Tool for updating documentation files.
/// 
/// Appends entries to CHANGELOG.md in the repository root.
/// Each entry is timestamped and formatted as a changelog item.
/// 
/// Implements ITool for use with agent tool execution.
/// </summary>
public sealed class DocsUpdateTool : ITool
{
    public string Id => "docs.update";
    public ToolSchema Schema => new(Id, "Append to CHANGELOG.md", """
    {"type":"object","required":["entry"],"properties":{"entry":{"type":"string"}}}
    """);

    // No `root` — see ToolSandbox. This tool appends to a file, so it is a write path.
    private sealed record Args(string entry);

    public async Task<ToolResult> InvokeAsync(ToolCall call, WorldSnapshot s, CancellationToken ct)
    {
        var args = System.Text.Json.JsonSerializer.Deserialize<Args>(call.Arguments)!;

        if (!ToolSandbox.TryResolveWritePath(s, "CHANGELOG.md", out var path, out var reason))
        {
            var rejected = new RepoDelta { TickFrom = s.Tick, TickTo = s.Tick + 1 };
            rejected.AddLog($"docs:update {reason}");
            return new ToolResult(rejected, new { updated = false, error = reason });
        }

        await File.AppendAllTextAsync(path, $"- {DateTimeOffset.UtcNow:u} {args.entry}\n", new UTF8Encoding(false), ct);

        var delta = new RepoDelta { TickFrom = s.Tick, TickTo = s.Tick + 1 };
        delta.AddLog("docs:update");
        return new ToolResult(delta, new { path });
    }
}
