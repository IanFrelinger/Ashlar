using Ashlar.Manifest.Signing;

namespace Ashlar.Manifest.Admission;

/// <summary>
/// The signature posture a <see cref="GateStore"/> judges its records against — the half of
/// SPEC-006 rule S-1 that the rule itself is silent about. S-1 says a signature that FAILS
/// verification is corruption; it says nothing about a signature that was REMOVED, and from a
/// record's own bytes a stripped signature and an honestly unsigned record are the same bytes.
/// So whether a missing signature is corruption cannot be decided from the record. It is decided
/// from what the STORE is known to be, and that knowledge is resolved once per read from the
/// store's own verifying records and the operator's key material — never from the bytes of the
/// record being judged, which is the attacker's input.
///
/// <para>Pure: no I/O, no clock, no ambient state. <see cref="GateStore"/> resolves one of these
/// and applies <see cref="Refuse"/> to every record it lets out.</para>
/// </summary>
/// <param name="Expected">True when this store is known to be signed, so an unsigned record
/// decided inside the expectation window is a stripped signature — corruption.</param>
/// <param name="GraceBefore">Records decided strictly before this instant predate signing and are
/// grandfathered unsigned. Null with <paramref name="Expected"/> true means no grace at all.</param>
/// <param name="TrustedSigners">Base64 public keys the reader's key material vouches for. When
/// non-empty, a verifying signature must also be from one of these: a forger who cannot strip a
/// signature can still REPLACE it with one from a key of their own, and only pinning catches that.
/// Empty for a keyless reader, which verifies intrinsically only — bundle consumers and fresh
/// checkouts must keep reading.</param>
/// <param name="Basis">Which anchors fired, in words, for renderers and refusal messages.</param>
public sealed record GateSignatureExpectation(
    bool Expected,
    DateTimeOffset? GraceBefore,
    IReadOnlyList<string> TrustedSigners,
    string Basis)
{
    /// <summary>No anchor: a present signature is verified and pinned, a missing one is
    /// tolerated — today's behaviour exactly (rule S-2).</summary>
    public static GateSignatureExpectation None(IReadOnlyList<string> trustedSigners, string basis) =>
        new(false, null, trustedSigners, basis);

    /// <summary>
    /// The reason <paramref name="record"/> must be refused as corrupt, or null when it passes.
    /// Three legs, in order: a PRESENT signature must verify (the S-1 refusal the store has always
    /// raised, byte-identical); a verifying signature must be from a trusted signer when any are
    /// pinned; and a MISSING signature is refused iff the store expects one and the record was
    /// decided at or after <see cref="GraceBefore"/>.
    /// </summary>
    /// <param name="record">The record as parsed from disk.</param>
    /// <param name="fileName">The record's file name, for the message — the operator's pointer.</param>
    public string? Refuse(GateRecord record, string fileName)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (record.Sig is not null)
        {
            var unsigned = record with { Sig = null, Signer = null };
            if (record.Signer is null
                || !OperatorKey.Verify(record.Signer, CanonicalJson.Bytes(unsigned), record.Sig))
            {
                return $"Corrupt gate record: {fileName} carries a signature that does not verify. "
                    + "Refusing to operate — a forged verdict is worse than a missing one.";
            }

            if (TrustedSigners.Count > 0 && !TrustedSigners.Contains(record.Signer, StringComparer.Ordinal))
            {
                // No fingerprint here (rule S-3): the signature verified cryptographically but this
                // machine's key material does not vouch for the key, so the record is not verified in
                // the sense a renderer may repeat. The file name is the actionable pointer.
                return $"Corrupt gate record: {fileName} is signed by a key that is neither the operator key nor "
                    + "any key retained under trusted/. A signature from a key this machine does not vouch for is a "
                    + "REPLACED signature, not a verified one. Refusing to operate — a forged verdict is worse than a "
                    + "missing one.";
            }

            return null;
        }

        if (Expected && (GraceBefore is null || record.DecidedAt >= GraceBefore.Value))
        {
            return $"Corrupt gate record: {fileName} carries no signature, but this store is signed ({Basis}). "
                + "A removed signature and an honestly unsigned record are the same bytes, so a missing signature on "
                + "a record decided after signing was activated is treated as stripped. Refusing to operate — a forged "
                + "verdict is worse than a missing one. Deleting records from gates/ is NOT the remedy; if this machine "
                + "should be signing, run `ashlar keys init`.";
        }

        return null;
    }
}
