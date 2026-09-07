using Ashlar.BackgroundAgents.Campaign;
using Ashlar.BackgroundAgents.Observations;
using Ashlar.Core.Application.Paths;
using FluentAssertions;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Dogfood;

/// <summary>
/// North Star gate: the automated dogfood campaign runs against this repository
/// and every specialist reports back to the release manager.
/// </summary>
[Trait("Category", "Dogfood")]
public sealed class DogfoodCampaignTests
{
    [Fact]
    public async Task Campaign_specialists_report_to_the_release_manager_against_this_repo()
    {
        var repoRoot = RepoPathResolver.FindRepoRoot();
        File.Exists(Path.Combine(repoRoot, "Ashlar.sln")).Should().BeTrue();

        var agentSet = await CampaignAgentSetLoader.LoadAsync(
            Path.Combine(repoRoot, CampaignAgentSetLoader.DefaultRelativePath));

        var store = new RecordingObservationStore();
        var coordinator = new ReleaseManagerCoordinator(
            new ICampaignLaneRunner[]
            {
                new DocsDriftLaneRunner(),
                new RegressionLaneRunner(),
                new DevToolLaneRunner()
            },
            store);

        var output = Path.Combine(Path.GetTempPath(), "ashlar-dogfood-campaign-" + Guid.NewGuid().ToString("N"));
        var report = await coordinator.RunAsync(
            agentSet,
            new CampaignRunContext(
                repoRoot,
                "dev-tool-dogfood",
                agentSet.ManagerId,
                "release-manager",
                Full: false,
                SkipProcessLanes: true,
                OutputDirectory: output));

        report.MissingReports.Should().BeEmpty("every specialist must report back");
        report.Reports.Should().HaveCount(3);
        report.Reports.Select(r => r.Lane).Should().BeEquivalentTo(new[]
        {
            CampaignLane.DocsDrift,
            CampaignLane.Regression,
            CampaignLane.DevTool
        });
        store.All.Select(o => o.source).Should().Contain(agentSet.ManagerId);
        foreach (var specialist in agentSet.Specialists)
            store.All.Select(o => o.source).Should().Contain(specialist.AgentId);

        report.Verdict.Should().Be(CampaignVerdictKind.Pass, report.Summary + " " + Describe(report));
        File.Exists(Path.Combine(output, "report.json")).Should().BeTrue();
    }

    private static string Describe(CampaignReport report)
    {
        return string.Join("; ", report.Reports.SelectMany(r => r.Findings.Select(f =>
            $"{r.Lane}:{f.Code}:{f.Path}:{f.Line}:{f.Message}")));
    }

    private sealed class RecordingObservationStore : IObservationStore
    {
        public List<RuntimeObservation> All { get; } = new();
        public string Location => "in-memory://dogfood-campaign";
        public void Append(RuntimeObservation observation) => All.Add(observation);
        public IEnumerable<RuntimeObservation> ReadSince(
            DateTimeOffset? since = null,
            ObservationKind? kind = null,
            int? limit = null) => All;
    }
}
