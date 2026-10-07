using System.Text.Json;
using Ashlar.Abstractions;
using Ashlar.Abstractions.Paths;
using Ashlar.Tools.Dev.Deltas;

namespace Ashlar.Tools.Dev;

/// <summary>
/// Tool for listing files and directories under the repository root.
///
/// Companion to <see cref="RepoFsReadTool"/>: gives tool-calling agents a discovery primitive so
/// they can enumerate what is in the repo before deciding which file to read or write. Without
/// this, an LLM presented only with "RepoRoot" has no way to bootstrap its understanding.
///
/// Behaviour:
/// - Lists entries under <c>root + path</c>.
/// - <c>recursive</c> (default false) enables a depth-limited recursive walk.
/// - <c>max_entries</c> (default 200) bounds the response size.
/// - Skips noisy build/IDE artefacts by default (<c>bin</c>, <c>obj</c>, <c>.git</c>, <c>.vs</c>,
///   <c>node_modules</c>, <c>.nuget</c>) so prompts stay useful.
/// </summary>
public sealed class RepoFsListTool : ITool
{
    private const int DefaultMaxEntries = 200;
    private const int MaxRecursionDepth = 6;

    private static readonly HashSet<string> SkippedDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", ".git", ".vs", "node_modules", ".nuget", ".idea", ".cache"
    };

    public string Id => "repo.fs.list";

    public ToolSchema Schema => new(Id, "List files and directories under the repo root", """
    {"type":"object","properties":{"path":{"type":"string","description":"Relative subpath (default '.')"},"recursive":{"type":"boolean","description":"Recurse into subdirectories (default false)"},"max_entries":{"type":"integer","description":"Cap on returned entries (default 200)"}}}
    """);

    // No `root` — see ToolSandbox.
    private sealed record Args(string? path, bool? recursive, int? max_entries);

    public Task<ToolResult> InvokeAsync(ToolCall call, WorldSnapshot s, CancellationToken ct)
    {
        var args = JsonSerializer.Deserialize<Args>(call.Arguments)!;
        var subpath = string.IsNullOrWhiteSpace(args.path) ? "." : args.path!;
        var recursive = args.recursive ?? false;
        var maxEntries = args.max_entries is > 0 ? args.max_entries.Value : DefaultMaxEntries;

        if (!ToolSandbox.TryResolvePath(s, subpath, out var full, out var error))
        {
            var errDelta = new RepoDelta { TickFrom = s.Tick, TickTo = s.Tick + 1 };
            errDelta.AddLog($"list:{subpath} REJECTED ({error})");
            return Task.FromResult(new ToolResult(errDelta, new { path = subpath, exists = false, error }));
        }

        if (!Directory.Exists(full))
        {
            var missDelta = new RepoDelta { TickFrom = s.Tick, TickTo = s.Tick + 1 };
            missDelta.AddLog($"list:{subpath} not_found");
            return Task.FromResult(new ToolResult(missDelta, new { path = subpath, exists = false }));
        }

        ToolSandbox.TryResolveRoot(s, out var rootFull, out _);
        var entries = new List<object>();
        var truncated = false;
        try
        {
            EnumerateInto(full, rootFull, recursive, depth: 0, maxEntries, entries, ref truncated, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var errDelta = new RepoDelta { TickFrom = s.Tick, TickTo = s.Tick + 1 };
            errDelta.AddLog($"list:{subpath} FAILED ({ex.Message})");
            return Task.FromResult(new ToolResult(errDelta, new { path = subpath, exists = true, error = ex.Message }));
        }

        var delta = new RepoDelta { TickFrom = s.Tick, TickTo = s.Tick + 1 };
        delta.AddLog($"list:{subpath} entries={entries.Count}{(truncated ? " (truncated)" : "")}");
        return Task.FromResult(new ToolResult(delta, new
        {
            path = subpath,
            exists = true,
            recursive,
            count = entries.Count,
            truncated,
            entries
        }));
    }

    private static void EnumerateInto(
        string dir,
        string rootFull,
        bool recursive,
        int depth,
        int maxEntries,
        List<object> entries,
        ref bool truncated,
        CancellationToken ct)
    {
        if (entries.Count >= maxEntries)
        {
            truncated = true;
            return;
        }
        ct.ThrowIfCancellationRequested();

        // Directories first, sorted, for deterministic output.
        var subDirs = Directory.EnumerateDirectories(dir).OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var sub in subDirs)
        {
            var name = Path.GetFileName(sub);
            if (SkippedDirs.Contains(name)) continue;
            if (entries.Count >= maxEntries) { truncated = true; return; }

            var rel = Path.GetRelativePath(rootFull, sub).Replace('\\', '/');
            entries.Add(new { kind = "dir", path = rel });

            if (recursive && depth + 1 <= MaxRecursionDepth)
            {
                EnumerateInto(sub, rootFull, recursive: true, depth + 1, maxEntries, entries, ref truncated, ct);
                if (truncated) return;
            }
        }

        var files = Directory.EnumerateFiles(dir).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var file in files)
        {
            if (entries.Count >= maxEntries) { truncated = true; return; }
            var info = new FileInfo(file);
            var rel = Path.GetRelativePath(rootFull, file).Replace('\\', '/');
            entries.Add(new { kind = "file", path = rel, bytes = info.Length });
        }
    }

    private static bool TryResolveRelative(string root, string path, out string full, out string error)
    {
        full = string.Empty;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(root))
        {
            error = "root is required";
            return false;
        }

        var raw = (path ?? "").Trim();
        if (raw == "." || raw.Length == 0)
        {
            full = Path.GetFullPath(root);
            return true;
        }

        if (Path.IsPathRooted(raw))
        {
            error = "absolute paths not permitted";
            return false;
        }

        var normalized = raw.Replace('\\', '/').TrimStart('/');
        if (normalized == "." || normalized.Length == 0)
        {
            full = Path.GetFullPath(root);
            return true;
        }
        if (normalized.Contains("..", StringComparison.Ordinal))
        {
            error = "path traversal not permitted";
            return false;
        }

        var rootFull = Path.GetFullPath(root);
        var combined = Path.GetFullPath(Path.Combine(rootFull, normalized));
        if (!PathContainment.IsWithin(combined, rootFull))
        {
            error = "resolved path escapes root";
            return false;
        }

        full = combined;
        return true;
    }
}
