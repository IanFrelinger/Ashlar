using System.Globalization;
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
/// The post-swap watch window (autonomy spec R5.2 through R5.5): certification proves the
/// gates passed, not that they were sufficient — the runtime is the last gate. A
/// generation healthy in gates but misbehaving post-swap ("regression theater") breaches
/// the watch, is quarantined (hashes revoked, lineage rollback recorded), and the
/// previous generation reactivates automatically; repeated rollback demotes the lineage;
/// and revocation propagates through certificate input chains.
/// </summary>
[Trait("Category", "Certification")]
[Collection("hot-swap-host")]
public sealed class GenerationWatchWindowTests : IDisposable
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

    private const string HmacKey = "watch-window-test-hmac";
    private const string ProbeBrickId = "hot-swap-probe";

    private const string HealthyTemplate = """
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

    private const string FaultingSource = """
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

    private const string SneakyWriterSource = """
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

    public override Task<BrickOutput> ExecuteAsync(
        BrickInput input,
        ImplementationType implementation,
        IExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var output = new BrickOutput { Summary = "sneaky" };
        output.Set("marker", "sneaky");
        output.Set("exfil", "undeclared payload");
        return Task.FromResult(output);
    }
}
""";

    private const string SlowSource = """
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
        await Task.Delay(50, cancellationToken);
        var output = new BrickOutput { Summary = "slow" };
        output.Set("marker", "slow");
        return output;
    }
}
""";

    [Fact]
    public async Task RegressionTheater_IsCaughtByTheWatch_QuarantinedAndRolledBack()
    {
        var revocations = new InMemoryCertificateRevocationList();
        var lineage = new InMemoryLineageAuthority();
        var sink = new RecordingSink();
        using var host = CreateHost(sink, revocations, lineage,
            new WatchThresholds { MinInvocations = 2, MaxErrorRateDelta = 0.2 });

        // Generation 1: healthy baseline.
        (await host.SwapAsync(new[] { AutonomousRequest(Healthy("v1"), "lineage-weather") })).Swapped.Should().BeTrue();
        for (var i = 0; i < 3; i++)
            (await Execute(host)).Get<string>("marker").Should().Be("v1");

        // Generation 2: healthy in gates, faulting at runtime.
        var faulting = AutonomousRequest(FaultingSource, "lineage-weather");
        (await host.SwapAsync(new[] { faulting })).Swapped.Should().BeTrue();

        // Invocations fault until the watch breaches and rolls back automatically.
        for (var i = 0; i < 2; i++)
        {
            var act = () => Execute(host);
            await act.Should().ThrowAsync<InvalidOperationException>();
        }

        (await Execute(host)).Get<string>("marker").Should().Be("v1",
            "the watch breach must reactivate the healthy generation");
        revocations.IsRevoked(faulting.Record.ContentHash!).Should().BeTrue("quarantine revokes the hash (R5.3)");
        sink.Snapshot().Should().Contain(e => e.Outcome == BrickSwapProvenanceOutcomes.WatchBreachQuarantined);
        sink.Snapshot().Should().Contain(e => e.Outcome == BrickSwapProvenanceOutcomes.RollbackCommitted);

        // And the quarantined hash can never come back (R5.3).
        (await host.SwapAsync(new[] { AutonomousRequest(FaultingSource, "lineage-weather") }))
            .Refusals.Should().ContainSingle().Which.FailureCode.Should().Be("revoked-hash");
    }

    [Fact]
    public async Task UndeclaredWrites_BreachTheContractLeg_WithoutNeedingABaseline()
    {
        var revocations = new InMemoryCertificateRevocationList();
        var sink = new RecordingSink();
        using var host = CreateHost(sink, revocations, lineage: null,
            new WatchThresholds { MinInvocations = 99, MaxUndeclaredWrites = 0 });

        (await host.SwapAsync(new[] { AutonomousRequest(Healthy("v1"), "lineage-a") })).Swapped.Should().BeTrue();
        // A different lineage: lineage-a's still-in-flight window must not block it (R6.1
        // is per-lineage), and the contract leg needs no baseline from it either.
        (await host.SwapAsync(new[] { AutonomousRequest(SneakyWriterSource, "lineage-b") })).Swapped.Should().BeTrue();

        // A single undeclared write breaches immediately — contract conformance is
        // absolute, no MinInvocations, no baseline (R5.2).
        await Execute(host);

        (await Execute(host)).Get<string>("marker").Should().Be("v1");
        sink.Snapshot().Should().Contain(e =>
            e.Outcome == BrickSwapProvenanceOutcomes.WatchBreachQuarantined &&
            e.Reason!.Contains("undeclared"));
    }

    [Fact]
    public async Task SlowInvocation_BreachesTheAbsoluteDurationCeiling_WithoutNeedingABaseline()
    {
        var revocations = new InMemoryCertificateRevocationList();
        var sink = new RecordingSink();
        // MinInvocations of 99 proves the point: the duration ceiling, like the contract
        // leg, judges immediately — a first-generation deploy has no baseline, and a
        // pathological single invocation must not hide inside a healthy mean.
        using var host = CreateHost(sink, revocations, lineage: null,
            new WatchThresholds
            {
                MinInvocations = 99,
                MaxInvocationDuration = TimeSpan.FromMilliseconds(1),
            });

        (await host.SwapAsync(new[] { AutonomousRequest(Healthy("v1"), "lineage-a") })).Swapped.Should().BeTrue();
        (await host.SwapAsync(new[] { AutonomousRequest(SlowSource, "lineage-b") })).Swapped.Should().BeTrue();

        // One slow invocation (~50ms against a 1ms cap) breaches.
        await Execute(host);

        (await Execute(host)).Get<string>("marker").Should().Be("v1",
            "the duration breach must reactivate the healthy generation");
        sink.Snapshot().Should().Contain(e =>
            e.Outcome == BrickSwapProvenanceOutcomes.WatchBreachQuarantined &&
            e.Reason!.Contains("ceiling"));
    }

    // The two facts below exist because the test ABOVE cannot see the defect they pin. It puts a
    // ~50ms invocation against a 1ms cap, so it breaches on any platform and on either side of the
    // bug: 50x of slack in the direction the defect already leans.
    //
    // The defect: the counter being compared holds TIMESTAMP ticks, whose unit is
    // Stopwatch.Frequency — 10,000,000 on Windows QPC, which coincidentally equals
    // TimeSpan.TicksPerSecond and makes the raw comparison read as correct on a developer box, and
    // 1,000,000,000 on Linux and macOS. Measured in the devtest container: Ubuntu 24.04 reports 1e9.
    //
    // Both facts INJECT the frequency rather than inheriting the machine's, and that is deliberate
    // for two reasons. It makes them two-sided on every platform instead of being silent no-ops on
    // Windows. And it removes the wall clock entirely: an earlier version of these asserted a real
    // ~50ms measurement and the macOS runner measured the same invocation at 2775ms under load, so
    // the absolute bound was a flake by construction. Elapsed time here is now exactly what the
    // clock says it is.

    /// <summary>
    /// A timestamp source whose unit is nanoseconds — the frequency Linux and macOS report — and where
    /// every read advances by one fixed interval. Since the host divides by the provider's own
    /// TimestampFrequency, measured durations are exact and identical on every platform; treating
    /// these ticks as TimeSpan ticks instead inflates them exactly 100x.
    /// </summary>
    private sealed class NanosecondIntervalClock : TimeProvider
    {
        private const long NanosecondsPerSecond = 1_000_000_000;
        private readonly long _stepTicks;
        private long _ticks;

        public NanosecondIntervalClock(TimeSpan step) =>
            _stepTicks = (long)(step.TotalSeconds * NanosecondsPerSecond);

        public override long TimestampFrequency => NanosecondsPerSecond;
        public override long GetTimestamp() => Interlocked.Add(ref _ticks, _stepTicks);
    }

    [Fact]
    public async Task A_fast_invocation_does_not_breach_a_generous_duration_ceiling()
    {
        var revocations = new InMemoryCertificateRevocationList();
        var sink = new RecordingSink();
        // MinInvocations of 99 keeps the two baseline-relative legs out of this: the only leg that
        // may speak here is the absolute ceiling.
        //
        // Every invocation measures exactly 50ms, against a one-second ceiling. Converted, that is
        // 20x inside the cap and cannot breach. Unconverted, 50,000,000 nanosecond ticks read as
        // TimeSpan ticks are five seconds, which breaches — so the defect is caught with 5x to spare
        // and neither margin depends on how busy the runner is.
        using var host = CreateHost(sink, revocations, lineage: null,
            new WatchThresholds
            {
                MinInvocations = 99,
                MaxInvocationDuration = TimeSpan.FromSeconds(1),
            },
            clock: new NanosecondIntervalClock(TimeSpan.FromMilliseconds(50)));

        (await host.SwapAsync(new[] { AutonomousRequest(Healthy("v1"), "lineage-a") })).Swapped.Should().BeTrue();
        (await host.SwapAsync(new[] { AutonomousRequest(SlowSource, "lineage-b") })).Swapped.Should().BeTrue();

        await Execute(host);

        (await Execute(host)).Get<string>("marker").Should().Be("slow",
            "50ms is comfortably inside a one-second ceiling, so the slow generation must keep serving");
        sink.Snapshot().Should().NotContain(e =>
            e.Outcome == BrickSwapProvenanceOutcomes.WatchBreachQuarantined &&
            e.Reason!.Contains("ceiling", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_ceiling_breach_reports_the_elapsed_time_an_operator_would_recognise()
    {
        var revocations = new InMemoryCertificateRevocationList();
        var sink = new RecordingSink();
        // Every invocation measures exactly 250ms against a 1ms ceiling, so the breach is certain and
        // the reported figure is an exact number rather than a range.
        using var host = CreateHost(sink, revocations, lineage: null,
            new WatchThresholds
            {
                MinInvocations = 99,
                MaxInvocationDuration = TimeSpan.FromMilliseconds(1),
            },
            clock: new NanosecondIntervalClock(TimeSpan.FromMilliseconds(250)));

        (await host.SwapAsync(new[] { AutonomousRequest(Healthy("v1"), "lineage-a") })).Swapped.Should().BeTrue();
        (await host.SwapAsync(new[] { AutonomousRequest(SlowSource, "lineage-b") })).Swapped.Should().BeTrue();

        // One invocation only. Every invocation measures 250ms here, so a second one would breach the
        // reactivated generation as well and there would be two events to disambiguate — which says
        // nothing about the number this fact is here to pin.
        await Execute(host);

        // This breaches on either side of the defect, so the breach is not the fact — the NUMBER in
        // the message is. Rendering a timestamp tick count through TimeSpan.FromTicks turns 250ms into
        // 25000ms, a figure that would send an operator hunting a twenty-five-second stall that never
        // happened.
        var reason = sink.Snapshot()
            .Where(e => e.Outcome == BrickSwapProvenanceOutcomes.WatchBreachQuarantined)
            .Select(e => e.Reason!)
            .Should().ContainSingle(r => r.Contains("ceiling", StringComparison.Ordinal)).Subject;

        var from = reason.IndexOf("took ", StringComparison.Ordinal) + "took ".Length;
        var to = reason.IndexOf("ms", from, StringComparison.Ordinal);
        (from > 4 && to > from).Should().BeTrue($"the message must still say 'took <n>ms'; got: {reason}");
        var reported = int.Parse(reason[from..to], CultureInfo.InvariantCulture);

        reported.Should().Be(250,
            "the clock says every invocation took exactly 250ms, so that is what an operator must be "
            + $"told; treating nanosecond ticks as TimeSpan ticks reports 25000. Got: {reason}");
    }

    [Fact]
    public async Task RepeatedRollback_DemotesTheLineage_AndTheHostRefusesFurtherAutoSwaps()
    {
        var lineage = new InMemoryLineageAuthority(threshold: 2);
        lineage.RecordRollback("lineage-flaky");
        lineage.RecordRollback("lineage-flaky");

        using var host = CreateHost(null, new InMemoryCertificateRevocationList(), lineage, watch: null);

        var result = await host.SwapAsync(new[] { AutonomousRequest(Healthy("v1"), "lineage-flaky") });

        result.Swapped.Should().BeFalse();
        result.Refusals.Should().ContainSingle().Which.FailureCode.Should().Be("lineage-demoted");
    }

    [Fact]
    public void RevocationChains_PropagateThroughCertificateInputs_Transitively()
    {
        var revoked = "sha256:revoked-artifact";
        var direct = Record("brick-b", "sha256:b", inputHashes: ["sha256:revoked-artifact"]);
        var transitive = Record("brick-c", "sha256:c", inputHashes: ["sha256:b"]);
        var unrelated = Record("brick-d", "sha256:d", inputHashes: ["sha256:clean"]);

        var suspects = RevocationChainScanner.FindSuspects(
            new[] { direct, transitive, unrelated }, revoked);

        suspects.Select(s => s.BrickId).Should().BeEquivalentTo(
            new[] { "brick-b", "brick-c" },
            "certificates built on a revoked artifact are suspect, transitively (R5.4)");
    }

    // --- helpers -------------------------------------------------------------------------

    private static string Healthy(string marker) => HealthyTemplate.Replace("{MARKER}", marker);

    private static CertifiedBrickHotSwapHost CreateHost(
        RecordingSink? sink,
        ICertificateRevocationList revocations,
        ILineageAuthority? lineage,
        WatchThresholds? watch,
        TimeProvider? clock = null) =>
        new(sink, logger: null, hmacKey: HmacKey, drainTimeout: TimeSpan.FromSeconds(10),
            revocations: revocations, retentionWindow: 2,
            watchThresholds: watch, lineageAuthority: lineage, clock: clock);

    private static CertifiedBrickLoadRequest AutonomousRequest(string source, string lineageKey) => new()
    {
        BrickId = ProbeBrickId,
        SourceCode = source,
        Record = CertifyRecord(ProbeBrickId, source),
        Autonomous = new AutonomousAdmission
        {
            Tier = ObjectiveTier.Tier0Autonomous,
            Lineage = GenerationLineage.Child(GenerationLineage.HumanAuthored, "sig-parent"),
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
            Gate = "watch-window-test-harness",
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

    private static CertificationRecordData Record(string brickId, string contentHash, string[] inputHashes) => new()
    {
        Status = "PASS",
        Stage = "S0-S2",
        Admitted = true,
        Signed = true,
        Timestamp = DateTimeOffset.UtcNow,
        BrickId = brickId,
        ContentHash = contentHash,
        Gate = "watch-window-test-harness",
        Inputs = inputHashes
            .Select(h => new CertificationInput { Kind = "artifact", Id = h, Hash = h })
            .ToArray()
    };

    private static Task<BrickOutput> Execute(CertifiedBrickHotSwapHost host) =>
        host.ExecuteAsync(
            ProbeBrickId,
            new BrickInput(new Dictionary<string, object>()),
            ImplementationType.Deterministic,
            new TestExecutionContext());

    private sealed class TestExecutionContext : IExecutionContext
    {
        public string AgentId => "watch-window-test";
        public string BehaviorId => "watch-window-test";
        public bool IsAirGapped => true;
        public bool AuditMode => true;
        public string Provider => "deterministic";
        public IReadOnlyDictionary<string, object> Variables { get; } = new Dictionary<string, object>();
    }

    private sealed class RecordingSink : ICertifiedBrickSwapProvenanceSink
    {
        private readonly object _gate = new();
        private readonly List<BrickSwapProvenanceEvent> _events = new();

        public void Record(BrickSwapProvenanceEvent provenanceEvent)
        {
            lock (_gate)
            {
                _events.Add(provenanceEvent);
            }
        }

        public IReadOnlyList<BrickSwapProvenanceEvent> Snapshot()
        {
            lock (_gate)
            {
                return _events.ToList();
            }
        }
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
