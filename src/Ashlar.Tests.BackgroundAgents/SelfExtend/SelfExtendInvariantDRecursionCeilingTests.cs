using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Ashlar.Abstractions;
using Ashlar.BackgroundAgents.Agents;
using Ashlar.BackgroundAgents.Configuration;
using Ashlar.BackgroundAgents.Extending;
using Ashlar.BackgroundAgents.Logging;
using Ashlar.Orchestration.Agents;
using Ashlar.Policies.Dev;
using Xunit;
using Ashlar.Tests.BackgroundAgents.Registry;

namespace Ashlar.Tests.BackgroundAgents.SelfExtend;

/// <summary>
/// SX-AUDIT invariant D — recursion / runaway ceiling for extender-triggered extension.
/// See <c>docs/SELF-EXTEND-AUDIT.md#invariant-d-recursion-runaway-ceiling</c>.
///
/// <para>Per-cycle caps (ReAct iterations, build/test budget) were always there; what was
/// missing was anything ACROSS cycles. <see cref="ExtensionCeiling"/> adds three, enforced by
/// the registry before the runner is invoked: lineage depth (recursion), unattended cycles
/// since a human armed the agent (runaway), and cycles per trailing hour (rate). Every
/// override may only lower a ceiling, and re-arm is an operator surface — an agent cannot
/// re-arm itself, including by re-registering.</para>
/// </summary>
public sealed class SelfExtendInvariantDRecursionCeilingTests
{
    [Fact]
    public void Characterization_per_cycle_react_iteration_cap_is_configured()
    {
        ToolCallingAgent.DefaultMaxIterations.Should().Be(5,
            "single-cycle ReAct bound exists inside ToolCallingAgent.RunCycleAsync");
        ToolCallingAgent.DefaultPerCycleDeadline.Should().Be(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void Characterization_per_cycle_build_test_budget_denies_excess_invocations()
    {
        var budget = new BuildTestBudget(buildBudget: 1, testBudget: 1);
        var snapshot = new WorldSnapshot(0, new Dictionary<string, object?>());
        var build = new ToolCall("dotnet.build", JsonSerializer.SerializeToElement(new { }));
        var test = new ToolCall("dotnet.test", JsonSerializer.SerializeToElement(new { }));

        budget.Approve(build, snapshot, out _).Should().BeTrue();
        budget.Approve(build, snapshot, out var buildReason).Should().BeFalse(buildReason);
        budget.Reset();
        budget.Approve(test, snapshot, out _).Should().BeTrue();
        budget.Approve(test, snapshot, out var testReason).Should().BeFalse(testReason);
    }

    /// <summary>
    /// The default ceiling bounds an Active extender across cycles, and every refusal is
    /// visible: a Warning in the agent log and a Warn observation with the reason. Back-to-back
    /// cycles hit the hourly rate first (4/h), before the unattended count (8).
    /// </summary>
    [Fact]
    public async Task Enforcement_default_ceiling_bounds_cross_cycle_extension_and_refusals_are_observable()
    {
        var runner = new SelfExtendAuditTestSupport.CountingSelfExtendRunner();
        var log = new InMemoryAgentLogStore();
        var observations = new SelfExtendAuditTestSupport.ListObservationStore();
        var registry = SelfExtendAuditTestSupport.CreateRegistry(
            selfExtendRunner: runner,
            modeStore: SelfExtendAuditTestSupport.ActiveModeStore(),
            logStore: log,
            observations: observations);
        var config = SelfExtendAuditTestSupport.ExtenderConfig("bounded-extender", Environment.CurrentDirectory);
        await registry.RegisterAuthoredAsync(
            new GenericAgent(SelfExtendAuditTestSupport.BuildSpec(config), NullLogger<GenericAgent>.Instance),
            config);

        const int cycles = 12;
        for (var i = 0; i < cycles; i++)
            await registry.ExecuteOnceAsync(config.Id);

        runner.CallCount.Should().Be(ExtensionCeiling.Default.MaxCyclesPerHour,
            "back-to-back cycles reach the hourly rate ceiling first; everything past it is refused");

        var refusals = log.GetRecent(config.Id, maxCount: 200)
            .Where(e => e.Message.Contains("Extension refused (invariant D)", StringComparison.Ordinal))
            .ToList();
        refusals.Should().HaveCount(cycles - ExtensionCeiling.Default.MaxCyclesPerHour);
        refusals.Should().OnlyContain(e => e.Level == "Warning");
        refusals.Should().OnlyContain(e => e.Message.Contains("MaxCyclesPerHour", StringComparison.Ordinal));

        observations.All.Where(o => o.facts is not null && o.facts.TryGetValue("stopped_reason", out var r) && r == "extension_ceiling")
            .Should().HaveCount(cycles - ExtensionCeiling.Default.MaxCyclesPerHour,
                "a held extender must be visible to the planner and the digest, not silent");
        registry.GetExtensionLedger(config.Id).UnattendedCycles.Should().Be(ExtensionCeiling.Default.MaxCyclesPerHour,
            "only cycles that reached the runner consume budget");
    }

    /// <summary>Rejection test for invariant D — the one that was skipped as a GAP.</summary>
    [Fact]
    public async Task Rejection_extension_past_configured_recursion_ceiling_is_refused()
    {
        var runner = new SelfExtendAuditTestSupport.CountingSelfExtendRunner();
        var registry = SelfExtendAuditTestSupport.CreateRegistry(
            selfExtendRunner: runner,
            modeStore: SelfExtendAuditTestSupport.ActiveModeStore());
        var config = SelfExtendAuditTestSupport.ExtenderConfig("ceiling-extender", Environment.CurrentDirectory);
        await registry.RegisterAuthoredAsync(
            new GenericAgent(SelfExtendAuditTestSupport.BuildSpec(config), NullLogger<GenericAgent>.Instance),
            config);

        for (var i = 0; i < 20; i++)
            await registry.ExecuteOnceAsync(config.Id);

        runner.CallCount.Should().BeLessThan(20,
            "invariant D requires extension past configured depth/rate ceiling to be REFUSED");
    }

    [Fact]
    public async Task Unattended_ceiling_binds_when_rate_is_not_the_limit()
    {
        var runner = new SelfExtendAuditTestSupport.CountingSelfExtendRunner();
        var registry = SelfExtendAuditTestSupport.CreateRegistry(
            selfExtendRunner: runner,
            modeStore: SelfExtendAuditTestSupport.ActiveModeStore(),
            extensionCeiling: new ExtensionCeiling(MaxLineageDepth: 1, MaxUnattendedCycles: 3, MaxCyclesPerHour: 100));
        var config = SelfExtendAuditTestSupport.ExtenderConfig("unattended-extender", Environment.CurrentDirectory);
        await registry.RegisterAuthoredAsync(
            new GenericAgent(SelfExtendAuditTestSupport.BuildSpec(config), NullLogger<GenericAgent>.Instance),
            config);

        for (var i = 0; i < 10; i++)
            await registry.ExecuteOnceAsync(config.Id);

        runner.CallCount.Should().Be(3, "three unattended cycles, then hold for a human");
    }

    /// <summary>
    /// The heart of the invariant: an agent cannot re-arm itself. Re-registration is something
    /// an agent can do to itself (UpdateAgentConfigTool registers as Authored), so it must not
    /// reset the ledger; only the operator surface does.
    /// </summary>
    [Fact]
    public async Task Rearm_is_an_operator_act_and_reregistration_does_not_reset_the_ledger()
    {
        var runner = new SelfExtendAuditTestSupport.CountingSelfExtendRunner();
        var registry = SelfExtendAuditTestSupport.CreateRegistry(
            selfExtendRunner: runner,
            modeStore: SelfExtendAuditTestSupport.ActiveModeStore(),
            extensionCeiling: new ExtensionCeiling(MaxLineageDepth: 1, MaxUnattendedCycles: 2, MaxCyclesPerHour: 100));
        var config = SelfExtendAuditTestSupport.ExtenderConfig("rearm-extender", Environment.CurrentDirectory);
        var agent = new GenericAgent(SelfExtendAuditTestSupport.BuildSpec(config), NullLogger<GenericAgent>.Instance);
        await registry.RegisterAuthoredAsync(agent, config);

        for (var i = 0; i < 4; i++)
            await registry.ExecuteOnceAsync(config.Id);
        runner.CallCount.Should().Be(2);

        // The agent re-registers itself (what UpdateAgentConfigTool does) — no relief.
        await registry.RegisterAsync(agent, config, AgentRegistrationOrigin.Authored);
        await registry.ExecuteOnceAsync(config.Id);
        runner.CallCount.Should().Be(2, "re-registration must not re-arm; agents can re-register themselves");

        // A human re-arms — the next cycle runs.
        registry.RearmExtension(config.Id).Should().Be(2, "the cleared unattended count is reported");
        await registry.ExecuteOnceAsync(config.Id);
        runner.CallCount.Should().Be(3);
    }

    /// <summary>
    /// Recursion proper: extension that spawns extenders that extend. Roots (depth 0) and their
    /// children (depth 1) may run; a grandchild (depth 2) may not under the default ceiling.
    /// </summary>
    [Fact]
    public async Task Lineage_depth_ceiling_refuses_machine_spawned_grandchildren()
    {
        var runner = new SelfExtendAuditTestSupport.CountingSelfExtendRunner();
        var registry = SelfExtendAuditTestSupport.CreateRegistry(
            selfExtendRunner: runner,
            modeStore: SelfExtendAuditTestSupport.ActiveModeStore(),
            extensionCeiling: new ExtensionCeiling(MaxLineageDepth: 1, MaxUnattendedCycles: 100, MaxCyclesPerHour: 100));

        var root = SelfExtendAuditTestSupport.ExtenderConfig("root", Environment.CurrentDirectory);
        var child = SelfExtendAuditTestSupport.ExtenderConfig("child", Environment.CurrentDirectory);
        child.ParentId = root.Id;
        var grandchild = SelfExtendAuditTestSupport.ExtenderConfig("grandchild", Environment.CurrentDirectory);
        grandchild.ParentId = child.Id;

        foreach (var config in new[] { root, child, grandchild })
        {
            await registry.RegisterAuthoredAsync(
                new GenericAgent(SelfExtendAuditTestSupport.BuildSpec(config), NullLogger<GenericAgent>.Instance),
                config);
        }

        registry.LineageDepth(root).Should().Be(0);
        registry.LineageDepth(child).Should().Be(1);
        registry.LineageDepth(grandchild).Should().Be(2);

        await registry.ExecuteOnceAsync(root.Id);
        await registry.ExecuteOnceAsync(child.Id);
        runner.CallCount.Should().Be(2, "a root and its direct child may extend");

        await registry.ExecuteOnceAsync(grandchild.Id);
        runner.CallCount.Should().Be(2, "a grandchild is refused: MaxLineageDepth 1");
    }

    [Theory]
    [InlineData("caller-child")]
    [InlineData("caller-ancestor")]
    [InlineData("instance-child")]
    [InlineData("instance-ancestor")]
    [InlineData("instance-id")]
    [InlineData("reregister-child")]
    [InlineData("reregister-ancestor")]
    public async Task Lineage_cannot_be_laundered_by_mutation_or_reregistration(string attack)
    {
        var runner = new SelfExtendAuditTestSupport.CountingSelfExtendRunner();
        var registry = SelfExtendAuditTestSupport.CreateRegistry(
            selfExtendRunner: runner, modeStore: SelfExtendAuditTestSupport.ActiveModeStore(),
            extensionCeiling: new ExtensionCeiling(1, 100, 100));
        var root = SelfExtendAuditTestSupport.ExtenderConfig("root", Environment.CurrentDirectory);
        var child = SelfExtendAuditTestSupport.ExtenderConfig("child", Environment.CurrentDirectory);
        child.ParentId = root.Id;
        var leaf = SelfExtendAuditTestSupport.ExtenderConfig("leaf", Environment.CurrentDirectory);
        leaf.ParentId = child.Id;
        foreach (var config in new[] { root, child, leaf })
            await registry.RegisterAuthoredAsync(
                new GenericAgent(SelfExtendAuditTestSupport.BuildSpec(config), NullLogger<GenericAgent>.Instance), config);

        switch (attack)
        {
            case "caller-child": leaf.ParentId = null; break;
            case "caller-ancestor": child.ParentId = null; break;
            case "instance-child": registry.GetAgent("leaf")!.Config.ParentId = null; break;
            case "instance-ancestor": registry.GetAgent("child")!.Config.ParentId = null; break;
            case "instance-id": registry.GetAgent("leaf")!.Config.Id = "root"; break;
            default:
                var replacement = attack == "reregister-child" ? leaf : child;
                replacement.ParentId = null;
                await registry.RegisterAuthoredAsync(
                    new GenericAgent(SelfExtendAuditTestSupport.BuildSpec(replacement), NullLogger<GenericAgent>.Instance), replacement);
                break;
        }

        await registry.ExecuteOnceAsync("leaf");
        runner.CallCount.Should().Be(0, "admitted ancestry cannot be erased by an exposed config or re-registration");
        registry.GetExtensionLedger("leaf").UnattendedCycles.Should().Be(0);
    }

    [Fact]
    public async Task Later_parent_registration_and_deepening_propagate_through_historical_edges()
    {
        var runner = new SelfExtendAuditTestSupport.CountingSelfExtendRunner();
        var registry = SelfExtendAuditTestSupport.CreateRegistry(
            selfExtendRunner: runner, modeStore: SelfExtendAuditTestSupport.ActiveModeStore(),
            extensionCeiling: new ExtensionCeiling(1, 100, 100));
        var leaf = SelfExtendAuditTestSupport.ExtenderConfig("leaf", Environment.CurrentDirectory);
        leaf.ParentId = "late-parent";
        await registry.RegisterAuthoredAsync(
            new GenericAgent(SelfExtendAuditTestSupport.BuildSpec(leaf), NullLogger<GenericAgent>.Instance), leaf);
        registry.LineageDepth(leaf).Should().Be(1);
        leaf.ParentId = null;
        await registry.RegisterAuthoredAsync(
            new GenericAgent(SelfExtendAuditTestSupport.BuildSpec(leaf), NullLogger<GenericAgent>.Instance), leaf);
        var parent = SelfExtendAuditTestSupport.ExtenderConfig("late-parent", Environment.CurrentDirectory);
        parent.ParentId = "unknown-root";
        await registry.RegisterAuthoredAsync(
            new GenericAgent(SelfExtendAuditTestSupport.BuildSpec(parent), NullLogger<GenericAgent>.Instance), parent);

        registry.LineageDepth(leaf).Should().Be(2);
        await registry.ExecuteOnceAsync("leaf");
        runner.CallCount.Should().Be(0);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(int.MaxValue)]
    public async Task Cyclic_lineage_fails_closed_even_with_a_large_configured_ceiling(int lineageCeiling)
    {
        var runner = new SelfExtendAuditTestSupport.CountingSelfExtendRunner();
        var registry = SelfExtendAuditTestSupport.CreateRegistry(
            selfExtendRunner: runner, modeStore: SelfExtendAuditTestSupport.ActiveModeStore(),
            extensionCeiling: new ExtensionCeiling(lineageCeiling, 100, 100));
        foreach (var (id, parent) in new[] { ("a", "b"), ("b", "a") })
        {
            var config = SelfExtendAuditTestSupport.ExtenderConfig(id, Environment.CurrentDirectory);
            config.ParentId = parent;
            await registry.RegisterAuthoredAsync(
                new GenericAgent(SelfExtendAuditTestSupport.BuildSpec(config), NullLogger<GenericAgent>.Instance), config);
        }
        await registry.ExecuteOnceAsync("a");
        await registry.ExecuteOnceAsync("b");
        runner.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task Admitted_parameter_ceiling_survives_mutation_and_reregistration()
    {
        var runner = new SelfExtendAuditTestSupport.CountingSelfExtendRunner();
        var registry = SelfExtendAuditTestSupport.CreateRegistry(
            selfExtendRunner: runner, modeStore: SelfExtendAuditTestSupport.ActiveModeStore(),
            extensionCeiling: new ExtensionCeiling(1, 100, 100));
        var config = SelfExtendAuditTestSupport.ExtenderConfig("narrow", Environment.CurrentDirectory);
        config.Parameters![ExtensionCeiling.UnattendedCyclesParameter] = 0;
        var agent = new GenericAgent(SelfExtendAuditTestSupport.BuildSpec(config), NullLogger<GenericAgent>.Instance);
        await registry.RegisterAuthoredAsync(agent, config);
        config.Parameters.Remove(ExtensionCeiling.UnattendedCyclesParameter);
        registry.GetAgent(config.Id)!.Config.Parameters!.Remove(ExtensionCeiling.UnattendedCyclesParameter);
        await registry.RegisterAuthoredAsync(agent, config);
        await registry.ExecuteOnceAsync(config.Id);
        runner.CallCount.Should().Be(0);
    }

    [Fact]
    public void Environment_may_only_lower_the_ceiling_never_raise_it()
    {
        var env = new Dictionary<string, string?>
        {
            [ExtensionCeiling.LineageDepthEnvVar] = "0",       // stricter: only roots
            [ExtensionCeiling.UnattendedCyclesEnvVar] = "999", // looser: ignored
            [ExtensionCeiling.CyclesPerHourEnvVar] = "garbage",
        };

        var resolved = ExtensionCeiling.Resolve(k => env.TryGetValue(k, out var v) ? v : null);

        resolved.MaxLineageDepth.Should().Be(0);
        resolved.MaxUnattendedCycles.Should().Be(ExtensionCeiling.Default.MaxUnattendedCycles,
            "a deployment may be stricter than the defaults, never looser");
        resolved.MaxCyclesPerHour.Should().Be(ExtensionCeiling.Default.MaxCyclesPerHour);
        ExtensionCeiling.Resolve(_ => null).Should().Be(ExtensionCeiling.Default);
    }

    [Fact]
    public void Agent_parameters_may_only_narrow_the_ceiling_whatever_their_json_shape()
    {
        var config = SelfExtendAuditTestSupport.ExtenderConfig("narrowing", Environment.CurrentDirectory);
        config.Parameters![ExtensionCeiling.UnattendedCyclesParameter] = 2;                                  // int, lower
        config.Parameters[ExtensionCeiling.CyclesPerHourParameter] = JsonSerializer.SerializeToElement(50);   // JSON number, higher
        config.Parameters[ExtensionCeiling.LineageDepthParameter] = "0";                                     // string, lower

        var narrowed = ExtensionCeiling.Default.NarrowedBy(config);

        narrowed.MaxUnattendedCycles.Should().Be(2);
        narrowed.MaxCyclesPerHour.Should().Be(ExtensionCeiling.Default.MaxCyclesPerHour,
            "an agent asking for MORE authority than the deployment grants is ignored (I-1)");
        narrowed.MaxLineageDepth.Should().Be(0);
    }

    [Fact]
    public void Ledger_rate_window_slides_and_rearm_does_not_forgive_the_rate()
    {
        var now = new DateTimeOffset(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);
        var ledger = new ExtensionLedger(() => now);
        var ceiling = new ExtensionCeiling(MaxLineageDepth: 1, MaxUnattendedCycles: 100, MaxCyclesPerHour: 2);

        ledger.Refusal(ceiling, lineageDepth: 0).Should().BeNull();
        ledger.RecordCycle();
        ledger.RecordCycle();
        ledger.Refusal(ceiling, lineageDepth: 0).Should().Contain("MaxCyclesPerHour");
        ledger.CyclesInWindow.Should().Be(2);

        ledger.Rearm();
        ledger.Refusal(ceiling, lineageDepth: 0).Should().Contain("MaxCyclesPerHour",
            "a human re-arm clears the unattended count, not the last hour's cycles");

        now = now.AddMinutes(61);
        ledger.CyclesInWindow.Should().Be(0);
        ledger.Refusal(ceiling, lineageDepth: 0).Should().BeNull("the trailing-hour window has slid past both cycles");
        ledger.Refusal(ceiling, lineageDepth: 2).Should().Contain("MaxLineageDepth", "depth is checked regardless of rate");
    }

    [Fact]
    public void The_ceiling_holds_when_cycles_race_TryBeginCycle_decides_and_reserves_atomically()
    {
        // The round-3 lab finding: Refusal() then RecordCycle() is check-then-act, and the
        // scheduler may overlap cycles for one agent (timer overlap), so every racing cycle read
        // the same unspent budget and passed — 16 cycles handed to the runner under a ceiling of
        // 4. A ceiling that only holds when nothing races is not a ceiling.
        var now = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
        var ledger = new ExtensionLedger(() => now);
        var ceiling = new ExtensionCeiling(MaxLineageDepth: 1, MaxUnattendedCycles: 1000, MaxCyclesPerHour: 4);

        var admitted = 0;
        Parallel.For(0, 64, _ =>
        {
            if (ledger.TryBeginCycle(ceiling, lineageDepth: 0) is null)
                Interlocked.Increment(ref admitted);
        });

        admitted.Should().Be(4, "the hourly ceiling must hold exactly, however many cycles race");
        ledger.CyclesInWindow.Should().Be(4, "a refused cycle consumes no budget");
        ledger.TryBeginCycle(ceiling, lineageDepth: 0).Should().Contain("MaxCyclesPerHour");
    }

    [Fact]
    public void TryBeginCycle_reserves_only_on_admission_and_refuses_depth_without_spending()
    {
        var now = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
        var ledger = new ExtensionLedger(() => now);
        var ceiling = new ExtensionCeiling(MaxLineageDepth: 1, MaxUnattendedCycles: 2, MaxCyclesPerHour: 10);

        ledger.TryBeginCycle(ceiling, lineageDepth: 5).Should().Contain("MaxLineageDepth");
        ledger.CyclesInWindow.Should().Be(0, "a depth refusal must not consume the rate budget");
        ledger.UnattendedCycles.Should().Be(0);

        ledger.TryBeginCycle(ceiling, lineageDepth: 0).Should().BeNull();
        ledger.TryBeginCycle(ceiling, lineageDepth: 0).Should().BeNull();
        ledger.UnattendedCycles.Should().Be(2);
        ledger.TryBeginCycle(ceiling, lineageDepth: 0).Should().Contain("MaxUnattendedCycles",
            "the unattended ceiling is enforced by the same atomic reserve");
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(3, 0, 0)]
    [InlineData(0, 2, 0)]
    [InlineData(0, 0, 2)]
    [InlineData(1, 2, 2)]
    public void The_observing_and_the_reserving_form_refuse_on_exactly_the_same_policy(
        int lineageDepth, int priorUnattended, int priorInWindow)
    {
        // The two entry points must never drift apart. Production calls only the reserving form,
        // so a ceiling tightened in the observing copy alone would leave the suite green and
        // enforce nothing — the reason the policy now lives in one place. This drives both across
        // the same states and demands the same verdict.
        var now = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
        var ceiling = new ExtensionCeiling(MaxLineageDepth: 1, MaxUnattendedCycles: 2, MaxCyclesPerHour: 2);

        var observing = Seeded(now, priorUnattended, priorInWindow);
        var reserving = Seeded(now, priorUnattended, priorInWindow);

        var observed = observing.Refusal(ceiling, lineageDepth);
        var reserved = reserving.TryBeginCycle(ceiling, lineageDepth);

        reserved.Should().Be(observed, "one policy, two entry points");

        if (observed is null)
        {
            observing.CyclesInWindow.Should().Be(priorInWindow, "the observing form answers without spending");
            observing.UnattendedCycles.Should().Be(priorUnattended);
            reserving.CyclesInWindow.Should().Be(priorInWindow + 1, "the reserving form spends exactly one cycle");
            reserving.UnattendedCycles.Should().Be(priorUnattended + 1);
        }
        else
        {
            reserving.CyclesInWindow.Should().Be(priorInWindow, "a refusal spends nothing on either form");
            reserving.UnattendedCycles.Should().Be(priorUnattended);
        }
    }

    /// <summary>
    /// Puts a ledger into a given (unattended, in-window) state. RecordCycle moves both counters
    /// at once, so the two are separated the only ways real time and real operators separate
    /// them: cycles recorded outside the rate window still count as unattended but prune out of
    /// the rate, and a human re-arm clears the unattended count while leaving the rate standing.
    /// </summary>
    private static ExtensionLedger Seeded(DateTimeOffset now, int unattended, int inWindow)
    {
        var clock = now - TimeSpan.FromHours(2);
        var ledger = new ExtensionLedger(() => clock);

        for (var i = 0; i < Math.Max(0, unattended - inWindow); i++)
            ledger.RecordCycle();

        clock = now;
        for (var i = 0; i < inWindow; i++)
            ledger.RecordCycle();

        if (unattended < inWindow)
            ledger.Rearm();

        ledger.CyclesInWindow.Should().Be(inWindow, "seeding precondition");
        ledger.UnattendedCycles.Should().Be(unattended, "seeding precondition");
        return ledger;
    }
}
