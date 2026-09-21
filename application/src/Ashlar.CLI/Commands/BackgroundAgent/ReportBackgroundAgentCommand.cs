using System.Text.Json;
using Microsoft.Extensions.Logging;
using Ashlar.BackgroundAgents.Telemetry;
using Ashlar.Manifest.Admission;
using Ashlar.Manifest.Signing;

namespace Ashlar.CLI.Commands.BackgroundAgent;

/// <summary>
/// The "what did the node do while I was away" report (A4). Where <c>stats</c> summarises raw cycle
/// throughput, this answers the operator's overnight trust question directly by joining two durable,
/// append-only records over a time window:
/// <list type="bullet">
///   <item>the cycle event log (<c>cycles.jsonl</c>) — how many cycles ran, per agent, and how they ended;</item>
///   <item>the admission gate records (<c>&lt;project&gt;/.ashlar/gates</c>) — what the node PROPOSED and what
///   the gate decided (held / admitted / rejected), with the rejection reasons.</item>
/// </list>
///
/// <para>Read-only and offline — it never starts the daemon or hits an LLM, so it is safe to run
/// against a live node. Fail-closed on a store it cannot read (a corrupt gate record surfaces as an
/// error, never a silently short report).</para>
///
/// <para>Post-apply canary REVERTS (A4) show up as rejected forge rows carrying "post-apply canary
/// failed" — inspect those with <c>background-agent proposals list --status rejected</c>; the gate
/// record itself stays Admitted (append-once: the admission decision was made before the canary ran).</para>
/// </summary>
public class ReportBackgroundAgentCommand
{
    private readonly CycleEventStore _cycles;
    private readonly ILogger<ReportBackgroundAgentCommand> _logger;

    /// <summary>Creates a new ReportBackgroundAgentCommand instance.</summary>
    public ReportBackgroundAgentCommand(
        CycleEventStore cycles,
        ILogger<ReportBackgroundAgentCommand> logger)
    {
        _cycles = cycles ?? throw new ArgumentNullException(nameof(cycles));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Print the report. See the stream overload for the actual work.</summary>
    /// <param name="sinceHours">Window; defaults to 24h when null or ≤ 0.</param>
    /// <param name="project">Project root whose <c>.ashlar/gates</c> to read; defaults to the CWD.</param>
    /// <param name="formatJson">Emit a JSON document instead of a human report.</param>
    public Task<int> ExecuteAsync(double? sinceHours, string? project, bool formatJson, CancellationToken ct = default)
        => ExecuteAsync(sinceHours, project, formatJson, Console.Out, Console.Error, ct);

    /// <summary>Test-friendly overload writing to caller-supplied streams.</summary>
    public async Task<int> ExecuteAsync(
        double? sinceHours, string? project, bool formatJson,
        TextWriter stdout, TextWriter stderr, CancellationToken ct = default)
    {
        try
        {
            var window = sinceHours is > 0 ? sinceHours.Value : 24.0;
            var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromHours(window);
            var projectRoot = string.IsNullOrWhiteSpace(project) ? Directory.GetCurrentDirectory() : project!;

            var events = _cycles.Read().Where(e => e.ts >= cutoff).ToList();
            var byAgent = events
                .GroupBy(e => e.agent, StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => new AgentActivity(
                    g.Key,
                    g.First().role,
                    g.Count(),
                    g.Count(e => e.success),
                    g.Count(e => !e.success),
                    g.Sum(e => e.tools_executed),
                    g.Sum(e => e.tools_denied),
                    g.Max(e => e.ts)))
                .ToList();

            var gates = await ReadGatesAsync(projectRoot, cutoff, ct).ConfigureAwait(false);

            if (formatJson)
            {
                var payload = new
                {
                    ok = true,
                    windowHours = window,
                    since = cutoff,
                    cyclesPath = _cycles.Path,
                    project = projectRoot,
                    activity = new { totalCycles = events.Count, agents = byAgent },
                    gates = gates.Error is not null
                        ? (object)new { error = gates.Error }
                        : new { held = gates.Held, admitted = gates.Admitted, rejected = gates.Rejected, recentRejections = gates.RecentRejections },
                };
                stdout.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
                return 0;
            }

            stdout.WriteLine($"Ashlar node report — last {window:0.#}h (since {cutoff:u})");
            stdout.WriteLine();
            stdout.WriteLine($"Activity (cycles.jsonl: {_cycles.Path})");
            if (events.Count == 0)
            {
                stdout.WriteLine("  No cycles in the window — the node was idle, disarmed (Passive), or logging elsewhere.");
            }
            else
            {
                stdout.WriteLine($"  {events.Count} cycle(s) across {byAgent.Count} agent(s):");
                foreach (var a in byAgent)
                {
                    stdout.WriteLine(
                        $"    {Truncate(a.Agent, 28),-28} {Truncate(a.Role, 10),-10} {a.Cycles,4} cycle(s)  {a.Ok} ok / {a.Fail} fail  "
                        + $"{a.ToolsExecuted} tool(s), {a.ToolsDenied} denied  last {a.LastCycle:u}");
                }
            }

            stdout.WriteLine();
            stdout.WriteLine($"Admissions ({Path.Combine(projectRoot, ".ashlar", "gates")})");
            if (gates.Error is not null)
            {
                stdout.WriteLine($"  {gates.Error}");
            }
            else if (gates.Held + gates.Admitted + gates.Rejected == 0)
            {
                stdout.WriteLine("  No proposals decided in the window.");
            }
            else
            {
                stdout.WriteLine($"  {gates.Held} held (awaiting review), {gates.Admitted} admitted, {gates.Rejected} rejected.");
                foreach (var (id, reason) in gates.RecentRejections)
                {
                    stdout.WriteLine($"    rejected {id}: {Truncate(reason, 100)}");
                }
                if (gates.Admitted > 0)
                {
                    stdout.WriteLine("  Note: a canary-reverted admission still reads as 'admitted' here — check "
                        + "`background-agent proposals list --status rejected` for post-apply reverts.");
                }
            }

            return 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Report command failed");
            if (formatJson)
                stdout.WriteLine(JsonSerializer.Serialize(new { ok = false, error = ex.Message }));
            else
                stderr.WriteLine(ex.Message);
            return 1;
        }
    }

    private static async Task<GateSummary> ReadGatesAsync(string projectRoot, DateTimeOffset cutoff, CancellationToken ct)
    {
        var gatesDir = Path.Combine(projectRoot, ".ashlar", "gates");
        if (!Directory.Exists(gatesDir))
        {
            return new GateSummary(0, 0, 0, Array.Empty<(string, string)>(),
                $"no gate records at {gatesDir} (not a self-extend project, or nothing proposed yet)");
        }
        try
        {
            // Read with the operator identity when there is one (SPEC-006 S-6): a report is where
            // an operator learns their store refuses itself, and a corrupt key surfaces here as the
            // store error below — the catch covers InvalidOperationException from TryLoad too.
            var records = await new GateStore(Path.Combine(projectRoot, ".ashlar"), OperatorKey.TryLoad()).ListAsync(ct: ct).ConfigureAwait(false);
            var inWindow = records.Where(r => r.DecidedAt >= cutoff).ToList();
            var recentRejections = inWindow
                .Where(r => r.State == ProposalState.Rejected)
                .OrderByDescending(r => r.DecidedAt)
                .Take(10)
                .Select(r => (r.Proposal.Id, r.Reason))
                .ToList();
            return new GateSummary(
                inWindow.Count(r => r.State == ProposalState.Held),
                inWindow.Count(r => r.State == ProposalState.Admitted),
                inWindow.Count(r => r.State == ProposalState.Rejected),
                recentRejections,
                null);
        }
        catch (Exception ex)
        {
            // Fail-closed: a store that cannot be fully read (a corrupt/forged record) is reported,
            // never silently summarised as empty.
            return new GateSummary(0, 0, 0, Array.Empty<(string, string)>(), $"could not read gate store: {ex.Message}");
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    private sealed record AgentActivity(
        string Agent, string Role, int Cycles, int Ok, int Fail, int ToolsExecuted, int ToolsDenied, DateTimeOffset LastCycle);

    private sealed record GateSummary(
        int Held, int Admitted, int Rejected, IReadOnlyList<(string Id, string Reason)> RecentRejections, string? Error);
}
