using Ashlar.BackgroundAgents.Observations;

namespace Ashlar.BackgroundAgents.Campaign;

/// <summary>
/// Release manager: dispatch specialist sub-agents, require a report from each,
/// fail closed on silence, publish observations, and write the campaign report.
/// </summary>
public sealed class ReleaseManagerCoordinator : IReleaseManagerCoordinator
{
    private readonly IReadOnlyDictionary<CampaignLane, ICampaignLaneRunner> _runners;
    private readonly IObservationStore? _observations;

    /// <summary>Create a coordinator from the specialist runners that will report in.</summary>
    public ReleaseManagerCoordinator(IEnumerable<ICampaignLaneRunner> runners, IObservationStore? observations = null)
    {
        ArgumentNullException.ThrowIfNull(runners);
        _runners = runners.ToDictionary(r => r.Lane);
        _observations = observations;
    }

    /// <inheritdoc />
    public async Task<CampaignReport> RunAsync(
        CampaignAgentSet agentSet,
        CampaignRunContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agentSet);
        ArgumentNullException.ThrowIfNull(context);

        var started = DateTimeOffset.UtcNow;
        var reports = new List<CampaignAgentReport>();
        var missing = new List<string>();

        var specialists = agentSet.Specialists
            .Where(s => context.LaneFilter is null ||
                        string.Equals(s.Lane.ToString(), context.LaneFilter, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(s.AgentId, context.LaneFilter, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (specialists.Count == 0)
        {
            missing.Add(context.LaneFilter ?? "(no specialists)");
        }

        foreach (var specialist in specialists)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!_runners.TryGetValue(specialist.Lane, out var runner))
            {
                missing.Add(specialist.AgentId);
                Publish(
                    specialist.AgentId,
                    ObservationKind.AgentAction,
                    ObservationSeverity.Error,
                    $"{specialist.Lane} runner is not registered with the release manager.",
                    context.CampaignId,
                    specialist.Lane,
                    CampaignVerdictKind.Error);
                continue;
            }

            var specialistContext = context with
            {
                AgentId = specialist.AgentId,
                Role = specialist.Role,
                Parameters = MergeParameters(context.Parameters, specialist.Parameters)
            };

            CampaignAgentReport agentReport;
            try
            {
                agentReport = await runner.RunAsync(specialistContext, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                agentReport = new CampaignAgentReport(
                    specialist.AgentId,
                    specialist.Role,
                    specialist.Lane,
                    CampaignVerdictKind.Error,
                    $"Specialist crashed: {ex.Message}",
                    new[] { new CampaignFinding("specialist-crash", ex.Message) },
                    started,
                    DateTimeOffset.UtcNow);
            }

            reports.Add(agentReport);
            Publish(
                agentReport.AgentId,
                KindFor(agentReport.Lane),
                SeverityFor(agentReport.Verdict),
                agentReport.Summary,
                context.CampaignId,
                agentReport.Lane,
                agentReport.Verdict,
                agentReport.Findings.Count);
        }

        var expectedIds = specialists.Select(s => s.AgentId).ToHashSet(StringComparer.Ordinal);
        foreach (var expected in expectedIds)
        {
            if (reports.All(r => !string.Equals(r.AgentId, expected, StringComparison.Ordinal)))
                missing.Add(expected);
        }

        var verdict = ResolveVerdict(reports, missing);
        var summary = verdict switch
        {
            CampaignVerdictKind.Pass =>
                $"All {reports.Count} specialist(s) reported Pass to {agentSet.ManagerId}.",
            CampaignVerdictKind.Error when missing.Count > 0 =>
                $"Fail-closed: {missing.Count} specialist(s) did not report back to {agentSet.ManagerId}.",
            _ =>
                $"{reports.Count(r => r.Verdict != CampaignVerdictKind.Pass)} specialist report(s) blocked the campaign."
        };

        var report = new CampaignReport(
            context.CampaignId,
            context.RepoRoot,
            TryReadCommitSha(context.RepoRoot),
            verdict,
            summary,
            reports,
            missing,
            DateTimeOffset.UtcNow,
            context.Full);

        Publish(
            agentSet.ManagerId,
            ObservationKind.AgentAction,
            SeverityFor(verdict),
            summary,
            context.CampaignId,
            lane: null,
            verdict,
            reports.Count);

        if (!string.IsNullOrWhiteSpace(context.OutputDirectory))
            CampaignReportWriter.Write(report, context.OutputDirectory);

        return report;
    }

    private static CampaignVerdictKind ResolveVerdict(
        IReadOnlyList<CampaignAgentReport> reports,
        IReadOnlyList<string> missing)
    {
        if (missing.Count > 0)
            return CampaignVerdictKind.Error;
        if (reports.Any(r => r.Verdict == CampaignVerdictKind.Error))
            return CampaignVerdictKind.Error;
        if (reports.Any(r => r.Verdict == CampaignVerdictKind.Fail))
            return CampaignVerdictKind.Fail;
        if (reports.Count == 0)
            return CampaignVerdictKind.Error;
        return CampaignVerdictKind.Pass;
    }

    private static ObservationKind KindFor(CampaignLane lane) => lane switch
    {
        CampaignLane.DocsDrift => ObservationKind.Analysis,
        CampaignLane.Regression => ObservationKind.Test,
        CampaignLane.DevTool => ObservationKind.AgentAction,
        _ => ObservationKind.AgentAction
    };

    private static ObservationSeverity SeverityFor(CampaignVerdictKind verdict) =>
        verdict == CampaignVerdictKind.Pass ? ObservationSeverity.Info : ObservationSeverity.Error;

    private void Publish(
        string source,
        ObservationKind kind,
        ObservationSeverity severity,
        string summary,
        string campaignId,
        CampaignLane? lane,
        CampaignVerdictKind verdict,
        int? findingCount = null)
    {
        if (_observations is null)
            return;

        var facts = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["campaign_id"] = campaignId,
            ["verdict"] = verdict.ToString()
        };
        if (lane is { } named)
            facts["lane"] = named.ToString();
        if (findingCount is int count)
            facts["finding_count"] = count.ToString();

        _observations.Append(new RuntimeObservation(
            DateTimeOffset.UtcNow,
            source,
            kind,
            summary,
            severity,
            facts));
    }

    private static IReadOnlyDictionary<string, string>? MergeParameters(
        IReadOnlyDictionary<string, string>? left,
        IReadOnlyDictionary<string, string>? right)
    {
        if (left is null && right is null)
            return null;
        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (left is not null)
        {
            foreach (var (key, value) in left)
                merged[key] = value;
        }

        if (right is not null)
        {
            foreach (var (key, value) in right)
                merged[key] = value;
        }

        return merged;
    }

    private static string TryReadCommitSha(string repoRoot)
    {
        try
        {
            var head = Path.Combine(repoRoot, ".git", "HEAD");
            if (!File.Exists(head))
                return "unknown";

            var value = File.ReadAllText(head).Trim();
            if (value.StartsWith("ref:", StringComparison.Ordinal))
            {
                var refPath = Path.Combine(repoRoot, ".git", value[4..].Trim().Replace('/', Path.DirectorySeparatorChar));
                return File.Exists(refPath) ? File.ReadAllText(refPath).Trim() : "unknown";
            }

            return value;
        }
        catch
        {
            return "unknown";
        }
    }
}
