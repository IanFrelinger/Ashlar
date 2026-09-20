using System.Text;
using Ashlar.Abstractions;
using Ashlar.Tools.Dev.Deltas;

namespace Ashlar.Tools.Dev;

/// <summary>
/// Tool for writing files to the repository filesystem.
/// 
/// Writes content to a file at the specified path relative to the repository root.
/// Creates parent directories if they don't exist.
/// Tracks file edits in the action delta with SHA1 hashes and line counts.
/// 
/// Implements ITool for use with agent tool execution.
/// </summary>
public sealed class RepoFsWriteTool : ITool
{
    public string Id => "repo.fs.write";
    public ToolSchema Schema => new(Id, "Write a file under the repo root", """
    {"type":"object","required":["path","content"],"properties":{"path":{"type":"string","description":"Path relative to the sandbox root"},"content":{"type":"string"}}}
    """);

    // No `root`. It used to be here, model-supplied, and Path.Combine'd with `path` — see
    // ToolSandbox for why that was an arbitrary-write escape. The root now comes from the
    // world snapshot, which the model cannot reach.
    private sealed record Args(string path, string content);

    public async Task<ToolResult> InvokeAsync(ToolCall call, WorldSnapshot s, CancellationToken ct)
    {
        var args = System.Text.Json.JsonSerializer.Deserialize<Args>(call.Arguments)!;

        if (!ToolSandbox.TryResolveWritePath(s, args.path, out var full, out var reason))
        {
            // "write-refused:", not "write:". SelfExtendRunnerAdapter.ExtractWritePaths harvests
            // "write:" lines into the write paths the signed admission record claims as its diff;
            // sharing the prefix turned every refusal into a signed claim that the write landed.
            var rejected = new RepoDelta { TickFrom = s.Tick, TickTo = s.Tick + 1 };
            rejected.AddLog($"write-refused:{args.path} {reason}");
            return new ToolResult(rejected, new { path = args.path, written = false, error = reason });
        }

        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var before = File.Exists(full) ? await File.ReadAllTextAsync(full, ct) : "";
        await File.WriteAllTextAsync(full, args.content, new UTF8Encoding(false), ct);
        var after = await File.ReadAllTextAsync(full, ct);

        var delta = new RepoDelta { TickFrom = s.Tick, TickTo = s.Tick + 1 };
        delta.AddLog($"write:{args.path} bytes={args.content.Length}");
        delta.AddEdit(new FileEdit(args.path, Sha1(before), Sha1(after), CountPlus(after, before), CountMinus(after, before)));
        return new ToolResult(delta, new { path = args.path, bytes = args.content.Length });
    }

    private static string Sha1(string s)
    {
        using var sha = System.Security.Cryptography.SHA1.Create();
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(s)));
    }

    private static int CountPlus(string a, string b) => Math.Max(0, a.Length - b.Length);
    private static int CountMinus(string a, string b) => Math.Max(0, b.Length - a.Length);
}
