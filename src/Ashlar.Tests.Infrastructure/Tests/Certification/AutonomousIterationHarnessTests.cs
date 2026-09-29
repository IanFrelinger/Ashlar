using FluentAssertions;
using Ashlar.Agents.TestKit;
using Ashlar.Certification.Contracts;
using Ashlar.Core.Application.Autonomy;
using Ashlar.Core.Application.Execution.Ports;
using Ashlar.Core.Application.Certification.Models;
using Ashlar.Core.Domain.Bricks;
using Ashlar.Core.Domain.Execution;
using Ashlar.Infrastructure.Certification;
using Ashlar.Infrastructure.Certification.HotSwap;
using Ashlar.Tests.Infrastructure.Certification.Fixtures;
using NSec.Cryptography;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>
/// The proposal-iteration harness (PR-E core): intake gating before any work, per-
/// iteration attested sandbox sessions, the REAL certification chain, Tier-0 autonomous
/// swap versus held admission — every run terminating in exactly one of the four R2.3
/// states, under the R4.6/B11.2 budget ceiling.
/// </summary>
[Trait("Category", "Certification")]
[Collection("hot-swap-host")]
public sealed class AutonomousIterationHarnessTests
{
    private static readonly WitnessSpec StrongWitness = new(
        "mutation-probe-brick",
        [
            new WitnessCase(
                new Dictionary<string, object>
                {
                    ["logText"] = "2024-01-01 ERROR First failure: connection reset\n2024-01-01 ERROR Second failure: timeout"
                },
                new Dictionary<string, object>
                {
                    ["errorCount"] = 2,
                    ["firstErrorMessage"] = "First failure: connection reset"
                })
        ]);

    private static readonly TouchSet LeafTouch = new()
    {
        PathPrefixes = ["src/Ashlar.Bricks.Probe/"],
        Namespaces = ["Ashlar.Tests.Infrastructure.Certification.Fixtures"],
    };

    [Fact]
    public async Task HoldAdmission_CertifiesFully_ButRefusesToSwap_EvenAtTier0()
    {
        // The operator hold is the difference between a loop that reports and a loop
        // that acts. A Tier-0 objective is precisely the case that WOULD swap unattended,
        // so that is the case worth pinning.
        var sandbox = new FakeSandboxedSessionRunner(FakeSandboxedSessionRunner.Success());
        var harness = Harness(sandbox: sandbox, holdAdmission: true);

        var result = await harness.RunIterationAsync(Context("obj-hold"), Candidate());

        result.Tier!.Tier.Should().Be(ObjectiveTier.Tier0Autonomous,
            "this objective would otherwise have swapped without a human");
        result.Outcome.Should().Be(IterationOutcome.CertifiedButHeld, result.Explanation);
        result.Explanation.Should().Contain("hold mode");
        result.Decision!.Admitted.Should().BeTrue(
            "hold stops ADMISSION, not certification - the evidence must still accrue");
        result.Decision.Record.Signed.Should().BeTrue();
    }

    [Fact]
    public async Task HoldAdmission_IsTheDefault_WhenTheCallerSaysNothing()
    {
        // A harness constructed with only its two required collaborators must hold: an
        // unattended swap is the opt-in, never what a caller gets by omitting an argument.
        var (privateKey, _) = CreateEd25519Key();
        var harness = new AutonomousIterationHarness(
            new CertificationGate(new CertificationRecordSigner(ed25519PrivateKeyBase64: privateKey)),
            new CertifiedBrickHotSwapHost(
                hmacKey: null, drainTimeout: TimeSpan.FromSeconds(10),
                revocations: new InMemoryCertificateRevocationList()));

        var result = await harness.RunIterationAsync(Context("obj-default-hold"), Candidate());

        result.Tier!.Tier.Should().Be(ObjectiveTier.Tier0Autonomous,
            "only a Tier-0 objective could have swapped, so only it proves the default");
        result.Outcome.Should().Be(IterationOutcome.CertifiedButHeld, result.Explanation);
        result.Explanation.Should().Contain("hold mode");
        result.Decision!.Admitted.Should().BeTrue("the default holds ADMISSION, not certification");
    }

    [Fact]
    public async Task IntakeGating_RefusesBeforeAnyWork()
    {
        var pause = new LoopPauseControl();
        var lineages = new InMemoryLineageAuthority();
        var harness = Harness(pause: pause, lineages: lineages);

        pause.Pause("operator hold");
        (await harness.RunIterationAsync(Context("obj-a"), Candidate()))
            .Should().Match<IterationResult>(r =>
                r.Outcome == IterationOutcome.ExplainedFailure && r.Explanation.Contains("paused"));
        pause.Resume();

        (await harness.RunIterationAsync(
                Context("obj-b") with
                {
                    Source = ObjectiveSource.Telemetry,
                    Touch = new TouchSet { PathPrefixes = ["src/Ashlar.Core.Application/Autonomy/"] },
                },
                Candidate()))
            .Should().Match<IterationResult>(r =>
                r.Outcome == IterationOutcome.ExplainedFailure && r.Explanation.Contains("Tier 2"));

        lineages.Demote("obj-c", "operator decision");
        (await harness.RunIterationAsync(Context("obj-c"), Candidate()))
            .Should().Match<IterationResult>(r =>
                r.Outcome == IterationOutcome.ExplainedFailure && r.Explanation.Contains("R5.5"));

        (await harness.RunIterationAsync(
                Context("obj-d") with
                {
                    ParentEnvelope = new TouchSet { Capabilities = ["repo.fs.write"] },
                    Touch = LeafTouch with { Capabilities = ["repo.fs.write", "network.fetch"] },
                },
                Candidate()))
            .Should().Match<IterationResult>(r =>
                r.Outcome == IterationOutcome.ExplainedFailure && r.Explanation.Contains("R4.4"));
    }

    [Fact]
    public async Task EndsGood_ThroughTheHarness_AdmittedAndSwapped_WithAttestedSession()
    {
        var sandbox = new FakeSandboxedSessionRunner(FakeSandboxedSessionRunner.Success());
        var harness = Harness(sandbox: sandbox);
        var context = Context("obj-weather") with
        {
            Lineage = GenerationLineage.Child(GenerationLineage.HumanAuthored, "sig-proposer"),
            SessionSpec = new SandboxSpec(
                "proposer:latest", Array.Empty<Mount>(), NetworkAccess.None,
                new[] { "sleep", "infinity" }, new ResourceLimits(Memory: "2g")),
        };

        var result = await harness.RunIterationAsync(context, Candidate());

        result.Outcome.Should().Be(IterationOutcome.AdmittedAndSwapped, result.Explanation);
        result.Attestation.Should().NotBeNull("the session was attested before any work counted");
        result.Decision!.Record.Inputs.Should().Contain(i => i.Kind == "sandbox-spec",
            "the certificate records the environment it was minted under");
        result.Decision.Record.Inputs.Should().Contain(i => i.Kind == "generation-depth" && i.Id == "1");
        sandbox.ActiveSessions.Should().Be(0, "sessions are per-iteration and torn down with it (B9.3)");
    }

    [Fact]
    public async Task Tier1Objective_TerminatesAsCertifiedButHeld_WithEvidence()
    {
        var harness = Harness();
        var context = Context("obj-kernel") with
        {
            // A kernel PATH tiers this at 1; the candidate's own namespace stays declared
            // so its reference graph conforms — the analyzer leg judges references, the
            // classifier judges blast radius, and both must hold.
            Touch = new TouchSet
            {
                PathPrefixes = ["src/Ashlar.Policies/"],
                Namespaces = ["Ashlar.Tests.Infrastructure.Certification.Fixtures"],
            },
        };

        var result = await harness.RunIterationAsync(context, Candidate());

        result.Outcome.Should().Be(IterationOutcome.CertifiedButHeld, result.Explanation);
        result.Decision!.Admitted.Should().BeTrue("certification proceeds autonomously; ADMISSION waits (R3.1)");
        result.Explanation.Should().Contain("human gate");
    }

    [Fact]
    public async Task GateRejection_TerminatesAsExplainedFailure_WithProbeFindings()
    {
        var harness = Harness();
        var weakWitness = new WitnessSpec(
            "mutation-probe-brick",
            [
                new WitnessCase(
                    new Dictionary<string, object>
                    {
                        ["logText"] = "2024-01-01 ERROR First failure: connection reset\n2024-01-01 ERROR Second failure: timeout"
                    },
                    new Dictionary<string, object> { ["errorCount"] = 2 })
            ]);

        var result = await harness.RunIterationAsync(
            Context("obj-weak"), Candidate() with { Witness = weakWitness });

        result.Outcome.Should().Be(IterationOutcome.ExplainedFailure);
        result.Explanation.Should().Contain("mutation").And.Contain("probe finding(s)");
        result.Decision!.ProbeFindings.Should().NotBeEmpty("V2's probes feed the explanation");
    }

    [Fact]
    public async Task InSessionBuild_CompilesInsideTheSession_AndRecordsTheInput()
    {
        // Scripted: the toolchain probe answers a version; every later exec (uploads,
        // decode, build) repeats that success — stdout content is irrelevant to them.
        var sandbox = new FakeSandboxedSessionRunner(FakeSandboxedSessionRunner.Success("9.0.100"));
        var harness = Harness(sandbox: sandbox, buildInSession: true);

        var result = await harness.RunIterationAsync(SessionContext("obj-in-session"), Candidate());

        result.Outcome.Should().Be(IterationOutcome.AdmittedAndSwapped, result.Explanation);
        result.Decision!.Record.Inputs.Should().Contain(i => i.Kind == "session-build",
            "a passed in-session build is certificate evidence, bound to the source hash");

        var session = sandbox.Sessions.Single();
        session.ExecCommands.First().Should().Equal(new[] { "dotnet", "--version" },
            "the leg probes the toolchain before uploading anything");
        session.ExecCommands.Should().Contain(c => c.Count >= 2 && c[0] == "dotnet" && c[1] == "build",
            "the candidate compiles via the session, not the harness process");
        sandbox.ActiveSessions.Should().Be(0, "the build leg must not leak the session");
    }

    [Fact]
    public async Task InSessionBuild_ToolchainOrStepFailure_TerminatesAsExplainedFailure()
    {
        // First exec (the probe) fails: no dotnet in the image.
        var noToolchain = new FakeSandboxedSessionRunner(
            FakeSandboxedSessionRunner.Failure("sh: dotnet: not found", 127));
        var harness = Harness(sandbox: noToolchain, buildInSession: true);

        var result = await harness.RunIterationAsync(SessionContext("obj-no-dotnet"), Candidate());

        result.Outcome.Should().Be(IterationOutcome.ExplainedFailure);
        result.Explanation.Should().Contain("session-build").And.Contain("dotnet");
        result.Decision.Should().BeNull("the candidate never reached the gate");
        noToolchain.ActiveSessions.Should().Be(0, "failure paths tear the session down too");

        // Probe passes, the very next step (work-directory reset) fails.
        var midFailure = new FakeSandboxedSessionRunner(
            FakeSandboxedSessionRunner.Success("9.0.100"),
            FakeSandboxedSessionRunner.Failure("mkdir: cannot create directory", 1));
        harness = Harness(sandbox: midFailure, buildInSession: true);

        result = await harness.RunIterationAsync(SessionContext("obj-mid-fail"), Candidate());

        result.Outcome.Should().Be(IterationOutcome.ExplainedFailure);
        result.Explanation.Should().Contain("session-build");
        midFailure.ActiveSessions.Should().Be(0);
    }

    [Fact]
    public async Task InSessionBuild_WithoutASession_RefusesFailClosed()
    {
        // The flag demands compilation containment; an iteration with no session must
        // refuse, never quietly fall back to building on the host.
        var noSpec = Harness(sandbox: new FakeSandboxedSessionRunner(
            FakeSandboxedSessionRunner.Success()), buildInSession: true);
        (await noSpec.RunIterationAsync(Context("obj-no-spec"), Candidate()))
            .Should().Match<IterationResult>(r =>
                r.Outcome == IterationOutcome.ExplainedFailure
                && r.Explanation.Contains("fail-closed")
                && r.Decision == null);

        var noRunner = Harness(sandbox: null, buildInSession: true);
        (await noRunner.RunIterationAsync(SessionContext("obj-no-runner"), Candidate()))
            .Should().Match<IterationResult>(r =>
                r.Outcome == IterationOutcome.ExplainedFailure && r.Explanation.Contains("fail-closed"));
    }

    [Fact]
    public async Task BudgetCeiling_TerminatesAsBudgetExhausted()
    {
        var harness = Harness(budget: new ClusterBudget(TimeSpan.FromMilliseconds(1)));

        var result = await harness.RunIterationAsync(Context("obj-slow"), Candidate());

        result.Outcome.Should().Be(IterationOutcome.BudgetExhausted);
        result.Explanation.Should().Contain("wall-clock ceiling");
    }

    [Fact]
    public void ThroughputGuard_TightensTheCeiling_OnceTheMedianForms()
    {
        var budget = new ClusterBudget(TimeSpan.FromMinutes(10), throughputGuardFactor: 4);
        budget.EffectiveCeiling().Should().Be(TimeSpan.FromMinutes(10), "no median yet");

        budget.RecordCompletion(TimeSpan.FromSeconds(10));
        budget.RecordCompletion(TimeSpan.FromSeconds(12));
        budget.RecordCompletion(TimeSpan.FromSeconds(14));

        budget.EffectiveCeiling().Should().Be(TimeSpan.FromSeconds(48),
            "median 12s x factor 4 — one degenerate session cannot starve the cadence (R4.6)");
    }

    // --- criterion 3a: the record is persisted and re-verified ON THE HELD PATH ---------------

    [Fact]
    public async Task CertifiedButHeld_StillPersistsAndReverifiesTheRecord_WhenAnArchiveIsComposed()
    {
        // THE FACT THAT CLOSES 3a's MECHANICS. Nine ledger rows to date say the sweep verifies
        // under NOTHING, because HoldAdmission returns CertifiedButHeld above the only Strict
        // verification on the path. This asserts a verification happens on the HELD path, with the
        // hold itself untouched.
        var sandbox = new FakeSandboxedSessionRunner(FakeSandboxedSessionRunner.Success());
        var harness = Harness(sandbox: sandbox, holdAdmission: true, evidenceRoot: EvidenceRoot());

        var result = await harness.RunIterationAsync(Context("obj-evidence"), Candidate());

        result.Outcome.Should().Be(IterationOutcome.CertifiedButHeld, result.Explanation);
        result.Evidence.Should().NotBeNull(
            "an archive was composed, so the held iteration must have produced a verdict");
        result.Evidence!.Verified.Should().BeTrue(
            "the record was persisted and re-read from disk; refused with {0}: {1}",
            result.Evidence.FailureCode, result.Evidence.FailureReason);
        File.Exists(result.Evidence.RecordPath).Should().BeTrue(
            "a ledger row cites this path, so it has to be a file that exists after the run");
        result.Evidence.RecordSha256.Should().NotBeNullOrEmpty();
        result.Evidence.SignerFingerprint.Should().NotBeNull(
            "the row cites a signer fingerprint, and this harness signs with an Ed25519 key");
        result.Explanation.Should().Contain(result.Evidence.RecordPath,
            "AutonomyLoopService logs this explanation verbatim, and that log is where a run's "
            + "operator finds the artefact the row cites");
    }

    [Fact]
    public async Task TheHoldStillBlocksTheSwap_WhenAnArchiveIsComposed()
    {
        // The containment guard. 3a must not be closed by quietly reaching the hot-swap verifier:
        // that would mean a CI runner hot-swapping model-proposed code into its own process, which
        // is the property the held sweep exists to demonstrate.
        using var swapped = new CertifiedBrickHotSwapHost(
            hmacKey: null, drainTimeout: TimeSpan.FromSeconds(10),
            revocations: new InMemoryCertificateRevocationList());
        using var held = new CertifiedBrickHotSwapHost(
            hmacKey: null, drainTimeout: TimeSpan.FromSeconds(10),
            revocations: new InMemoryCertificateRevocationList());

        // POSITIVE CONTROL, and it is load-bearing: without it, "nothing swapped" would be equally
        // true of a harness that can no longer swap anything at all, and this fact would pass while
        // proving nothing about the hold.
        var unheld = await Harness(
                sandbox: new FakeSandboxedSessionRunner(FakeSandboxedSessionRunner.Success()),
                holdAdmission: false, evidenceRoot: EvidenceRoot(), host: swapped)
            .RunIterationAsync(Context("obj-swaps"), Candidate());
        unheld.Outcome.Should().Be(IterationOutcome.AdmittedAndSwapped, unheld.Explanation);
        swapped.CurrentGenerationId.Should().NotBeNull("the control must actually have swapped");
        swapped.CurrentBrickIds.Should().NotBeEmpty();

        var result = await Harness(
                sandbox: new FakeSandboxedSessionRunner(FakeSandboxedSessionRunner.Success()),
                holdAdmission: true, evidenceRoot: EvidenceRoot(), host: held)
            .RunIterationAsync(Context("obj-no-swaps"), Candidate());

        result.Outcome.Should().Be(IterationOutcome.CertifiedButHeld, result.Explanation);
        result.Evidence!.Verified.Should().BeTrue(
            "the archive ran, so 'nothing swapped' is not simply 'nothing happened'");
        held.CurrentGenerationId.Should().BeNull(
            "persisting and re-verifying a record must not put a generation into the host process");
        held.CurrentBrickIds.Should().BeEmpty(
            "the operator is holding this brick; nothing about the archive may admit it");
    }

    [Fact]
    public async Task NoArchiveComposed_LeavesEvidenceNull_AndChangesNothingElse()
    {
        // The change is opt-in and cannot alter an existing host.
        var sandbox = new FakeSandboxedSessionRunner(FakeSandboxedSessionRunner.Success());
        var harness = Harness(sandbox: sandbox, holdAdmission: true);

        var result = await harness.RunIterationAsync(Context("obj-no-archive"), Candidate());

        result.Outcome.Should().Be(IterationOutcome.CertifiedButHeld, result.Explanation);
        result.Evidence.Should().BeNull("no archive was composed");
        result.Explanation.Should().Be(
            "certified; the operator holds admission (loop is in hold mode, no unattended "
            + "swap) with full evidence on the record",
            "the explanation an existing host already logs must be unchanged, to the character, "
            + "when no archive is composed");
    }

    [Fact]
    public async Task AFailedReverificationIsReported_AndDoesNotBecomeAnAdmission()
    {
        // A record that does not verify is a reported verdict, never an escalation. Turning it into
        // ExplainedFailure would report the model's candidate as failed for a reason that has
        // nothing to do with it, and AutonomyLoopService branches on outcome for its repair channel.
        var (_, foreignPublicKey) = CreateEd25519Key();
        using var host = new CertifiedBrickHotSwapHost(
            hmacKey: null, drainTimeout: TimeSpan.FromSeconds(10),
            revocations: new InMemoryCertificateRevocationList());
        var harness = Harness(
            sandbox: new FakeSandboxedSessionRunner(FakeSandboxedSessionRunner.Success()),
            holdAdmission: true,
            evidenceRoot: EvidenceRoot(),
            evidenceTrustPolicy: CertificationTrustPolicy.FromTrustedKeys([foreignPublicKey]),
            host: host);

        var result = await harness.RunIterationAsync(Context("obj-untrusted-signer"), Candidate());

        result.Evidence.Should().NotBeNull();
        result.Evidence!.Verified.Should().BeFalse("the operator pinned a signer this run is not");
        result.Evidence.FailureCode.Should().Be("ed25519-key-not-trusted");
        result.Outcome.Should().Be(IterationOutcome.CertifiedButHeld,
            "the archive's verdict is about the RECORD, not about the candidate");
        host.CurrentGenerationId.Should().BeNull("a refused re-verification must never admit anything");
        File.Exists(result.Evidence.RecordPath).Should().BeTrue(
            "the record is still written: a row reporting a refusal must be able to cite the bytes "
            + "it refused");
    }

    // --- helpers -------------------------------------------------------------------------

    private static AutonomousIterationHarness Harness(
        LoopPauseControl? pause = null,
        ILineageAuthority? lineages = null,
        ISandboxedSessionRunner? sandbox = null,
        ClusterBudget? budget = null,
        bool holdAdmission = false,
        bool buildInSession = false,
        string? evidenceRoot = null,
        CertificationTrustPolicy? evidenceTrustPolicy = null,
        CertifiedBrickHotSwapHost? host = null)
    {
        var (privateKey, _) = CreateEd25519Key();
        var signer = new CertificationRecordSigner(ed25519PrivateKeyBase64: privateKey);
        var archive = evidenceRoot is null
            ? null
            : new CertificationEvidenceArchive(
                evidenceRoot, signer, evidenceTrustPolicy ?? CertificationTrustPolicy.Unpinned);

        return new AutonomousIterationHarness(
            new CertificationGate(signer),
            host ?? new CertifiedBrickHotSwapHost(
                hmacKey: null, drainTimeout: TimeSpan.FromSeconds(10),
                revocations: new InMemoryCertificateRevocationList(),
                lineageAuthority: lineages),
            pause, lineages, sandbox, budget,
            buildCandidateInSession: buildInSession,
            holdAdmission: holdAdmission,
            evidenceArchive: archive);
    }

    private static string EvidenceRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ashlar-harness-evidence-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static ProposalIterationContext Context(string objectiveId) => new()
    {
        ObjectiveId = objectiveId,
        Source = ObjectiveSource.Triage,
        Touch = LeafTouch,
    };

    private static ProposalIterationContext SessionContext(string objectiveId) =>
        Context(objectiveId) with
        {
            SessionSpec = new SandboxSpec(
                "sdk:pinned", Array.Empty<Mount>(), NetworkAccess.None,
                new[] { "sleep", "infinity" }, new ResourceLimits(Memory: "2g")),
        };

    private static ProposalCandidate Candidate() => new()
    {
        Brick = new MutationProbeBrick(),
        SourceCode = MutationProbeBrickSource.Code,
        Witness = StrongWitness,
        ProjectPath = CleanProjectFile(),
        CompilationReferences =
        [
            typeof(DomainBrick).Assembly.Location,
            typeof(BrickInput).Assembly.Location
        ],
        BrickTypeName = typeof(MutationProbeBrick).FullName,
    };

    private static string CleanProjectFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ashlar-harness-{Guid.NewGuid():N}.csproj");
        File.WriteAllText(path, """
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <PackageReference Include="Ashlar.Brick.Contracts" Version="0.1.0" />
  </ItemGroup>
</Project>
""");
        return path;
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
