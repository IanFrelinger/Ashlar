using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ashlar.Abstractions;
using Ashlar.BackgroundAgents.Configuration;
using Ashlar.Core.Application.Trust.Ports;
using Ashlar.BackgroundAgents.DataSensitivity;
using Ashlar.BackgroundAgents.Extending;
using Ashlar.BackgroundAgents.Logging;
using Ashlar.BackgroundAgents.Registry;
using Ashlar.BackgroundAgents.Scheduling;
using Ashlar.BackgroundAgents.Trust;
using Ashlar.Core.Application.Trust.Models;
using Ashlar.Core.Domain;
using Ashlar.Orchestration.Agents;
using Ashlar.Tests.Infrastructure.Helpers;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.BackgroundAgents;

/// <summary>
/// E2E lifecycle tests for background agents: registry, mode transitions,
/// approval gates, audit trail, and resilience under concurrent operations.
/// </summary>
[Trait("Category", "E2E")]
public sealed class BackgroundAgentLifecycleE2ETests
{
    private static (BackgroundAgentRegistry Registry, InMemoryAgentLogStore LogStore) BuildRegistry(
        BackgroundAgentAggressivenessMode mode = BackgroundAgentAggressivenessMode.Active,
        ISelfExtendRunner? extendRunner = null,
        IApprovalGate? approvalGate = null,
        IDataDecisionAuditLog? auditLog = null)
    {
        var modeStore = new InMemoryAggressivenessModeStore();
        modeStore.SetMode(mode);
        var logStore = new InMemoryAgentLogStore();
        var scheduleExecutor = new ScheduleExecutor();
        var scheduler = new AgentScheduler(scheduleExecutor, null);
        var registry = new BackgroundAgentRegistry(
            scheduler, null, logStore, null, null,
            extendRunner, selfImprovementLoop: null, modeStore, approvalGate,
            sensitivityRegistry: new DataSensitivityRegistry(),
            auditLog: auditLog);
        return (registry, logStore);
    }

    private static async Task<GenericAgent> RegisterExtenderAgent(
        BackgroundAgentRegistry registry,
        string agentId)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"BackgroundAgents:Agents:0:Id"] = agentId,
                [$"BackgroundAgents:Agents:0:Name"] = "Extender",
                [$"BackgroundAgents:Agents:0:Role"] = "extender",
                [$"BackgroundAgents:Agents:0:Enabled"] = "true",
                [$"BackgroundAgents:Agents:0:MaxDataSensitivity"] = "Public",
                [$"BackgroundAgents:Agents:0:Commands:0"] = "extend",
                [$"BackgroundAgents:Agents:0:Parameters:RepoRoot"] = Environment.CurrentDirectory,
                [$"BackgroundAgents:Agents:0:Schedule:Type"] = "Interval",
                [$"BackgroundAgents:Agents:0:Schedule:Interval"] = "00:01:00",
            })
            .Build();

        var sensitivityRegistry = new DataSensitivityRegistry();
        var configLoader = new BackgroundAgentConfigLoader(config, sensitivityRegistry, null);
        var specBuilder = new BackgroundAgentSpecBuilder(sensitivityRegistry, null);
        var services = new ServiceCollection()
            .AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning))
            .BuildServiceProvider();

        var configs = await configLoader.LoadAsync(default);
        var specDto = specBuilder.BuildSpec(configs[0]);
        // Convert DTO to full spec for GenericAgent constructor
        var spec = new Ashlar.Orchestration.Architect.Models.AgentSpawnSpec
        {
            AgentId = specDto.AgentId,
            Name = specDto.Name,
            Domain = specDto.Domain,
            Goal = specDto.Goal,
            Description = specDto.Description,
            Dependencies = specDto.Dependencies,
            OllamaModel = specDto.OllamaModel
        };
        var agent = new GenericAgent(spec, services.GetRequiredService<ILogger<GenericAgent>>());
        await registry.RegisterAsync(agent, configs[0], AgentRegistrationOrigin.Authored);
        return agent;
    }

    [Fact(Timeout = TestTimeouts.E2E)]
    public async Task ModeTransition_PassiveToActive_StartsExecution()
    {
        var runCount = 0;
        var mockExtend = new CountingSelfExtendRunner(() => runCount++);
        var modeStore = new InMemoryAggressivenessModeStore();
        modeStore.SetMode(BackgroundAgentAggressivenessMode.Passive);

        var logStore = new InMemoryAgentLogStore();
        var scheduler = new AgentScheduler(new ScheduleExecutor(), null);
        var registry = new BackgroundAgentRegistry(
            scheduler, null, logStore, null, null,
            mockExtend, selfImprovementLoop: null, modeStore,
            sensitivityRegistry: new DataSensitivityRegistry());

        await RegisterExtenderAgent(registry, "transition-test");

        await registry.ExecuteOnceAsync("transition-test", default);
        runCount.Should().Be(0, "passive mode should skip");

        modeStore.SetMode(BackgroundAgentAggressivenessMode.Active);
        await registry.ExecuteOnceAsync("transition-test", default);
        runCount.Should().Be(1, "active mode should execute");
    }

    [Fact(Timeout = TestTimeouts.E2E)]
    public async Task AuditLog_AmbientMode_RecordsActions()
    {
        var ambientActions = new ConcurrentBag<(string AgentId, string Summary, int ToolCallsExecuted)>();
        var mockAuditLog = new CaptureAmbientAuditLog(ambientActions);
        var mockExtend = new CountingSelfExtendRunnerWithResult(2, "2 edits");

        var (registry, _) = BuildRegistry(
            BackgroundAgentAggressivenessMode.Ambient,
            extendRunner: mockExtend,
            auditLog: mockAuditLog);

        await RegisterExtenderAgent(registry, "ambient-audit");
        await registry.ExecuteOnceAsync("ambient-audit", default);

        ambientActions.Should().HaveCount(1);
        var entry = ambientActions.Single();
        entry.AgentId.Should().Be("ambient-audit");
        entry.ToolCallsExecuted.Should().Be(2);
    }

    [Fact(Timeout = TestTimeouts.E2E)]
    public async Task SemiActive_ApprovalDenied_SkipsExecution()
    {
        var runCount = 0;
        var mockExtend = new CountingSelfExtendRunner(() => runCount++);
        var gate = new DenyApprovalGate();

        var (registry, logStore) = BuildRegistry(
            BackgroundAgentAggressivenessMode.SemiActive,
            extendRunner: mockExtend,
            approvalGate: gate);

        await RegisterExtenderAgent(registry, "denied-agent");
        await registry.ExecuteOnceAsync("denied-agent", default);

        runCount.Should().Be(0);
        var logs = logStore.GetRecent("denied-agent", 20);
        logs.Should().Contain(l => l.Message.Contains("denied"));
    }

    [Fact(Timeout = TestTimeouts.E2E)]
    public async Task SemiActive_ApprovalGranted_Executes()
    {
        var runCount = 0;
        var mockExtend = new CountingSelfExtendRunner(() => runCount++);
        var gate = new AlwaysApprovalGate();

        var (registry, _) = BuildRegistry(
            BackgroundAgentAggressivenessMode.SemiActive,
            extendRunner: mockExtend,
            approvalGate: gate);

        await RegisterExtenderAgent(registry, "approved-agent");
        await registry.ExecuteOnceAsync("approved-agent", default);

        runCount.Should().Be(1);
    }

    [Fact(Timeout = TestTimeouts.E2E)]
    public async Task LogStore_BoundsPerAgent()
    {
        var (registry, logStore) = BuildRegistry(
            BackgroundAgentAggressivenessMode.Active,
            extendRunner: new CountingSelfExtendRunner(() => { }));

        await RegisterExtenderAgent(registry, "log-bound-test");

        for (var i = 0; i < AshlarDefaults.AgentLogMaxEntriesPerAgent + 100; i++)
            logStore.Append("log-bound-test", "Info", $"msg-{i}");

        var logs = logStore.GetRecent("log-bound-test", int.MaxValue);
        logs.Count.Should().BeLessThanOrEqualTo(AshlarDefaults.AgentLogMaxEntriesPerAgent);
    }

    [Fact(Timeout = TestTimeouts.E2E)]
    public async Task InMemorySanitizationAuditLog_Bounds()
    {
        await Task.CompletedTask;
        var auditLog = new InMemorySanitizationAuditLog();
        for (var i = 0; i < AshlarDefaults.SanitizationAuditMaxEntries + 500; i++)
            auditLog.LogRedaction(DateTimeOffset.UtcNow, "v1", "field", "redact", null);

        var recent = auditLog.GetRecent(int.MaxValue);
        recent.Count.Should().BeLessThanOrEqualTo(AshlarDefaults.SanitizationAuditMaxEntries);
    }

    [Fact(Timeout = TestTimeouts.E2E)]
    public async Task DataDecisionAuditLog_Bounds()
    {
        await Task.CompletedTask;
        var auditLog = new DataDecisionAuditLog();
        for (var i = 0; i < AshlarDefaults.DataDecisionAuditMaxEntries + 500; i++)
            auditLog.LogClassification("type", "level", "reason");

        var recent = auditLog.GetRecent(int.MaxValue);
        recent.Count.Should().BeLessThanOrEqualTo(AshlarDefaults.DataDecisionAuditMaxEntries);
    }

    // ── Test helpers ────────────────────────────────────────────────

    private sealed class CountingSelfExtendRunner : ISelfExtendRunner
    {
        private readonly Action _onRun;
        public CountingSelfExtendRunner(Action onRun) => _onRun = onRun;
        public Task<SelfExtendRunResult> RunAsync(string repoRoot, CancellationToken ct = default)
        {
            _onRun();
            return Task.FromResult(new SelfExtendRunResult(true, 0, 0, "Mock"));
        }
    }

    private sealed class CountingSelfExtendRunnerWithResult : ISelfExtendRunner
    {
        private readonly int _toolCalls;
        private readonly string _summary;
        public CountingSelfExtendRunnerWithResult(int toolCalls, string summary)
        {
            _toolCalls = toolCalls;
            _summary = summary;
        }
        public Task<SelfExtendRunResult> RunAsync(string repoRoot, CancellationToken ct = default) =>
            Task.FromResult(new SelfExtendRunResult(true, _toolCalls, 0, _summary));
    }

    private sealed class CaptureAmbientAuditLog : IDataDecisionAuditLog
    {
        private readonly ConcurrentBag<(string, string, int)> _captured;
        public CaptureAmbientAuditLog(ConcurrentBag<(string, string, int)> c) => _captured = c;
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
