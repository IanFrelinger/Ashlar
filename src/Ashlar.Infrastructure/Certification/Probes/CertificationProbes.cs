using Ashlar.Core.Application.Certification.Models;
using Ashlar.Core.Application.Certification.Ports;

namespace Ashlar.Infrastructure.Certification.Probes;

/// <summary>
/// Probe catalog v1 (G4): started from the failure classes already in the gate's own
/// history, exactly as the integration plan prescribes. Every probe reads only the failed
/// decision and its request; none can change a verdict.
/// </summary>
internal static class CertificationProbeCatalog
{
    /// <summary>The default probes the gate runs on rejection.</summary>
    public static IReadOnlyList<IDiagnosticProbe> Default { get; } = new IDiagnosticProbe[]
    {
        new MutationSurvivorProbe(),
        new AnalyzerFindingGroupProbe(),
        new NonCompilingCandidateProbe(),
    };
}

/// <summary>
/// Mutation failures: names the surviving mutants and the escape arithmetic, so the
/// repair prompt can say "kill these" instead of "mutation failed".
/// </summary>
public sealed class MutationSurvivorProbe : IDiagnosticProbe
{
    /// <inheritdoc />
    public string FailureCheck => "mutation";

    /// <inheritdoc />
    public DiagnosticProbeFinding? Probe(CertificationRequest request, CertificationDecision decision)
    {
        var record = decision.Record;
        if (record.TotalMutants is null or 0)
        {
            return new DiagnosticProbeFinding(
                nameof(MutationSurvivorProbe),
                "no mutants were generated from the catalog — the witness was never challenged",
                new Dictionary<string, string> { ["totalMutants"] = "0" });
        }

        if (record.SurvivingMutantIds.Count == 0)
            return null;

        return new DiagnosticProbeFinding(
            nameof(MutationSurvivorProbe),
            $"{record.SurvivingMutantIds.Count} mutant(s) escaped the witness — strengthen the witness to kill them",
            new Dictionary<string, string>
            {
                ["survivors"] = string.Join(",", record.SurvivingMutantIds),
                ["escapeRate"] = record.EscapeRate?.ToString("F2") ?? "",
                ["totalMutants"] = record.TotalMutants?.ToString() ?? "",
            });
    }
}

/// <summary>
/// Analyzer failures: groups the verbatim findings by rule id so the repair loop sees
/// which rule dominates. The verbatim messages themselves stay on the record (A3.1).
/// </summary>
internal sealed class AnalyzerFindingGroupProbe : IDiagnosticProbe
{
    /// <inheritdoc />
    public string FailureCheck => "analyzer";

    /// <inheritdoc />
    public DiagnosticProbeFinding? Probe(CertificationRequest request, CertificationDecision decision)
    {
        var reason = decision.Record.Reason ?? "";
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var index = 0;
        while ((index = reason.IndexOf("[ASHLAR", index, StringComparison.Ordinal)) >= 0)
        {
            var close = reason.IndexOf(']', index);
            if (close < 0)
                break;
            var id = reason.Substring(index + 1, close - index - 1);
            counts[id] = counts.TryGetValue(id, out var n) ? n + 1 : 1;
            index = close + 1;
        }

        if (counts.Count == 0)
            return null;

        return new DiagnosticProbeFinding(
            nameof(AnalyzerFindingGroupProbe),
            "analyzer findings grouped by rule; repair the dominant rule first",
            counts.OrderByDescending(kv => kv.Value)
                .ToDictionary(kv => kv.Key, kv => kv.Value.ToString(), StringComparer.Ordinal));
    }
}

/// <summary>
/// The non-compiling candidate class (the vacuous-mutation-kill hole's neighbor): pulls
/// the first compiler error forward as the single fix target.
/// </summary>
internal sealed class NonCompilingCandidateProbe : IDiagnosticProbe
{
    /// <inheritdoc />
    public string FailureCheck => "analyzer";

    /// <inheritdoc />
    public DiagnosticProbeFinding? Probe(CertificationRequest request, CertificationDecision decision)
    {
        var reason = decision.Record.Reason ?? "";
        const string marker = "does not compile";
        if (!reason.Contains(marker, StringComparison.Ordinal))
            return null;

        var colon = reason.IndexOf(':', reason.IndexOf(marker, StringComparison.Ordinal));
        var firstError = colon >= 0
            ? reason.Substring(colon + 1).Split('|')[0].Trim()
            : "";

        return new DiagnosticProbeFinding(
            nameof(NonCompilingCandidateProbe),
            "the candidate does not compile; fix the first error before anything else matters",
            new Dictionary<string, string> { ["firstError"] = firstError });
    }
}
