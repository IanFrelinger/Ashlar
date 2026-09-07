using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Ashlar.Abstractions;
using Ashlar.BackgroundAgents.Configuration;
using Ashlar.BackgroundAgents.DataSensitivity;
using Ashlar.BackgroundAgents.Extending;
using Ashlar.BackgroundAgents.Logging;
using Ashlar.BackgroundAgents.Observations;
using Ashlar.BackgroundAgents.Registry;
using Ashlar.BackgroundAgents.Scheduling;
using Ashlar.Orchestration.Agents;
using Xunit;

namespace Ashlar.Tests.BackgroundAgents.Registry;

/// <summary>
/// The defect: an extender agent in a host that never registered an <see cref="ISelfExtendRunner"/>
/// fell through every role branch to the generic "Default: simple success" tail and logged
/// "Execution completed successfully" — every cycle, having done nothing. Two things followed from
/// that silence. An operator watching logs saw a healthy agent. And because the invariant-D
/// <c>ExtensionCeiling</c> is enforced INSIDE the extender branch, the headline blast-radius
/// control could never fire: the code that consults it was skipped by the same condition that
/// skipped the work.
///
/// <para>Neither AddAshlar() nor AddBackgroundAgents() registers a runner, and the adapter that
/// implements one is not shipped as a package — so a package-composed host reaches this state by
/// default. The lane is now loud about not running.</para>
/// </summary>
public sealed class UnwiredLaneReportsFailureTests
{
    [Fact]
    public async Task An_extender_with_no_runner_reports_failure_not_success()
    {
        var log = new ListLogStore();
        var observations = new ListObservationStore();
        var registry = BuildRegistry(selfExtendRunner: null, log, observations);
        var config = ExtenderConfig("extender-1", repoRoot: "/some/repo");

        await registry.RegisterAuthoredAsync(Agent(config), config);
        await registry.ExecuteOnceAsync(config.Id);

        var instance = registry.GetAgent(config.Id)!;
        instance.SuccessCount.Should().Be(0, "nothing ran, so nothing succeeded");
        instance.FailureCount.Should().Be(1);
        log.Entries.Should().NotContain(e => e.Message.Contains("Execution completed successfully"));
        log.Entries.Should().Contain(e => e.Level == "Error" && e.Message.Contains("did NOT run"));
    }

    [Fact]
    public async Task The_refusal_names_the_missing_registration_and_the_ceiling_it_costs()
    {
        var log = new ListLogStore();
        var registry = BuildRegistry(selfExtendRunner: null, log, new ListObservationStore());
        var config = ExtenderConfig("extender-2", repoRoot: "/some/repo");

        await registry.RegisterAuthoredAsync(Agent(config), config);
        await registry.ExecuteOnceAsync(config.Id);

        var message = log.Entries.Single(e => e.Level == "Error").Message;
        message.Should().Contain("ISelfExtendRunner", "the operator is told exactly what is missing");
        message.Should().Contain("ExtensionCeiling", "and what its absence cost them");
        message.Should().Contain("AddBackgroundAgents", "and where to register it");
    }

    [Fact]
    public async Task The_failure_is_published_as_an_observation_the_planner_can_see()
    {
        var observations = new ListObservationStore();
        var registry = BuildRegistry(selfExtendRunner: null, new ListLogStore(), observations);
        var config = ExtenderConfig("extender-3", repoRoot: "/some/repo");

        await registry.RegisterAuthoredAsync(Agent(config), config);
        await registry.ExecuteOnceAsync(config.Id);

        var observation = observations.All.Should().ContainSingle().Subject;
        observation.severity.Should().Be(ObservationSeverity.Error);
        observation.facts!["stopped_reason"].Should().Be("lane_preconditions_unmet");
        observation.facts!["executed"].Should().Be("0");
    }

    [Fact]
    public async Task An_extender_with_a_runner_but_no_repo_root_is_also_loud()
    {
        // The other clause of the same guard. "extender did not run" is useless; "the agent
        // declares no RepoRoot" is actionable.
        var log = new ListLogStore();
        var registry = BuildRegistry(new NoOpSelfExtendRunner(), log, new ListObservationStore());
        var config = ExtenderConfig("extender-4", repoRoot: null);

        await registry.RegisterAuthoredAsync(Agent(config), config);
        await registry.ExecuteOnceAsync(config.Id);

        log.Entries.Single(e => e.Level == "Error").Message.Should().Contain("RepoRoot");
    }

    [Fact]
    public async Task A_fully_wired_extender_still_succeeds()
    {
        // The lane must WORK, not merely refuse.
        var log = new ListLogStore();
        var runner = new NoOpSelfExtendRunner();
        var registry = BuildRegistry(runner, log, new ListObservationStore());
        var config = ExtenderConfig("extender-5", repoRoot: "/some/repo");

        await registry.RegisterAuthoredAsync(Agent(config), config);
        await registry.ExecuteOnceAsync(config.Id);

        runner.CallCount.Should().Be(1);
        registry.GetAgent(config.Id)!.SuccessCount.Should().Be(1);
        log.Entries.Should().Contain(e => e.Message.Contains("Execution completed successfully"));
    }

    [Fact]
    public async Task A_role_with_no_dedicated_lane_is_untouched()
    {
        // Observational roles legitimately reach the generic success branch — the fix must not
        // turn every unremarkable agent red.
        var log = new ListLogStore();
        var registry = BuildRegistry(selfExtendRunner: null, log, new ListObservationStore());
        var config = new BackgroundAgentConfig { Id = "watcher-1", Role = "observer", Enabled = true };

        await registry.RegisterAuthoredAsync(Agent(config), config);
        await registry.ExecuteOnceAsync(config.Id);

        registry.GetAgent(config.Id)!.SuccessCount.Should().Be(1);
        log.Entries.Should().Contain(e => e.Message.Contains("Execution completed successfully"));
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static BackgroundAgentConfig ExtenderConfig(string id, string? repoRoot) => new()
    {
        Id = id,
        Role = "extender",
        Enabled = true,
        Parameters = repoRoot is null
            ? new Dictionary<string, object>()
            : new Dictionary<string, object> { ["RepoRoot"] = repoRoot },
    };

    private static GenericAgent Agent(BackgroundAgentConfig config)
    {
        var builder = new BackgroundAgentSpecBuilder(new DataSensitivityRegistry(), null);
        return new GenericAgent(builder.BuildSpec(config), NullLogger<GenericAgent>.Instance);
    }

    private static BackgroundAgentRegistry BuildRegistry(
        ISelfExtendRunner? selfExtendRunner,
        IBackgroundAgentLogStore logStore,
        IObservationStore observations)
    {
        var modeStore = new InMemoryAggressivenessModeStore();
        modeStore.SetMode(BackgroundAgentAggressivenessMode.Active);
        var scheduler = new AgentScheduler(new ScheduleExecutor(), NullLogger<AgentScheduler>.Instance);
        return new BackgroundAgentRegistry(
            scheduler,
            NullLogger<BackgroundAgentRegistry>.Instance,
            logStore: logStore,
            codeAnalysisRunner: null,
            testRunRunner: null,
            selfExtendRunner: selfExtendRunner,
            selfImprovementLoop: null,
            modeStore: modeStore,
            sensitivityRegistry: new DataSensitivityRegistry(),
            observations: observations);
    }

    private sealed class NoOpSelfExtendRunner : ISelfExtendRunner
    {
        public int CallCount { get; private set; }

        public Task<SelfExtendRunResult> RunAsync(string repoRoot, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(new SelfExtendRunResult(true, 1, 0, "did something"));
        }

        public Task<SelfExtendRunResult> RunAsync(
            string repoRoot, string? objective, string? agentName, string? modelProvider, string? modelName,
            CancellationToken cancellationToken = default) => RunAsync(repoRoot, cancellationToken);

        public Task<SelfExtendRunResult> RunAsync(
            string repoRoot, string? objective, string? agentName, string? modelProvider, string? modelName,
            string? agentId, CancellationToken cancellationToken = default) => RunAsync(repoRoot, cancellationToken);
    }

    private sealed record LogEntry(string Level, string Message);

    private sealed class ListLogStore : IBackgroundAgentLogStore
    {
        public List<LogEntry> Entries { get; } = new();

        public void Append(string agentId, string level, string message) =>
            Entries.Add(new LogEntry(level, message));

        public IReadOnlyList<AgentLogEntry> GetRecent(
            string agentId, int maxCount = 100, string? levelFilter = null, DateTimeOffset? since = null) => [];
    }

    private sealed class ListObservationStore : IObservationStore
    {
        public List<RuntimeObservation> All { get; } = new();

        public string Location => "in-memory://unwired-lane";

        public void Append(RuntimeObservation observation) => All.Add(observation);

        public IEnumerable<RuntimeObservation> ReadSince(
            DateTimeOffset? since = null, ObservationKind? kind = null, int? limit = null) => All;
    }
}
