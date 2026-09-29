using System.Security.Cryptography;

namespace Ashlar.Infrastructure.Certification;

/// <summary>
/// SPEC-006 §3 signer fingerprint for a certification record's Ed25519 public key, so a ledger
/// row can cite WHO signed a record rather than only that it was signed.
///
/// <para><b>Why a second declaration of a format that already exists.</b>
/// <c>Ashlar.Manifest.Signing.OperatorKey.Fingerprint</c> is the SPEC-006 §3 implementation, and it
/// is the one this must agree with — but <c>Ashlar.Manifest</c> is not in the sweep binary's
/// closure (<c>FirstFlight.csproj</c> references Infrastructure, BackgroundAgents and
/// Agents.TestKit; none of those reference it), so the sweep cannot call it. It is not put in
/// <c>Ashlar.Certification.Contracts</c> either: that assembly is packable, doc-linted,
/// <c>TreatWarningsAsErrors</c>, and multi-targets netstandard2.0, where neither
/// <c>Convert.ToHexString</c> nor <c>SHA256.HashData</c> exists.</para>
///
/// <para><b>The drift risk is closed by a test, not by this comment.</b>
/// <c>CertificationEvidenceArchiveTests.PersistAndReverify_ReportsTheFingerprintOfTheKeyThatSignedTheRecord</c>
/// asserts equality against the real <c>OperatorKey.Fingerprint</c> (the test project does
/// reference <c>Ashlar.Manifest</c>), so changing either format reddens cert-gate. Restating the
/// format in prose would not have caught it.</para>
/// </summary>
public static class CertificationSignerFingerprint
{
    /// <summary>
    /// The SPEC-006 §3 fingerprint of a Base64 raw Ed25519 public key: <c>ed25519:</c> plus the
    /// first 16 lowercase hex characters of SHA-256 over the RAW key bytes.
    /// </summary>
    /// <param name="ed25519PublicKeyBase64">
    /// Base64 raw Ed25519 public key, as a certification record carries it.
    /// </param>
    /// <returns>
    /// The fingerprint, or <c>null</c> when the input is absent or is not Base64. A fingerprint
    /// that cannot be computed is reported as ABSENT rather than guessed at: a ledger row cites
    /// this string, and a plausible-looking value derived from bytes that are not a key would be
    /// a citation to nothing. The caller's own verdict fields say whether the record verified;
    /// this method never contributes to that decision.
    /// </returns>
    public static string? Of(string? ed25519PublicKeyBase64)
    {
        if (string.IsNullOrWhiteSpace(ed25519PublicKeyBase64))
            return null;

        byte[] raw;
        try
        {
            raw = Convert.FromBase64String(ed25519PublicKeyBase64);
        }
        catch (FormatException)
        {
            return null;
        }

        return "ed25519:" + Convert.ToHexString(SHA256.HashData(raw))[..16].ToLowerInvariant();
    }
}
