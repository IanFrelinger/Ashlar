using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Ashlar.Abstractions;
using Ashlar.BackgroundAgents.Configuration;
using Ashlar.Core.Application.Trust.Ports;
using Ashlar.BackgroundAgents.DataSensitivity;
using Ashlar.BackgroundAgents.Extending;
using Ashlar.BackgroundAgents.Observations;
using Ashlar.BackgroundAgents.Optimization;
using Ashlar.BackgroundAgents.Registry;
using Ashlar.BackgroundAgents.Scheduling;
using Ashlar.BackgroundAgents.Telemetry;
using Ashlar.BackgroundAgents.Testing;
using Ashlar.Core.Application.SelfImprovement.Models;
using Ashlar.Core.Application.SelfImprovement.Ports;
using Ashlar.Orchestration.Agents;
using Ashlar.Orchestration.Architect.Models;
using Xunit;
using Ashlar.Tests.BackgroundAgents.Registry;
using Ashlar.Tests.BackgroundAgents.TestHelpers;

namespace Ashlar.Tests.BackgroundAgents.Registry;

/// <summary>Tests for background agent registry gap coverage.</summary>
public sealed class BackgroundAgentRegistryGapCoverageTests
{
    [Fact]
    public async Task RegisterAsync_overwrites_existing_agent_with_same_id()
    {
        var registry = BuildRegistry();
        var config = MonitorConfig("dup-agent");

        await registry.RegisterAuthoredAsync(new GenericAgent(BuildSpec(config), NullLogger<GenericAgent>.Instance), config);
        await registry.RegisterAuthoredAsync(new GenericAgent(BuildSpec(config), NullLogger<GenericAgent>.Instance), config);

        registry.GetAll().Should().ContainSingle(a => a.Config.Id == "dup-agent");
    }

    [Fact]
    public async Task RegisterAsync_rejects_null_agent_or_config()
    {
        var registry = BuildRegistry();
        var config = MonitorConfig("null-test");

        var actAgent = () => registry.RegisterAsync(null!, config);
        var actConfig = () => registry.RegisterAsync(new GenericAgent(BuildSpec(config), NullLogger<GenericAgent>.Instance), null!);

        await actAgent.Should().ThrowAsync<ArgumentNullException>();
        await actConfig.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task StartAsync_when_already_running_is_idempotent()
    {
        var registry = BuildRegistry();
        var config = MonitorConfig("running-agent");
        await registry.RegisterAuthoredAsync(new GenericAgent(BuildSpec(config), NullLogger<GenericAgent>.Instance), config);

        await registry.StartAsync(config.Id);
        await registry.StartAsync(config.Id);

        registry.GetAgent(config.Id)!.State.Should().Be(BackgroundAgentState.Running);
    }

    [Fact]
    public async Task StopAsync_on_idle_agent_is_noop()
    {
        var registry = BuildRegistry();
        var config = MonitorConfig("idle-agent");
        await registry.RegisterAuthoredAsync(new GenericAgent(BuildSpec(config), NullLogger<GenericAgent>.Instance), config);

        await registry.StopAsync(config.Id);

        registry.GetAgent(config.Id)!.State.Should().Be(BackgroundAgentState.Idle);
    }

    [Fact]
    public async Task StartAsync_unknown_agent_throws()
    {
        var registry = BuildRegistry();
        var act = () => registry.StartAsync("missing");
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not found*");
    }

    [Fact]
    public async Task SelfImprover_in_passive_mode_skips_execution()
    {
        var loop = new Mock<ISelfImprovementLoop>();
        var modeStore = new Mock<IAggressivenessModeStore>();
        modeStore.Setup(m => m.GetMode()).Returns(BackgroundAgentAggressivenessMode.Passive);

        var registry = BuildRegistry(selfImprovementLoop: loop.Object, modeStore: modeStore.Object);
        var config = new BackgroundAgentConfig
        {
            Id = "self-improver-passive",
            Role = "self-improver",
            Enabled = true,
        };

        await registry.RegisterAuthoredAsync(new GenericAgent(BuildSpec(config), NullLogger<GenericAgent>.Instance), config);
        await registry.ExecuteOnceAsync(config.Id);

        loop.Verify(l => l.RunOnceAsync(It.IsAny<CancellationToken>()), Times.Never);
        registry.GetAgent(config.Id)!.SuccessCount.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteOnceAsync_unknown_agent_throws()
    {
        var registry = BuildRegistry();
        var act = () => registry.ExecuteOnceAsync("missing");
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not found*");
    }

    [Fact]
    public async Task StopAsync_unknown_agent_throws()
    {
        var registry = BuildRegistry();
        var act = () => registry.StopAsync("missing");
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not found*");
    }

    [Fact]
    public async Task Execution_failure_increments_failure_count_and_records_error()
    {
        var registry = BuildRegistry(testRunRunner: new ThrowingTestRunner());
        var config = new BackgroundAgentConfig { Id = "failing-tester", Role = "tester", Enabled = true };
        await registry.RegisterAuthoredAsync(new GenericAgent(BuildSpec(config), NullLogger<GenericAgent>.Instance), config);

        await registry.ExecuteOnceAsync(config.Id);

        var instance = registry.GetAgent(config.Id)!;
        instance.FailureCount.Should().Be(1);
        instance.LastError.Should().Contain("boom");
    }

    [Fact]
    public async Task Extender_active_mode_publishes_agent_action_observation()
    {
        var store = new InMemoryObservationStore();
        var runner = new ConfigurableSelfExtendRunner(new SelfExtendRunResult(
            Success: true,
            ToolCallsExecuted: 2,
            ToolCallsDenied: 1,
            Summary: "extended",
            Iterations: 3,
            StoppedReason: "empty"));
        var registry = BuildRegistry(selfExtendRunner: runner, observations: store);

        var config = ExtenderConfig("extender-obs", Environment.CurrentDirectory);
        await registry.RegisterAuthoredAsync(new GenericAgent(BuildSpec(config), NullLogger<GenericAgent>.Instance), config);
        await registry.ExecuteOnceAsync(config.Id);

        var obs = store.All.Should().ContainSingle().Subject;
        obs.kind.Should().Be(ObservationKind.AgentAction);
        obs.severity.Should().Be(ObservationSeverity.Info);
        obs.facts!["executed"].Should().Be("2");
        obs.facts!["denied"].Should().Be("1");
        obs.facts!["stopped_reason"].Should().Be("empty");
    }

    [Fact]
    public async Task Extender_failed_run_publishes_warn_severity_observation()
    {
        var store = new InMemoryObservationStore();
        var runner = new ConfigurableSelfExtendRunner(new SelfExtendRunResult(
            Success: false,
            ToolCallsExecuted: 0,
            ToolCallsDenied: 0,
            Summary: "failed cycle"));
        var registry = BuildRegistry(selfExtendRunner: runner, observations: store);

        var config = ExtenderConfig("extender-fail", Environment.CurrentDirectory);
        await registry.RegisterAuthoredAsync(new GenericAgent(BuildSpec(config), NullLogger<GenericAgent>.Instance), config);
        await registry.ExecuteOnceAsync(config.Id);

        store.All.Should().ContainSingle().Which.severity.Should().Be(ObservationSeverity.Warn);
    }

    [Fact]
    public async Task ExecuteOnceAsync_appends_cycle_event_telemetry()
    {
        var path = Path.Combine(Path.GetTempPath(), "ashlar-cycles-" + Guid.NewGuid().ToString("N") + ".jsonl");
        var cycleEvents = new CycleEventStore(path);
        var runner = new ConfigurableSelfExtendRunner(new SelfExtendRunResult(
            Success: true,
            ToolCallsExecuted: 4,
            ToolCallsDenied: 0,
            Summary: "ok",
            Iterations: 2,
            StoppedReason: "empty"));
        var registry = BuildRegistry(selfExtendRunner: runner, cycleEvents: cycleEvents);

        var config = ExtenderConfig("extender-telem", Environment.CurrentDirectory);
        config.ModelProvider = "ollama";
        config.ModelName = "llama3";
        await registry.RegisterAuthoredAsync(new GenericAgent(BuildSpec(config), NullLogger<GenericAgent>.Instance), config);
        await registry.ExecuteOnceAsync(config.Id);

        var evt = cycleEvents.Read().Should().ContainSingle().Subject;
        evt.agent.Should().Be("extender-telem");
        evt.tools_executed.Should().Be(4);
        evt.iterations.Should().Be(2);
        evt.provider.Should().Be("ollama");
        evt.model.Should().Be("llama3");
        evt.stopped_reason.Should().Be("empty");
        evt.success.Should().BeTrue();

        try { File.Delete(path); } catch { /* best effort */ }
    }

    [Fact]
    public async Task SelfImprover_semi_active_without_approval_skips_loop()
    {
        var loop = new Mock<ISelfImprovementLoop>();
        var modeStore = new Mock<IAggressivenessModeStore>();
        modeStore.Setup(m => m.GetMode()).Returns(BackgroundAgentAggressivenessMode.SemiActive);

        var registry = BuildRegistry(selfImprovementLoop: loop.Object, modeStore: modeStore.Object);
        var config = new BackgroundAgentConfig { Id = "self-improver-semi", Role = "self-improver", Enabled = true };
        await registry.RegisterAuthoredAsync(new GenericAgent(BuildSpec(config), NullLogger<GenericAgent>.Instance), config);
        await registry.ExecuteOnceAsync(config.Id);

        loop.Verify(l => l.RunOnceAsync(It.IsAny<CancellationToken>()), Times.Never);
        registry.GetAgent(config.Id)!.SuccessCount.Should().Be(1);
    }

    [Fact]
    public async Task SelfImprover_with_approval_runs_loop_and_logs_report()
    {
        var loop = new Mock<ISelfImprovementLoop>();
        loop.Setup(l => l.GetLastRunReportAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SelfImprovementReport(
                DateTimeOffset.UtcNow,
                FailuresProcessed: 3,
                FixesGenerated: 2,
                FixesValidated: 2,
                FixesPromoted: 1,
                FixesRejected: 2,
                PromotedAdaptationIds: Array.Empty<string>(),
                RejectedReasons: Array.Empty<string>()));
        var modeStore = new Mock<IAggressivenessModeStore>();
        modeStore.Setup(m => m.GetMode()).Returns(BackgroundAgentAggressivenessMode.SemiActive);

        var registry = BuildRegistry(
            selfImprovementLoop: loop.Object,
            modeStore: modeStore.Object,
            approvalGate: new AlwaysApprovalGate());
        var config = new BackgroundAgentConfig { Id = "self-improver-run", Role = "self-improver", Enabled = true };
        await registry.RegisterAuthoredAsync(new GenericAgent(BuildSpec(config), NullLogger<GenericAgent>.Instance), config);
        await registry.ExecuteOnceAsync(config.Id);

        loop.Verify(l => l.RunOnceAsync(It.IsAny<CancellationToken>()), Times.Once);
        registry.GetAgent(config.Id)!.SuccessCount.Should().Be(1);
    }

    [Fact]
    public async Task SelfImprover_with_approval_and_null_report_logs_fallback_summary()
    {
        var loop = new Mock<ISelfImprovementLoop>();
        loop.Setup(l => l.GetLastRunReportAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((SelfImprovementReport?)null);
        var modeStore = new Mock<IAggressivenessModeStore>();
        modeStore.Setup(m => m.GetMode()).Returns(BackgroundAgentAggressivenessMode.SemiActive);

        var registry = BuildRegistry(
            selfImprovementLoop: loop.Object,
            modeStore: modeStore.Object,
            approvalGate: new AlwaysApprovalGate());
        var config = new BackgroundAgentConfig { Id = "self-improver-null-report", Role = "self-improver", Enabled = true };
        await registry.RegisterAuthoredAsync(new GenericAgent(BuildSpec(config), NullLogger<GenericAgent>.Instance), config);
        await registry.ExecuteOnceAsync(config.Id);

        loop.Verify(l => l.RunOnceAsync(It.IsAny<CancellationToken>()), Times.Once);
        registry.GetAgent(config.Id)!.SuccessCount.Should().Be(1);
    }

    [Fact]
    public async Task Optimizer_without_path_falls_through_to_default_success()
    {
        var registry = BuildRegistry(codeAnalysisRunner: new FakeCodeAnalysisRunner(true, "unused", 0));
        var config = new BackgroundAgentConfig { Id = "optimizer-default", Role = "optimizer", Enabled = true };
        await registry.RegisterAuthoredAsync(new GenericAgent(BuildSpec(config), NullLogger<GenericAgent>.Instance), config);
        await registry.ExecuteOnceAsync(config.Id);

        registry.GetAgent(config.Id)!.SuccessCount.Should().Be(1);
    }

    [Fact]
    public async Task StopAllAsync_waits_for_in_flight_execute_once_cycles_to_drain()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new GateSelfExtendRunner(gate.Task);
        var registry = BuildRegistry(
            selfExtendRunner: runner,
            shutdownGracePeriod: TimeSpan.FromSeconds(2));

        var config = ExtenderConfig("drain-agent", Environment.CurrentDirectory);
        await registry.RegisterAuthoredAsync(new GenericAgent(BuildSpec(config), NullLogger<GenericAgent>.Instance), config);

        var cycle = registry.ExecuteOnceAsync(config.Id);
        await Task.Delay(50);
        var stop = registry.StopAllAsync();

        gate.SetResult();
        await stop;
        await cycle;

        registry.GetAgent(config.Id)!.SuccessCount.Should().Be(1);
    }

    [Fact]
    public async Task StopAllAsync_abandons_in_flight_cycles_after_grace_period()
    {
        var runner = new DelaySelfExtendRunner(TimeSpan.FromSeconds(2));
        var registry = BuildRegistry(
            selfExtendRunner: runner,
            shutdownGracePeriod: TimeSpan.FromMilliseconds(50));

        var config = ExtenderConfig("slow-agent", Environment.CurrentDirectory);
        await registry.RegisterAuthoredAsync(new GenericAgent(BuildSpec(config), NullLogger<GenericAgent>.Instance), config);

        var cycle = registry.ExecuteOnceAsync(config.Id);
        await Task.Delay(30);
        await registry.StopAllAsync();

        await cycle;
        registry.GetAgent(config.Id)!.SuccessCount.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteOnceAsync_honors_cancellation_token()
    {
        var runner = new DelaySelfExtendRunner(TimeSpan.FromSeconds(5));
        var registry = BuildRegistry(selfExtendRunner: runner);
        var config = ExtenderConfig("cancel-agent", Environment.CurrentDirectory);
        await registry.RegisterAuthoredAsync(new GenericAgent(BuildSpec(config), NullLogger<GenericAgent>.Instance), config);

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        var act = () => registry.ExecuteOnceAsync(config.Id, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Extender_passive_mode_skips_self_extend_runner()
    {
        var runner = new ConfigurableSelfExtendRunner(new SelfExtendRunResult(true, 1, 0, "ran"));
        var modeStore = new Mock<IAggressivenessModeStore>();
        modeStore.Setup(m => m.GetMode()).Returns(BackgroundAgentAggressivenessMode.Passive);

        var registry = BuildRegistry(selfExtendRunner: runner, modeStore: modeStore.Object);
        var config = ExtenderConfig("extender-passive", Environment.CurrentDirectory);
        await registry.RegisterAuthoredAsync(new GenericAgent(BuildSpec(config), NullLogger<GenericAgent>.Instance), config);
        await registry.ExecuteOnceAsync(config.Id);

        registry.GetAgent(config.Id)!.SuccessCount.Should().Be(1);
    }

    [Fact]
    public async Task Extender_accepts_Path_parameter_as_repo_root()
    {
        var runner = new RecordingSelfExtendRunner();
        var registry = BuildRegistry(selfExtendRunner: runner);

        var config = new BackgroundAgentConfig
        {
            Id = "extender-path",
            Role = "extender",
            Enabled = true,
            Parameters = new Dictionary<string, object> { ["Path"] = Environment.CurrentDirectory },
        };
        await registry.RegisterAuthoredAsync(new GenericAgent(BuildSpec(config), NullLogger<GenericAgent>.Instance), config);
        await registry.ExecuteOnceAsync(config.Id);

        runner.LastRepoRoot.Should().Be(Environment.CurrentDirectory);
    }

    [Fact]
    public async Task Extender_without_repo_root_skips_self_extend_runner()
    {
        var runner = new RecordingSelfExtendRunner();
        var registry = BuildRegistry(selfExtendRunner: runner);
        var config = new BackgroundAgentConfig { Id = "extender-no-path", Role = "extender", Enabled = true };
        await registry.RegisterAuthoredAsync(new GenericAgent(BuildSpec(config), NullLogger<GenericAgent>.Instance), config);
        await registry.ExecuteOnceAsync(config.Id);

        runner.LastRepoRoot.Should().BeNull();
        registry.GetAgent(config.Id)!.SuccessCount.Should().Be(1);
    }

    [Fact]
    public async Task StartAllAsync_starts_enabled_agents()
    {
        var registry = BuildRegistry();
        var config = MonitorConfig("start-all-agent");
        await registry.RegisterAuthoredAsync(new GenericAgent(BuildSpec(config), NullLogger<GenericAgent>.Instance), config);

        await registry.StartAllAsync();

        registry.GetAgent(config.Id)!.State.Should().Be(BackgroundAgentState.Running);
        await registry.StopAllAsync();
    }

    [Fact]
    public async Task Failed_execution_records_cycle_event_with_error()
    {
        var path = Path.Combine(Path.GetTempPath(), "ashlar-cycles-fail-" + Guid.NewGuid().ToString("N") + ".jsonl");
        var cycleEvents = new CycleEventStore(path);
        var registry = BuildRegistry(testRunRunner: new ThrowingTestRunner(), cycleEvents: cycleEvents);

        var config = new BackgroundAgentConfig { Id = "fail-telem", Role = "tester", Enabled = true };
        await registry.RegisterAuthoredAsync(new GenericAgent(BuildSpec(config), NullLogger<GenericAgent>.Instance), config);
        await registry.ExecuteOnceAsync(config.Id);

        var evt = cycleEvents.Read().Should().ContainSingle().Subject;
        evt.success.Should().BeFalse();
        evt.error.Should().Contain("boom");
        evt.stopped_reason.Should().Be("error");

        try { File.Delete(path); } catch { /* best effort */ }
    }

    private static BackgroundAgentRegistry BuildRegistry(
        ISelfImprovementLoop? selfImprovementLoop = null,
        IAggressivenessModeStore? modeStore = null,
        ISelfExtendRunner? selfExtendRunner = null,
        IObservationStore? observations = null,
        CycleEventStore? cycleEvents = null,
        ITestRunRunner? testRunRunner = null,
        ICodeAnalysisRunner? codeAnalysisRunner = null,
        IApprovalGate? approvalGate = null,
        TimeSpan? shutdownGracePeriod = null)
    {
        if (selfExtendRunner is not null && modeStore is null)
            modeStore = new InMemoryAggressivenessModeStore();

        var scheduler = new AgentScheduler(new ScheduleExecutor(), NullLogger<AgentScheduler>.Instance);
        return new BackgroundAgentRegistry(
            scheduler,
            NullLogger<BackgroundAgentRegistry>.Instance,
            codeAnalysisRunner: codeAnalysisRunner,
            testRunRunner: testRunRunner,
            selfExtendRunner: selfExtendRunner,
            selfImprovementLoop: selfImprovementLoop,
            modeStore: modeStore,
            approvalGate: approvalGate,
            sensitivityRegistry: new DataSensitivityRegistry(),
            cycleEvents: cycleEvents,
            observations: observations,
            shutdownGracePeriod: shutdownGracePeriod);
    }

    /// <summary>Extender config.</summary>
    /// <param name="id">Id.</param>
    /// <param name="repoRoot">Repo root.</param>
    private static BackgroundAgentConfig ExtenderConfig(string id, string repoRoot) => new()
    {
        Id = id,
        Role = "extender",
        Enabled = true,
        MaxDataSensitivity = "Public",
        Commands = ["extend"],
        Parameters = new Dictionary<string, object> { ["RepoRoot"] = repoRoot },
        Schedule = new BackgroundAgentSchedule { Type = ScheduleType.Interval, Interval = TimeSpan.FromMinutes(1) },
    };

    /// <summary>Monitor config.</summary>
    /// <param name="id">Id.</param>
    private static BackgroundAgentConfig MonitorConfig(string id) => new()
    {
        Id = id,
        Name = id,
        Role = "monitor",
        Enabled = true,
        MaxDataSensitivity = "Public",
        Commands = ["ping"],
        Schedule = new BackgroundAgentSchedule { Type = ScheduleType.Interval, Interval = TimeSpan.FromMinutes(1) },
    };

    /// <summary>Creates spec.</summary>
    /// <param name="config">Config.</param>
    private static AgentSpawnSpec BuildSpec(BackgroundAgentConfig config) =>
        new BackgroundAgentSpecBuilder(new DataSensitivityRegistry(), null).BuildSpec(config).ToOrchestrationSpec();

    /// <summary>In memory observation store.</summary>
    private sealed class InMemoryObservationStore : IObservationStore
    {
        /// <summary>All.</summary>
        public List<RuntimeObservation> All { get; } = new();
        public string Location => "in-memory://test";
        /// <summary>Append.</summary>
        /// <param name="observation">Observation.</param>
        public void Append(RuntimeObservation observation) => All.Add(observation);
        public IEnumerable<RuntimeObservation> ReadSince(
            DateTimeOffset? since = null,
            ObservationKind? kind = null,
            int? limit = null) => All;
    }

    /// <summary>Configurable self extend runner.</summary>
    private sealed class ConfigurableSelfExtendRunner : ISelfExtendRunner
    {
        private readonly SelfExtendRunResult _result;
        /// <summary>Configurable self extend runner.</summary>
        /// <param name="result">Result.</param>
        public ConfigurableSelfExtendRunner(SelfExtendRunResult result) => _result = result;

        /// <summary>Run async.</summary>
        /// <param name="repoRoot">Repo root.</param>
        /// <param name="default">Default.</param>
        public Task<SelfExtendRunResult> RunAsync(string repoRoot, CancellationToken cancellationToken = default) =>
            Task.FromResult(_result);

        public Task<SelfExtendRunResult> RunAsync(
            string repoRoot,
            string? objective,
            string? agentName,
            string? modelProvider,
            string? modelName,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_result);
    }

    /// <summary>Throwing test runner.</summary>
    private sealed class ThrowingTestRunner : ITestRunRunner
    {
        /// <summary>Run async.</summary>
        /// <param name="filter">Filter.</param>
        /// <param name="default">Default.</param>
        public Task<TestRunResult> RunAsync(string? filter, CancellationToken cancellationToken = default) =>
            /// <summary>Invalid operation exception.</summary>
            throw new InvalidOperationException("boom");
    }

    /// <summary>Fake code analysis runner.</summary>
    private sealed class FakeCodeAnalysisRunner : ICodeAnalysisRunner
    {
        private readonly CodeAnalysisRunResult _result;
        /// <summary>Fake code analysis runner.</summary>
        /// <param name="success">Success.</param>
        /// <param name="summary">Summary.</param>
        /// <param name="violations">Violations.</param>
        public FakeCodeAnalysisRunner(bool success, string summary, int violations) =>
            _result = new CodeAnalysisRunResult(success, violations, summary);

        /// <summary>Run async.</summary>
        /// <param name="path">Path.</param>
        /// <param name="default">Default.</param>
        public Task<CodeAnalysisRunResult> RunAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult(_result);
    }

    /// <summary>Recording self extend runner.</summary>
    private sealed class RecordingSelfExtendRunner : ISelfExtendRunner
    {
        /// <summary>Last repo root.</summary>
        public string? LastRepoRoot { get; private set; }

        public Task<SelfExtendRunResult> RunAsync(string repoRoot, CancellationToken cancellationToken = default)
        {
            LastRepoRoot = repoRoot;
            return Task.FromResult(new SelfExtendRunResult(true, 0, 0, "path ok"));
        }

        public Task<SelfExtendRunResult> RunAsync(
            string repoRoot,
            string? objective,
            string? agentName,
            string? modelProvider,
            string? modelName,
            CancellationToken cancellationToken = default) =>
            /// <summary>Run async.</summary>
            RunAsync(repoRoot, cancellationToken);
    }

    /// <summary>Gate self extend runner.</summary>
    private sealed class GateSelfExtendRunner : ISelfExtendRunner
    {
        private readonly Task _gate;
        /// <summary>Gate self extend runner.</summary>
        /// <param name="gate">Gate.</param>
        public GateSelfExtendRunner(Task gate) => _gate = gate;

        public async Task<SelfExtendRunResult> RunAsync(string repoRoot, CancellationToken cancellationToken = default)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            /// <summary>Self extend run result.</summary>
            return new SelfExtendRunResult(true, 0, 0, "gated");
        }

        public Task<SelfExtendRunResult> RunAsync(
            string repoRoot,
            string? objective,
            string? agentName,
            string? modelProvider,
            string? modelName,
            CancellationToken cancellationToken = default) =>
            /// <summary>Run async.</summary>
            RunAsync(repoRoot, cancellationToken);
    }

    /// <summary>Delay self extend runner.</summary>
    private sealed class DelaySelfExtendRunner : ISelfExtendRunner
    {
        private readonly TimeSpan _delay;
        /// <summary>Delay self extend runner.</summary>
        /// <param name="delay">Delay.</param>
        public DelaySelfExtendRunner(TimeSpan delay) => _delay = delay;

        public async Task<SelfExtendRunResult> RunAsync(string repoRoot, CancellationToken cancellationToken = default)
        {
            await Task.Delay(_delay, cancellationToken).ConfigureAwait(false);
            /// <summary>Self extend run result.</summary>
            return new SelfExtendRunResult(true, 0, 0, "delayed");
        }

        public Task<SelfExtendRunResult> RunAsync(
            string repoRoot,
            string? objective,
            string? agentName,
            string? modelProvider,
            string? modelName,
            CancellationToken cancellationToken = default) =>
            /// <summary>Run async.</summary>
            RunAsync(repoRoot, cancellationToken);
    }
}
