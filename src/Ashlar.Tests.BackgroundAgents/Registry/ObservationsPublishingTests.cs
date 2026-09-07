using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Ashlar.BackgroundAgents.Configuration;
using Ashlar.BackgroundAgents.DataSensitivity;
using Ashlar.BackgroundAgents.Extending;
using Ashlar.BackgroundAgents.Observations;
using Ashlar.BackgroundAgents.Optimization;
using Ashlar.BackgroundAgents.Registry;
using Ashlar.BackgroundAgents.Scheduling;
using Ashlar.BackgroundAgents.Testing;
using Ashlar.Orchestration.Agents;
using Xunit;
using Ashlar.Tests.BackgroundAgents.Registry;
using Ashlar.Tests.BackgroundAgents.TestHelpers;

namespace Ashlar.Tests.BackgroundAgents.Registry;

/// <summary>
/// End-to-end tests that the registry actually publishes observations to the
/// configured store when the optimizer / tester / extender code paths complete.
/// These guard against silent regressions where the publish call is removed
/// (the cycle still succeeds, but downstream agents go blind).
/// </summary>
public sealed class ObservationsPublishingTests
{
    [Fact]
    public async Task Tester_role_publishes_test_observation_with_failed_count()
    {
        var store = new InMemoryObservationStore();
        var registry = BuildRegistry(
            testRunRunner: new FakeTestRunner(success: false, total: 12, failed: 3, summary: "3 failures"),
            observations: store);

        var config = new BackgroundAgentConfig
        {
            Id = "tester-1",
            Role = "tester",
            Enabled = true,
            Parameters = new Dictionary<string, object> { ["Filter"] = "PathAllowlist" }
        };
        var agent = new GenericAgent(BuildSpec(config), NullLogger<GenericAgent>.Instance);
        await registry.RegisterAuthoredAsync(agent, config);
        await registry.ExecuteOnceAsync(config.Id);

        store.All.Should().ContainSingle();
        var obs = store.All[0];
        obs.kind.Should().Be(ObservationKind.Test);
        obs.severity.Should().Be(ObservationSeverity.Error, "failed > 0 must escalate even when Success is true");
        obs.source.Should().Be("tester-1");
        obs.facts!["filter"].Should().Be("PathAllowlist");
        obs.facts!["failed"].Should().Be("3");
        obs.facts!["total"].Should().Be("12");
    }

    [Fact]
    public async Task Tester_role_emits_info_severity_when_all_pass()
    {
        var store = new InMemoryObservationStore();
        var registry = BuildRegistry(
            testRunRunner: new FakeTestRunner(success: true, total: 5, failed: 0, summary: "all green"),
            observations: store);

        var config = new BackgroundAgentConfig
        {
            Id = "tester-2",
            Role = "tester",
            Enabled = true
        };
        await registry.RegisterAuthoredAsync(new GenericAgent(BuildSpec(config), NullLogger<GenericAgent>.Instance), config);
        await registry.ExecuteOnceAsync(config.Id);

        store.All.Should().ContainSingle().Which.severity.Should().Be(ObservationSeverity.Info);
    }

    [Fact]
    public async Task Optimizer_role_publishes_analysis_observation_with_violation_count()
    {
        var store = new InMemoryObservationStore();
        var registry = BuildRegistry(
            codeAnalysisRunner: new FakeCodeAnalysisRunner(success: true, summary: "analysed", violations: 7),
            observations: store);

        var config = new BackgroundAgentConfig
        {
            Id = "optimizer-1",
            Role = "optimizer",
            Enabled = true,
            Parameters = new Dictionary<string, object> { ["Path"] = "src/Ashlar.Core" }
        };
        await registry.RegisterAuthoredAsync(new GenericAgent(BuildSpec(config), NullLogger<GenericAgent>.Instance), config);
        await registry.ExecuteOnceAsync(config.Id);

        var obs = store.All.Should().ContainSingle().Subject;
        obs.kind.Should().Be(ObservationKind.Analysis);
        obs.severity.Should().Be(ObservationSeverity.Warn);
        obs.facts!["violations"].Should().Be("7");
        obs.facts!["path"].Should().Be("src/Ashlar.Core");
    }

    [Fact]
    public async Task Registry_does_not_throw_when_observation_store_absent()
    {
        // Regression: the publishing call sites must remain null-conditional. If
        // somebody removes the `?` operator, the registry would throw on the very
        // first scheduled cycle in any deployment that doesn't register the store.
        var registry = BuildRegistry(
            testRunRunner: new FakeTestRunner(true, 1, 0, "ok"),
            observations: null);

        var config = new BackgroundAgentConfig { Id = "tester-3", Role = "tester", Enabled = true };
        await registry.RegisterAuthoredAsync(new GenericAgent(BuildSpec(config), NullLogger<GenericAgent>.Instance), config);

        var act = async () => await registry.ExecuteOnceAsync(config.Id);
        await act.Should().NotThrowAsync();
    }

    private static IBackgroundAgentRegistry BuildRegistry(
        ICodeAnalysisRunner? codeAnalysisRunner = null,
        ITestRunRunner? testRunRunner = null,
        ISelfExtendRunner? selfExtendRunner = null,
        IObservationStore? observations = null)
    {
        var scheduler = new AgentScheduler(new ScheduleExecutor(), NullLogger<AgentScheduler>.Instance);
        return new BackgroundAgentRegistry(
            scheduler,
            NullLogger<BackgroundAgentRegistry>.Instance,
            logStore: null,
            codeAnalysisRunner: codeAnalysisRunner,
            testRunRunner: testRunRunner,
            selfExtendRunner: selfExtendRunner,
            selfImprovementLoop: null,
            modeStore: null,
            approvalGate: null,
            auditLog: null,
            cycleEvents: null,
            observations: observations);
    }

    private static Orchestration.Architect.Models.AgentSpawnSpec BuildSpec(BackgroundAgentConfig c)
    {
        var sensitivity = new DataSensitivityRegistry();
        var builder = new BackgroundAgentSpecBuilder(sensitivity, null);
        return builder.BuildSpec(c).ToOrchestrationSpec();
    }

    private sealed class InMemoryObservationStore : IObservationStore
    {
        public List<RuntimeObservation> All { get; } = new();
        public string Location => "in-memory://test";
        public void Append(RuntimeObservation observation) => All.Add(observation);
        public IEnumerable<RuntimeObservation> ReadSince(
            DateTimeOffset? since = null,
            ObservationKind? kind = null,
            int? limit = null) => All;
    }

    private sealed class FakeTestRunner : ITestRunRunner
    {
        private readonly TestRunResult _result;
        public FakeTestRunner(bool success, int total, int failed, string summary)
            => _result = new TestRunResult(success, total, total - failed, failed, summary);

        public Task<TestRunResult> RunAsync(string? filter, CancellationToken cancellationToken = default)
            => Task.FromResult(_result);
    }

    private sealed class FakeCodeAnalysisRunner : ICodeAnalysisRunner
    {
        private readonly CodeAnalysisRunResult _result;
        public FakeCodeAnalysisRunner(bool success, string summary, int violations)
            => _result = new CodeAnalysisRunResult(success, violations, summary);

        public Task<CodeAnalysisRunResult> RunAsync(string path, CancellationToken cancellationToken = default)
            => Task.FromResult(_result);
    }
}
