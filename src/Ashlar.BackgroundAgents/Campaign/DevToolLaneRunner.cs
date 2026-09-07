namespace Ashlar.BackgroundAgents.Campaign;

/// <summary>
/// Specialist that checks Ashlar still presents as a developer tool: CLI
/// dogfood/campaign surface, brick scaffold, authoring docs, and the packable
/// <c>ashlar</c> tool. Full mode also invokes <c>ashlar --help</c>.
/// </summary>
public sealed class DevToolLaneRunner : ICampaignLaneRunner
{
    private readonly ICampaignProcessInvoker? _invoker;

    /// <summary>Create a runner. <paramref name="invoker"/> is required for full-mode CLI invocation.</summary>
    public DevToolLaneRunner(ICampaignProcessInvoker? invoker = null)
    {
        _invoker = invoker;
    }

    /// <inheritdoc />
    public CampaignLane Lane => CampaignLane.DevTool;

    /// <inheritdoc />
    public async Task<CampaignAgentReport> RunAsync(CampaignRunContext context, CancellationToken cancellationToken = default)
    {
        var started = DateTimeOffset.UtcNow;
        var findings = new List<CampaignFinding>();
        var repoRoot = context.RepoRoot;

        if (string.IsNullOrWhiteSpace(repoRoot) || !Directory.Exists(repoRoot))
        {
            return Error(context, started, "Repo root is missing.", "missing-repo");
        }

        RequireFile(repoRoot, "application/src/Ashlar.CLI/Commands/DogfoodCommand.cs", "missing-dogfood-cli", findings);
        RequireFile(repoRoot, "application/src/Ashlar.CLI/Commands/DogfoodCampaignCommand.cs", "missing-campaign-cli", findings);
        RequireFile(repoRoot, "application/src/Ashlar.CLI/Commands/NewCommand.cs", "missing-new-cli", findings);
        RequireFile(repoRoot, "docs/AuthoringBricks.md", "missing-authoring-docs", findings);
        RequireFile(repoRoot, "docs/GettingStarted.md", "missing-getting-started", findings);
        RequireFile(repoRoot, "docs/DogfoodCampaign.md", "missing-campaign-docs", findings);
        RequireFile(repoRoot, "docs/background-agents/examples/dogfood-campaign.json", "missing-campaign-agent-set", findings);
        RequireFile(repoRoot, "scripts/run-in-devcontainer.sh", "missing-devcontainer-wrapper", findings);
        RequireFile(repoRoot, "scripts/handoff/devbox.sh", "missing-devbox", findings);
        RequireFile(repoRoot, ".docker/Dockerfile.devtest", "missing-devtest-image", findings);
        RequireFile(repoRoot, "scripts/run-dogfood-campaign.sh", "missing-campaign-script", findings);

        var campaignScript = Path.Combine(repoRoot, "scripts", "run-dogfood-campaign.sh");
        if (File.Exists(campaignScript) &&
            !File.ReadAllText(campaignScript).Contains("run-in-devcontainer.sh", StringComparison.Ordinal))
        {
            findings.Add(new CampaignFinding(
                "campaign-not-containerized",
                "scripts/run-dogfood-campaign.sh must enter the dev/test container so the SDK is not a host install.",
                "scripts/run-dogfood-campaign.sh"));
        }

        var dogfoodCommand = Path.Combine(repoRoot, "application", "src", "Ashlar.CLI", "Commands", "DogfoodCommand.cs");
        if (File.Exists(dogfoodCommand) &&
            !File.ReadAllText(dogfoodCommand).Contains("campaign", StringComparison.OrdinalIgnoreCase))
        {
            findings.Add(new CampaignFinding(
                "campaign-not-registered",
                "DogfoodCommand does not register the campaign subcommand.",
                "application/src/Ashlar.CLI/Commands/DogfoodCommand.cs"));
        }

        var cliProject = Path.Combine(repoRoot, "application", "src", "Ashlar.CLI", "Ashlar.CLI.csproj");
        if (File.Exists(cliProject) &&
            !File.ReadAllText(cliProject).Contains("<PackAsTool>true</PackAsTool>", StringComparison.Ordinal))
        {
            findings.Add(new CampaignFinding(
                "cli-not-packable-tool",
                "Ashlar.CLI must remain PackAsTool so developers can install `ashlar`.",
                "application/src/Ashlar.CLI/Ashlar.CLI.csproj"));
        }

        var brickTemplate = Path.Combine(repoRoot, "samples", "templates", "brick");
        if (!Directory.Exists(brickTemplate))
        {
            findings.Add(new CampaignFinding(
                "missing-brick-template",
                "samples/templates/brick is missing; `ashlar new brick` has nothing to scaffold.",
                "samples/templates/brick"));
        }

        var consumer = Path.Combine(repoRoot, "consumer-template");
        if (!Directory.Exists(consumer))
        {
            findings.Add(new CampaignFinding(
                "missing-consumer-template",
                "consumer-template is missing; the package-only developer path has no starter.",
                "consumer-template"));
        }

        if (context.Full && !context.SkipProcessLanes && _invoker is not null)
        {
            var result = await _invoker.RunAsync(
                    "dotnet",
                    new[]
                    {
                        "run",
                        "--project",
                        "application/src/Ashlar.CLI",
                        "--no-build",
                        "--",
                        "dogfood",
                        "--help"
                    },
                    repoRoot,
                    cancellationToken)
                .ConfigureAwait(false);

            var output = result.StdOut + Environment.NewLine + result.StdErr;
            if (result.ExitCode != 0)
            {
                findings.Add(new CampaignFinding(
                    "cli-help-failed",
                    $"ashlar dogfood --help exited {result.ExitCode}."));
            }
            else if (!output.Contains("campaign", StringComparison.OrdinalIgnoreCase))
            {
                findings.Add(new CampaignFinding(
                    "cli-help-missing-campaign",
                    "ashlar dogfood --help does not list the campaign subcommand."));
            }
        }

        var blockers = findings.Count(f => string.Equals(f.Severity, "error", StringComparison.OrdinalIgnoreCase));
        var verdict = blockers == 0 ? CampaignVerdictKind.Pass : CampaignVerdictKind.Fail;
        var summary = blockers == 0
            ? "Developer-tool surface is intact (CLI, scaffold, authoring docs, campaign agent set)."
            : $"{blockers} developer-tool surface blocker(s).";

        return new CampaignAgentReport(
            context.AgentId,
            context.Role,
            Lane,
            verdict,
            summary,
            findings,
            started,
            DateTimeOffset.UtcNow,
            new Dictionary<string, string>
            {
                ["finding_count"] = findings.Count.ToString(),
                ["blocker_count"] = blockers.ToString()
            });
    }

    private static void RequireFile(string repoRoot, string relative, string code, List<CampaignFinding> findings)
    {
        var full = Path.Combine(repoRoot, relative.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(full))
        {
            findings.Add(new CampaignFinding(code, $"Required developer-tool file is missing: {relative}", relative));
        }
    }

    private static CampaignAgentReport Error(CampaignRunContext context, DateTimeOffset started, string message, string code)
    {
        return new CampaignAgentReport(
            context.AgentId,
            context.Role,
            CampaignLane.DevTool,
            CampaignVerdictKind.Error,
            message,
            new[] { new CampaignFinding(code, message) },
            started,
            DateTimeOffset.UtcNow);
    }
}
