using System.Text.Json;
using Ashlar.Abstractions;
using Ashlar.Core.Application.Paths;

namespace Ashlar.BackgroundAgents.HostRunners;

/// <summary>
/// The governance floor's twin in the POLICY chain: refuses, as a policy DENIAL, the writes the tool
/// edge (<c>ToolSandbox.TryResolveWritePath</c>) would refuse anyway.
///
/// <para>Why a second copy of the judgement exists. A <c>ToolSandbox</c> rejection comes back as a
/// <c>ToolResult</c>, so <c>ToolCallingAgent</c> counts the call as EXECUTED: the cycle's denial
/// count stays zero, the sandbox course passes, and the admission bridge never sees that anything
/// was refused. Only a <c>PolicyEngine</c> refusal is counted, fails the sandbox course, and is fed
/// back to the planner as "DENIED (reason). Adjust your plan and try again." The tool edge is the
/// floor; this is its failure shape and its planner feedback.</para>
///
/// <para>Judged by tool id — exactly the write tools — and not by argument shape. The read-intent
/// tools (<c>repo.fs.read</c>, <c>repo.fs.list</c>) carry a string <c>path</c> too, and a read of
/// <c>.ashlar/</c>, a build import or the operator policy is legitimate; a planner that cannot list
/// the repository root cannot plan at all. The id set is the five-file inventory
/// <c>WriteToolResolverConventionTests</c> freezes, and that test also checks that every inventory
/// tool's id is named here and in <c>PolicyMcpInvocationGate</c>, so a new write tool cannot join
/// the inventory without joining both lists. A tool this list does not name is still refused at the
/// tool edge — the floor is there whatever this policy says — only uncounted.</para>
///
/// <para>PURE PREDICATES ONLY, and the whole body is caught. This calls
/// <see cref="MediatedWritePath.IsSafeRelativePath"/> and
/// <see cref="MediatedWritePath.IsAuthoringGovernancePath"/> and nothing that touches the
/// filesystem: the containment and reparse-point probes stay in the tool, one call per real write.
/// Two things depend on that. <c>PolicyEngine.Approve</c> has no try/catch and the approval call
/// sits inside <c>ToolCallingAgent.RunCycleAsync</c>'s outer handler, so a policy that throws ends
/// the whole cycle rather than one call. And the empty snapshot
/// (<c>new WorldSnapshot(0, new Dictionary&lt;string, object?&gt;())</c>) is the universal fixture
/// across the policy test classes, which sample the chain at hundreds of arbitrary paths and hammer it
/// fifty-wide; a root-requiring, probing policy would deny all of them and go flaky under load.
/// <c>WriteToolResolverConventionTests</c> freezes the no-filesystem-work rule.</para>
///
/// <para>The refusal says the path is governance and not writable from a cycle. It does not offer
/// <c>forge.propose_change</c> as the remedy: the forge door refuses the same targets, so naming it
/// would steer the planner into a call that is itself refused.</para>
/// </summary>
internal sealed class GovernanceFloorPolicy : IPolicy
{
    /// <summary>
    /// The tools that mutate the repository — the inventory <c>WriteToolResolverConventionTests</c>
    /// freezes, which also checks that each id is named here. <c>docs.update</c> and
    /// <c>repo.git.commit</c> take no <c>path</c> (their targets are fixed root files the resolver
    /// judges), so they pass through; they are listed so the inventory and this set stay one thing.
    /// </summary>
    private static readonly HashSet<string> WriteToolIds = new(StringComparer.Ordinal)
    {
        "repo.fs.write",
        "repo.fs.search_replace",
        "repo.fs.ensure_file",
        "docs.update",
        "repo.git.commit",
    };

    public bool Approve(ToolCall call, WorldSnapshot s, out string reason)
    {
        reason = "OK";
        try
        {
            if (!WriteToolIds.Contains(call.Id))
            {
                return true;
            }

            if (call.Arguments.ValueKind != JsonValueKind.Object
                || !call.Arguments.TryGetProperty("path", out var p)
                || p.ValueKind != JsonValueKind.String)
            {
                // Nothing to judge: a write tool called without a string path fails at the tool
                // edge ("path is required"), and the fixed-target tools never carry one.
                return true;
            }

            var raw = p.GetString() ?? string.Empty;
            var rel = raw.Replace('\\', '/');

            // The safe-shape leg first, so a '.' or '..' segment can never reach the governance
            // predicate un-normalized. An absolute path is refused here too: the tool edge never
            // accepts one, and a denial the planner can read beats an executed-then-rejected call.
            if (!MediatedWritePath.IsSafeRelativePath(rel))
            {
                reason = $"Governance floor: '{raw}' is not a safe repo-relative path — no '.', '..', "
                    + "empty, rooted, drive-letter or ':' stream, reserved-device or trailing dot/space segments.";
                return false;
            }

            if (MediatedWritePath.IsAuthoringGovernancePath(rel))
            {
                reason = IsUnderAshlar(rel)
                    ? $"Governance floor: '{rel}' is under .ashlar/ — the admission ledger and runtime state. "
                        + "It is not writable from a cycle, by any tool: a cycle cannot extend its own budget. "
                        + "Continue with the objective elsewhere."
                    : $"Governance floor: '{rel}' is a governance or build-tooling path (the project contract, the "
                        + "operator policy, a build import, or tooling configuration). It is not writable from a cycle, "
                        + "by any tool; a change here is an operator's to make. Continue with the objective elsewhere.";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            // A policy that throws kills the whole cycle; a refusal with a reason costs one call.
            reason = $"Governance floor: could not evaluate the call ({ex.GetType().Name}); refusing.";
            return false;
        }
    }

    private static bool IsUnderAshlar(string rel)
    {
        var first = rel.Split('/')[0];
        return string.Equals(first, ".ashlar", StringComparison.OrdinalIgnoreCase);
    }
}
