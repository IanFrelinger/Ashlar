using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Ashlar.Abstractions;
using Ashlar.BackgroundAgents.Configuration;
using Ashlar.BackgroundAgents.DataSensitivity;
using Ashlar.BackgroundAgents.HostRunners;
using Ashlar.BackgroundAgents.Registry;
using Ashlar.BackgroundAgents.Scheduling;
using Ashlar.BackgroundAgents.Telemetry;
using Ashlar.Manifest.Admission;
using Ashlar.Orchestration.Agents;
using Ashlar.Tests.BackgroundAgents.Registry;
using Xunit;

namespace Ashlar.Tests.BackgroundAgents.SelfExtend;

/// <summary>
/// The governance floor refuses a CYCLE's write into <c>.ashlar/</c>; it must not refuse the
/// HOST's. The gate record, the planner scratchpad and the cycle event are all written into
/// <c>.ashlar/</c> by host-side writers (<c>GateStore</c>, <c>PlannerScratchpad</c>,
/// <c>CycleEventStore</c>) that use the file APIs directly and never pass through
/// <c>ToolSandbox</c>. A floor placed anywhere a host-side writer would hit it — in the read
/// resolver, or in a shared file helper — would leave a refused cycle unable to record that it
/// was refused, which is the one outcome the ledger exists to keep.
///
/// <para>Drives a real cycle end to end: a scripted model, the real runner adapter with the
/// real toolbox and policy chain, through the registry so the cycle event is emitted where
/// production emits it.</para>
/// </summary>
public sealed class SelfExtendCycleStateWritesTests : IDisposable
{
    private readonly string _repo;

    public SelfExtendCycleStateWritesTests()
    {
        _repo = Path.Combine(Path.GetTempPath(), "sx-state-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(_repo);
        // An ashlar project in proposing mode, so the cycle's outcome is recorded through the gate.
        File.WriteAllText(Path.Combine(_repo, "ashlar.policy.yaml"), """
            apiVersion: ashlar/v1
            kind: Policy
            sandbox:
              root: .
              writable: []
            selfExtend:
              mode: proposing
              budget:
                extensions: 3
                window: 24h
              mayAdd: [brick]
              gatesRequired: [sandbox]
            never:
              - modify_gate
              - widen_sandbox
              - access_signing_keys
              - truncate_ledger
              - grant_capability
            """);
    }

    public void Dispose()
    {
        try { Directory.Delete(_repo, recursive: true); } catch { /* best effort */ }
    }

    private static string Turn(params (string id, string argsJson)[] calls)
    {
        var arr = string.Join(",", calls.Select(c => $"{{\"id\":\"{c.id}\",\"arguments\":{c.argsJson}}}"));
        return $"{{\"tool_calls\":[{arr}],\"rationale\":\"scripted\"}}";
    }

    private static string Done() =>
        JsonSerializer.Serialize(new { tool_calls = Array.Empty<object>(), rationale = "done" });

    [Fact]
    public async Task A_cycle_whose_governance_write_is_refused_still_records_its_own_outcome()
    {
        // Turn 1: a legitimate write FIRST, then the forged ledger write. Order matters — a denial
        // stops the rest of the chain — and the legitimate write is what gives the gate something
        // to record. Turn 2: stop.
        var model = new Mock<IModel>();
        model.SetupSequence(m => m.CompleteAsync(It.IsAny<ModelInput>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ModelOutput(Turn(
                ("repo.fs.write", """{"path":"docs/notes.md","content":"# notes"}"""),
                ("repo.fs.write", """{"path":".ashlar/gates/forged.json","content":"{\"State\":\"Admitted\"}"}"""))))
            .ReturnsAsync(new ModelOutput(Done()));

        var runner = new SelfExtendRunnerAdapter(
            model.Object, NullLogger<SelfExtendRunnerAdapter>.Instance, NullLoggerFactory.Instance);
        var cycles = new CycleEventStore(Path.Combine(_repo, ".ashlar", "runtime-studio", "cycles.jsonl"));
        var registry = new BackgroundAgentRegistry(
            new AgentScheduler(new ScheduleExecutor(), NullLogger<AgentScheduler>.Instance),
            NullLogger<BackgroundAgentRegistry>.Instance,
            selfExtendRunner: runner,
            modeStore: new InMemoryAggressivenessModeStore(),
            sensitivityRegistry: new DataSensitivityRegistry(),
            cycleEvents: cycles);
        var config = new BackgroundAgentConfig
        {
            Id = "night-agent",
            Role = "extender",
            Enabled = true,
            MaxDataSensitivity = "Public",
            Commands = ["extend"],
            Parameters = new Dictionary<string, object> { ["RepoRoot"] = _repo, ["Objective"] = "record the cycle" },
            Schedule = new BackgroundAgentSchedule { Type = ScheduleType.Interval, Interval = TimeSpan.FromMinutes(1) },
        };
        await registry.RegisterAuthoredAsync(
            new GenericAgent(SelfExtendAuditTestSupport.BuildSpec(config), NullLogger<GenericAgent>.Instance), config);

        await registry.ExecuteOnceAsync(config.Id);

        // The forged write never landed; the control write did.
        File.Exists(Path.Combine(_repo, ".ashlar", "gates", "forged.json")).Should().BeFalse(
            "the cycle's write into the ledger is refused");
        File.Exists(Path.Combine(_repo, "docs", "notes.md")).Should().BeTrue("the control write landed");

        // The gate record — a host-side write into .ashlar/gates/ — is there, and it is honest.
        var record = (await new GateStore(Path.Combine(_repo, ".ashlar")).ListAsync()).Should().ContainSingle(
            "the refused cycle must still be recorded through the gate").Which;
        record.State.Should().Be(ProposalState.Rejected, "one denial fails the sandbox course");
        var sandbox = record.Proposal.Courses.Should().ContainSingle(c => c.Name == "sandbox").Which;
        sandbox.Passed.Should().BeFalse();
        sandbox.Detail.Should().Contain("1 denied");
        record.Proposal.Diff.Should().Contain("docs/notes.md").And.NotContain("forged.json",
            "the record claims the write that landed and never the one that was refused");

        // The planner scratchpad — a host-side write into .ashlar/runtime-studio/ — is there.
        var scratchpad = Path.Combine(_repo, ".ashlar", "runtime-studio", "night-agent-notes.md");
        File.Exists(scratchpad).Should().BeTrue("the scratchpad is written by the host, not by a tool");
        (await File.ReadAllTextAsync(scratchpad)).Should().Contain("denied=1").And.Contain("docs/notes.md");

        // The cycle event — a host-side append into .ashlar/runtime-studio/cycles.jsonl — is there.
        var evt = cycles.Read().Should().ContainSingle("the registry records every cycle, refused or not").Which;
        evt.agent.Should().Be("night-agent");
        evt.tools_executed.Should().Be(1);
        evt.tools_denied.Should().Be(1);
        evt.success.Should().BeFalse();
    }
}
