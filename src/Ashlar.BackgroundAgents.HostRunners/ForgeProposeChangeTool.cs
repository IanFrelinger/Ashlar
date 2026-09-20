using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ashlar.Abstractions;
using Ashlar.BackgroundAgents.Forge;
using Ashlar.Core.Application.Paths;
using Ashlar.Manifest.Packaging;
using Ashlar.Tools.Dev.Deltas;

namespace Ashlar.BackgroundAgents.HostRunners;

/// <summary>
/// Tool the planner calls instead of <c>repo.fs.write</c> when the daemon is
/// running in a low-trust mode (Passive / SemiActive). The proposal is recorded
/// in the <see cref="IChangeProposalStore"/> so an operator (or, eventually, an
/// auto-approver agent) can review the diff and choose to apply it.
///
/// <para>The tool is intentionally a no-op against the source tree: it never
/// touches the file at <c>target_path</c> directly. The full new contents are
/// captured in the proposal so review can happen offline. A SHA-256 of the
/// current file contents is recorded as <c>BaseSha256</c> to detect drift at
/// apply time.</para>
/// </summary>
public sealed class ForgeProposeChangeTool : ITool
{
    private readonly IChangeProposalStore _store;
    private readonly Func<string?> _agentIdProvider;
    private readonly Func<string?> _objectiveIdProvider;

    public ForgeProposeChangeTool(
        IChangeProposalStore store,
        Func<string?>? agentIdProvider = null,
        Func<string?>? objectiveIdProvider = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _agentIdProvider = agentIdProvider ?? (() => null);
        _objectiveIdProvider = objectiveIdProvider ?? (() => null);
    }

    public string Id => "forge.propose_change";

    public ToolSchema Schema => new(Id,
        "Propose a file change for operator review instead of writing directly. Use when forge-mediated writes are required.",
        """
        {"type":"object","required":["target_path","new_content","summary"],"properties":{"target_path":{"type":"string","description":"Repo-relative path to modify"},"new_content":{"type":"string","description":"Full new file content"},"summary":{"type":"string","description":"One-line summary of the change"},"reason":{"type":"string","description":"Why this change is needed"}}}
        """);

    private sealed record Args(string target_path, string new_content, string summary, string? reason);

    public Task<ToolResult> InvokeAsync(ToolCall call, WorldSnapshot s, CancellationToken ct)
    {
        var args = JsonSerializer.Deserialize<Args>(call.Arguments)
                   ?? throw new InvalidOperationException("forge.propose_change: empty arguments");
        var delta = new RepoDelta { TickFrom = s.Tick, TickTo = s.Tick + 1 };

        try
        {
            if (string.IsNullOrWhiteSpace(args.target_path))
                throw new ArgumentException("target_path is required");
            if (string.IsNullOrWhiteSpace(args.summary))
                throw new ArgumentException("summary is required");
            if (args.new_content is null)
                throw new ArgumentException("new_content is required");

            var rel = args.target_path.Replace('\\', '/').TrimStart('/');
            if (!ExtensionPackaging.IsSafeRelativePath(rel))
                throw new ArgumentException(
                    $"target_path '{args.target_path}' is not a safe repo-relative path: no '.', '..', "
                    + "empty, rooted, drive-letter, reserved-device or trailing-dot/space segments. The apply "
                    + "choke point enforces the same floor; this refuses the malformed proposal at the door.");
            // The governance floor at the door, with the predicate the apply choke point uses:
            // ForgeApplier.StageWrites calls MediatedWritePath.Refuse, whose governance leg is
            // IsGovernancePath, so a proposal against any of these could never apply. Parking it
            // anyway would let the cycle sign a record claiming an unfulfillable parked row.
            if (MediatedWritePath.IsGovernancePath(rel))
                throw new ArgumentException(
                    $"target_path '{args.target_path}' is a governance path — the project contract, the operator "
                    + "policy, .ashlar/ state, a build import, tooling configuration, or a project/solution file. "
                    + "The apply choke point refuses it, so it cannot be proposed either; a change here is an "
                    + "operator's to make.");
            // Compute base sha against the existing file (when it exists) so the
            // operator can detect drift between proposal time and apply time.
            string? baseSha = null;
            var sandboxRoot = ResolveSandboxRoot(s);
            if (!string.IsNullOrWhiteSpace(sandboxRoot))
            {
                var fullPath = Path.GetFullPath(Path.Combine(sandboxRoot, rel));
                if (File.Exists(fullPath))
                {
                    using var stream = File.OpenRead(fullPath);
                    var hash = SHA256.HashData(stream);
                    baseSha = Convert.ToHexString(hash);
                }
            }

            var id = $"prop-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}".Substring(0, 32);
            var proposal = _store.Add(new ChangeProposal
            {
                Id = id,
                TargetPath = rel,
                NewContent = args.new_content,
                Summary = args.summary.Trim(),
                Reason = args.reason,
                AgentId = _agentIdProvider() ?? (s.Data.TryGetValue("AgentName", out var n) ? n as string : null),
                ObjectiveId = _objectiveIdProvider() ?? (s.Data.TryGetValue("ObjectiveId", out var o) ? o as string : null),
                BaseSha256 = baseSha,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });

            delta.AddLog($"forge.propose_change:{proposal.Id} target={rel} bytes={Encoding.UTF8.GetByteCount(args.new_content)}");
            return Task.FromResult(new ToolResult(delta, new
            {
                ok = true,
                id = proposal.Id,
                status = proposal.Status.ToString(),
                target_path = proposal.TargetPath,
                base_sha256 = proposal.BaseSha256,
                hint = "Operator must approve via `ashlar background-agent proposals approve <id>` before this lands."
            }));
        }
        catch (Exception ex)
        {
            delta.AddLog($"forge.propose_change REJECTED ({ex.Message})");
            return Task.FromResult(new ToolResult(delta, new
            {
                ok = false,
                error = ex.Message
            }));
        }
    }

    private static string? ResolveSandboxRoot(WorldSnapshot s)
    {
        if (s.Data.TryGetValue("RepoRoot", out var root) && root is string r)
            return r;
        if (s.Data.TryGetValue("SandboxRoot", out var sb) && sb is string sbs)
            return sbs;
        return Environment.GetEnvironmentVariable("ASHLAR_SANDBOX_ROOT");
    }
}
