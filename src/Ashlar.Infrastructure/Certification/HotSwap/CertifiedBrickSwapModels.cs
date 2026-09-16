using System.Diagnostics.CodeAnalysis;
using Ashlar.Certification.Contracts;
using Ashlar.Core.Application.Autonomy;

namespace Ashlar.Infrastructure.Certification.HotSwap;

/// <summary>
/// One brick to load in a generation swap: exact source bytes plus the certification
/// record the host re-verifies against them at load time.
/// </summary>
[Experimental(AutonomyExperimental.DiagnosticId, UrlFormat = AutonomyExperimental.UrlFormat)]
public sealed record CertifiedBrickLoadRequest
{
    /// <summary>Brick identifier the record was minted for; must match the instantiated brick's Id.</summary>
    public required string BrickId { get; init; }

    /// <summary>Exact certified brick source; hashed and compared to the record's content hash before any load.</summary>
    public required string SourceCode { get; init; }

    /// <summary>Certification record covering <see cref="SourceCode"/>.</summary>
    public required CertificationRecordData Record { get; init; }

    /// <summary>Optional fully-qualified brick type name; discovered from the assembly when null.</summary>
    public string? BrickTypeName { get; init; }

    /// <summary>Additional compilation reference paths beyond the brick contract assemblies.</summary>
    public IReadOnlyList<string> AdditionalCompilationReferences { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Present when the LOOP is driving this swap (autonomous self-extension spec R3.2,
    /// swap-host leg). Null means a human is driving — the historical hand-certified flow.
    /// When present, the host independently refuses non-Tier-0 admissions and re-enforces
    /// the recursion ceiling: three checks, one declaration, no shared failure mode.
    /// </summary>
    public AutonomousAdmission? Autonomous { get; init; }

    /// <summary>
    /// Pre-compiled assembly image for this exact certified source. The loop and the
    /// exporter pass the gate-emitted bytes so the first load is the judged PE, not a
    /// second compile. The host also populates this on retention/rollback so a rollback
    /// loads without any build (R5.1). Verify-at-load still runs against
    /// <see cref="SourceCode"/> either way. When null, the host rematerializes with
    /// <c>CandidateSourceWrapper</c> and <c>BrickCompileOptions</c> and still runs the
    /// IL import fence before load.
    /// </summary>
    public byte[]? PrecompiledAssembly { get; init; }
}

/// <summary>
/// The autonomy context of a loop-driven swap request: the tier its objective classified
/// at and the candidate's recursion pedigree. The swap host enforces both independently
/// of the certifier (defense in depth, R3.2/R4.2).
/// </summary>
[Experimental(AutonomyExperimental.DiagnosticId, UrlFormat = AutonomyExperimental.UrlFormat)]
public sealed record AutonomousAdmission
{
    /// <summary>Tier the objective's touch-set classified at (only Tier 0 may auto-swap).</summary>
    public required Ashlar.Core.Application.Autonomy.ObjectiveTier Tier { get; init; }

    /// <summary>The candidate's recursion pedigree; null claims human-authored context.</summary>
    public Ashlar.Core.Application.Autonomy.GenerationLineage? Lineage { get; init; }

    /// <summary>
    /// Objective lineage key (R5.5): repeated rollback on one lineage demotes it to Tier 1,
    /// and the host refuses further auto-swaps for it. Null = unattributed (no demotion
    /// tracking, no demotion protection either).
    /// </summary>
    public string? LineageKey { get; init; }
}

/// <summary>
/// Why a generation is being installed: because the loop (or a human) is absorbing a
/// change, or because the host is putting back a generation it already committed and
/// retained. The pacing and authority controls (pause, cadence floor, in-flight watch
/// window, lineage demotion, recursion ceiling) bound the <em>absorption</em> of change;
/// a containment rollback replays content the host itself retained, so those controls
/// never gate it — revocation and verify-at-load still do.
/// </summary>
/// <remarks>
/// Deliberately <c>internal</c>, accepted only by the host's private swap overload, and
/// never a member of <see cref="CertifiedBrickLoadRequest"/> or
/// <see cref="AutonomousAdmission"/>: intent is a property of the call path, not of
/// caller-supplied data. On the request it would be an unsigned, uncertified lane
/// selector exempting five gates at once. <c>RollbackIntentConventionTests</c> pins this.
/// </remarks>
internal enum SwapIntent
{
    /// <summary>A new generation is being absorbed; every autonomy gate applies.</summary>
    Forward,

    /// <summary>A retained generation is being put back after containment; only verification and revocation apply.</summary>
    Rollback
}

/// <summary>
/// Post-swap watch thresholds (autonomy spec R5.2): declared-contract conformance,
/// error-rate delta and latency factor versus the previous generation's baseline. Breach
/// triggers automatic quarantine + rollback. Null thresholds on the host = no watch
/// (the human-driven flow).
/// </summary>
[Experimental(AutonomyExperimental.DiagnosticId, UrlFormat = AutonomyExperimental.UrlFormat)]
public sealed record WatchThresholds
{
    /// <summary>Invocations of the new generation before verdicts are meaningful.</summary>
    public int MinInvocations { get; init; } = 5;

    /// <summary>Tolerated error-rate increase over the baseline (0.2 = +20 points).</summary>
    public double MaxErrorRateDelta { get; init; } = 0.2;

    /// <summary>Tolerated mean-latency multiple of the baseline.</summary>
    public double MaxLatencyFactor { get; init; } = 3.0;

    /// <summary>Tolerated writes of keys the brick's declared interface does not name.</summary>
    public int MaxUndeclaredWrites { get; init; }

    /// <summary>
    /// Absolute wall-clock ceiling for a SINGLE invocation; null disables the leg. Like the
    /// contract-conformance leg, this needs no baseline and no minimum invocation count — a
    /// first-generation deploy (which HAS no baseline for the relative legs to judge
    /// against) is exactly who needs it. Wall clock is the one per-invocation resource
    /// honestly measurable in a shared host process: per-brick memory/CPU attribution is
    /// not, and pretending otherwise would be a fake ceiling.
    /// </summary>
    public TimeSpan? MaxInvocationDuration { get; init; }
}

/// <summary>Stage at which a brick (and therefore the whole swap) was refused.</summary>
[Experimental(AutonomyExperimental.DiagnosticId, UrlFormat = AutonomyExperimental.UrlFormat)]
public enum BrickSwapRefusalStage
{
    /// <summary>Request shape was invalid (duplicate/mismatched brick id).</summary>
    Request,

    /// <summary>Certification verification failed (record not admitted, signature, content hash).</summary>
    Verification,

    /// <summary>The certified source did not compile.</summary>
    Compilation,

    /// <summary>The compiled assembly could not be loaded or the brick type was not found.</summary>
    Load,

    /// <summary>The brick could not be instantiated as a domain brick, or its Id did not match.</summary>
    Instantiation
}

/// <summary>One refused brick within a refused swap.</summary>
[Experimental(AutonomyExperimental.DiagnosticId, UrlFormat = AutonomyExperimental.UrlFormat)]
public sealed record BrickSwapRefusal
{
    /// <summary>Brick the refusal applies to.</summary>
    public required string BrickId { get; init; }

    /// <summary>Stage that refused.</summary>
    public required BrickSwapRefusalStage Stage { get; init; }

    /// <summary>Machine-readable failure code (verifier codes such as <c>content-hash-mismatch</c>, or host codes).</summary>
    public string? FailureCode { get; init; }

    /// <summary>Human-readable reason.</summary>
    public string? Reason { get; init; }
}

/// <summary>Outcome of a swap attempt. A refused swap leaves the previous generation serving.</summary>
[Experimental(AutonomyExperimental.DiagnosticId, UrlFormat = AutonomyExperimental.UrlFormat)]
public sealed record CertifiedBrickSwapResult
{
    /// <summary>Whether the new generation was committed.</summary>
    public required bool Swapped { get; init; }

    /// <summary>Committed generation number when <see cref="Swapped"/>.</summary>
    public int? GenerationId { get; init; }

    /// <summary>Load-context name of the committed generation (leak attribution).</summary>
    public string? GenerationContextName { get; init; }

    /// <summary>Brick ids serving in the committed generation.</summary>
    public IReadOnlyList<string> LoadedBrickIds { get; init; } = Array.Empty<string>();

    /// <summary>Every refusal when the swap was refused; empty on success.</summary>
    public IReadOnlyList<BrickSwapRefusal> Refusals { get; init; } = Array.Empty<BrickSwapRefusal>();

    /// <summary>Whether the retired generation's load context was observed collected before returning.</summary>
    public bool PreviousGenerationCollected { get; init; }

    /// <summary>Load-context name of the retired generation, when one existed.</summary>
    public string? PreviousGenerationContextName { get; init; }
}

/// <summary>
/// Provenance event emitted for every swap outcome — committed and refused alike — and
/// for each generation lifecycle transition (spec §3: cert identity, content hash,
/// generation, outcome).
/// </summary>
[Experimental(AutonomyExperimental.DiagnosticId, UrlFormat = AutonomyExperimental.UrlFormat)]
public sealed record BrickSwapProvenanceEvent
{
    /// <summary>Generation number the event concerns.</summary>
    public required int Generation { get; init; }

    /// <summary>Event outcome (see <see cref="BrickSwapProvenanceOutcomes"/>).</summary>
    public required string Outcome { get; init; }

    /// <summary>UTC event time.</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>Brick the event concerns; null for generation-level events.</summary>
    public string? BrickId { get; init; }

    /// <summary>Content hash bound into the brick's certification record.</summary>
    public string? ContentHash { get; init; }

    /// <summary>Signature of the certification record (certificate identity alongside brick id + hash).</summary>
    public string? CertificateSignature { get; init; }

    /// <summary>Machine-readable failure code on refusals.</summary>
    public string? FailureCode { get; init; }

    /// <summary>Human-readable detail.</summary>
    public string? Reason { get; init; }

    /// <summary>Load-context name for generation-level events (leak attribution).</summary>
    public string? ContextName { get; init; }
}

/// <summary>Well-known <see cref="BrickSwapProvenanceEvent.Outcome"/> values.</summary>
[Experimental(AutonomyExperimental.DiagnosticId, UrlFormat = AutonomyExperimental.UrlFormat)]
public static class BrickSwapProvenanceOutcomes
{
    /// <summary>A brick passed verify-at-load and was loaded into the new generation.</summary>
    public const string BrickLoaded = "brick-loaded";

    /// <summary>A brick was refused; the whole swap is refused with it.</summary>
    public const string BrickRefused = "brick-refused";

    /// <summary>The new generation was committed and is serving.</summary>
    public const string SwapCommitted = "swap-committed";

    /// <summary>The swap was refused; the previous generation keeps serving.</summary>
    public const string SwapRefused = "swap-refused";

    /// <summary>A previous generation drained and its unload was requested.</summary>
    public const string GenerationRetired = "generation-retired";

    /// <summary>A retired generation's load context was observed collected.</summary>
    public const string GenerationCollected = "generation-collected";

    /// <summary>A retired generation's load context stayed alive after forced collection; attributable by <see cref="BrickSwapProvenanceEvent.ContextName"/>.</summary>
    public const string GenerationLeakSuspected = "generation-leak-suspected";

    /// <summary>A retained earlier generation was reactivated (autonomy spec R5.1 rollback).</summary>
    public const string RollbackCommitted = "rollback-committed";

    /// <summary>The watch window breached its thresholds; the generation was quarantined (R5.2/R5.3).</summary>
    public const string WatchBreachQuarantined = "watch-breach-quarantined";

    /// <summary>A rollback was refused at verify-at-load or materialization; the generation it was meant to displace is still serving.</summary>
    public const string RollbackRefused = "rollback-refused";

    /// <summary>A watch breach found no unrevoked earlier generation to restore, or the restore did not land; the loop is paused and the breaching generation is still serving.</summary>
    public const string RollbackExhausted = "rollback-exhausted";

    /// <summary>A retained generation failed a check that cannot legitimately fail on a replay (the tier gate); retention is corrupt.</summary>
    public const string RetentionInvariantViolated = "retention-invariant-violated";
}
