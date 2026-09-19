using FluentAssertions;
using Ashlar.Certification.Contracts;
using Ashlar.Core.Application.Autonomy;
using Ashlar.Core.Application.Certification.Models;
using Ashlar.Core.Domain.Bricks;
using Ashlar.Core.Domain.Execution;
using Ashlar.Infrastructure.Certification;
using Ashlar.Infrastructure.Certification.HotSwap;
using NSec.Cryptography;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>
/// A rollback is not an absorption (autonomy spec R5.2, cross-referenced from R5.5, R6.1
/// and R6.2). The pacing and authority gates — pause, cadence floor, in-flight watch
/// window, lineage demotion, recursion ceiling — bound how fast and on whose authority the
/// runtime takes on CHANGE; a containment rollback replays content the host already
/// committed and retained, so those gates never refuse it, while revocation and
/// verify-at-load still do. Each fact here reproduces one way the old shared method let a
/// gate refuse the very rollback the breach demanded, or one defect the exemption made
/// reachable (baseline rotation, cadence refresh, positional subject, silent exhaustion).
///
/// <para>The precondition that makes the defects reproducible: TWO consecutive autonomous
/// generations, so the retained requests themselves carry <c>Autonomous</c>. No earlier
/// suite constructs a host with both a non-null cadence floor and a watch.</para>
/// </summary>
[Trait("Category", "Certification")]
[Collection("hot-swap-host")]
public sealed class RollbackGateExemptionTests : IDisposable
{
    /// <summary>
    /// A sibling suite asserts globally that no <c>BrickGeneration_*</c> load context
    /// survives; drive collection after each test here so this suite's disposed hosts
    /// cannot bleed into that assertion on GC timing.
    /// </summary>
    public void Dispose()
    {
        for (var i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    }

    private const string HmacKey = "rollback-gate-exemption-test-hmac";
    private const string ProbeBrickId = "hot-swap-probe";
    private const string SecondBrickId = "hot-swap-probe-b";
    private static readonly TimeSpan Floor = TimeSpan.FromSeconds(300);

    private static WatchThresholds DefaultWatch => new() { MinInvocations = 2, MaxErrorRateDelta = 0.2 };

    private const string HealthyTemplate = """
using Ashlar.Core.Domain.Bricks;
using Ashlar.Core.Domain.Execution;

namespace Ashlar.Tests.HotSwapProbe;

public sealed class HotSwapProbeBrick : DomainBrick
{
    public HotSwapProbeBrick()
    {
        Id = "{ID}";
        Name = "Hot Swap Probe";
        Interface = new BrickInterface
        {
            Inputs = [],
            Outputs = [new BrickOutputDefinition("marker", "string", "marker")]
        };
    }

    public override Task<BrickOutput> ExecuteAsync(
        BrickInput input,
        ImplementationType implementation,
        IExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var output = new BrickOutput { Summary = "{MARKER}" };
        output.Set("marker", "{MARKER}");
        return Task.FromResult(output);
    }
}
""";

    private const string FaultingTemplate = """
using Ashlar.Core.Domain.Bricks;
using Ashlar.Core.Domain.Execution;

namespace Ashlar.Tests.HotSwapProbe;

public sealed class HotSwapProbeBrick : DomainBrick
{
    public HotSwapProbeBrick()
    {
        Id = "{ID}";
        Name = "Hot Swap Probe";
        Interface = new BrickInterface
        {
            Inputs = [],
            Outputs = [new BrickOutputDefinition("marker", "string", "marker")]
        };
    }

    public override Task<BrickOutput> ExecuteAsync(
        BrickInput input,
        ImplementationType implementation,
        IExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException("regressed post-swap");
    }
}
""";

    /// <summary>
    /// Does real work (a 10ms delay) and regresses on demand. Real work is what a
    /// fast-throwing breacher's stats would make look pathologically slow if they became
    /// the baseline; regressing on demand is how the restored generation proves it is still
    /// judged against SOMETHING.
    /// </summary>
    private const string WorkingTemplate = """
using Ashlar.Core.Domain.Bricks;
using Ashlar.Core.Domain.Execution;

namespace Ashlar.Tests.HotSwapProbe;

public sealed class HotSwapProbeBrick : DomainBrick
{
    public HotSwapProbeBrick()
    {
        Id = "hot-swap-probe";
        Name = "Hot Swap Probe";
        Interface = new BrickInterface
        {
            Inputs = [],
            Outputs = [new BrickOutputDefinition("marker", "string", "marker")]
        };
    }

    public override async Task<BrickOutput> ExecuteAsync(
        BrickInput input,
        ImplementationType implementation,
        IExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        await Task.Delay(10, cancellationToken);
        if (input.Get<string>("mode", "ok") == "fail")
            throw new InvalidOperationException("regressed on demand");
        var output = new BrickOutput { Summary = "{MARKER}" };
        output.Set("marker", "{MARKER}");
        return output;
    }
}
""";

    /// <summary>
    /// Breaches the contract leg on its first invocation (an undeclared output) and, in
    /// doing so, hands the test the one window a competing forward swap can land in.
    /// <c>Count</c> on the declared outputs is the first thing the host's contract leg reads
    /// — AFTER the host has checked that this generation still owns the watch window and
    /// BEFORE the quarantine resolves its subject. The IL import fence denies the brick
    /// <c>System.IO</c>, reflection and every <c>IExecutionContext</c> member, so the
    /// signals travel through <c>BrickInput</c>, which the test builds.
    /// </summary>
    private const string HookedSneakySource = """
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using Ashlar.Core.Domain.Bricks;
using Ashlar.Core.Domain.Execution;

namespace Ashlar.Tests.HotSwapProbe;

public sealed class HotSwapProbeBrick : DomainBrick
{
    public static SemaphoreSlim Ready;
    public static SemaphoreSlim Go;

    public HotSwapProbeBrick()
    {
        Id = "hot-swap-probe";
        Name = "Hot Swap Probe";
        Interface = new BrickInterface
        {
            Inputs = [],
            Outputs = new HookedOutputs()
        };
    }

    public override async Task<BrickOutput> ExecuteAsync(
        BrickInput input,
        ImplementationType implementation,
        IExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        Ready = input.Get<SemaphoreSlim>("ready", null);
        Go = input.Get<SemaphoreSlim>("go", null);
        // Go asynchronous so the caller holds an incomplete task before the contract leg
        // blocks this continuation on the test's signal. Task.Delay, not Task.Yield: the
        // fence resolves YieldAwaiter (a nested type) to the global namespace and refuses it.
        await Task.Delay(1, cancellationToken);
        var output = new BrickOutput { Summary = "sneaky" };
        output.Set("marker", "sneaky");
        output.Set("exfil", "undeclared payload");
        return output;
    }
}

public sealed class HookedOutputs : IReadOnlyList<BrickOutputDefinition>
{
    private static int _armed = 1;
    private readonly BrickOutputDefinition[] _items = [new BrickOutputDefinition("marker", "string", "marker")];

    public int Count
    {
        get
        {
            if (Interlocked.Exchange(ref _armed, 0) == 1
                && HotSwapProbeBrick.Ready is { } ready
                && HotSwapProbeBrick.Go is { } go)
            {
                ready.Release();
                go.Wait(TimeSpan.FromSeconds(30));
            }

            return _items.Length;
        }
    }

    public BrickOutputDefinition this[int index] => _items[index];

    public IEnumerator<BrickOutputDefinition> GetEnumerator() =>
        ((IEnumerable<BrickOutputDefinition>)_items).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
""";

    // --- the cadence floor ------------------------------------------------------------

    [Fact]
    public async Task A_watch_breach_inside_the_cadence_floor_still_rolls_back()
    {
        var clock = new MutableClock(DateTimeOffset.UnixEpoch);
        var sink = new RecordingSink();
        using var host = CreateHost(sink, watch: DefaultWatch, cadenceFloor: Floor, clock: clock);

        (await host.SwapAsync(new[] { AutonomousRequest(Healthy("v1"), "lineage-a") })).Swapped.Should().BeTrue();
        await Warm(host, 3);
        clock.Advance(Floor + TimeSpan.FromSeconds(1));
        (await host.SwapAsync(new[] { AutonomousRequest(Faulting(), "lineage-a") })).Swapped.Should().BeTrue();

        // No time passes: the breach and the rollback it demands land INSIDE the floor that
        // the breacher's own absorption started.
        await Breach(host, 2);

        sink.Snapshot().Should().Contain(e => e.Outcome == BrickSwapProvenanceOutcomes.RollbackCommitted,
            "the floor bounds absorption; a rollback replays content the host already committed");
        sink.Snapshot().Should().NotContain(e => e.FailureCode == "cadence-floor");
        sink.Snapshot().Should().NotContain(e => e.Outcome == BrickSwapProvenanceOutcomes.RollbackExhausted);
        (await Execute(host)).Get<string>("marker").Should().Be("v1", "the restored generation is serving");
    }

    [Fact]
    public async Task A_forward_swap_immediately_after_a_rollback_is_still_paced()
    {
        var clock = new MutableClock(DateTimeOffset.UnixEpoch);
        var sink = new RecordingSink();
        using var host = CreateHost(sink, watch: DefaultWatch, cadenceFloor: Floor, clock: clock);

        (await host.SwapAsync(new[] { AutonomousRequest(Healthy("v1"), "lineage-a") })).Swapped.Should().BeTrue();
        await Warm(host, 3);
        clock.Advance(Floor + TimeSpan.FromSeconds(1)); // t = 301s: the last ABSORPTION
        (await host.SwapAsync(new[] { AutonomousRequest(Faulting(), "lineage-a") })).Swapped.Should().BeTrue();
        clock.Advance(TimeSpan.FromSeconds(100)); // t = 401s: the breach and its rollback
        await Breach(host, 2);
        sink.Snapshot().Should().Contain(e => e.Outcome == BrickSwapProvenanceOutcomes.RollbackCommitted);

        // Still paced: 100s since the last absorption is inside the floor.
        var tooSoon = await host.SwapAsync(new[] { AutonomousRequest(Healthy("v3"), "lineage-b") });
        tooSoon.Refusals.Should().ContainSingle().Which.FailureCode.Should().Be("cadence-floor");

        // Paced from the last ABSORPTION (t = 301s), not from the rollback (t = 401s): at
        // t = 602s the floor has elapsed even though only 201s have passed since the restore.
        clock.Advance(TimeSpan.FromSeconds(201));
        var landed = await host.SwapAsync(new[] { AutonomousRequest(Healthy("v3"), "lineage-b") });
        landed.Swapped.Should().BeTrue(
            "a rollback must not refresh the cadence clock: that adds a full floor to the delay the breach already cost");
    }

    // --- lineage demotion, the pause, the recursion ceiling ------------------------------

    [Fact]
    public async Task A_breach_that_demotes_its_own_lineage_still_rolls_back()
    {
        var authority = new SpyLineageAuthority(new InMemoryLineageAuthority(threshold: 2));
        var sink = new RecordingSink();
        using var host = CreateHost(sink, lineage: authority, watch: DefaultWatch);
        authority.Host = host;

        // Two bricks sharing one lineage key, twice over.
        (await host.SwapAsync(new[]
        {
            AutonomousRequest(Healthy("v1"), "lineage-shared"),
            AutonomousRequest(Healthy("v1", SecondBrickId), "lineage-shared", SecondBrickId),
        })).Swapped.Should().BeTrue();
        await Warm(host, 3);
        (await host.SwapAsync(new[]
        {
            AutonomousRequest(Faulting(), "lineage-shared"),
            AutonomousRequest(Healthy("v2", SecondBrickId), "lineage-shared", SecondBrickId),
        })).Swapped.Should().BeTrue();

        // The operator demotes the lineage after the commit (R7.2: one operation). The
        // rollback that follows undoes that lineage's LAST absorption; it is not its next one.
        authority.Demote("lineage-shared", "operator decision");

        await Breach(host, 2);

        sink.Snapshot().Should().Contain(e => e.Outcome == BrickSwapProvenanceOutcomes.RollbackCommitted,
            "demotion bounds what the lineage may absorb next, never the rollback whose evidence produced it");
        sink.Snapshot().Should().NotContain(e => e.FailureCode == "lineage-demoted");
        (await Execute(host)).Get<string>("marker").Should().Be("v1");
        (await Execute(host, SecondBrickId)).Get<string>("marker").Should().Be("v1");

        authority.Rollbacks.Should().ContainSingle(
                "one breach of one lineage is one piece of evidence, however many bricks share the key")
            .Which.Should().Be(("lineage-shared", (int?)3),
                "the evidence is recorded after the rollback landed (generation 3 serving), never before "
                + "it could refuse itself");
    }

    [Fact]
    public async Task A_paused_loop_still_rolls_back_and_says_so()
    {
        var pause = new LoopPauseControl();
        var sink = new RecordingSink();
        using var host = CreateHost(sink, watch: DefaultWatch, pause: pause);

        (await host.SwapAsync(new[] { AutonomousRequest(Healthy("v1"), "lineage-a") })).Swapped.Should().BeTrue();
        await Warm(host, 3);
        (await host.SwapAsync(new[] { AutonomousRequest(Faulting(), "lineage-a") })).Swapped.Should().BeTrue();

        pause.Pause("operator investigating");

        await Breach(host, 2);

        var committed = sink.Snapshot().Should()
            .ContainSingle(e => e.Outcome == BrickSwapProvenanceOutcomes.RollbackCommitted).Subject;
        committed.Reason.Should().Contain("paused").And.Contain("operator investigating",
            "the provenance says the containment landed under a pause rather than pretending the pause was not there");
        sink.Snapshot().Should().NotContain(e => e.FailureCode == "loop-paused");
        pause.IsPaused.Should().BeTrue("containment does not resume the loop");
        (await Execute(host)).Get<string>("marker").Should().Be("v1");

        // And the pause still bounds absorption.
        (await host.SwapAsync(new[] { AutonomousRequest(Healthy("v3"), "lineage-b") }))
            .Refusals.Should().ContainSingle().Which.FailureCode.Should().Be("loop-paused");
    }

    [Fact]
    public async Task A_lowered_depth_ceiling_does_not_strand_a_retained_generation()
    {
        var depthTwo = GenerationLineage.Child(GenerationLineage.Child(GenerationLineage.HumanAuthored, "sig-a"), "sig-b");
        var sink = new RecordingSink();
        using var host = CreateHost(sink, watch: DefaultWatch);

        (await host.SwapAsync(new[] { AutonomousRequest(Healthy("v1"), "lineage-a", lineage: depthTwo) })).Swapped.Should().BeTrue(
            "depth 2 is admissible under the default ceiling");
        await Warm(host, 3);
        (await host.SwapAsync(new[] { AutonomousRequest(Faulting(), "lineage-a", lineage: depthTwo) })).Swapped.Should().BeTrue();

        var previous = Environment.GetEnvironmentVariable(RecursionDiscipline.CeilingEnvVar);
        try
        {
            // The operator tightens the ceiling mid-process. "1", not "0": depth-1 lineages in
            // concurrently running suites keep admitting, so this window cannot flip their verdicts.
            Environment.SetEnvironmentVariable(RecursionDiscipline.CeilingEnvVar, "1");

            await Breach(host, 2);

            sink.Snapshot().Should().Contain(e => e.Outcome == BrickSwapProvenanceOutcomes.RollbackCommitted,
                "ResolveCeiling re-reads the environment on every call; a retained generation that passed once "
                + "must not be stranded by a stricter reading");
            sink.Snapshot().Should().NotContain(e => e.FailureCode == "recursion-refused");
            (await Execute(host)).Get<string>("marker").Should().Be("v1");

            // The ceiling really did lower: the same depth is refused as a NEW absorption.
            (await host.SwapAsync(new[] { AutonomousRequest(Healthy("v3"), "lineage-b", lineage: depthTwo) }))
                .Refusals.Should().ContainSingle().Which.FailureCode.Should().Be("recursion-refused");
        }
        finally
        {
            Environment.SetEnvironmentVariable(RecursionDiscipline.CeilingEnvVar, previous);
        }
    }

    // --- what the exemption makes reachable ---------------------------------------------

    [Fact]
    public async Task A_rollback_does_not_inherit_the_breaching_generations_baseline()
    {
        var revocations = new InMemoryCertificateRevocationList();
        var pause = new LoopPauseControl();
        var sink = new RecordingSink();
        using var host = CreateHost(sink, revocations, pause: pause,
            watch: new WatchThresholds { MinInvocations = 2, MaxErrorRateDelta = 0.2, MaxLatencyFactor = 10.0 });

        var v1 = AutonomousRequest(Working("v1"), "lineage-a");
        (await host.SwapAsync(new[] { v1 })).Swapped.Should().BeTrue();
        await Warm(host, 3);
        (await host.SwapAsync(new[] { AutonomousRequest(Faulting(), "lineage-a") })).Swapped.Should().BeTrue();

        // The breacher faults in microseconds. As a baseline, its stats would make any real
        // work look hundreds of times slower and its error rate near 1.0 would blind the error leg.
        await Breach(host, 2);
        sink.Snapshot().Should().Contain(e => e.Outcome == BrickSwapProvenanceOutcomes.RollbackCommitted);
        host.CurrentGenerationId.Should().Be(3);

        // The restored generation does real work through a full window and is NOT quarantined.
        for (var i = 0; i < 3; i++)
            (await Execute(host)).Get<string>("marker").Should().Be("v1");
        sink.Snapshot().Count(e => e.Outcome == BrickSwapProvenanceOutcomes.WatchBreachQuarantined).Should().Be(1,
            "the restored generation is judged against the pre-breach baseline, not against the breacher");
        revocations.IsRevoked(v1.Record.ContentHash!).Should().BeFalse();

        // The comparand is the pre-breach baseline, not nothing: a real regression of the
        // restored generation is still caught against it...
        await Breach(host, 2, mode: "fail");
        var quarantines = sink.Snapshot().Where(e => e.Outcome == BrickSwapProvenanceOutcomes.WatchBreachQuarantined).ToList();
        quarantines.Should().HaveCount(2, "a nulled baseline would switch the relative legs off for the restored generation");
        quarantines[1].Generation.Should().Be(3);
        quarantines[1].Reason.Should().Contain("error rate");

        // ...and it quarantines THAT content — the restore replayed generation 1's requests — with
        // nothing unrevoked left to restore, so the loop stops loudly instead of replaying it again.
        revocations.IsRevoked(v1.Record.ContentHash!).Should().BeTrue(
            "a restore is not retained under its own id; its breach resolves to the retained origin it replayed");
        sink.Snapshot().Should().ContainSingle(e => e.Outcome == BrickSwapProvenanceOutcomes.RollbackExhausted)
            .Which.Generation.Should().Be(3);
        pause.IsPaused.Should().BeTrue();
        pause.PausedReason.Should().Contain("generation 3");
        host.CurrentGenerationId.Should().Be(3, "no retry replayed the quarantined content");
    }

    [Fact]
    public async Task Quarantine_names_the_breaching_generation_even_when_a_swap_lands_first()
    {
        var revocations = new InMemoryCertificateRevocationList();
        var sink = new RecordingSink();
        // Retention 3 so the newcomer's retention does not evict generation 1; the contract
        // leg needs no baseline and no minimum invocation count.
        using var host = CreateHost(sink, revocations, retentionWindow: 3,
            watch: new WatchThresholds { MinInvocations = 99, MaxUndeclaredWrites = 0 });

        var v1 = AutonomousRequest(Healthy("v1"), "lineage-a");
        var breacher = AutonomousRequest(HookedSneakySource, "lineage-b");
        var newcomer = AutonomousRequest(Healthy("v3"), "lineage-c");
        (await host.SwapAsync(new[] { v1 })).Swapped.Should().BeTrue();
        var breacherSwap = await host.SwapAsync(new[] { breacher });
        breacherSwap.Swapped.Should().BeTrue(Describe(breacherSwap));

        using var ready = new SemaphoreSlim(0);
        using var go = new SemaphoreSlim(0);
        var breaching = Execute(host, input: new Dictionary<string, object> { ["ready"] = ready, ["go"] = go });
        (await ready.WaitAsync(TimeSpan.FromSeconds(30))).Should().BeTrue(
            "the breaching invocation must reach the watch's contract leg");

        // Detection has passed the ownership guard for generation 2; the quarantine has not
        // yet resolved its subject. Land generation 3 in exactly that window.
        var forward = host.SwapAsync(new[] { newcomer });
        await sink.WhenRecordedAsync(e => e.Outcome == BrickSwapProvenanceOutcomes.SwapCommitted && e.Generation == 3)
            .WaitAsync(TimeSpan.FromSeconds(60));
        go.Release();

        await breaching;
        (await forward).Swapped.Should().BeTrue();

        revocations.IsRevoked(breacher.Record.ContentHash!).Should().BeTrue("the breacher is named by id");
        revocations.IsRevoked(newcomer.Record.ContentHash!).Should().BeFalse(
            "the newcomer is innocent; a positional subject would have revoked it permanently");
        revocations.IsRevoked(v1.Record.ContentHash!).Should().BeFalse();
        sink.Snapshot().Should().ContainSingle(e => e.Outcome == BrickSwapProvenanceOutcomes.WatchBreachQuarantined)
            .Which.Generation.Should().Be(2);
        sink.Snapshot().Should().Contain(e =>
            e.Outcome == BrickSwapProvenanceOutcomes.RollbackCommitted && e.Reason!.Contains("generation 1"));
        (await Execute(host)).Get<string>("marker").Should().Be("v1",
            "the target is neither the breacher nor the newcomer");
    }

    // --- the loud terminal state ------------------------------------------------------

    [Fact]
    public async Task An_exhausted_rollback_pauses_the_loop_loudly()
    {
        var revocations = new InMemoryCertificateRevocationList();
        var pause = new LoopPauseControl();
        var sink = new RecordingSink();
        using var host = CreateHost(sink, revocations, watch: DefaultWatch, pause: pause, retentionWindow: 1);

        (await host.SwapAsync(new[] { AutonomousRequest(Healthy("v1"), "lineage-a") })).Swapped.Should().BeTrue();
        await Warm(host, 3);
        var breacher = AutonomousRequest(Faulting(), "lineage-a");
        (await host.SwapAsync(new[] { breacher })).Swapped.Should().BeTrue("retention 1: only generation 2 is retained now");

        await Breach(host, 2);

        var exhausted = sink.Snapshot().Should()
            .ContainSingle(e => e.Outcome == BrickSwapProvenanceOutcomes.RollbackExhausted).Subject;
        exhausted.Generation.Should().Be(2);
        exhausted.Reason.Should().Contain("still serving");
        pause.IsPaused.Should().BeTrue("a breach nothing can contain must stop the loop, not wait for the next one");
        pause.PausedReason.Should().Contain("generation 2");
        revocations.IsRevoked(breacher.Record.ContentHash!).Should().BeTrue();
        host.CurrentGenerationId.Should().Be(2,
            "R5.2 mandates rollback, not refusing to serve: the breacher keeps serving, loudly");

        // The latch stays set: the same breach is not re-detected into a revoke/retry loop.
        await Breach(host, 2);
        sink.Snapshot().Should().ContainSingle(e => e.Outcome == BrickSwapProvenanceOutcomes.WatchBreachQuarantined);
        sink.Snapshot().Should().ContainSingle(e => e.Outcome == BrickSwapProvenanceOutcomes.RollbackExhausted);

        AutonomyDigest.Render(sink.Snapshot()).Should().Contain("rollback-exhausted",
            "an outcome the digest cannot print is a silent terminal state");
    }

    [Fact]
    public async Task A_rollback_that_does_not_land_is_exhausted_not_discarded()
    {
        var revocations = new RacingRevocations();
        var pause = new LoopPauseControl();
        var sink = new RecordingSink();
        using var host = CreateHost(sink, revocations, watch: DefaultWatch, pause: pause);

        var v1 = AutonomousRequest(Healthy("v1"), "lineage-a");
        (await host.SwapAsync(new[] { v1 })).Swapped.Should().BeTrue();
        await Warm(host, 3);
        (await host.SwapAsync(new[] { AutonomousRequest(Faulting(), "lineage-a") })).Swapped.Should().BeTrue();

        // An operator single-op revocation (R7.2) races the quarantine: generation 1 reads
        // unrevoked when it is selected as the target and revoked by the time verify-at-load asks.
        revocations.RevokeOnNextQuery(v1.Record.ContentHash!);

        await Breach(host, 2);

        var exhausted = sink.Snapshot().Should()
            .ContainSingle(e => e.Outcome == BrickSwapProvenanceOutcomes.RollbackExhausted).Subject;
        exhausted.Generation.Should().Be(2);
        exhausted.Reason.Should().Contain("generation 1").And.Contain("revoked-hash",
            "the refused swap result is read, not discarded, and its cause is on the record");
        sink.Snapshot().Should().Contain(e => e.Outcome == BrickSwapProvenanceOutcomes.RollbackRefused);
        pause.IsPaused.Should().BeTrue();
        pause.PausedReason.Should().Contain("generation 2");
        host.CurrentGenerationId.Should().Be(2);

        var digest = AutonomyDigest.Render(sink.Snapshot());
        digest.Should().Contain("rollback-refused").And.Contain("rollback-exhausted");
    }

    // --- what is NOT exempted -----------------------------------------------------------

    /// <summary>
    /// Two independent verifiers reached this failure the same way. At the default retention
    /// window of 2: breach -> rollback (a restore is not retained, by design) -> the loop's next
    /// absorption -> RetainCommitted evicts by age, so the revoked breacher keeps its slot and the
    /// known-good origin is evicted. The second breach then finds no unrevoked target, reports
    /// rollback-exhausted, and the faulting generation keeps serving: containment was lost one
    /// absorption after the first breach, at the window every production composition uses.
    /// A quarantined generation now gives up its slot the moment it is revoked.
    /// </summary>
    [Fact]
    public async Task A_second_breach_at_the_default_retention_window_is_still_contained()
    {
        var revocations = new InMemoryCertificateRevocationList();
        var pause = new LoopPauseControl();
        var sink = new RecordingSink();
        using var host = CreateHost(sink, revocations, watch: DefaultWatch, pause: pause); // retentionWindow: 2

        var v1 = AutonomousRequest(Healthy("v1"), "lineage-a");
        (await host.SwapAsync(new[] { v1 })).Swapped.Should().BeTrue();
        await Warm(host, 3);
        (await host.SwapAsync(new[] { AutonomousRequest(Faulting(), "lineage-a") })).Swapped.Should().BeTrue();
        await Breach(host, 2);
        host.CurrentGenerationId.Should().Be(3, "the first breach rolled back to v1");
        await Warm(host, 3);

        // The loop's next absorption, on another lineage so no in-flight window applies. A Working
        // brick with its own marker, not Faulting(): the same faulting source would carry the same
        // content hash, which the first breach revoked, and verify-at-load would refuse it.
        (await host.SwapAsync(new[] { AutonomousRequest(Working("g4"), "lineage-b") })).Swapped.Should().BeTrue();
        await Breach(host, 2, mode: "fail");

        var events = sink.Snapshot();
        events.Count(e => e.Outcome == BrickSwapProvenanceOutcomes.RollbackCommitted)
            .Should().Be(2, "both breaches must be contained");
        events.Should().NotContain(e => e.Outcome == BrickSwapProvenanceOutcomes.RollbackExhausted);
        pause.IsPaused.Should().BeFalse();
        revocations.IsRevoked(v1.Record.ContentHash!).Should().BeFalse("v1 is the known-good origin");
        (await Execute(host)).Get<string>("marker").Should().Be("v1", "the restored generation serves");
    }

    /// <summary>
    /// A watch without a revocation list cannot contain what it detects: the quarantine revokes
    /// nothing, SelectRollbackTarget re-selects the same origin, and the host replays the regressed
    /// content on every breach with no terminal state. Every real composition supplies both; the
    /// half-configured host is refused at construction rather than at its first breach.
    /// </summary>
    [Fact]
    public void A_watch_without_a_revocation_list_is_refused_at_construction()
    {
        var act = () => new CertifiedBrickHotSwapHost(
            new RecordingSink(), logger: null, hmacKey: HmacKey, drainTimeout: TimeSpan.FromSeconds(10),
            revocations: null, retentionWindow: 2, watchThresholds: DefaultWatch,
            lineageAuthority: new InMemoryLineageAuthority());

        act.Should().Throw<ArgumentException>()
            .WithParameterName("revocations")
            .WithMessage("*revocation list*");
    }

    [Fact]
    public async Task A_revoked_retained_generation_is_still_refused_via_rollback()
    {
        var revocations = new InMemoryCertificateRevocationList();
        var sink = new RecordingSink();
        using var host = CreateHost(sink, revocations, watch: DefaultWatch);

        var v1 = AutonomousRequest(Healthy("v1"), "lineage-a");
        (await host.SwapAsync(new[] { v1 })).Swapped.Should().BeTrue();
        await Warm(host, 3);
        (await host.SwapAsync(new[] { AutonomousRequest(Healthy("v2"), "lineage-a") })).Swapped.Should().BeTrue();

        revocations.Revoke(v1.Record.ContentHash!, "operator quarantine");

        var rollback = await host.RollbackToAsync(1);

        rollback.Swapped.Should().BeFalse("quarantine outranks retention on every intent (R5.3 over R5.1)");
        rollback.Refusals.Should().ContainSingle().Which.FailureCode.Should().Be("revoked-hash");
        sink.Snapshot().Should().Contain(e =>
            e.Outcome == BrickSwapProvenanceOutcomes.RollbackRefused && e.Reason!.Contains("revoked-hash"));
        sink.Snapshot().Should().NotContain(e => e.Outcome == BrickSwapProvenanceOutcomes.SwapRefused,
            "a refused rollback is named for what it is, not as a refused absorption");
        (await Execute(host)).Get<string>("marker").Should().Be("v2", "the unrevoked generation keeps serving");
    }

    // --- helpers -------------------------------------------------------------------------

    private static string Healthy(string marker, string brickId = ProbeBrickId) =>
        HealthyTemplate.Replace("{ID}", brickId).Replace("{MARKER}", marker);

    private static string Faulting(string brickId = ProbeBrickId) =>
        FaultingTemplate.Replace("{ID}", brickId);

    private static string Working(string marker) =>
        WorkingTemplate.Replace("{MARKER}", marker);

    private static CertifiedBrickHotSwapHost CreateHost(
        RecordingSink? sink = null,
        ICertificateRevocationList? revocations = null,
        ILineageAuthority? lineage = null,
        WatchThresholds? watch = null,
        LoopPauseControl? pause = null,
        TimeSpan? cadenceFloor = null,
        TimeProvider? clock = null,
        int retentionWindow = 2) =>
        new(sink, logger: null, hmacKey: HmacKey, drainTimeout: TimeSpan.FromSeconds(10),
            revocations: revocations ?? new InMemoryCertificateRevocationList(), retentionWindow: retentionWindow,
            watchThresholds: watch, lineageAuthority: lineage ?? new InMemoryLineageAuthority(),
            pauseControl: pause, cadenceFloor: cadenceFloor, clock: clock);

    private static CertifiedBrickLoadRequest AutonomousRequest(
        string source,
        string lineageKey,
        string brickId = ProbeBrickId,
        GenerationLineage? lineage = null) => new()
    {
        BrickId = brickId,
        SourceCode = source,
        Record = CertifyRecord(brickId, source),
        Autonomous = new AutonomousAdmission
        {
            Tier = ObjectiveTier.Tier0Autonomous,
            Lineage = lineage ?? GenerationLineage.Child(GenerationLineage.HumanAuthored, "sig-parent"),
            LineageKey = lineageKey,
        }
    };

    private static CertificationRecordData CertifyRecord(string brickId, string source)
    {
        var (privateKey, publicKey) = CreateEd25519Key();

        var record = new CertificationRecordData
        {
            Status = "PASS",
            Stage = "S0-S2",
            Admitted = true,
            Signed = true,
            Timestamp = DateTimeOffset.UtcNow,
            BrickId = brickId,
            ContentHash = BrickContentHasher.ComputeSha256(source),
            Gate = "rollback-gate-exemption-test-harness",
            SchemaVersion = CertificationRecordData.TrustLoopSchemaVersion,
            Inputs =
            [
                new CertificationInput
                {
                    Kind = CertificationInputKinds.GateEmittedArtifact,
                    Id = brickId,
                    Hash = BrickContentHasher.ComputeSha256(source)
                },
                CertifierIdentity.ToInput()
            ],
            Ed25519PublicKey = publicKey
        };

        return record with
        {
            Signature = CertificationRecordSigning.Sign(record, HmacKey),
            Ed25519Signature = CertificationRecordEd25519.Sign(record, Convert.FromBase64String(privateKey))
        };
    }

    private static Task<BrickOutput> Execute(
        CertifiedBrickHotSwapHost host,
        string brickId = ProbeBrickId,
        Dictionary<string, object>? input = null) =>
        host.ExecuteAsync(
            brickId,
            new BrickInput(input ?? new Dictionary<string, object>()),
            ImplementationType.Deterministic,
            new TestExecutionContext());

    private static string Describe(CertifiedBrickSwapResult result) =>
        result.Swapped
            ? "swapped"
            : string.Join("; ", result.Refusals.Select(r => $"{r.BrickId} {r.Stage} {r.FailureCode}: {r.Reason}"));

    /// <summary>Clears the serving generation's watch window (MinInvocations without breach).</summary>
    private static async Task Warm(CertifiedBrickHotSwapHost host, int invocations)
    {
        for (var i = 0; i < invocations; i++)
            await Execute(host);
    }

    /// <summary>Invokes a faulting generation until the watch breaches on the last invocation.</summary>
    private static async Task Breach(CertifiedBrickHotSwapHost host, int invocations, string? mode = null)
    {
        var input = mode is null ? null : new Dictionary<string, object> { ["mode"] = mode };
        for (var i = 0; i < invocations; i++)
        {
            var act = () => Execute(host, input: input);
            await act.Should().ThrowAsync<InvalidOperationException>();
        }
    }

    private sealed class TestExecutionContext : IExecutionContext
    {
        public string AgentId => "rollback-gate-exemption-test";
        public string BehaviorId => "rollback-gate-exemption-test";
        public bool IsAirGapped => true;
        public bool AuditMode => true;
        public string Provider => "deterministic";
        public IReadOnlyDictionary<string, object> Variables { get; } = new Dictionary<string, object>();
    }

    private sealed class RecordingSink : ICertifiedBrickSwapProvenanceSink
    {
        private readonly object _gate = new();
        private readonly List<BrickSwapProvenanceEvent> _events = new();
        private readonly List<(Func<BrickSwapProvenanceEvent, bool> Match, TaskCompletionSource Done)> _waiters = new();

        public void Record(BrickSwapProvenanceEvent provenanceEvent)
        {
            var fire = new List<TaskCompletionSource>();
            lock (_gate)
            {
                _events.Add(provenanceEvent);
                for (var i = _waiters.Count - 1; i >= 0; i--)
                {
                    if (_waiters[i].Match(provenanceEvent))
                    {
                        fire.Add(_waiters[i].Done);
                        _waiters.RemoveAt(i);
                    }
                }
            }

            foreach (var done in fire)
                done.TrySetResult();
        }

        public IReadOnlyList<BrickSwapProvenanceEvent> Snapshot()
        {
            lock (_gate)
            {
                return _events.ToList();
            }
        }

        /// <summary>Completes when an event matching <paramref name="match"/> has been recorded.</summary>
        public Task WhenRecordedAsync(Func<BrickSwapProvenanceEvent, bool> match)
        {
            lock (_gate)
            {
                if (_events.Any(match))
                    return Task.CompletedTask;
                var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Add((match, done));
                return done.Task;
            }
        }
    }

    /// <summary>Records each rollback with the generation serving at the moment it was recorded.</summary>
    private sealed class SpyLineageAuthority : ILineageAuthority
    {
        private readonly ILineageAuthority _inner;

        public SpyLineageAuthority(ILineageAuthority inner) => _inner = inner;

        public CertifiedBrickHotSwapHost? Host { get; set; }

        public List<(string LineageKey, int? ServingGeneration)> Rollbacks { get; } = new();

        public int RecordRollback(string lineageKey)
        {
            lock (Rollbacks)
            {
                Rollbacks.Add((lineageKey, Host?.CurrentGenerationId));
            }

            return _inner.RecordRollback(lineageKey);
        }

        public bool IsDemoted(string lineageKey) => _inner.IsDemoted(lineageKey);

        public void Demote(string lineageKey, string reason) => _inner.Demote(lineageKey, reason);
    }

    /// <summary>
    /// An operator single-op revocation (R7.2) racing the quarantine: the armed hash reads
    /// unrevoked once — when the rollback target is selected — and revoked from then on.
    /// </summary>
    private sealed class RacingRevocations : ICertificateRevocationList
    {
        private readonly InMemoryCertificateRevocationList _inner = new();
        private string? _armedHash;

        public void RevokeOnNextQuery(string contentHash) => _armedHash = contentHash;

        public bool IsRevoked(string contentHash)
        {
            var revoked = _inner.IsRevoked(contentHash);
            if (!revoked && _armedHash is { } armed
                && string.Equals(armed, contentHash, StringComparison.OrdinalIgnoreCase))
            {
                _armedHash = null;
                _inner.Revoke(contentHash, "operator quarantine racing the breach rollback");
            }

            return revoked;
        }

        public void Revoke(string contentHash, string reason) => _inner.Revoke(contentHash, reason);

        public string? TryGetReason(string contentHash) => _inner.TryGetReason(contentHash);

        public IReadOnlyCollection<string> Snapshot() => _inner.Snapshot();
    }

    private sealed class MutableClock : TimeProvider
    {
        private DateTimeOffset _now;
        public MutableClock(DateTimeOffset start) => _now = start;
        public void Advance(TimeSpan by) => _now += by;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private static (string PrivateKeyBase64, string PublicKeyBase64) CreateEd25519Key()
    {
        using var key = Key.Create(
            SignatureAlgorithm.Ed25519,
            new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
        return (
            Convert.ToBase64String(key.Export(KeyBlobFormat.RawPrivateKey)),
            Convert.ToBase64String(key.PublicKey.Export(KeyBlobFormat.RawPublicKey)));
    }
}
