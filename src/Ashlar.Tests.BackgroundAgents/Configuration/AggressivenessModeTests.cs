using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ashlar.Abstractions;
using Ashlar.BackgroundAgents.Configuration;
using Ashlar.BackgroundAgents.DataSensitivity;
using Ashlar.BackgroundAgents.Extending;
using Ashlar.BackgroundAgents.Logging;
using Ashlar.BackgroundAgents.Registry;
using Ashlar.BackgroundAgents.Scheduling;
using Ashlar.Core.Application.Trust.Models;
using Ashlar.Core.Application.Trust.Ports;
using Ashlar.Orchestration.Agents;
using Xunit;
using Ashlar.Tests.BackgroundAgents.Registry;
using Ashlar.Tests.BackgroundAgents.TestHelpers;

namespace Ashlar.Tests.BackgroundAgents.Configuration;

/// <summary>
/// P3.1: Verify all four background agent aggressiveness modes.
/// </summary>
public sealed class AggressivenessModeTests
{
    [Fact]
    public async Task BackgroundAgent_PassiveMode_ExtenderSkipped_TakesNoAction()
    {
        var modeStore = new InMemoryAggressivenessModeStore();
        modeStore.SetMode(BackgroundAgentAggressivenessMode.Passive);

        var runCount = 0;
        var mockExtend = new MockSelfExtendRunner(() => runCount++);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BackgroundAgents:Agents:0:Id"] = "extender-passive",
                ["BackgroundAgents:Agents:0:Name"] = "Extender",
                ["BackgroundAgents:Agents:0:Role"] = "extender",
                ["BackgroundAgents:Agents:0:Enabled"] = "true",
                ["BackgroundAgents:Agents:0:MaxDataSensitivity"] = "Public",
                ["BackgroundAgents:Agents:0:Commands:0"] = "extend",
                ["BackgroundAgents:Agents:0:Parameters:RepoRoot"] = Environment.CurrentDirectory,
                ["BackgroundAgents:Agents:0:Schedule:Type"] = "Interval",
                ["BackgroundAgents:Agents:0:Schedule:Interval"] = "00:01:00",
            })
            .Build();

        var services = new ServiceCollection()
            .AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning))
            .BuildServiceProvider();

        var sensitivityRegistry = new DataSensitivityRegistry();
        var configLoader = new BackgroundAgentConfigLoader(config, sensitivityRegistry, null);
        var specBuilder = new BackgroundAgentSpecBuilder(sensitivityRegistry, null);
        var logStore = new InMemoryAgentLogStore();
        var scheduleExecutor = new ScheduleExecutor();
        var scheduler = new AgentScheduler(scheduleExecutor, null);
        var registry = new BackgroundAgentRegistry(scheduler, null, logStore, null, null, mockExtend, selfImprovementLoop: null, modeStore);

        var configs = await configLoader.LoadAsync(default);
        var agentConfig = configs[0];
        var spec = specBuilder.BuildSpec(agentConfig);
        var agent = new GenericAgent(spec.ToOrchestrationSpec(), services.GetRequiredService<ILogger<GenericAgent>>());
        await registry.RegisterAuthoredAsync(agent, agentConfig);

        await registry.ExecuteOnceAsync("extender-passive", default);

        runCount.Should().Be(0, "Passive mode must skip extender execution");
        var logs = logStore.GetRecent("extender-passive", 20, null, null);
        logs.Should().Contain(l => l.Message.Contains("Passive mode") || l.Message.Contains("observe only"));
    }

    [Fact]
    public async Task BackgroundAgent_ActiveMode_ExtenderRuns()
    {
        var modeStore = new InMemoryAggressivenessModeStore();
        modeStore.SetMode(BackgroundAgentAggressivenessMode.Active);

        var runCount = 0;
        var mockExtend = new MockSelfExtendRunner(() => runCount++);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BackgroundAgents:Agents:0:Id"] = "extender-active",
                ["BackgroundAgents:Agents:0:Name"] = "Extender",
                ["BackgroundAgents:Agents:0:Role"] = "extender",
                ["BackgroundAgents:Agents:0:Enabled"] = "true",
                ["BackgroundAgents:Agents:0:MaxDataSensitivity"] = "Public",
                ["BackgroundAgents:Agents:0:Commands:0"] = "extend",
                ["BackgroundAgents:Agents:0:Parameters:RepoRoot"] = Environment.CurrentDirectory,
                ["BackgroundAgents:Agents:0:Schedule:Type"] = "Interval",
                ["BackgroundAgents:Agents:0:Schedule:Interval"] = "00:01:00",
            })
            .Build();

        var services = new ServiceCollection()
            .AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning))
            .BuildServiceProvider();

        var sensitivityRegistry = new DataSensitivityRegistry();
        var configLoader = new BackgroundAgentConfigLoader(config, sensitivityRegistry, null);
        var specBuilder = new BackgroundAgentSpecBuilder(sensitivityRegistry, null);
        var logStore = new InMemoryAgentLogStore();
        var scheduleExecutor = new ScheduleExecutor();
        var scheduler = new AgentScheduler(scheduleExecutor, null);
        var registry = new BackgroundAgentRegistry(scheduler, null, logStore, null, null, mockExtend, selfImprovementLoop: null, modeStore);

        var configs = await configLoader.LoadAsync(default);
        var agentConfig = configs[0];
        var spec = specBuilder.BuildSpec(agentConfig);
        var agent = new GenericAgent(spec.ToOrchestrationSpec(), services.GetRequiredService<ILogger<GenericAgent>>());
        await registry.RegisterAuthoredAsync(agent, agentConfig);

        await registry.ExecuteOnceAsync("extender-active", default);

        runCount.Should().Be(1, "Active mode must run extender");
    }

    [Fact]
    public void AllFourModes_CanBeSetAndRetrieved()
    {
        var modeStore = new InMemoryAggressivenessModeStore();

        modeStore.SetMode(BackgroundAgentAggressivenessMode.Passive);
        modeStore.GetMode().Should().Be(BackgroundAgentAggressivenessMode.Passive);

        modeStore.SetMode(BackgroundAgentAggressivenessMode.SemiActive);
        modeStore.GetMode().Should().Be(BackgroundAgentAggressivenessMode.SemiActive);

        modeStore.SetMode(BackgroundAgentAggressivenessMode.Active);
        modeStore.GetMode().Should().Be(BackgroundAgentAggressivenessMode.Active);

        modeStore.SetMode(BackgroundAgentAggressivenessMode.Ambient);
        modeStore.GetMode().Should().Be(BackgroundAgentAggressivenessMode.Ambient);
    }

    [Fact]
    public void Mode_SwitchableAtRuntime_NoRestartRequired()
    {
        var modeStore = new InMemoryAggressivenessModeStore();
        modeStore.SetMode(BackgroundAgentAggressivenessMode.Passive);
        modeStore.SetMode(BackgroundAgentAggressivenessMode.Active);
        modeStore.GetMode().Should().Be(BackgroundAgentAggressivenessMode.Active);
    }

    [Fact]
    public void BackgroundAgent_ModeSwitch_TakesEffect_WithoutProcessRestart()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ashlar-mode-test-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "agent-mode.json");

            // Simulate CLI process: sets mode
            var cliStore = new FileBasedAggressivenessModeStore(path);
            cliStore.SetMode(BackgroundAgentAggressivenessMode.Passive);

            // Simulate background agent process: reads mode (separate store instance = separate process)
            var agentStore = new FileBasedAggressivenessModeStore(path);
            agentStore.GetMode().Should().Be(BackgroundAgentAggressivenessMode.Passive);

            // CLI changes mode (user runs "ashlar background-agent mode set active")
            cliStore.SetMode(BackgroundAgentAggressivenessMode.Active);

            // Agent's next cycle reads updated mode without restart
            agentStore.GetMode().Should().Be(BackgroundAgentAggressivenessMode.Active);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task BackgroundAgent_SemiActiveMode_WithoutApproval_SkipsExecution()
    {
        var modeStore = new InMemoryAggressivenessModeStore();
        modeStore.SetMode(BackgroundAgentAggressivenessMode.SemiActive);

        var runCount = 0;
        var mockExtend = new MockSelfExtendRunner(() => runCount++);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BackgroundAgents:Agents:0:Id"] = "extender-semiactive",
                ["BackgroundAgents:Agents:0:Name"] = "Extender",
                ["BackgroundAgents:Agents:0:Role"] = "extender",
                ["BackgroundAgents:Agents:0:Enabled"] = "true",
                ["BackgroundAgents:Agents:0:MaxDataSensitivity"] = "Public",
                ["BackgroundAgents:Agents:0:Commands:0"] = "extend",
                ["BackgroundAgents:Agents:0:Parameters:RepoRoot"] = Environment.CurrentDirectory,
                ["BackgroundAgents:Agents:0:Schedule:Type"] = "Interval",
                ["BackgroundAgents:Agents:0:Schedule:Interval"] = "00:01:00",
            })
            .Build();

        var services = new ServiceCollection()
            .AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning))
            .BuildServiceProvider();

        var sensitivityRegistry = new DataSensitivityRegistry();
        var configLoader = new BackgroundAgentConfigLoader(config, sensitivityRegistry, null);
        var specBuilder = new BackgroundAgentSpecBuilder(sensitivityRegistry, null);
        var logStore = new InMemoryAgentLogStore();
        var scheduleExecutor = new ScheduleExecutor();
        var scheduler = new AgentScheduler(scheduleExecutor, null);
        var registry = new BackgroundAgentRegistry(scheduler, null, logStore, null, null, mockExtend, selfImprovementLoop: null, modeStore, approvalGate: null);

        var configs = await configLoader.LoadAsync(default);
        var agentConfig = configs[0];
        var spec = specBuilder.BuildSpec(agentConfig);
        var agent = new GenericAgent(spec.ToOrchestrationSpec(), services.GetRequiredService<ILogger<GenericAgent>>());
        await registry.RegisterAuthoredAsync(agent, agentConfig);

        await registry.ExecuteOnceAsync("extender-semiactive", default);

        runCount.Should().Be(0, "SemiActive without approval must skip extender execution");
        var logs = logStore.GetRecent("extender-semiactive", 20, null, null);
        logs.Should().Contain(l => l.Message.Contains("SemiActive") || l.Message.Contains("denied") || l.Message.Contains("no approval"));
    }

    [Fact]
    public async Task BackgroundAgent_SemiActiveMode_SkipsAction_OnDenial()
    {
        var modeStore = new InMemoryAggressivenessModeStore();
        modeStore.SetMode(BackgroundAgentAggressivenessMode.SemiActive);

        var runCount = 0;
        var mockExtend = new MockSelfExtendRunner(() => runCount++);
        var approvalGate = new DenyApprovalGate();

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BackgroundAgents:Agents:0:Id"] = "extender-semiactive-denied",
                ["BackgroundAgents:Agents:0:Name"] = "Extender",
                ["BackgroundAgents:Agents:0:Role"] = "extender",
                ["BackgroundAgents:Agents:0:Enabled"] = "true",
                ["BackgroundAgents:Agents:0:MaxDataSensitivity"] = "Public",
                ["BackgroundAgents:Agents:0:Commands:0"] = "extend",
                ["BackgroundAgents:Agents:0:Parameters:RepoRoot"] = Environment.CurrentDirectory,
                ["BackgroundAgents:Agents:0:Schedule:Type"] = "Interval",
                ["BackgroundAgents:Agents:0:Schedule:Interval"] = "00:01:00",
            })
            .Build();

        var services = new ServiceCollection()
            .AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning))
            .BuildServiceProvider();

        var sensitivityRegistry = new DataSensitivityRegistry();
        var configLoader = new BackgroundAgentConfigLoader(config, sensitivityRegistry, null);
        var specBuilder = new BackgroundAgentSpecBuilder(sensitivityRegistry, null);
        var logStore = new InMemoryAgentLogStore();
        var scheduleExecutor = new ScheduleExecutor();
        var scheduler = new AgentScheduler(scheduleExecutor, null);
        var registry = new BackgroundAgentRegistry(scheduler, null, logStore, null, null, mockExtend, selfImprovementLoop: null, modeStore, approvalGate);

        var configs = await configLoader.LoadAsync(default);
        var agentConfig = configs[0];
        var spec = specBuilder.BuildSpec(agentConfig);
        var agent = new GenericAgent(spec.ToOrchestrationSpec(), services.GetRequiredService<ILogger<GenericAgent>>());
        await registry.RegisterAuthoredAsync(agent, agentConfig);

        await registry.ExecuteOnceAsync("extender-semiactive-denied", default);

        runCount.Should().Be(0, "SemiActive with denial must skip extender execution");
        var logs = logStore.GetRecent("extender-semiactive-denied", 20, null, null);
        logs.Should().Contain(l => l.Message.Contains("denied"));
    }

    [Fact]
    public async Task BackgroundAgent_SemiActiveMode_SkipsAction_OnTimeout_AndLogs()
    {
        var modeStore = new InMemoryAggressivenessModeStore();
        modeStore.SetMode(BackgroundAgentAggressivenessMode.SemiActive);

        var runCount = 0;
        var mockExtend = new MockSelfExtendRunner(() => runCount++);
        var approvalGate = new TimeoutApprovalGate();

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BackgroundAgents:Agents:0:Id"] = "extender-semiactive-timeout",
                ["BackgroundAgents:Agents:0:Name"] = "Extender",
                ["BackgroundAgents:Agents:0:Role"] = "extender",
                ["BackgroundAgents:Agents:0:Enabled"] = "true",
                ["BackgroundAgents:Agents:0:MaxDataSensitivity"] = "Public",
                ["BackgroundAgents:Agents:0:Commands:0"] = "extend",
                ["BackgroundAgents:Agents:0:Parameters:RepoRoot"] = Environment.CurrentDirectory,
                ["BackgroundAgents:Agents:0:Schedule:Type"] = "Interval",
                ["BackgroundAgents:Agents:0:Schedule:Interval"] = "00:01:00",
            })
            .Build();

        var services = new ServiceCollection()
            .AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning))
            .BuildServiceProvider();

        var sensitivityRegistry = new DataSensitivityRegistry();
        var configLoader = new BackgroundAgentConfigLoader(config, sensitivityRegistry, null);
        var specBuilder = new BackgroundAgentSpecBuilder(sensitivityRegistry, null);
        var logStore = new InMemoryAgentLogStore();
        var scheduleExecutor = new ScheduleExecutor();
        var scheduler = new AgentScheduler(scheduleExecutor, null);
        var registry = new BackgroundAgentRegistry(scheduler, null, logStore, null, null, mockExtend, selfImprovementLoop: null, modeStore, approvalGate);

        var configs = await configLoader.LoadAsync(default);
        var agentConfig = configs[0];
        var spec = specBuilder.BuildSpec(agentConfig);
        var agent = new GenericAgent(spec.ToOrchestrationSpec(), services.GetRequiredService<ILogger<GenericAgent>>());
        await registry.RegisterAuthoredAsync(agent, agentConfig);

        await registry.ExecuteOnceAsync("extender-semiactive-timeout", default);

        runCount.Should().Be(0, "SemiActive with timeout must skip extender execution");
        var logs = logStore.GetRecent("extender-semiactive-timeout", 20, null, null);
        logs.Should().Contain(l => l.Message.Contains("timeout"));
    }

    [Fact]
    public async Task BackgroundAgent_SemiActiveMode_WithApproval_Runs()
    {
        var modeStore = new InMemoryAggressivenessModeStore();
        modeStore.SetMode(BackgroundAgentAggressivenessMode.SemiActive);

        var runCount = 0;
        var mockExtend = new MockSelfExtendRunner(() => runCount++);
        var approvalGate = new AlwaysApprovalGate();

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BackgroundAgents:Agents:0:Id"] = "extender-semiactive-approved",
                ["BackgroundAgents:Agents:0:Name"] = "Extender",
                ["BackgroundAgents:Agents:0:Role"] = "extender",
                ["BackgroundAgents:Agents:0:Enabled"] = "true",
                ["BackgroundAgents:Agents:0:MaxDataSensitivity"] = "Public",
                ["BackgroundAgents:Agents:0:Commands:0"] = "extend",
                ["BackgroundAgents:Agents:0:Parameters:RepoRoot"] = Environment.CurrentDirectory,
                ["BackgroundAgents:Agents:0:Schedule:Type"] = "Interval",
                ["BackgroundAgents:Agents:0:Schedule:Interval"] = "00:01:00",
            })
            .Build();

        var services = new ServiceCollection()
            .AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning))
            .BuildServiceProvider();

        var sensitivityRegistry = new DataSensitivityRegistry();
        var configLoader = new BackgroundAgentConfigLoader(config, sensitivityRegistry, null);
        var specBuilder = new BackgroundAgentSpecBuilder(sensitivityRegistry, null);
        var logStore = new InMemoryAgentLogStore();
        var scheduleExecutor = new ScheduleExecutor();
        var scheduler = new AgentScheduler(scheduleExecutor, null);
        var registry = new BackgroundAgentRegistry(scheduler, null, logStore, null, null, mockExtend, selfImprovementLoop: null, modeStore, approvalGate);

        var configs = await configLoader.LoadAsync(default);
        var agentConfig = configs[0];
        var spec = specBuilder.BuildSpec(agentConfig);
        var agent = new GenericAgent(spec.ToOrchestrationSpec(), services.GetRequiredService<ILogger<GenericAgent>>());
        await registry.RegisterAuthoredAsync(agent, agentConfig);

        await registry.ExecuteOnceAsync("extender-semiactive-approved", default);

        runCount.Should().Be(1, "SemiActive with approval must run extender");
    }

    [Fact]
    public async Task BackgroundAgent_AmbientMode_ExtenderRuns_Silently()
    {
        var modeStore = new InMemoryAggressivenessModeStore();
        modeStore.SetMode(BackgroundAgentAggressivenessMode.Ambient);

        var runCount = 0;
        var mockExtend = new MockSelfExtendRunner(() => runCount++);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BackgroundAgents:Agents:0:Id"] = "extender-ambient",
                ["BackgroundAgents:Agents:0:Name"] = "Extender",
                ["BackgroundAgents:Agents:0:Role"] = "extender",
                ["BackgroundAgents:Agents:0:Enabled"] = "true",
                ["BackgroundAgents:Agents:0:MaxDataSensitivity"] = "Public",
                ["BackgroundAgents:Agents:0:Commands:0"] = "extend",
                ["BackgroundAgents:Agents:0:Parameters:RepoRoot"] = Environment.CurrentDirectory,
                ["BackgroundAgents:Agents:0:Schedule:Type"] = "Interval",
                ["BackgroundAgents:Agents:0:Schedule:Interval"] = "00:01:00",
            })
            .Build();

        var services = new ServiceCollection()
            .AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning))
            .BuildServiceProvider();

        var sensitivityRegistry = new DataSensitivityRegistry();
        var configLoader = new BackgroundAgentConfigLoader(config, sensitivityRegistry, null);
        var specBuilder = new BackgroundAgentSpecBuilder(sensitivityRegistry, null);
        var logStore = new InMemoryAgentLogStore();
        var scheduleExecutor = new ScheduleExecutor();
        var scheduler = new AgentScheduler(scheduleExecutor, null);
        var registry = new BackgroundAgentRegistry(scheduler, null, logStore, null, null, mockExtend, selfImprovementLoop: null, modeStore);

        var configs = await configLoader.LoadAsync(default);
        var agentConfig = configs[0];
        var spec = specBuilder.BuildSpec(agentConfig);
        var agent = new GenericAgent(spec.ToOrchestrationSpec(), services.GetRequiredService<ILogger<GenericAgent>>());
        await registry.RegisterAuthoredAsync(agent, agentConfig);

        await registry.ExecuteOnceAsync("extender-ambient", default);

        runCount.Should().Be(1, "Ambient mode must run extender");
        var logs = logStore.GetRecent("extender-ambient", 20, null, null);
        logs.Should().Contain(l => l.Message.Contains("Ambient") && l.Message.Contains("executed silently"));
    }

    [Fact]
    public async Task BackgroundAgent_AmbientMode_LogsAllActions_ToAuditLog()
    {
        var modeStore = new InMemoryAggressivenessModeStore();
        modeStore.SetMode(BackgroundAgentAggressivenessMode.Ambient);

        var ambientActions = new ConcurrentBag<(string AgentId, string Summary, int ToolCallsExecuted)>();
        var mockAuditLog = new CaptureAmbientAuditLog(ambientActions);
        var mockExtend = new MockSelfExtendRunnerWithResult(3, "Applied 3 edits");

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BackgroundAgents:Agents:0:Id"] = "extender-ambient-audit",
                ["BackgroundAgents:Agents:0:Name"] = "Extender",
                ["BackgroundAgents:Agents:0:Role"] = "extender",
                ["BackgroundAgents:Agents:0:Enabled"] = "true",
                ["BackgroundAgents:Agents:0:MaxDataSensitivity"] = "Public",
                ["BackgroundAgents:Agents:0:Commands:0"] = "extend",
                ["BackgroundAgents:Agents:0:Parameters:RepoRoot"] = Environment.CurrentDirectory,
                ["BackgroundAgents:Agents:0:Schedule:Type"] = "Interval",
                ["BackgroundAgents:Agents:0:Schedule:Interval"] = "00:01:00",
            })
            .Build();

        var services = new ServiceCollection()
            .AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning))
            .BuildServiceProvider();

        var sensitivityRegistry = new DataSensitivityRegistry();
        var configLoader = new BackgroundAgentConfigLoader(config, sensitivityRegistry, null);
        var specBuilder = new BackgroundAgentSpecBuilder(sensitivityRegistry, null);
        var logStore = new InMemoryAgentLogStore();
        var scheduleExecutor = new ScheduleExecutor();
        var scheduler = new AgentScheduler(scheduleExecutor, null);
        var registry = new BackgroundAgentRegistry(scheduler, null, logStore, null, null, mockExtend, selfImprovementLoop: null, modeStore, approvalGate: null, auditLog: mockAuditLog);

        var configs = await configLoader.LoadAsync(default);
        var agentConfig = configs[0];
        var spec = specBuilder.BuildSpec(agentConfig);
        var agent = new GenericAgent(spec.ToOrchestrationSpec(), services.GetRequiredService<ILogger<GenericAgent>>());
        await registry.RegisterAuthoredAsync(agent, agentConfig);

        await registry.ExecuteOnceAsync("extender-ambient-audit", default);

        ambientActions.Should().HaveCount(1);
        var entry = ambientActions.Single();
        entry.AgentId.Should().Be("extender-ambient-audit");
        entry.Summary.Should().Be("Applied 3 edits");
        entry.ToolCallsExecuted.Should().Be(3);
    }

    private sealed class MockSelfExtendRunner : ISelfExtendRunner
    {
        private readonly Action _onRun;

        public MockSelfExtendRunner(Action onRun) => _onRun = onRun;

        public Task<SelfExtendRunResult> RunAsync(string repoRoot, CancellationToken cancellationToken = default)
        {
            _onRun();
            return Task.FromResult(new SelfExtendRunResult(true, 0, 0, "Mock"));
        }
    }

    private sealed class MockSelfExtendRunnerWithResult : ISelfExtendRunner
    {
        private readonly int _toolCallsExecuted;
        private readonly string _summary;

        public MockSelfExtendRunnerWithResult(int toolCallsExecuted, string summary)
        {
            _toolCallsExecuted = toolCallsExecuted;
            _summary = summary;
        }

        public Task<SelfExtendRunResult> RunAsync(string repoRoot, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SelfExtendRunResult(true, _toolCallsExecuted, 0, _summary));
    }

    private sealed class CaptureAmbientAuditLog : IDataDecisionAuditLog
    {
        private readonly ConcurrentBag<(string AgentId, string Summary, int ToolCallsExecuted)> _captured;

        public CaptureAmbientAuditLog(ConcurrentBag<(string, string, int)> captured) => _captured = captured;

        public void LogAmbientAction(string agentId, string summary, int toolCallsExecuted) =>
            _captured.Add((agentId, summary, toolCallsExecuted));

        public void LogCopilotTask(string tenantId, string taskId, bool success) { }

        public void LogSanitization(SanitizationAuditEntryDto entry) { }
        public void LogBoundaryChange(BoundaryChangeEvent evt) { }
        public void LogClassification(string dataType, string levelName, string? reason) { }
        public void LogScopeChainRejection(IReadOnlyList<string> chainIds, int rejectedStep, string? resolvedPath, string reason) { }
        public IReadOnlyList<DataDecisionAuditEntry> GetRecent(int maxCount, DateTimeOffset? since = null, DateTimeOffset? until = null, string? eventType = null) =>
            Array.Empty<DataDecisionAuditEntry>();
        public string ExportToJson(int maxCount = 1000, DateTimeOffset? since = null, DateTimeOffset? until = null, string? eventType = null) => "{}";
        public string ExportToMarkdown(int maxCount = 1000, DateTimeOffset? since = null, DateTimeOffset? until = null, string? eventType = null) => "";
        public string ExportToCsv(int maxCount = 1000, DateTimeOffset? since = null, DateTimeOffset? until = null, string? eventType = null) => "";
    }
}
