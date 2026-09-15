using System.Text;
using Ashlar.Abstractions;
using Ashlar.Tools.Dev.Deltas;

namespace Ashlar.Tools.Dev;

/// <summary>
/// Tool for ensuring a file exists with specified content.
/// 
/// Creates a file with the specified content if it doesn't exist.
/// If the file already exists, no changes are made.
/// Tracks file creation in the action delta with SHA1 hashes.
/// 
/// Implements ITool for use with agent tool execution.
/// </summary>
public sealed class RepoFsEnsureFileTool : ITool
{
    public string Id => "repo.fs.ensure_file";
    public ToolSchema Schema => new(Id, "Create file with content if it doesn't exist", """
    {"type":"object","required":["path","content"],"properties":{"path":{"type":"string","description":"Path relative to the sandbox root"},"content":{"type":"string"}}}
    """);

    // No `root` — see ToolSandbox.
    private sealed record Args(string path, string content);

    public async Task<ToolResult> InvokeAsync(ToolCall call, WorldSnapshot s, CancellationToken ct)
    {
        var args = System.Text.Json.JsonSerializer.Deserialize<Args>(call.Arguments)!;

        if (!ToolSandbox.TryResolveWritePath(s, args.path, out var full, out var reason))
        {
            var rejected = new RepoDelta { TickFrom = s.Tick, TickTo = s.Tick + 1 };
            rejected.AddLog($"ensure:{args.path} {reason}");
            return new ToolResult(rejected, new { path = args.path, created = false, error = reason });
        }

        if (!File.Exists(full))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            await File.WriteAllTextAsync(full, args.content, new UTF8Encoding(false), ct);
            var delta = new RepoDelta { TickFrom = s.Tick, TickTo = s.Tick + 1 };
            delta.AddLog($"ensure:{args.path} created");
            delta.AddEdit(new FileEdit(args.path, null, Sha1(args.content), args.content.Length, 0));
            return new ToolResult(delta, new { created = true });
        }
        else
        {
            var delta = new RepoDelta { TickFrom = s.Tick, TickTo = s.Tick + 1 };
            delta.AddLog($"ensure:{args.path} exists");
            return new ToolResult(delta, new { created = false });
        }
    }

    private static string Sha1(string s)
    {
        using var sha = System.Security.Cryptography.SHA1.Create();
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(s)));
    }
}
