using System.Diagnostics.CodeAnalysis;
using System.Text;
using Ashlar.Core.Application.Autonomy;

namespace Ashlar.Infrastructure.Certification.HotSwap;

/// <summary>
/// The human-reviewable digest (autonomous self-extension spec R7.1/R7.3): Tier-0
/// autonomy never means silence. Renders swap provenance into markdown that keeps two
/// categories visibly distinct — "the loop DID X" (committed swaps, rollbacks,
/// quarantines) versus "the loop PROPOSES X and is HOLDING" (certified-but-held Tier-1
/// artifacts awaiting the human gate, supplied by the caller from the record store) —
/// so held work is presented with evidence, never auto-expired into abandonment.
/// </summary>
[Experimental(AutonomyExperimental.DiagnosticId, UrlFormat = AutonomyExperimental.UrlFormat)]
public static class AutonomyDigest
{
    /// <summary>One held Tier-1 artifact awaiting human admission (R7.3).</summary>
    public sealed record HeldArtifact(string BrickId, string ContentHash, string Reason);

    /// <summary>Renders the digest for a set of provenance events and held artifacts.</summary>
    public static string Render(
        IReadOnlyList<BrickSwapProvenanceEvent> events,
        IReadOnlyList<HeldArtifact>? held = null,
        AutonomyLedgerScan.Report? ledgerScan = null)
    {
        if (events is null)
            throw new ArgumentNullException(nameof(events));

        var sb = new StringBuilder();
        sb.AppendLine("# Autonomy digest");
        sb.AppendLine();

        sb.AppendLine("## The loop did");
        sb.AppendLine();
        // A closed allowlist: an outcome not listed here is a silent terminal state, which
        // is worse than any misleading sentence — the refused, exhausted and corrupt-
        // retention rollback outcomes are exactly the ones an operator must not miss.
        var did = events
            .Where(e => e.Outcome is BrickSwapProvenanceOutcomes.SwapCommitted
                or BrickSwapProvenanceOutcomes.RollbackCommitted
                or BrickSwapProvenanceOutcomes.WatchBreachQuarantined
                or BrickSwapProvenanceOutcomes.SwapRefused
                or BrickSwapProvenanceOutcomes.RollbackRefused
                or BrickSwapProvenanceOutcomes.RollbackExhausted
                or BrickSwapProvenanceOutcomes.RetentionInvariantViolated)
            .OrderBy(e => e.Timestamp)
            .ToList();
        if (did.Count == 0)
        {
            sb.AppendLine("- (nothing)");
        }
        else
        {
            foreach (var e in did)
            {
                sb.Append("- ").Append(e.Timestamp.ToString("u"))
                    .Append(" — **").Append(e.Outcome).Append("** generation ").Append(e.Generation);
                if (!string.IsNullOrWhiteSpace(e.Reason))
                    sb.Append(": ").Append(e.Reason);
                sb.AppendLine();
            }
        }

        sb.AppendLine();
        sb.AppendLine("## The loop proposes and is holding");
        sb.AppendLine();
        if (held is null || held.Count == 0)
        {
            sb.AppendLine("- (nothing held)");
        }
        else
        {
            foreach (var artifact in held)
            {
                sb.Append("- **HELD** ").Append(artifact.BrickId)
                    .Append(" (").Append(artifact.ContentHash).Append(") — ")
                    .AppendLine(artifact.Reason);
            }
        }

        if (ledgerScan is not null)
        {
            sb.AppendLine();
            sb.AppendLine("## Ledger scan");
            sb.AppendLine();
            if (ledgerScan.IsClean)
            {
                sb.AppendLine("- clean: no revocation-chain suspects, no depth contradictions");
            }
            else
            {
                foreach (var suspect in ledgerScan.RevocationSuspects)
                {
                    sb.Append("- **SUSPECT** ").Append(suspect.BrickId)
                        .Append(" (").Append(suspect.ContentHash)
                        .AppendLine(") — inputs include a revoked artifact; flag for re-verification (R5.4)");
                }

                foreach (var contradiction in ledgerScan.LaunderingContradictions)
                {
                    sb.Append("- **DEPTH CONTRADICTION** ").Append(contradiction.ContentHash)
                        .Append(" certified at depths [")
                        .Append(string.Join(", ", contradiction.RecordedDepths))
                        .AppendLine("] — possible laundering (§8)");
                }
            }
        }

        return sb.ToString();
    }
}
