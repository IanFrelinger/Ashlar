using Ashlar.BackgroundAgents.Campaign;
using Ashlar.BackgroundAgents.Observations;
using FluentAssertions;
using Xunit;

namespace Ashlar.Tests.BackgroundAgents.Campaign;

/// <summary>Release manager fail-closed aggregation of specialist reports.</summary>
public sealed class ReleaseManagerCoordinatorTests
{
    [Fact]
    public async Task All_specialists_reporting_pass_yields_pass_and_publishes_observations()
    {
        var store = new RecordingObservationStore();
        var coordinator = new ReleaseManagerCoordinator(
            new ICampaignLaneRunner[]
            {
                new StubRunner(CampaignLane.DocsDrift, CampaignVerdictKind.Pass),
                new StubRunner(CampaignLane.Regression, CampaignVerdictKind.Pass),
                new StubRunner(CampaignLane.DevTool, CampaignVerdictKind.Pass)
            },
            store);

        var report = await coordinator.RunAsync(SampleSet(), BaseContext());

        report.Verdict.Should().Be(CampaignVerdictKind.Pass);
        report.MissingReports.Should().BeEmpty();
        report.Reports.Should().HaveCount(3);
        store.All.Should().HaveCount(4, "three specialists plus the release manager");
        store.All.Select(o => o.source).Should().Contain(new[] { "docs-drift", "regression", "dev-tool", "release-manager" });
        store.All.Should().OnlyContain(o => o.facts!["campaign_id"] == "dev-tool-dogfood");
    }

    [Fact]
    public async Task Specialist_fail_blocks_the_campaign()
    {
        var coordinator = new ReleaseManagerCoordinator(new ICampaignLaneRunner[]
        {
            new StubRunner(CampaignLane.DocsDrift, CampaignVerdictKind.Fail, "stale path"),
            new StubRunner(CampaignLane.Regression, CampaignVerdictKind.Pass),
            new StubRunner(CampaignLane.DevTool, CampaignVerdictKind.Pass)
        });

        var report = await coordinator.RunAsync(SampleSet(), BaseContext());

        report.Verdict.Should().Be(CampaignVerdictKind.Fail);
        report.Reports.Single(r => r.Lane == CampaignLane.DocsDrift).Verdict.Should().Be(CampaignVerdictKind.Fail);
    }

    [Fact]
    public async Task Silent_specialist_is_fail_closed_error()
    {
        var coordinator = new ReleaseManagerCoordinator(new ICampaignLaneRunner[]
        {
            new StubRunner(CampaignLane.DocsDrift, CampaignVerdictKind.Pass),
            new StubRunner(CampaignLane.DevTool, CampaignVerdictKind.Pass)
        });

        var report = await coordinator.RunAsync(SampleSet(), BaseContext());

        report.Verdict.Should().Be(CampaignVerdictKind.Error);
        report.MissingReports.Should().Contain("regression");
    }

    [Fact]
    public async Task Crashed_specialist_is_recorded_as_error()
    {
        var coordinator = new ReleaseManagerCoordinator(new ICampaignLaneRunner[]
        {
            new StubRunner(CampaignLane.DocsDrift, CampaignVerdictKind.Pass),
            new CrashingRunner(CampaignLane.Regression),
            new StubRunner(CampaignLane.DevTool, CampaignVerdictKind.Pass)
        });

        var report = await coordinator.RunAsync(SampleSet(), BaseContext());

        report.Verdict.Should().Be(CampaignVerdictKind.Error);
        report.Reports.Single(r => r.Lane == CampaignLane.Regression).Verdict.Should().Be(CampaignVerdictKind.Error);
        report.MissingReports.Should().BeEmpty();
    }

    [Fact]
    public async Task Writes_report_files_when_output_directory_is_set()
    {
        var output = Path.Combine(Path.GetTempPath(), "ashlar-campaign-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        try
        {
            var coordinator = new ReleaseManagerCoordinator(new ICampaignLaneRunner[]
            {
                new StubRunner(CampaignLane.DocsDrift, CampaignVerdictKind.Pass),
                new StubRunner(CampaignLane.Regression, CampaignVerdictKind.Pass),
                new StubRunner(CampaignLane.DevTool, CampaignVerdictKind.Pass)
            });

            var report = await coordinator.RunAsync(SampleSet(), BaseContext() with { OutputDirectory = output });

            report.Verdict.Should().Be(CampaignVerdictKind.Pass);
            File.Exists(Path.Combine(output, "report.json")).Should().BeTrue();
            File.Exists(Path.Combine(output, "report.md")).Should().BeTrue();
            File.ReadAllText(Path.Combine(output, "latest.md")).Should().Contain("Dogfood campaign");
        }
        finally
        {
            try { Directory.Delete(output, recursive: true); } catch { /* best effort */ }
        }
    }

    private static CampaignAgentSet SampleSet() => new(
        "release-manager",
        "Release Manager",
        new[]
        {
            new CampaignSpecialistSpec("docs-drift", "Docs", "docs-auditor", CampaignLane.DocsDrift, "release-manager"),
            new CampaignSpecialistSpec("regression", "Reg", "tester", CampaignLane.Regression, "release-manager"),
            new CampaignSpecialistSpec("dev-tool", "Dev", "dev-tool-auditor", CampaignLane.DevTool, "release-manager")
        });

    private static CampaignRunContext BaseContext() => new(
        RepoRoot: Path.GetTempPath(),
        CampaignId: "dev-tool-dogfood",
        AgentId: "release-manager",
        Role: "release-manager",
        Full: false,
        SkipProcessLanes: true);

    private sealed class StubRunner : ICampaignLaneRunner
    {
        private readonly CampaignVerdictKind _verdict;
        private readonly string _summary;

        public StubRunner(CampaignLane lane, CampaignVerdictKind verdict, string summary = "ok")
        {
            Lane = lane;
            _verdict = verdict;
            _summary = summary;
        }

        public CampaignLane Lane { get; }

        public Task<CampaignAgentReport> RunAsync(CampaignRunContext context, CancellationToken cancellationToken = default)
        {
            var findings = _verdict == CampaignVerdictKind.Pass
                ? Array.Empty<CampaignFinding>()
                : new[] { new CampaignFinding("stub", _summary) };
            return Task.FromResult(new CampaignAgentReport(
                context.AgentId,
                context.Role,
                Lane,
                _verdict,
                _summary,
                findings,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow));
        }
    }

    private sealed class CrashingRunner : ICampaignLaneRunner
    {
        public CrashingRunner(CampaignLane lane) => Lane = lane;
        public CampaignLane Lane { get; }

        public Task<CampaignAgentReport> RunAsync(CampaignRunContext context, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("boom");
    }

    private sealed class RecordingObservationStore : IObservationStore
    {
        public List<RuntimeObservation> All { get; } = new();
        public string Location => "in-memory://campaign";
        public void Append(RuntimeObservation observation) => All.Add(observation);
        public IEnumerable<RuntimeObservation> ReadSince(
            DateTimeOffset? since = null,
            ObservationKind? kind = null,
            int? limit = null) => All;
    }
}
