using System.Text;
using Ashlar.Abstractions;
using Ashlar.Tools.Dev.Deltas;

namespace Ashlar.Tools.Dev;

/// <summary>
/// Tool for performing search and replace operations within files.
/// 
/// Searches for a string and replaces it with another string in a UTF-8 encoded file.
/// Tracks file edits in the action delta with SHA1 hashes and line counts.
/// 
/// Implements ITool for use with agent tool execution.
/// </summary>
public sealed class RepoFsSearchReplaceTool : ITool
{
    public string Id => "repo.fs.search_replace";
    public ToolSchema Schema => new(Id, "Search/replace within a file (UTF-8)", """
    {"type":"object","required":["path","find","replace"],"properties":{"path":{"type":"string","description":"Path relative to the sandbox root"},"find":{"type":"string"},"replace":{"type":"string"}}}
    """);

    // No `root` — see ToolSandbox.
    private sealed record Args(string path, string find, string replace);

    public async Task<ToolResult> InvokeAsync(ToolCall call, WorldSnapshot s, CancellationToken ct)
    {
        var args = System.Text.Json.JsonSerializer.Deserialize<Args>(call.Arguments)!;

        if (!ToolSandbox.TryResolveWritePath(s, args.path, out var full, out var reason))
        {
            var rejected = new RepoDelta { TickFrom = s.Tick, TickTo = s.Tick + 1 };
            rejected.AddLog($"search_replace:{args.path} {reason}");
            return new ToolResult(rejected, new { path = args.path, replaced = false, error = reason });
        }

        if (!File.Exists(full)) throw new FileNotFoundException(full);

        var before = await File.ReadAllTextAsync(full, ct);
        var after = before.Replace(args.find, args.replace);
        await File.WriteAllTextAsync(full, after, new UTF8Encoding(false), ct);

        var delta = new RepoDelta { TickFrom = s.Tick, TickTo = s.Tick + 1 };
        delta.AddLog($"s&r:{args.path} find='{args.find}'");
        delta.AddEdit(new FileEdit(args.path, Sha1(before), Sha1(after), CountPlus(after, before), CountMinus(after, before)));
        return new ToolResult(delta, new { path = args.path, replaced = before != after });
    }

    private static string Sha1(string s)
    {
        using var sha = System.Security.Cryptography.SHA1.Create();
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(s)));
    }

    private static int CountPlus(string a, string b) => Math.Max(0, a.Length - b.Length);
    private static int CountMinus(string a, string b) => Math.Max(0, b.Length - a.Length);
}
