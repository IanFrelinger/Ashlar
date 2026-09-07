using Ashlar.Orchestration.Agents;
using Ashlar.Orchestration.Coordination;

namespace Ashlar.CLI.Formatting;

/// <summary>
/// The single judgement of whether an orchestration actually DID anything, shared by the renderer
/// that prints the outcome and the command that returns an exit code, so the two can never disagree.
///
/// <para>The defect this closes: <c>ashlar run</c> printed "Orchestration completed successfully"
/// on the line above "Progress: 0/0 agents completed (0 %)" and exited 0. Both statements came from
/// the same result object; nothing reconciled them. <c>Success</c> on an orchestration result means
/// "the integrated output was structurally valid", which is true of the empty output produced when
/// no agent ran — so the success flag alone is not evidence that work happened, and the machinery
/// reported the absence of work as a clean run.</para>
///
/// <para>Deliberately conservative: when there is no progress summary at all, this reports that
/// work was done. An unmeasured run is not the reported defect, and refusing one would turn a
/// reporting fix into a behavioural regression for every caller that does not track progress.</para>
/// </summary>
public static class OrchestrationWorkReport
{
    /// <summary>
    /// True unless the run is measurably empty. Two ways it can be:
    /// <list type="bullet">
    /// <item>a progress summary saying no agent completed; or</item>
    /// <item>every integrated result being a <see cref="PlaceholderAgentResult"/> — the marker
    /// <c>GenericAgent</c> returns when a domain had no specialized agent and it performed no
    /// work. That marker existed before this method did, and nothing looked at it.</item>
    /// </list>
    /// </summary>
    public static bool DidWork(OrchestrationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.ProgressSummary is { } progress && progress.Completed == 0)
        {
            return false;
        }

        var integrated = result.IntegratedOutput?.IntegratedResults;
        if (integrated is { Count: > 0 } && integrated.Values.All(v => v is PlaceholderAgentResult))
        {
            return false;
        }

        return true;
    }

    /// <summary>The domains that produced a placeholder — named, so the report can say which.</summary>
    public static IReadOnlyList<string> PlaceholderDomains(OrchestrationResult result)
    {
        var integrated = result.IntegratedOutput?.IntegratedResults;
        if (integrated is null)
        {
            return [];
        }
        return integrated
            .Where(kvp => kvp.Value is PlaceholderAgentResult)
            .Select(kvp => kvp.Key)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// True when the result claims success but nothing completed — the exact shape that was
    /// printed as "completed successfully" and exited 0.
    /// </summary>
    public static bool IsSilentSuccess(OrchestrationResult result) => result.Success && !DidWork(result);

    /// <summary>
    /// What to print instead of a success banner. Names the fix, in the order a person can act on
    /// it, rather than restating the counter.
    /// </summary>
    public static IReadOnlyList<string> NoWorkReport(OrchestrationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var total = result.ProgressSummary?.TotalAgents ?? 0;
        var failed = result.ProgressSummary?.Failed ?? 0;
        var planned = result.Decomposition?.Agents.Count ?? 0;
        var placeholders = PlaceholderDomains(result);

        var lines = new List<string>();
        if (placeholders.Count > 0)
        {
            lines.Add("Orchestration finished, but NO WORK WAS PERFORMED — every result was a placeholder.");
            lines.Add($"  Domains with no specialized agent: {string.Join(", ", placeholders)}.");
            lines.Add("  A generic fallback agent ran for each and returned \"no work performed\". That is a");
            lines.Add("  real result object, which is why this used to print \"completed successfully\".");
        }
        else if (total == 0)
        {
            lines.Add("Orchestration finished, but NO AGENT RAN — 0 agents were active.");
        }
        else
        {
            lines.Add($"Orchestration finished, but NO AGENT COMPLETED — 0 of {total} agents completed"
                + (failed > 0 ? $" ({failed} failed)." : "."));
        }

        lines.Add("  Nothing was produced, so there is nothing here to trust. This exits non-zero on purpose:");
        lines.Add("  a run that did no work must never report success.");
        lines.Add("  what to check, in order:");

        if (planned > 0 && total == 0)
        {
            lines.Add($"  - the request decomposed into {planned} agent role(s) but none became active;");
            lines.Add("    re-run with --verbose to see the decomposition and where it stopped.");
        }
        else
        {
            lines.Add("  - the request: it is decomposed into named domains, and a domain this build has no");
            lines.Add("    specialized agent for falls back to a generic agent that does nothing. Rephrase the");
            lines.Add("    request toward a domain that is implemented, or implement an agent for that domain.");
        }

        lines.Add("  - the provider: `ashlar run` takes it from the first modelled agent in ashlar.yaml.");
        lines.Add("    The scaffold ships `provider: mock`, which answers offline but does not do your work —");
        lines.Add("    point that agent at a real provider (e.g. `provider: ollama`, `id: llama3`).");
        lines.Add("  - re-run with --verbose for the per-agent trace.");
        return lines;
    }
}
