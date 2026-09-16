using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Ashlar.Abstractions;
using Ashlar.BackgroundAgents.Agents;
using Ashlar.Runtime;
using Xunit;

namespace Ashlar.Tests.BackgroundAgents.Agents;

/// <summary>
/// Behavioural tests for <see cref="ToolCallingAgent.RunCycleAsync"/>'s ReAct loop.
///
/// The model is stubbed with a deterministic, scripted IModel so we can assert exactly
/// how iterations, denials, deadlines, and empty-call termination interact without depending
/// on a live LLM.
/// </summary>
public sealed class ToolCallingAgentReActTests
{
    [Fact]
    public async Task Multi_turn_chain_executes_all_steps_and_stops_on_empty()
    {
        var model = ScriptedModel.Of(
            ResponseWithCalls("rationale: list first", ("noop", "{\"q\":\"a\"}")),
            ResponseWithCalls("rationale: now act", ("noop", "{\"q\":\"b\"}")),
            ResponseEmpty("done"));
        var (tools, policies) = BuildHarness(allowAll: true, out var noop);

        var agent = new ToolCallingAgent("planner", model, NullLogger<ToolCallingAgent>.Instance);
        var snapshot = WorldSnapshot.ForRepo("/repo", "/repo/out");

        var cycle = await agent.RunCycleAsync(snapshot, tools, policies, onRejected: null, memory: null, CancellationToken.None);

        cycle.Iterations.Should().Be(3);
        cycle.ToolCallsExecuted.Should().Be(2);
        cycle.ToolCallsDenied.Should().Be(0);
        cycle.StoppedReason.Should().Be("empty");
        cycle.FinalRationale.Should().Be("done");
        noop.Invocations.Should().Be(2);
        // Tool observations should be visible in the second iteration's prompt.
        model.SeenMessages[1].Should().Contain(m => m.role == "user" && m.content.StartsWith("tool noop:", StringComparison.Ordinal));
    }

    /// <summary>
    /// The operating rules are part of the write contract, and they outrank the last observation.
    /// A prompt that advertises <c>.ashlar/</c> as writable makes the governance floor cost ReAct
    /// iterations the loop cannot win — the planner is fed a DENIED observation and the rules steer
    /// it straight back — and one that omits <c>application/</c> hides a prefix that is open.
    /// </summary>
    [Fact]
    public async Task The_system_prompt_states_the_true_allowlist_and_the_governance_exclusions()
    {
        var model = ScriptedModel.Of(ResponseEmpty("nothing to do"));
        var (tools, policies) = BuildHarness(allowAll: true, out _);
        var agent = new ToolCallingAgent("planner", model, NullLogger<ToolCallingAgent>.Instance);

        await agent.RunCycleAsync(WorldSnapshot.ForRepo("/repo", "/repo/out"), tools, policies, onRejected: null, memory: null, CancellationToken.None);

        var system = model.SeenMessages[0].Should().Contain(m => m.role == "system").Which.content;
        system.Should().Contain("live under one of: src/, tests/, docs/, application/.");
        system.Should().NotContain("docs/, .ashlar/", "the prompt must not advertise the ledger as writable");
        system.Should().Contain("Never write governance")
            .And.Contain(".ashlar/")
            .And.Contain("*.props")
            .And.Contain("Project and solution files (*.csproj, *.sln) are fine to write");
    }

    [Fact]
    public async Task Empty_first_iteration_terminates_immediately()
    {
        var model = ScriptedModel.Of(ResponseEmpty("nothing to do"));
        var (tools, policies) = BuildHarness(allowAll: true, out _);

        var agent = new ToolCallingAgent("planner", model, NullLogger<ToolCallingAgent>.Instance);
        var snapshot = WorldSnapshot.ForRepo("/repo", "/repo/out");

        var cycle = await agent.RunCycleAsync(snapshot, tools, policies, onRejected: null, memory: null, CancellationToken.None);

        cycle.Iterations.Should().Be(1);
        cycle.ToolCallsExecuted.Should().Be(0);
        cycle.StoppedReason.Should().Be("empty");
        cycle.FinalRationale.Should().Be("nothing to do");
        cycle.MergedDelta.Should().BeNull();
    }

    [Fact]
    public async Task Hits_max_iterations_when_model_never_returns_empty()
    {
        var alwaysCall = ResponseWithCalls("loop", ("noop", "{\"q\":\"x\"}"));
        // 6+ scripted turns to guarantee we don't run out before the cap.
        var model = ScriptedModel.Of(alwaysCall, alwaysCall, alwaysCall, alwaysCall, alwaysCall, alwaysCall);
        var (tools, policies) = BuildHarness(allowAll: true, out var noop);

        var agent = new ToolCallingAgent(
            "planner", model, NullLogger<ToolCallingAgent>.Instance,
            maxIterations: 3);
        var snapshot = WorldSnapshot.ForRepo("/repo", "/repo/out");

        var cycle = await agent.RunCycleAsync(snapshot, tools, policies, onRejected: null, memory: null, CancellationToken.None);

        cycle.Iterations.Should().Be(3);
        cycle.ToolCallsExecuted.Should().Be(3);
        cycle.StoppedReason.Should().Be("max_iterations");
        noop.Invocations.Should().Be(3);
    }

    [Fact]
    public async Task Denied_call_is_reported_back_to_model_and_loop_can_recover()
    {
        var model = ScriptedModel.Of(
            ResponseWithCalls("attempt 1: bad path", ("noop", "{\"path\":\"forbidden\"}")),
            ResponseWithCalls("attempt 2: allowed path", ("noop", "{\"path\":\"ok\"}")),
            ResponseEmpty("done"));

        // Deny anything whose JSON args contain "forbidden".
        var (tools, policies) = BuildHarness(
            customApprove: (call) =>
            {
                var raw = call.Arguments.GetRawText();
                return !raw.Contains("forbidden", StringComparison.Ordinal);
            },
            out var noop);

        var rejections = new List<(string id, string reason)>();
        ChainRejectionCallback onRejected = (chain, idx, reason) => rejections.Add((chain[idx].Id, reason));

        var agent = new ToolCallingAgent("planner", model, NullLogger<ToolCallingAgent>.Instance);
        var snapshot = WorldSnapshot.ForRepo("/repo", "/repo/out");

        var cycle = await agent.RunCycleAsync(snapshot, tools, policies, onRejected, memory: null, CancellationToken.None);

        cycle.ToolCallsDenied.Should().Be(1);
        cycle.ToolCallsExecuted.Should().Be(1);
        cycle.StoppedReason.Should().Be("empty");
        rejections.Should().ContainSingle().Which.id.Should().Be("noop");
        // Iteration 2's prompt should contain a DENIED hint from the previous turn.
        model.SeenMessages[1].Should().Contain(m => m.content.Contains("DENIED", StringComparison.Ordinal));
        noop.Invocations.Should().Be(1);
    }

    [Fact]
    public async Task Deadline_terminates_cycle_with_deadline_reason()
    {
        // The model never answers, so the deadline is the only thing that can end this cycle.
        var model = new StallingModel();
        var (tools, policies) = BuildHarness(allowAll: true, out _);

        var agent = new ToolCallingAgent(
            "planner", model, NullLogger<ToolCallingAgent>.Instance,
            maxIterations: 5,
            perCycleDeadline: TimeSpan.FromMilliseconds(200));

        var snapshot = WorldSnapshot.ForRepo("/repo", "/repo/out");
        var cycle = await agent.RunCycleAsync(snapshot, tools, policies, onRejected: null, memory: null, CancellationToken.None);

        cycle.StoppedReason.Should().Be("deadline");
        cycle.ToolCallsExecuted.Should().Be(0);
    }

    [Fact]
    public async Task External_cancellation_propagates_as_OperationCanceledException()
    {
        // Never answers, so the caller's token is the only thing that can end this cycle.
        var model = new StallingModel();
        var (tools, policies) = BuildHarness(allowAll: true, out _);

        var agent = new ToolCallingAgent("planner", model, NullLogger<ToolCallingAgent>.Instance);
        var snapshot = WorldSnapshot.ForRepo("/repo", "/repo/out");
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        var act = () => agent.RunCycleAsync(snapshot, tools, policies, onRejected: null, memory: null, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task RunCycleAsync_null_tools_or_policies_throw()
    {
        var agent = new ToolCallingAgent("planner", new EmptyMockModel(), NullLogger<ToolCallingAgent>.Instance);
        var snapshot = WorldSnapshot.ForRepo("/repo", "/repo/out");
        var (tools, policies) = BuildHarness(allowAll: true, out _);

        var actTools = () => agent.RunCycleAsync(snapshot, null!, policies, null, null, CancellationToken.None);
        var actPolicies = () => agent.RunCycleAsync(snapshot, tools, null!, null, null, CancellationToken.None);

        await actTools.Should().ThrowAsync<ArgumentNullException>();
        await actPolicies.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task RunCycleAsync_with_no_tools_returns_empty_cycle()
    {
        var agent = new ToolCallingAgent("planner", new EmptyMockModel(), NullLogger<ToolCallingAgent>.Instance);
        var cycle = await agent.RunCycleAsync(
            WorldSnapshot.ForRepo("/repo", "/repo/out"),
            new EmptyToolbox(),
            new PolicyEngine(Array.Empty<IPolicy>()),
            null,
            null,
            CancellationToken.None);

        cycle.StoppedReason.Should().Be("empty");
        cycle.Iterations.Should().Be(0);
    }

    [Fact]
    public async Task RunCycleAsync_tool_failure_stops_with_error_reason()
    {
        var model = ScriptedModel.Of(ResponseWithCalls("boom", ("boom", "{}")));
        var tb = new CapabilityRegistry();
        tb.Register(new ThrowingTool());
        var policies = new PolicyEngine(new IPolicy[] { new PredicatePolicy(_ => true) });

        var agent = new ToolCallingAgent("planner", model, NullLogger<ToolCallingAgent>.Instance);
        var cycle = await agent.RunCycleAsync(
            WorldSnapshot.ForRepo("/repo", "/repo/out"),
            tb,
            policies,
            null,
            null,
            CancellationToken.None);

        cycle.StoppedReason.Should().Be("error");
    }

    [Fact]
    public async Task RunCycleAsync_truncates_non_serializable_tool_payload()
    {
        var model = ScriptedModel.Of(
            ResponseWithCalls("call", ("weird", "{}")),
            ResponseEmpty("done"));
        var tb = new CapabilityRegistry();
        tb.Register(new CircularPayloadTool());
        var policies = new PolicyEngine(new IPolicy[] { new PredicatePolicy(_ => true) });

        var agent = new ToolCallingAgent("planner", model, NullLogger<ToolCallingAgent>.Instance);
        var cycle = await agent.RunCycleAsync(
            WorldSnapshot.ForRepo("/repo", "/repo/out"),
            tb,
            policies,
            null,
            null,
            CancellationToken.None);

        cycle.StoppedReason.Should().Be("empty");
        model.SeenMessages[1].Should().Contain(m => m.content.Contains("payload_serialization_failed"));
    }

    [Fact]
    public async Task RunCycleAsync_truncates_large_tool_payload_in_conversation()
    {
        var model = ScriptedModel.Of(
            ResponseWithCalls("read", ("big", "{}")),
            ResponseEmpty("done"));
        var tb = new CapabilityRegistry();
        tb.Register(new LargePayloadTool());
        var policies = new PolicyEngine(new IPolicy[] { new PredicatePolicy(_ => true) });

        var agent = new ToolCallingAgent("planner", model, NullLogger<ToolCallingAgent>.Instance);
        await agent.RunCycleAsync(
            WorldSnapshot.ForRepo("/repo", "/repo/out"),
            tb,
            policies,
            null,
            null,
            CancellationToken.None);

        model.SeenMessages[1].Should().ContainSingle(m => m.content.Contains('…'));
    }

    [Fact]
    public async Task RunCycleAsync_policy_denial_invokes_callback_and_continues()
    {
        var model = ScriptedModel.Of(
            ResponseWithCalls("try write", ("noop", "{}")),
            ResponseEmpty("give up"));
        var (tools, policies) = BuildHarness(allowAll: false, out _);
        string? rejectedReason = null;

        var agent = new ToolCallingAgent("planner", model, NullLogger<ToolCallingAgent>.Instance);
        var cycle = await agent.RunCycleAsync(
            WorldSnapshot.ForRepo("/repo", "/repo/out"),
            tools,
            policies,
            (calls, index, reason) => rejectedReason = reason,
            null,
            CancellationToken.None);

        cycle.ToolCallsDenied.Should().Be(1);
        rejectedReason.Should().Contain("predicate");
        model.SeenMessages[1].Should().Contain(m => m.content.Contains("DENIED"));
    }

    [Fact]
    public async Task RunCycleAsync_skips_tool_calls_with_blank_ids()
    {
        var model = ScriptedModel.Of(new ScriptedModel.Turn("""
            {"tool_calls":[{"id":"","arguments":{}},{"id":"noop","arguments":{}}],"rationale":"mixed"}
            """));
        var (tools, policies) = BuildHarness(allowAll: true, out var noop);

        var agent = new ToolCallingAgent("planner", model, NullLogger<ToolCallingAgent>.Instance);
        await agent.RunCycleAsync(
            WorldSnapshot.ForRepo("/repo", "/repo/out"),
            tools,
            policies,
            null,
            null,
            CancellationToken.None);

        noop.Invocations.Should().Be(1);
    }

    [Fact]
    public async Task RunCycleAsync_invalid_json_from_model_stops_on_empty()
    {
        var model = ScriptedModel.Of(new ScriptedModel.Turn("not-json-at-all"));
        var (tools, policies) = BuildHarness(allowAll: true, out _);

        var agent = new ToolCallingAgent("planner", model, NullLogger<ToolCallingAgent>.Instance);
        var cycle = await agent.RunCycleAsync(
            WorldSnapshot.ForRepo("/repo", "/repo/out"),
            tools,
            policies,
            null,
            null,
            CancellationToken.None);

        cycle.StoppedReason.Should().Be("empty");
        cycle.ToolCallsExecuted.Should().Be(0);
    }

    private sealed class LargePayloadTool : ITool
    {
        public string Id => "big";
        public ToolSchema Schema => new(Id, "big", "{\"type\":\"object\"}");
        public Task<ToolResult> InvokeAsync(ToolCall call, WorldSnapshot s, CancellationToken ct)
        {
            var delta = new SimpleDelta(s.Tick, s.Tick + 1, "big");
            return Task.FromResult(new ToolResult(delta, new { blob = new string('x', 5000) }));
        }
    }

    private sealed class EmptyToolbox : IToolbox
    {
        public IEnumerable<ToolSchema> Schemas() => Array.Empty<ToolSchema>();
        public Task<ToolResult> InvokeAsync(ToolCall call, WorldSnapshot s, CancellationToken ct) =>
            throw new NotSupportedException();
        public IAgentMemory MemoryFor(IAgent agent) => throw new NotSupportedException();
    }

    private sealed class ThrowingTool : ITool
    {
        public string Id => "boom";
        public ToolSchema Schema => new(Id, "boom", "{\"type\":\"object\"}");
        public Task<ToolResult> InvokeAsync(ToolCall call, WorldSnapshot s, CancellationToken ct) =>
            throw new InvalidOperationException("tool exploded");
    }

    private sealed class CircularPayloadTool : ITool
    {
        public string Id => "weird";
        public ToolSchema Schema => new(Id, "weird", "{\"type\":\"object\"}");
        public Task<ToolResult> InvokeAsync(ToolCall call, WorldSnapshot s, CancellationToken ct)
        {
            var dict = new Dictionary<string, object>();
            dict["self"] = dict;
            var delta = new SimpleDelta(s.Tick, s.Tick + 1, "weird");
            return Task.FromResult(new ToolResult(delta, dict));
        }
    }

    private sealed class EmptyMockModel : IModel
    {
        public Task<ModelOutput> CompleteAsync(ModelInput input, CancellationToken ct) =>
            Task.FromResult(new ModelOutput("{}"));
    }

    private static (CapabilityRegistry tools, PolicyEngine policies) BuildHarness(
        bool allowAll,
        out RecordingTool noop)
    {
        return BuildHarness(call => allowAll, out noop);
    }

    private static (CapabilityRegistry tools, PolicyEngine policies) BuildHarness(
        Func<ToolCall, bool> customApprove,
        out RecordingTool noop)
    {
        noop = new RecordingTool();
        var tb = new CapabilityRegistry();
        tb.Register(noop);
        var pe = new PolicyEngine(new IPolicy[] { new PredicatePolicy(customApprove) });
        return (tb, pe);
    }

    private static ScriptedModel.Turn ResponseWithCalls(string rationale, params (string id, string argsJson)[] calls)
    {
        var arr = string.Join(",", calls.Select(c => $"{{\"id\":\"{c.id}\",\"arguments\":{c.argsJson}}}"));
        return new ScriptedModel.Turn($"{{\"tool_calls\":[{arr}],\"rationale\":\"{rationale}\"}}");
    }

    private static ScriptedModel.Turn ResponseEmpty(string rationale)
        => new($"{{\"tool_calls\":[],\"rationale\":\"{rationale}\"}}");

    private sealed class RecordingTool : ITool
    {
        public int Invocations;
        public string Id => "noop";
        public ToolSchema Schema => new(Id, "noop tool", "{\"type\":\"object\"}");
        public Task<ToolResult> InvokeAsync(ToolCall call, WorldSnapshot s, CancellationToken ct)
        {
            Invocations++;
            var delta = new SimpleDelta(s.Tick, s.Tick + 1, $"noop:invocation#{Invocations}");
            var payload = new { ok = true, n = Invocations };
            return Task.FromResult(new ToolResult(delta, payload));
        }
    }

    private sealed class SimpleDelta : IActionDelta
    {
        private readonly List<string> _log;
        public SimpleDelta(int from, int to, params string[] log) { TickFrom = from; TickTo = to; _log = log.ToList(); }
        public int TickFrom { get; }
        public int TickTo { get; }
        public IReadOnlyList<string> Log => _log;
        public IReadOnlyList<byte>? Signature { get; set; }
    }

    private sealed class PredicatePolicy : IPolicy
    {
        private readonly Func<ToolCall, bool> _approve;
        public PredicatePolicy(Func<ToolCall, bool> approve) { _approve = approve; }
        public bool Approve(ToolCall call, WorldSnapshot s, out string reason)
        {
            if (_approve(call)) { reason = "OK"; return true; }
            reason = "policy: rejected by predicate";
            return false;
        }
    }

    private sealed class ScriptedModel : IModel
    {
        private readonly Queue<Turn> _turns;
        public List<IReadOnlyList<(string role, string content)>> SeenMessages { get; } = new();

        private ScriptedModel(IEnumerable<Turn> turns) { _turns = new Queue<Turn>(turns); }
        public static ScriptedModel Of(params Turn[] turns) => new(turns);

        public Task<ModelOutput> CompleteAsync(ModelInput input, CancellationToken ct)
        {
            SeenMessages.Add(input.Messages);
            if (_turns.Count == 0)
                return Task.FromResult(new ModelOutput("{\"tool_calls\":[],\"rationale\":\"script_exhausted\"}"));
            return Task.FromResult(new ModelOutput(_turns.Dequeue().Text));
        }

        public sealed record Turn(string Text);
    }

    /// <summary>
    /// A model that never answers. It completes only by cancellation, and its two callers both
    /// exist to prove that something else — a deadline, an external token — ends the cycle.
    ///
    /// It used to stall for a fixed span instead, and that made both of those tests a race. The
    /// deadline is armed with <see cref="CancellationTokenSource.CancelAfter(TimeSpan)"/>, whose
    /// timer callback can be delayed arbitrarily under load; on a contended Windows agent it
    /// slipped past a two-second stall, the model returned normally, and the loop recorded
    /// <c>"empty"</c> for a cycle the test asserts is <c>"deadline"</c>. Widening the stall would
    /// only lengthen the odds — an infinite one removes the competing completion outright, so the
    /// cancellation path is the only way out and the assertion can no longer pass or fail on
    /// timer scheduling.
    ///
    /// The returned payload is unreachable and kept only to satisfy the signature.
    /// </summary>
    private sealed class StallingModel : IModel
    {
        public async Task<ModelOutput> CompleteAsync(ModelInput input, CancellationToken ct)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
            return new ModelOutput("{\"tool_calls\":[],\"rationale\":\"unreachable\"}");
        }
    }
}
