using System.Diagnostics.CodeAnalysis;
using Ashlar.Certification.Contracts;
using Ashlar.Core.Domain.Bricks;
using Ashlar.Core.Domain.Bricks.Ports;

namespace Ashlar.Core.Application.Certification.Models;

/// <summary>
/// Input to the brick certification gate.
/// </summary>
public sealed record CertificationRequest
{
    /// <summary>Declared maximum data level. Missing declarations are SystemHigh, never Public.</summary>
    public string HighestDataLabel { get; init; } = "SystemHigh";

    /// <summary>Brick definition under certification.</summary>
    public required DomainBrick Brick { get; init; }

    /// <summary>Witness cases for behavioral verification.</summary>
    public required WitnessSpec Witness { get; init; }

    /// <summary>Brick source code for compilation and mutation testing.</summary>
    public required string SourceCode { get; init; }

    /// <summary>Project path used for compilation context.</summary>
    public required string ProjectPath { get; init; }

    /// <summary>Additional assembly references required for compilation.</summary>
    public IReadOnlyList<string> CompilationReferences { get; init; } = Array.Empty<string>();

    /// <summary>Optional fully-qualified brick type name override.</summary>
    public string? BrickTypeName { get; init; }

    /// <summary>
    /// Extra hash-identified inputs to record on the certificate beyond the witness
    /// (trust-loop spec §2.1 <c>inputs</c>) — e.g. the assembled proposal context via
    /// <c>ProposalContext.ToCertificationInput</c>. Evidence, not a gate: entries are
    /// recorded under the v2 signature, in authored order after the witness input.
    /// </summary>
    public IReadOnlyList<CertificationInput> AdditionalInputs { get; init; } = Array.Empty<CertificationInput>();

    /// <summary>
    /// The constraint manifest this candidate was generated under, when there is one.
    /// Pass the SAME instance the proposer's instructions were rendered from (extension spec
    /// A2.3): the analyzer gate constructs its manifest-derived rules from this object, which is
    /// what keeps prompt and enforcement from drifting. Null = no manifest rules; the static
    /// analyzer catalog still runs.
    /// </summary>
    public BrickConstraintManifest? ConstraintManifest { get; init; }

    /// <summary>
    /// The objective's declared touch-set, when the candidate came from a tiered objective
    /// (autonomous self-extension spec R1.3/R3.2). Pass the SAME declaration that classified
    /// the tier and derived the session's sandbox mounts: the analyzer gate enforces the
    /// candidate's actual reference graph against it, and an undeclared reference into the
    /// trust kernel is a certification FAIL. Null = no touch-set rules.
    /// </summary>
    [Experimental(Autonomy.AutonomyExperimental.DiagnosticId, UrlFormat = Autonomy.AutonomyExperimental.UrlFormat)]
    public Autonomy.TouchSet? TouchSet { get; init; }

    /// <summary>
    /// The candidate's recursion pedigree (autonomy spec R4.1). Null is treated as
    /// human-authored context (depth 0) — the certifier validates coherent lineages
    /// (laundering matrix) and enforces the depth ceiling before any other gate runs, and
    /// records the depth plus a parent-chain hash into the certificate's inputs.
    /// </summary>
    [Experimental(Autonomy.AutonomyExperimental.DiagnosticId, UrlFormat = Autonomy.AutonomyExperimental.UrlFormat)]
    public Autonomy.GenerationLineage? Lineage { get; init; }

    /// <summary>
    /// Where candidate and mutant code EXECUTES during certification. Null (the default)
    /// keeps today's in-process path byte-for-byte. When set, the witness, determinism,
    /// and mutation legs run every execution of untrusted candidate code through this
    /// backend and judge the raw observations in the gate — the backend runs, the gate
    /// decides. A <c>session-execution</c> input naming the backend is recorded on the
    /// certificate either way the verdict goes.
    /// </summary>
    public Ports.ICandidateExecutionBackend? ExecutionBackend { get; init; }

    /// <summary>
    /// Assembly the certifier compiled from <see cref="SourceCode"/>. When set, the
    /// certificate records its hash as a signed <c>gate-emitted-artifact</c> input and
    /// exporters ship these exact bytes. Null keeps the in-process fixture path used by
    /// unit tests that construct <see cref="DomainBrick"/> directly.
    /// </summary>
    public GateEmittedArtifact? EmittedArtifact { get; init; }
}
