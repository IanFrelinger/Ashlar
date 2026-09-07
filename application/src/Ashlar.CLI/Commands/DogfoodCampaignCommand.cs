using Ashlar.BackgroundAgents.Campaign;
using Ashlar.BackgroundAgents.Observations;
using Ashlar.Core.Application.Paths;

namespace Ashlar.CLI.Commands;

/// <summary>
/// Runs the automated dogfood campaign: specialist sub-agents report to the
/// release manager, which fail-closes on silence or a failing lane.
/// </summary>
internal static class DogfoodCampaignCommand
{
    /// <summary>Execute the campaign and return a process exit code.</summary>
    public static async Task<int> ExecuteAsync(
        bool json,
        bool full,
        bool verbose,
        string? configPath,
        string? outputDirectory,
        string? lane,
        CancellationToken cancellationToken = default)
    {
        var repoRoot = RepoPathResolver.FindRepoRoot();
        var slnPath = Path.Combine(repoRoot, "Ashlar.sln");
        if (!File.Exists(slnPath))
        {
            WriteFailure(json, "Not in Ashlar repo. Run from the Ashlar repository root.");
            return 1;
        }

        var resolvedConfig = string.IsNullOrWhiteSpace(configPath)
            ? Path.Combine(repoRoot, CampaignAgentSetLoader.DefaultRelativePath)
            : Path.GetFullPath(configPath);
        var resolvedOutput = string.IsNullOrWhiteSpace(outputDirectory)
            ? Path.Combine(repoRoot, CampaignReportWriter.DefaultRelativeOutputDirectory)
            : Path.GetFullPath(outputDirectory);

        CampaignAgentSet agentSet;
        try
        {
            agentSet = await CampaignAgentSetLoader.LoadAsync(resolvedConfig, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            WriteFailure(json, ex.Message);
            return 1;
        }

        Directory.CreateDirectory(resolvedOutput);
        var observationsPath = Path.Combine(resolvedOutput, "observations.jsonl");
        IObservationStore observations = new JsonlObservationStore(observationsPath);
        var invoker = new ProcessCampaignProcessInvoker();
        var coordinator = new ReleaseManagerCoordinator(
            new ICampaignLaneRunner[]
            {
                new DocsDriftLaneRunner(),
                new RegressionLaneRunner(invoker),
                new DevToolLaneRunner(invoker)
            },
            observations);

        var context = new CampaignRunContext(
            repoRoot,
            CampaignId: "dev-tool-dogfood",
            AgentId: agentSet.ManagerId,
            Role: "release-manager",
            Full: full,
            SkipProcessLanes: false,
            OutputDirectory: resolvedOutput,
            LaneFilter: lane);

        if (verbose && !json)
        {
            Console.WriteLine($"dogfood campaign: manager={agentSet.ManagerId} specialists={agentSet.Specialists.Count} mode={(full ? "full" : "fast")}");
            foreach (var specialist in agentSet.Specialists)
                Console.WriteLine($"  {specialist.AgentId} ({specialist.Role}) → {specialist.Lane}");
        }

        CampaignReport report;
        try
        {
            report = await coordinator.RunAsync(agentSet, context, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            WriteFailure(json, ex.Message);
            return 1;
        }

        if (json)
        {
            Console.WriteLine(CampaignReportWriter.ToJson(report));
        }
        else
        {
            Console.WriteLine($"dogfood campaign {report.Verdict}: {report.Summary}");
            foreach (var agentReport in report.Reports)
            {
                Console.WriteLine($"  {agentReport.Lane} / {agentReport.AgentId}: {agentReport.Verdict} — {agentReport.Summary}");
                foreach (var finding in agentReport.Findings)
                {
                    var location = finding.Path is null
                        ? string.Empty
                        : finding.Line is int line
                            ? $" ({finding.Path}:{line})"
                            : $" ({finding.Path})";
                    Console.WriteLine($"    - [{finding.Code}] {finding.Message}{location}");
                }
            }

            if (report.MissingReports.Count > 0)
            {
                foreach (var missing in report.MissingReports)
                    Console.WriteLine($"  missing: {missing}");
            }

            Console.WriteLine($"Report: {Path.Combine(resolvedOutput, "report.json")}");
        }

        return report.Verdict == CampaignVerdictKind.Pass ? 0 : 1;
    }

    private static void WriteFailure(bool json, string message)
    {
        if (json)
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { passed = false, reason = message }));
        else
            Console.Error.WriteLine($"dogfood campaign FAILED: {message}");
    }
}
