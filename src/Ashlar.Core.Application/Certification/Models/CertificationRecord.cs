using Ashlar.Certification.Contracts;

namespace Ashlar.Core.Application.Certification.Models;

/// <summary>
/// Signed certification record emitted when the gate runs. Status is PASS (admitted) or FAIL (refused).
/// </summary>
public sealed record CertificationRecord
{
    /// <summary>Certification status label: <c>PASS</c> when the gate admitted the candidate, <c>FAIL</c> when it refused. (Distinct from the CLI-facing admit/refuse vocabulary.)</summary>
    public required string Status { get; init; }

    /// <summary>Certification stage that produced this record.</summary>
    public required string Stage { get; init; }

    /// <summary>Whether the brick was admitted for runtime use.</summary>
    public required bool Admitted { get; init; }

    /// <summary>Whether the record carries a cryptographic signature.</summary>
    public required bool Signed { get; init; }

    /// <summary>UTC timestamp when certification completed.</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>Brick identifier under certification.</summary>
    public required string BrickId { get; init; }

    /// <summary>Hash of the certified source content.</summary>
    public string? ContentHash { get; init; }

    /// <summary>Fraction of mutants that escaped detection (0.0–1.0).</summary>
    public double? EscapeRate { get; init; }

    /// <summary>Total mutants generated during certification.</summary>
    public int? TotalMutants { get; init; }

    /// <summary>Mutants that survived certification checks.</summary>
    public int? SurvivingMutants { get; init; }

    /// <summary>Identifiers of mutants killed by certification.</summary>
    public IReadOnlyList<string> KilledMutants { get; init; } = Array.Empty<string>();

    /// <summary>Identifiers of mutants that survived certification.</summary>
    public IReadOnlyList<string> SurvivingMutantIds { get; init; } = Array.Empty<string>();

    /// <summary>Cryptographic signature of the admission record.</summary>
    public string? Signature { get; init; }

    /// <summary>Human-readable reason for admission or rejection.</summary>
    public string? Reason { get; init; }

    /// <summary>Gate identifier that produced this record.</summary>
    public string? Gate { get; init; }

    /// <summary>Certificate schema version; null means legacy v1 (pre-trust-loop payload).</summary>
    public int? SchemaVersion { get; init; }

    /// <summary>The skill's declared highest data label, bound by schema v3 signatures.</summary>
    public string? HighestDataLabel { get; init; }

    /// <summary>Ordered chain of gates evaluated at the recorded content hash; on FAIL records, the gates passed before the failure.</summary>
    public IReadOnlyList<CertificationGatePass> GatesPassed { get; init; } = Array.Empty<CertificationGatePass>();

    /// <summary>Hashes of the truth sources and context used during certification.</summary>
    public IReadOnlyList<CertificationInput> Inputs { get; init; } = Array.Empty<CertificationInput>();

    /// <summary>Identity and parameters of the proposer of the certified artifact.</summary>
    public CertificationProposer? Proposer { get; init; }

    /// <summary>Effort ledger of proposal/repair attempts.</summary>
    public IReadOnlyList<CertificationAttempt> Attempts { get; init; } = Array.Empty<CertificationAttempt>();

    /// <summary>Base64 Ed25519 signature dual-written alongside <see cref="Signature"/>.</summary>
    public string? Ed25519Signature { get; init; }

    /// <summary>Base64 raw Ed25519 public key of the minter; covered by the signed payload.</summary>
    public string? Ed25519PublicKey { get; init; }
}
