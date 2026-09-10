namespace Ashlar.Certification.Contracts;

/// <summary>
/// External consumer verifier: signature + content binding. No gate or generator required.
/// </summary>
public static class CertificationTrustVerifier
{
    /// <summary>
    /// Verifies that a certification record is trusted for the supplied brick source.
    /// Checks admission status, signature validity, and content hash binding.
    /// </summary>
    /// <param name="record">Certification record to verify.</param>
    /// <param name="brickSource">Canonical brick source text to hash and compare.</param>
    /// <param name="hmacKey">Optional HMAC key override for signature verification.</param>
    /// <param name="options">
    /// Strictness to apply (SPEC-006 S-1 and S-5). Null uses
    /// <see cref="CertificationVerifyOptions.Default"/>, which is now fail-closed (Ed25519
    /// required, trust-loop schema floor). Use <see cref="CertificationVerifyOptions.Legacy"/>
    /// for pre-trust-loop records.
    /// </param>
    public static CertificationTrustResult Verify(
        CertificationRecordData record,
        string brickSource,
        string? hmacKey = null,
        CertificationVerifyOptions? options = null)
    {
        var strictness = options ?? CertificationVerifyOptions.Default;

        if (!record.Admitted || !string.Equals(record.Status, "PASS", StringComparison.Ordinal))
            return Untrusted("record-not-admitted", "Certification record is not an admitted PASS.");

        // The floor is checked BEFORE any signature, because the schema version selects which
        // canonical payload the signature covers. A downgraded record can carry a perfectly
        // valid HMAC over a payload that omits Gate, GatesPassed, Inputs, Proposer, Attempts
        // and Ed25519PublicKey, so verifying the signature first would answer the wrong
        // question. A null schema version is the legacy lane and counts as 0.
        if ((record.SchemaVersion ?? 0) < strictness.MinimumSchemaVersion)
        {
            return Untrusted(
                "schema-version-below-floor",
                $"Certification record schema version {record.SchemaVersion?.ToString() ?? "(none)"} is below "
                + $"the minimum accepted version {strictness.MinimumSchemaVersion}.");
        }

        if (!record.Signed)
            return Untrusted("record-unsigned", "Certification record is not signed.");

        if (string.IsNullOrWhiteSpace(record.ContentHash))
            return Untrusted("content-hash-missing", "Certification record has no content hash.");

        // Built BEFORE the signature is compared, and unconditionally. Both sides of a comparison
        // recompute the payload from the same serializer, so the comparison cannot establish
        // anything about bytes whose shape has not been established first. Establish the shape,
        // then compare.
        try
        {
            CertificationRecordSigning.BuildPayload(record);
        }
        catch (CanonicalPayloadException ex)
        {
            return Untrusted("payload-not-canonical", ex.Message);
        }
        catch (Exception ex)
        {
            // Record JSON arrives from a file and is deserialized without its non-null
            // annotations being enforced, so building the payload can fail on a member the
            // shape guard never gets to see. Every way of failing to establish the bytes is the
            // same answer — they cannot back a signature — and the caller is owed that answer
            // rather than an exception raised in its host.
            return Untrusted(
                "payload-unbuildable",
                $"Certification record canonical payload could not be built: {ex.Message}");
        }

        if (!CertificationRecordSigning.VerifySignature(record, hmacKey))
            return Untrusted("signature-invalid", "Certification record signature is invalid.");

        // Dual-write window: the Ed25519 signature is enforced whenever present. Presence is
        // controlled by the record's own bytes, so this alone is not a strictness control —
        // a record can arrive without the field as easily as with a bad one.
        // RequireEd25519Signature is what turns absence into a refusal.
        //
        // The presence test and the key test sit OUTSIDE the target-framework fence on purpose.
        // "Present" has to mean the same bytes on every target, and a signature without a key
        // needs no cryptography to refuse; only the evaluation itself differs per target. Keeping
        // the prelude shared is what makes the failure code for a multi-fault record the same
        // on every target, not just the verdict.
        if (!string.IsNullOrWhiteSpace(record.Ed25519Signature))
        {
            if (string.IsNullOrWhiteSpace(record.Ed25519PublicKey))
                return Untrusted("ed25519-key-missing", "Certification record carries an Ed25519 signature but no public key.");

#if NET8_0_OR_GREATER
            if (!CertificationRecordEd25519.VerifySignature(record))
                return Untrusted("ed25519-signature-invalid", "Certification record Ed25519 signature is invalid.");
#else
            // netstandard2.0 has no NSec target, so the signature cannot be evaluated here. A
            // signature that cannot be checked is refused rather than skipped: the net8.0+
            // targets refuse this record unless the signature verifies, so falling through to
            // the content-hash check would trust here what they refuse there — the same bytes,
            // a contradictory verdict. The set of records this target trusts is therefore a
            // subset of what the others trust (HMAC-only records), never a superset.
            return Untrusted(
                "ed25519-signature-unverifiable",
                "Certification record carries an Ed25519 signature that this build cannot evaluate "
                + "(netstandard2.0 has no NSec target). A signature that cannot be checked is refused rather than skipped.");
#endif
        }
        else if (strictness.RequireEd25519Signature || strictness.PinningEnabled)
        {
#if NET8_0_OR_GREATER
            return Untrusted(
                "ed25519-signature-required",
                "Certification record carries no Ed25519 signature and this verifier requires one.");
#else
            // Under strictness the absence is refused here as well, in this target's own terms:
            // returning "trusted" for a check that did not run — or stamping a record as pinned
            // when the signature math never executed — would be worse than the silent skip it
            // replaces. Only an absent signature reaches this code; a present one was refused
            // above.
            return Untrusted(
                "ed25519-verification-unavailable",
                "Ed25519 strictness was requested but this build cannot verify Ed25519 signatures "
                + "(netstandard2.0 has no NSec target). Refusing rather than reporting an unchecked pass.");
#endif
        }

#if NET8_0_OR_GREATER
        // Pinning closes the remaining gap: VerifySignature checks the signature against the
        // public key the RECORD carries, so a record signed with an attacker's own keypair is
        // self-consistent and passes. Requiring a signature without pinning only forces the
        // attacker to sign instead of strip.
        if (strictness.PinningEnabled
            && !strictness.TrustedEd25519PublicKeys!.Contains(record.Ed25519PublicKey!, StringComparer.Ordinal))
        {
            return Untrusted(
                "ed25519-key-not-trusted",
                "Certification record is signed by a key this verifier does not accept.");
        }
#endif

        var actualHash = BrickContentHasher.ComputeSha256(brickSource);
        if (!string.Equals(actualHash, record.ContentHash, StringComparison.Ordinal))
        {
            return Untrusted(
                "content-hash-mismatch",
                $"Brick source hash does not match certified content (expected {record.ContentHash}, got {actualHash}).");
        }

        if (strictness.RequireCertifierIdentity
            && !HasInputKind(record, CertificationInputKinds.CertifierIdentity))
        {
            return Untrusted(
                "certifier-identity-missing",
                "Certification record does not name its judge (certifier-identity input) and this verifier requires one.");
        }

        if (strictness.RequireGateEmittedArtifact
            && !HasInputKind(record, CertificationInputKinds.GateEmittedArtifact))
        {
            return Untrusted(
                "gate-emitted-artifact-missing",
                "Certification record does not bind a gate-emitted assembly and this verifier requires one.");
        }

        return new CertificationTrustResult(true, null, null);
    }

    /// <summary>
    /// Verifies a record against both source and the gate-emitted assembly bytes that a
    /// consumer is about to load. Source binding plus artifact-hash binding is what makes
    /// "judged = shipped" checkable outside the certifier.
    /// </summary>
    public static CertificationTrustResult Verify(
        CertificationRecordData record,
        string brickSource,
        byte[] artifactBytes,
        string? hmacKey = null,
        CertificationVerifyOptions? options = null)
    {
        var sourceResult = Verify(record, brickSource, hmacKey, options);
        if (!sourceResult.Trusted)
            return sourceResult;

        if (artifactBytes is null || artifactBytes.Length == 0)
            return Untrusted("artifact-bytes-missing", "Gate-emitted assembly bytes were not supplied.");

        var expected = FindInputHash(record, CertificationInputKinds.GateEmittedArtifact);
        if (expected is null)
        {
            return Untrusted(
                "gate-emitted-artifact-missing",
                "Certification record does not bind a gate-emitted assembly.");
        }

        var actual = BrickContentHasher.ComputeSha256(artifactBytes);
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            return Untrusted(
                "artifact-hash-mismatch",
                $"Gate-emitted assembly hash does not match the certificate (expected {expected}, got {actual}).");
        }

        return new CertificationTrustResult(true, null, null);
    }

    private static bool HasInputKind(CertificationRecordData record, string kind) =>
        record.Inputs.Any(i => string.Equals(i.Kind, kind, StringComparison.Ordinal));

    private static string? FindInputHash(CertificationRecordData record, string kind) =>
        record.Inputs.FirstOrDefault(i => string.Equals(i.Kind, kind, StringComparison.Ordinal))?.Hash;

    private static CertificationTrustResult Untrusted(string code, string reason) =>
        new(false, code, reason);
}
