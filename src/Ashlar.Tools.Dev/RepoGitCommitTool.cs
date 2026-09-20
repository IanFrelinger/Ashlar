using Ashlar.Abstractions;
using Ashlar.Tools.Dev.Deltas;

namespace Ashlar.Tools.Dev;

/// <summary>
/// Tool for recording pseudo-commits (demo purposes).
/// 
/// Appends commit messages to a COMMIT_LOG.txt file in the repository root.
/// Used for demonstration purposes to track changes without actual git commits.
/// 
/// Implements ITool for use with agent tool execution.
/// </summary>
public sealed class RepoGitCommitTool : ITool
{
    public string Id => "repo.git.commit";
    public ToolSchema Schema => new(Id, "Record a pseudo-commit (demo)", """
    {"type":"object","required":["message"],"properties":{"message":{"type":"string"}}}
    """);

    // No `root` — see ToolSandbox.
    private sealed record Args(string message);

    public async Task<ToolResult> InvokeAsync(ToolCall call, WorldSnapshot s, CancellationToken ct)
    {
        var args = System.Text.Json.JsonSerializer.Deserialize<Args>(call.Arguments)!;

        if (!ToolSandbox.TryResolveWritePath(s, "COMMIT_LOG.txt", out var logPath, out var reason))
        {
            var rejected = new RepoDelta { TickFrom = s.Tick, TickTo = s.Tick + 1 };
            rejected.AddLog($"commit:{args.message} {reason}");
            return new ToolResult(rejected, new { committed = false, error = reason });
        }

        await File.AppendAllTextAsync(logPath, $"[{DateTimeOffset.UtcNow:u}] {args.message}\n");
        var delta = new RepoDelta { TickFrom = s.Tick, TickTo = s.Tick + 1 };
        delta.AddLog($"commit:{args.message}");
        return new ToolResult(delta, new { path = logPath });
    }
}
