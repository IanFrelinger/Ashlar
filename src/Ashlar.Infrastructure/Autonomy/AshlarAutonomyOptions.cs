using System.Diagnostics.CodeAnalysis;
using Ashlar.Core.Application.Autonomy;

namespace Ashlar.Infrastructure.Autonomy;

/// <summary>
/// Host configuration for the autonomous self-extension loop, bound from
/// <c>Ashlar:Autonomy</c>. Fail-closed: <see cref="Enabled"/> is false, so a host that
/// merely references the package runs nothing until an operator opts in.
/// </summary>
[Experimental(AutonomyExperimental.DiagnosticId, UrlFormat = AutonomyExperimental.UrlFormat)]
public sealed class AshlarAutonomyOptions
{
    /// <summary>Configuration section this binds from.</summary>
    public const string SectionName = "Ashlar:Autonomy";

    /// <summary>Master switch. Off by default; nothing autonomous runs until this is true.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Container image proposal sessions start from. Required when
    /// <see cref="UseSandboxSessions"/> is true — an unnamed image cannot be attested,
    /// and an unattestable environment must not reach a certificate.
    /// </summary>
    public string? SessionImage { get; set; }

    /// <summary>
    /// Optional pin on the session image's IDENTITY: the engine image ID
    /// (<c>sha256:…</c>) that <see cref="SessionImage"/> must resolve to — the same value
    /// attestation records and every certificate carries as its <c>image-digest</c> input,
    /// so an operator pins by copying it from a certificate they have already read. When
    /// set, a session whose image resolves to anything else refuses to start; null (the
    /// default) keeps capture-only behaviour, where the resolved identity is recorded but
    /// a retagged image is not refused.
    /// </summary>
    public string? SessionImageDigest { get; set; }

    /// <summary>Whether iterations open sandbox sessions at all (attestation + environment inputs).</summary>
    public bool UseSandboxSessions { get; set; }

    /// <summary>
    /// Whether every iteration must compile its candidate INSIDE the attested session
    /// (the in-container toolchain leg). Requires <see cref="UseSandboxSessions"/> and a
    /// <see cref="SessionImage"/> whose SDK can target the candidate project (e.g.
    /// <c>mcr.microsoft.com/dotnet/sdk:10.0</c>). Iterations without a session then refuse
    /// fail-closed rather than building on the host.
    /// </summary>
    public bool BuildCandidateInSession { get; set; }

    /// <summary>
    /// Whether the gate's witness, determinism, and mutation legs EXECUTE candidate and
    /// mutant code inside the attested session — untrusted candidate code then never runs
    /// in the host process. Requires <see cref="BuildCandidateInSession"/>: the execution
    /// leg loads the session-built assembly.
    /// </summary>
    public bool ExecuteCandidateInSession { get; set; }

    /// <summary>
    /// Certify fully but admit nothing without a human. Default TRUE: a loop that starts
    /// swapping the moment it is wired up gives its operator no chance to read the
    /// evidence first. Distinct from the pause control, which halts intake entirely -
    /// hold runs the whole chain and stops at the swap, so evidence still accrues.
    /// </summary>
    public bool HoldAdmission { get; set; } = true;

    /// <summary>
    /// When set, every certified iteration persists its certification record under
    /// <c>&lt;dir&gt;/records/&lt;brickId&gt;.json</c>, re-reads it FROM THAT FILE, re-verifies it
    /// under <c>CertificationVerifyOptions.Strict</c> plus whatever
    /// <c>ASHLAR_CERT_TRUSTED_ED25519_KEYS</c> pins, and writes a verdict sidecar at
    /// <c>&lt;dir&gt;/&lt;brickId&gt;.evidence.json</c>. Default null (off).
    ///
    /// <para><b>This directory is EVIDENCE, not an admission store.</b> Nothing resolves it as
    /// <c>ICertificationRecordStore</c>, and a record sitting in it grants no admission: the brick
    /// registry still reports the held brick as unadmitted. The persist-and-re-verify step runs
    /// ABOVE <see cref="HoldAdmission"/> precisely so that reaching a Strict verification never
    /// requires hot-swapping a model-proposed candidate into the host process. Wiring this
    /// directory as the admission store instead would make persisting evidence manufacture
    /// admission for a brick the operator is holding.</para>
    ///
    /// <para>The verdict is reported, never escalated: a record that fails re-verification leaves
    /// the iteration's outcome unchanged and rides on <c>IterationResult.Evidence</c>.</para>
    /// </summary>
    public string? EvidenceArchiveDirectory { get; set; }

    /// <summary>Committed generations retained for no-build rollback (R5.1). Minimum 1 to roll back at all.</summary>
    public int RetentionWindow { get; set; } = 2;

    /// <summary>Minimum seconds between autonomous swaps (R6.1). 0 disables the cadence floor.</summary>
    public int CadenceFloorSeconds { get; set; } = 300;

    /// <summary>Invocations of a new generation before error/latency verdicts are meaningful (R5.2).</summary>
    public int WatchMinInvocations { get; set; } = 5;

    /// <summary>Tolerated error-rate increase over the previous generation's baseline.</summary>
    public double WatchMaxErrorRateDelta { get; set; } = 0.2;

    /// <summary>Tolerated mean-latency multiple of the baseline.</summary>
    public double WatchMaxLatencyFactor { get; set; } = 3.0;

    /// <summary>
    /// Absolute wall-clock ceiling for a single invocation, in seconds; 0 disables the
    /// leg. Unlike the relative legs this needs no baseline — a first-generation deploy
    /// is exactly who needs it. Wall clock is the one per-invocation resource honestly
    /// measurable in a shared host process.
    /// </summary>
    public double WatchMaxInvocationSeconds { get; set; }

    /// <summary>Rollbacks on one lineage before it loses Tier-0 autonomy (R5.5).</summary>
    public int LineageDemotionThreshold { get; set; } = 2;

    /// <summary>Seconds between leaked-session reaper sweeps. 0 disables the sweep service.</summary>
    public int ReaperSweepSeconds { get; set; } = 60;

    /// <summary>
    /// Seconds between human-digest renders (spec §7) when the digest job is added.
    /// 0 disables the job without unregistering it.
    /// </summary>
    public int DigestIntervalSeconds { get; set; } = 3600;

    /// <summary>Absolute per-iteration wall-clock ceiling in seconds (R4.6/B11.2).</summary>
    public int IterationCeilingSeconds { get; set; } = 600;

    /// <summary>Multiple of the median iteration a single iteration may take before the guard trims it.</summary>
    public double ThroughputGuardFactor { get; set; } = 4;
}
