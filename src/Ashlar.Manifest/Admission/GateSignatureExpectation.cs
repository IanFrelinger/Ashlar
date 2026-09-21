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
/// <param name="DerivedGraceBefore">The WEAK floor, used only when no inventory-bearing marker
/// fired: the minimum <c>DecidedAt</c> over the records that still verify. It is a date comparison
/// against a field an unsigned record's writer controls, so it grandfathers anything back-dated
/// below it — which is why the inventory replaced it, and why the residual that this mode still
/// exists is written into SPEC-006 rather than papered over. Note also that a derived anchor can
/// never date the EARLIEST record in the store: the floor is the minimum over the survivors, so it
/// is at or before that record.</param>
/// <param name="TrustedSigners">Base64 public keys the reader's key material vouches for. When
/// non-empty, a verifying signature must also be from one of these: a forger who cannot strip a
/// signature can still REPLACE it with one from a key of their own, and only pinning catches that.
/// Empty for a keyless reader, which verifies intrinsically only — bundle consumers and fresh
/// checkouts must keep reading.</param>
/// <param name="Basis">Which anchors fired, in words, for renderers and refusal messages.</param>
/// <param name="MarkerActivatedAt">The instant on the activation marker when one is on disk —
/// whether or not this reader honours it — and null when there is no marker at all. It is NOT a
/// grace floor and MUST NOT be compared against a record: it exists so the keyless write guard can
/// refuse on the marker's mere presence and name the instant, without reading the marker a second
/// time and disagreeing with the read that judged the records.</param>
/// <param name="Grandfathered">The inventory of unsigned records the operator authorized, carried
/// off an activation marker this reader HONOURS — and null when no such marker fired, which is the
/// tell that grandfathering has fallen back to <paramref name="DerivedGraceBefore"/>. Null and
/// empty are different answers: empty means the operator authorized nothing, so every unsigned
/// record is refused; null means nobody authorized anything and the weak date floor applies.</param>
/// <param name="SignedRecordAnchors">How many records in this store carry a signature that
/// verifies and pins. Zero with <paramref name="Expected"/> true means the whole posture rests on
/// the marker — a structural tell worth showing an operator.</param>
public sealed record GateSignatureExpectation(
    bool Expected,
    DateTimeOffset? DerivedGraceBefore,
    IReadOnlyList<string> TrustedSigners,
    string Basis,
    DateTimeOffset? MarkerActivatedAt,
    IReadOnlyList<GrandfatheredRecord>? Grandfathered,
    int SignedRecordAnchors)
{
    /// <summary>Whether an activation marker exists on disk at all, honoured or ignored. The
    /// keyless write guard is deliberately stricter than the read rule and turns on this rather
    /// than on <see cref="Expected"/>: a keyless writer cannot vouch for a marker and must not
    /// gamble that a keyed reader will not honour it.</summary>
    public bool MarkerPresent => MarkerActivatedAt is not null;

    /// <summary>No anchor: a present signature is verified and pinned, a missing one is
    /// tolerated — today's behaviour exactly (rule S-2).</summary>
    public static GateSignatureExpectation None(IReadOnlyList<string> trustedSigners, string basis) =>
        new(false, null, trustedSigners, basis, null, null, 0);

    /// <summary>
    /// The reason <paramref name="record"/> must be refused as corrupt, or null when it passes.
    /// Four legs, in order: a PRESENT signature must verify (the S-1 refusal the store has always
    /// raised, byte-identical); a verifying signature must be from a trusted signer when any are
    /// pinned; a MISSING signature is refused unless the operator's inventory names this record
    /// with these bytes (or, on the weak derived-only basis, unless it predates
    /// <see cref="DerivedGraceBefore"/>); and, evaluated LAST so every message above stays
    /// byte-identical, a record's file name must be the id inside its own bytes.
    /// </summary>
    /// <param name="record">The record as parsed from disk.</param>
    /// <param name="fileName">The record's file name, for the message — the operator's pointer.</param>
    /// <param name="canonicalSha256">Lowercase hex sha256 over the record's canonical bytes, which
    /// the store computed where the bytes were in hand. This type stays pure: the store hashes,
    /// the expectation compares.</param>
    public string? Refuse(GateRecord record, string fileName, string canonicalSha256)
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
        }
        else if (Expected && Grandfathered is not null)
        {
            // MEMBERSHIP, not a timestamp. The record cannot testify about itself, and DecidedAt on
            // an unsigned record is the writer's own field — a floor compared against it
            // grandfathers anything back-dated below it, with no stripping needed at all.
            var entry = Grandfathered.FirstOrDefault(g => string.Equals(g.Id, record.Proposal.Id, StringComparison.Ordinal));
            if (entry is null)
            {
                return $"Corrupt gate record: {fileName} carries no signature, but this store is signed ({Basis}). "
                    + "The activation marker names every unsigned record the operator authorized, by id and by hash, "
                    + "and this one is not among them. A removed signature and an honestly unsigned record are the same "
                    + "bytes, so a record nobody vouched for is treated as stripped. Refusing to operate — a forged "
                    + "verdict is worse than a missing one. Deleting records from gates/ is NOT the remedy; if this "
                    + "machine should be signing, run `ashlar keys init`.";
            }
            if (!string.Equals(entry.Sha256, canonicalSha256, StringComparison.Ordinal))
            {
                return $"Corrupt gate record: {fileName} is grandfathered unsigned, but its bytes have changed since "
                    + $"the operator authorized it: the activation marker pins sha256 {entry.Sha256} for proposal "
                    + $"'{record.Proposal.Id}' and this record canonicalizes to {canonicalSha256}. A grandfathered "
                    + "record is trusted for the bytes it had at activation and for nothing else. Refusing to "
                    + "operate — a forged verdict is worse than a missing one.";
            }
        }
        else if (Expected && !(DerivedGraceBefore is not null && record.DecidedAt < DerivedGraceBefore.Value))
        {
            // The WEAK basis: no marker fired, so the floor is derived from the records that still
            // verify. Named as weaker in the message, because the operator reading it should reach
            // for the verb that replaces the date with a list rather than treat this as normal.
            return $"Corrupt gate record: {fileName} carries no signature, but this store is signed ({Basis}). "
                + "There is no activation marker here, so grandfathering falls back to a date floor derived from the "
                + "records that still verify — a weaker rule, because the date on an unsigned record is written by "
                + "whoever wrote the record. Refusing to operate — a forged verdict is worse than a missing one. "
                + "Deleting records from gates/ is NOT the remedy; re-mint the marker with "
                + "`ashlar gates sign-activate --repair`, or, if this machine should be signing, run "
                + "`ashlar keys init`.";
        }

        // The id is INSIDE the signed bytes; the file name is not. Copy one legitimately signed
        // admission to two more names and every copy verifies, every copy is enumerated, and the
        // self-extension budget — which counts files, because that is all there is to count —
        // reads three admissions where the operator made one. Scoped to a store that is signed or
        // to a record that carries a signature, because an unconditional refusal would change what
        // a never-signed store does today (S-2); conditioning on the signature still fires during
        // the anchor pass, so a duplicate cannot anchor a posture for the rest of the store.
        if (Expected || record.Sig is not null)
        {
            var owned = record.Proposal.Id + ".json";
            if (!string.Equals(fileName, owned, StringComparison.Ordinal))
            {
                return $"Corrupt gate record: {fileName} holds proposal '{record.Proposal.Id}', whose record is "
                    + $"{owned}. A record under a second name is a COPY — the id is signed, the file name is not, and "
                    + "the self-extension budget counts files. Refusing to operate — a forged verdict is worse than a "
                    + "missing one.";
            }
        }

        return null;
    }
}
