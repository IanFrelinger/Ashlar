using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Ashlar.Certification.Contracts;
using Ashlar.Core.Application.Autonomy;
using Ashlar.Core.Application.Certification.Models;
using Ashlar.Core.Application.Certification.Ports;
using Ashlar.Core.Application.Execution.Ports;
using Ashlar.Core.Domain.Bricks;
using Ashlar.Core.Domain.Bricks.Ports;

namespace Ashlar.Infrastructure.Certification.HotSwap;

/// <summary>The four terminal states of one loop iteration (autonomy spec R2.3).</summary>
[Experimental(AutonomyExperimental.DiagnosticId, UrlFormat = AutonomyExperimental.UrlFormat)]
public enum IterationOutcome
{
    /// <summary>Certified and hot-swapped without human involvement (Tier 0).</summary>
    AdmittedAndSwapped,

    /// <summary>Certified; admission is holding for the human gate (or the swap host held it).</summary>
    CertifiedButHeld,

    /// <summary>The iteration failed with its evidence attached — including intake refusals.</summary>
    ExplainedFailure,

    /// <summary>The budget ceiling expired; partial artifacts retained.</summary>
    BudgetExhausted,
}

/// <summary>One iteration's terminal state with its evidence.</summary>
/// <param name="Outcome">How the iteration ended.</param>
/// <param name="Explanation">Human-facing account of why.</param>
/// <param name="Tier">The objective's tier classification, once intake reached it.</param>
/// <param name="Decision">The certification decision, when the chain ran.</param>
/// <param name="Attestation">The session attestation, when a session was opened.</param>
/// <param name="BuildDiagnostics">
/// When the candidate failed to compile inside the session: the compiler's diagnostics in the
/// candidate's own line coordinates (possibly empty if none parsed). Non-null marks the failure
/// as REPAIRABLE — a proposer may be handed these, because they describe its own text and
/// nothing of the witness. Null for every other outcome.
/// </param>
/// <param name="Evidence">
/// The persist-and-re-verify verdict for this iteration's certification RECORD, non-null only when
/// the harness was composed with a <see cref="CertificationEvidenceArchive"/>.
/// <para><b>Read what it is about.</b> It says the record's bytes survived a round trip to disk and
/// still verify under the archive's strictness. It says NOTHING about the admission path: the
/// archive runs above the operator hold, so a verified record here does not mean anything was
/// admitted, swapped, or verified at load. <c>Evidence.Verified == false</c> is a reported verdict
/// and never changes <see cref="Outcome"/>.</para>
/// </param>
[Experimental(AutonomyExperimental.DiagnosticId, UrlFormat = AutonomyExperimental.UrlFormat)]
public sealed record IterationResult(
    IterationOutcome Outcome,
    string Explanation,
    TierClassification? Tier = null,
    CertificationDecision? Decision = null,
    SessionAttestation? Attestation = null,
    IReadOnlyList<string>? BuildDiagnostics = null,
    CertificationEvidenceResult? Evidence = null);

/// <summary>The context an iteration runs under: the objective's declarations, projected to core types.</summary>
[Experimental(AutonomyExperimental.DiagnosticId, UrlFormat = AutonomyExperimental.UrlFormat)]
public sealed record ProposalIterationContext
{
    /// <summary>Objective id; doubles as the lineage key for demotion tracking (R5.5).</summary>
    public required string ObjectiveId { get; init; }

    /// <summary>Objective source in R1.1 trust order.</summary>
    public required ObjectiveSource Source { get; init; }

    /// <summary>Declared touch-set; null classifies fail-closed (R1.4).</summary>
    public TouchSet? Touch { get; init; }

    /// <summary>Parent session's envelope for depth&gt;0 iterations; child must be within it (R4.4).</summary>
    public TouchSet? ParentEnvelope { get; init; }

    /// <summary>Recursion pedigree (R4.1); null = human-authored context.</summary>
    public GenerationLineage? Lineage { get; init; }

    /// <summary>Constraint manifest, same instance the prompt was rendered from (A2.3).</summary>
    public BrickConstraintManifest? Manifest { get; init; }

    /// <summary>Sandbox spec for the iteration's session; null = no session (attestation skipped).</summary>
    public SandboxSpec? SessionSpec { get; init; }
}

/// <summary>What the proposer produced. Producing it (the model cluster) is outside this harness.</summary>
[Experimental(AutonomyExperimental.DiagnosticId, UrlFormat = AutonomyExperimental.UrlFormat)]
public sealed record ProposalCandidate
{
    /// <summary>Brick instance for witness execution.</summary>
    public required DomainBrick Brick { get; init; }

    /// <summary>Candidate source (hash-bound into the certificate).</summary>
    public required string SourceCode { get; init; }

    /// <summary>Witness cases.</summary>
    public required WitnessSpec Witness { get; init; }

    /// <summary>Project path for dependency checks.</summary>
    public required string ProjectPath { get; init; }

    /// <summary>Compilation references.</summary>
    public IReadOnlyList<string> CompilationReferences { get; init; } = Array.Empty<string>();

    /// <summary>Optional brick type name override.</summary>
    public string? BrickTypeName { get; init; }
}

/// <summary>
/// One loop iteration end to end (the PR-E core): intake gating (pause, tier-source,
/// lineage demotion, envelope narrowing) → optional sandbox session with attestation →
/// certification with the objective's declarations → Tier-0 autonomous swap or held
/// admission — terminating in exactly one of the four R2.3 states. Sessions are opened
/// per iteration and torn down with it (B9.3 restart-on-patch holds by construction: no
/// session outlives the image decision it started under). The budget ceiling (R4.6 /
/// B11.2) bounds the whole iteration's wall clock, tightened by the throughput guard.
///
/// <para><b>What the session does and does not do, precisely.</b> When a
/// <see cref="ProposalIterationContext.SessionSpec"/> is supplied, the session is started,
/// attested (image digest, engine version, effective resource caps — refused if weaker
/// than requested — and the containment the engine reports it applied: network mode,
/// read-only rootfs, dropped capabilities, security options), recorded onto the
/// certificate as environment inputs, and torn down with the iteration. With the
/// in-session build leg enabled (<c>buildCandidateInSession</c>), the harness compiles the
/// candidate INSIDE the session via <see cref="ISandboxedSession.ExecAsync"/> — the exact
/// wrapped bytes every certification-path compile sees (<see cref="SessionCandidateBuild"/>)
/// — refusing fail-closed when the leg is demanded but no session exists, and recording a
/// <c>session-build</c> input when it passes. With the execution leg additionally enabled
/// (<c>executeCandidateInSession</c>), the gate's witness, determinism, and mutation legs
/// EXECUTE candidate and mutant code inside that same session
/// (<see cref="SessionExecutionBackend"/>; <c>session-execution</c> input) and untrusted
/// candidate code never runs in this process. With both legs OFF — the option defaults —
/// the witness and mutation runs happen <b>in the harness process</b>: <c>sandbox-spec</c>/
/// <c>attestation</c> inputs are then provisioning evidence only, and the certificate
/// claims no containment it did not have. Orchestration, Roslyn compilation of mutants,
/// and all judgment stay on the host by design; the write surface is confined separately,
/// by <c>ProposerConfinement</c>'s tool allowlist.</para>
///
/// <para><b>Hold is the default.</b> A harness constructed without an explicit
/// <c>holdAdmission</c> certifies fully and admits nothing (<see cref="IterationOutcome.CertifiedButHeld"/>
/// even at Tier 0), matching <c>AshlarAutonomyOptions.HoldAdmission</c>. Unattended swapping
/// is the opt-in, never the thing a caller gets by forgetting an argument.</para>
/// </summary>
[Experimental(AutonomyExperimental.DiagnosticId, UrlFormat = AutonomyExperimental.UrlFormat)]
public sealed class AutonomousIterationHarness
{
    private readonly ICertificationGate _gate;
    private readonly CertifiedBrickHotSwapHost _host;
    private readonly LoopPauseControl? _pause;
    private readonly ILineageAuthority? _lineages;
    private readonly ISandboxedSessionRunner? _sandbox;
    private readonly ClusterBudget _budget;
    private readonly ILogger<AutonomousIterationHarness>? _logger;
    private readonly bool _buildCandidateInSession;
    private readonly bool _executeCandidateInSession;
    private readonly bool _holdAdmission;
    private readonly CertificationEvidenceArchive? _evidenceArchive;

    /// <summary>
    /// Creates the harness over the real gate and swap host. With
    /// <c>buildCandidateInSession</c> true, every iteration MUST compile its candidate
    /// inside the attested session (<see cref="SessionCandidateBuild"/>); an iteration
    /// without a session then refuses fail-closed rather than silently building on the host.
    /// With <c>executeCandidateInSession</c> additionally true, the gate's witness,
    /// determinism, and mutation legs EXECUTE candidate and mutant code inside that same
    /// session (<see cref="SessionExecutionBackend"/> over the session-built assembly) —
    /// untrusted candidate code then never runs in this process. <c>holdAdmission</c>
    /// defaults to TRUE: pass false explicitly to let Tier-0 certificates swap unattended.
    ///
    /// <para><c>evidenceArchive</c> is null by default, and null keeps today's behaviour exactly:
    /// every existing host and every existing test is unaffected, and
    /// <see cref="IterationResult.Evidence"/> stays null.</para>
    /// </summary>
    /// <param name="gate">The certification gate.</param>
    /// <param name="host">The certified-brick hot-swap host.</param>
    /// <param name="pause">Loop pause control, when the host has one.</param>
    /// <param name="lineages">Lineage authority, for demotion checks.</param>
    /// <param name="sandbox">Sandboxed session runner, when sessions are in use.</param>
    /// <param name="budget">Cluster budget; a default one is built when null.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="buildCandidateInSession">Compile the candidate inside the attested session.</param>
    /// <param name="executeCandidateInSession">Execute the witness/mutation legs inside that session.</param>
    /// <param name="holdAdmission">Certify fully, admit nothing. Defaults to TRUE.</param>
    /// <param name="evidenceArchive">
    /// When supplied, every certified iteration persists its record, re-reads it from disk and
    /// re-verifies it, reporting the verdict on <see cref="IterationResult.Evidence"/>. This is
    /// evidence about record persistence; it is NOT an admission store and grants no admission.
    /// </param>
    public AutonomousIterationHarness(
        ICertificationGate gate,
        CertifiedBrickHotSwapHost host,
        LoopPauseControl? pause = null,
        ILineageAuthority? lineages = null,
        ISandboxedSessionRunner? sandbox = null,
        ClusterBudget? budget = null,
        ILogger<AutonomousIterationHarness>? logger = null,
        bool buildCandidateInSession = false,
        bool executeCandidateInSession = false,
        bool holdAdmission = true,
        CertificationEvidenceArchive? evidenceArchive = null)
    {
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _pause = pause;
        _lineages = lineages;
        _sandbox = sandbox;
        _budget = budget ?? new ClusterBudget();
        _logger = logger;
        _buildCandidateInSession = buildCandidateInSession;
        _executeCandidateInSession = executeCandidateInSession;
        _holdAdmission = holdAdmission;
        _evidenceArchive = evidenceArchive;
    }

    /// <summary>Runs one iteration to a terminal state. Never throws for loop-shaped failures.</summary>
    public async Task<IterationResult> RunIterationAsync(
        ProposalIterationContext context,
        ProposalCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        // --- intake gating: refusals are explained failures that never start work ------
        if (_pause?.IsPaused == true)
        {
            return new IterationResult(IterationOutcome.ExplainedFailure,
                $"intake refused: the loop is paused ({_pause.PausedReason}); objective intake halts immediately (R6.2)");
        }

        var tier = ObjectiveTierClassifier.Classify(context.Touch ?? new TouchSet());
        if (!tier.MaySessionOpen(context.Source))
        {
            return new IterationResult(IterationOutcome.ExplainedFailure,
                "intake refused: Tier 2 objectives must originate from a human; a machine source may not open a session (R3.1)",
                tier);
        }

        if (_lineages?.IsDemoted(context.ObjectiveId) == true)
        {
            return new IterationResult(IterationOutcome.ExplainedFailure,
                $"intake refused: lineage '{context.ObjectiveId}' lost Tier-0 autonomy on rollback evidence (R5.5)",
                tier);
        }

        if (context.ParentEnvelope is { } parent && context.Touch is { } childTouch)
        {
            var widenings = TouchSetNarrowing.FindWidenings(childTouch, parent);
            if (widenings.Count > 0)
            {
                return new IterationResult(IterationOutcome.ExplainedFailure,
                    "intake refused: the child session widens beyond its parent's envelope (R4.4): "
                    + string.Join(" | ", widenings),
                    tier);
            }
        }

        // --- the budgeted run ----------------------------------------------------------
        var ceiling = _budget.EffectiveCeiling();
        using var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budgetCts.CancelAfter(ceiling);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var result = await RunBudgetedAsync(context, candidate, tier, budgetCts.Token).ConfigureAwait(false);
            _budget.RecordCompletion(stopwatch.Elapsed);
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The ceiling, not the caller: terminal state four, artifacts retained
            // (whatever certification recorded before the ceiling is already persisted
            // by the caller's stores; nothing here is discarded).
            return new IterationResult(IterationOutcome.BudgetExhausted,
                $"budget exhausted: the iteration exceeded its wall-clock ceiling of {ceiling.TotalSeconds:F0}s "
                + "(R4.6/B11.2; the throughput guard tightens this as the cluster's median forms)",
                tier);
        }
    }

    private async Task<IterationResult> RunBudgetedAsync(
        ProposalIterationContext context,
        ProposalCandidate candidate,
        TierClassification tier,
        CancellationToken cancellationToken)
    {
        SessionAttestation? attestation = null;
        var environmentInputs = Array.Empty<CertificationInput>() as IReadOnlyList<CertificationInput>;

        ISandboxedSession? session = null;
        try
        {
            // Per-iteration session (B9.3 by construction), attested before any work counts.
            if (_sandbox is not null && context.SessionSpec is { } spec)
            {
                session = await _sandbox.StartAsync(spec, cancellationToken).ConfigureAwait(false);
                attestation = await session.AttestAsync(cancellationToken).ConfigureAwait(false);
                attestation.ThrowIfBelowRequested();
                environmentInputs = SessionEnvironmentInputs.From(spec, attestation);
            }

            if (_buildCandidateInSession)
            {
                if (session is null)
                {
                    // Fail-closed: the configuration demanded compilation containment, and
                    // building on the host instead would silently drop it (R1.4 shape).
                    return new IterationResult(IterationOutcome.ExplainedFailure,
                        "session-build refused: the harness requires the candidate to build inside an "
                        + "attested session, but this iteration has none (no SessionSpec supplied, or no "
                        + "session runner composed); building on the host instead would silently drop the "
                        + "demanded containment (fail-closed)",
                        tier);
                }

                var build = await SessionCandidateBuild.RunAsync(session, candidate, cancellationToken)
                    .ConfigureAwait(false);
                if (!build.Passed)
                {
                    // A compile failure is REPAIRABLE: the diagnostics ride on the result so the
                    // loop can hand them to the proposer. Nothing of the witness has run yet.
                    return new IterationResult(IterationOutcome.ExplainedFailure,
                        $"session-build failed (exit {build.ExitCode}, toolchain '{build.ToolchainVersion}'): "
                        + build.OutputTail,
                        tier, null, attestation,
                        BuildDiagnostics: build.Diagnostics);
                }

                _logger?.LogInformation(
                    "Candidate for {ObjectiveId} compiled in session {SessionId} (toolchain {Toolchain})",
                    context.ObjectiveId, session.SessionId, build.ToolchainVersion);
                environmentInputs = environmentInputs
                    .Append(SessionCandidateBuild.ToInput(build, session.SessionId, candidate.SourceCode))
                    .ToArray();
            }

            // The execution leg rides the build leg: it loads the session-built assembly,
            // so demanding it without the build is a configuration error the validator
            // refuses at boot. Reaching here without a session is impossible for the same
            // reason (the build leg above already refused).
            SessionExecutionBackend? executionBackend = null;
            if (_executeCandidateInSession && session is not null)
                executionBackend = new SessionExecutionBackend(session);

            // Real brick source: emit, fence, and (for in-process legs) activate those
            // bytes. Identity-handle probes and incomplete proposals compile to no
            // DomainBrick — keep the supplied handle so the gate can still name the
            // in-process wiring refusal. A compile error or IL-fence hit is a refusal.
            GateEmittedArtifact? artifact = null;
            var brick = candidate.Brick;
            var brickTypeName = candidate.BrickTypeName;
            try
            {
                artifact = GateEmittedArtifactCompiler.Compile(
                    candidate.SourceCode, candidate.CompilationReferences);
            }
            catch (InvalidOperationException ex) when (
                ex.Message.Contains("contains no public, non-nested, concrete type", StringComparison.Ordinal))
            {
                artifact = null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new IterationResult(IterationOutcome.ExplainedFailure,
                    "gate-emitted compile refused the candidate: " + ex.Message,
                    tier, null, attestation);
            }

            if (artifact is not null)
            {
                try
                {
                    IlImportFence.Inspect(artifact.AssemblyBytes);
                }
                catch (InvalidOperationException ex)
                {
                    return new IterationResult(IterationOutcome.ExplainedFailure,
                        "IL import fence refused the candidate: " + ex.Message,
                        tier, null, attestation);
                }

                brickTypeName = artifact.BrickTypeName;
                if (executionBackend is null)
                    brick = CertifiedBrickActivator.Activate(artifact);
            }

            var decision = await _gate.CertifyAsync(new CertificationRequest
            {
                Brick = brick,
                Witness = candidate.Witness,
                SourceCode = candidate.SourceCode,
                ProjectPath = candidate.ProjectPath,
                CompilationReferences = candidate.CompilationReferences,
                BrickTypeName = brickTypeName,
                ConstraintManifest = context.Manifest,
                TouchSet = context.Touch,
                Lineage = context.Lineage,
                AdditionalInputs = environmentInputs,
                ExecutionBackend = executionBackend,
                EmittedArtifact = artifact,
            }, cancellationToken).ConfigureAwait(false);

            if (!decision.Admitted)
            {
                return new IterationResult(IterationOutcome.ExplainedFailure,
                    $"certification rejected at '{decision.FailureCheck}' with {decision.ProbeFindings.Count} probe finding(s): "
                    + decision.Record.Reason,
                    tier, decision, attestation);
            }

            // --- evidence archive: persist the record, read it back off disk, re-verify ---------
            //
            // THIS SITS HERE, ABOVE THE HOLD, ON PURPOSE, AND THE PLACEMENT IS THE POINT.
            //
            // The only other verification under this strictness on this path is inside
            // CertifiedBrickHotSwapHost (verify-at-load, `CertifiedBrickHotSwapHost.cs` around the
            // Strict call sites), fifteen lines below and BEHIND `_holdAdmission`. Reaching it
            // would mean flipping the hold so a CI runner hot-swaps model-proposed code into its
            // own process — buying a verification by deleting the containment the held loop exists
            // to demonstrate. So persist-and-re-verify is its own mechanism, invoked before the
            // hold is even consulted, and the hold below is untouched: no outcome changes, nothing
            // is admitted, nothing is swapped.
            //
            // What this establishes is narrow and should not be overquoted: the record's bytes
            // survived a round trip to disk and still verify. It is not a verification of the
            // admission path.
            CertificationEvidenceResult? evidence = null;
            if (_evidenceArchive is not null)
            {
                evidence = _evidenceArchive.PersistAndReverify(
                    decision.Record, candidate.SourceCode, artifact?.AssemblyBytes);

                if (evidence.Verified)
                {
                    _logger?.LogInformation(
                        "Certification record for {BrickId} persisted to {RecordPath} (sha256 {RecordSha256}) and "
                        + "re-verified from those bytes; signer {Fingerprint}, pinning {Pinning}, committed dev HMAC key {DevKey}",
                        decision.Record.BrickId, evidence.RecordPath, evidence.RecordSha256,
                        evidence.SignerFingerprint, evidence.PinningEnabled, evidence.UsesDevHmacKey);
                }
                else
                {
                    _logger?.LogWarning(
                        "Certification record for {BrickId} was persisted to {RecordPath} but did NOT re-verify: "
                        + "{FailureCode} - {FailureReason}",
                        decision.Record.BrickId, evidence.RecordPath, evidence.FailureCode, evidence.FailureReason);
                }
            }

            // Operator hold (host-level): certify fully, admit nothing. Distinct from
            // LoopPauseControl, which halts intake before any work happens - this runs the
            // whole chain and stops at the swap, so the evidence accrues while a human
            // still decides. Deliberately checked BEFORE the tier gate so hold covers
            // Tier 0 too, which is the only tier that would otherwise swap unattended.
            if (_holdAdmission)
            {
                return new IterationResult(IterationOutcome.CertifiedButHeld,
                    "certified; the operator holds admission (loop is in hold mode, no unattended "
                    + "swap) with full evidence on the record"
                    + DescribeEvidence(evidence),
                    tier, decision, attestation, Evidence: evidence);
            }

            if (tier.Tier != ObjectiveTier.Tier0Autonomous)
            {
                return new IterationResult(IterationOutcome.CertifiedButHeld,
                    $"certified; admission holds for the human gate (tier {tier.Tier}, R3.1) with full evidence on the record",
                    tier, decision, attestation, Evidence: evidence);
            }

            var swap = await _host.SwapAsync(new[]
            {
                new CertifiedBrickLoadRequest
                {
                    BrickId = decision.Record.BrickId,
                    SourceCode = candidate.SourceCode,
                    Record = CertificationRecordMapper.ToData(decision.Record),
                    BrickTypeName = brickTypeName,
                    AdditionalCompilationReferences = candidate.CompilationReferences,
                    PrecompiledAssembly = artifact?.AssemblyBytes,
                    Autonomous = new AutonomousAdmission
                    {
                        Tier = tier.Tier,
                        Lineage = context.Lineage,
                        LineageKey = context.ObjectiveId,
                    },
                }
            }, cancellationToken).ConfigureAwait(false);

            if (swap.Swapped)
            {
                return new IterationResult(IterationOutcome.AdmittedAndSwapped,
                    $"certified and swapped as generation {swap.GenerationId}; the watch window is now the last gate",
                    tier, decision, attestation, Evidence: evidence);
            }

            // A refused autonomous swap (cadence floor, in-flight window, pause landing
            // mid-run, lineage demotion racing in) is a HELD certificate, not a failure:
            // the artifact and its evidence persist for the next admission opportunity.
            return new IterationResult(IterationOutcome.CertifiedButHeld,
                "certified; the swap host held admission: "
                + string.Join(" | ", swap.Refusals.Select(r => $"{r.FailureCode}: {r.Reason}")),
                tier, decision, attestation, Evidence: evidence);
        }
        finally
        {
            if (session is not null)
                await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The archive's verdict, appended to the hold explanation that <c>AutonomyLoopService</c>
    /// already logs verbatim — so a run's own console output names the record file and the signer,
    /// which is what a ledger row cites. Empty when no archive is composed.
    /// </summary>
    private static string DescribeEvidence(CertificationEvidenceResult? evidence) =>
        evidence is null
            ? string.Empty
            : evidence.Verified
                ? $"; record at {evidence.RecordPath} (sha256 {evidence.RecordSha256}) re-read and verified"
                    + $", signer {evidence.SignerFingerprint ?? "(none)"}, pinned={evidence.PinningEnabled}"
                    + $", dev-hmac={evidence.UsesDevHmacKey}"
                : $"; record at {evidence.RecordPath} did NOT re-verify: {evidence.FailureCode}";
}
