using System.Text.Json;
using Ashlar.Abstractions;
using Ashlar.Abstractions.Paths;
using Ashlar.Tools.Dev.Deltas;

namespace Ashlar.Tools.Dev;

/// <summary>
/// Tool for reading a text file under the repository root.
///
/// Companion to <see cref="RepoFsWriteTool"/> and <see cref="RepoFsSearchReplaceTool"/>: gives
/// tool-calling agents a way to inspect current file contents before deciding to modify them.
/// Without a read capability, the LLM is asked to write blind and rationally returns no actions.
///
/// Behaviour:
/// - Resolves the file relative to the supplied <c>root</c> argument.
/// - Refuses to follow paths that escape the root (basic ".." / absolute-path guard).
/// - Truncates the returned content at <c>max_bytes</c> (default 64 KiB) to keep prompts bounded.
/// - Returns <c>{ path, exists, bytes, truncated, content }</c>.
/// </summary>
public sealed class RepoFsReadTool : ITool
{
    private const int DefaultMaxBytes = 64 * 1024;

    public string Id => "repo.fs.read";

    public ToolSchema Schema => new(Id, "Read a text file under the repo root", """
    {"type":"object","required":["path"],"properties":{"path":{"type":"string","description":"Path relative to the sandbox root"},"max_bytes":{"type":"integer","description":"Optional cap on returned bytes (default 65536)"}}}
    """);

    // No `root` — see ToolSandbox. Read-only, but a model-chosen root made it an
    // arbitrary-file-disclosure primitive.
    private sealed record Args(string path, int? max_bytes);

    public async Task<ToolResult> InvokeAsync(ToolCall call, WorldSnapshot s, CancellationToken ct)
    {
        var args = JsonSerializer.Deserialize<Args>(call.Arguments)!;
        var maxBytes = args.max_bytes is > 0 ? args.max_bytes.Value : DefaultMaxBytes;

        if (!ToolSandbox.TryResolvePath(s, args.path, out var full, out var error))
        {
            var errDelta = new RepoDelta { TickFrom = s.Tick, TickTo = s.Tick + 1 };
            errDelta.AddLog($"read:{args.path} REJECTED ({error})");
            return new ToolResult(errDelta, new { path = args.path, exists = false, error });
        }

        if (!File.Exists(full))
        {
            var missDelta = new RepoDelta { TickFrom = s.Tick, TickTo = s.Tick + 1 };
            missDelta.AddLog($"read:{args.path} not_found");
            return new ToolResult(missDelta, new { path = args.path, exists = false });
        }

        var bytes = await File.ReadAllBytesAsync(full, ct).ConfigureAwait(false);
        var truncated = bytes.Length > maxBytes;
        var slice = truncated ? bytes.AsMemory(0, maxBytes).ToArray() : bytes;
        // Best-effort UTF-8 decode; binary content will surface as replacement chars but the
        // LLM is expected to call this on text files (it can list first to discover them).
        var text = System.Text.Encoding.UTF8.GetString(slice);

        var delta = new RepoDelta { TickFrom = s.Tick, TickTo = s.Tick + 1 };
        delta.AddLog($"read:{args.path} bytes={bytes.Length}{(truncated ? " (truncated)" : "")}");
        return new ToolResult(delta, new
        {
            path = args.path,
            exists = true,
            bytes = bytes.Length,
            truncated,
            content = text
        });
    }

    /// <summary>Path sandbox helper shared by repo filesystem tools.</summary>
    internal static bool TryResolveRelative(string root, string path, out string full, out string error)
    {
        full = string.Empty;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(path))
        {
            error = "root and path are required";
            return false;
        }

        var trimmed = path.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            error = "path is required";
            return false;
        }

        if (Path.IsPathRooted(trimmed))
        {
            error = "absolute paths not permitted";
            return false;
        }

        var normalized = trimmed.Replace('\\', '/').TrimStart('/');
        if (normalized.Contains("..", StringComparison.Ordinal))
        {
            error = "path traversal not permitted";
            return false;
        }
        if (Path.IsPathRooted(normalized))
        {
            error = "absolute paths not permitted";
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
