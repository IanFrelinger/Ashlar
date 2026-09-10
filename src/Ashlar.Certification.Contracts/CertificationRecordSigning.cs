using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Ashlar.Certification.Contracts;

/// <summary>
/// Canonical HMAC signing for certification records (shared by gate and external verifier).
/// </summary>
public static class CertificationRecordSigning
{
    /// <summary>
    /// Development-only default HMAC key; override via <c>ASHLAR_CERT_DEV_HMAC_KEY</c> in production.
    /// This constant is COMMITTED and PUBLIC: any record signed with it can be forged by anyone
    /// who has read this file, so a signature under it proves integrity against accident, not
    /// against an adversary. Signers warn at construction while it is in effect
    /// (<see cref="UsesDevKey"/>).
    /// </summary>
    public const string DefaultDevKey = "ashlar-cert-dev-hmac-v0";

    /// <summary>Environment variable that supplies the HMAC key when no explicit key is given.</summary>
    public const string HmacKeyEnvVar = "ASHLAR_CERT_DEV_HMAC_KEY";

    /// <summary>
    /// Whether signing with <paramref name="hmacKey"/> (explicit key, else <see cref="HmacKeyEnvVar"/>,
    /// else <see cref="DefaultDevKey"/>) would use the committed development key — or a blank one,
    /// which is no better. True means every certificate minted or verified through this key is
    /// forgeable by anyone with the source.
    /// </summary>
    /// <param name="hmacKey">Optional explicit key, resolved the same way <see cref="Sign"/> resolves it.</param>
    public static bool UsesDevKey(string? hmacKey = null)
    {
        var effective = ResolveKey(hmacKey);
        return string.IsNullOrWhiteSpace(effective)
            || string.Equals(effective, DefaultDevKey, StringComparison.Ordinal);
    }

    /// <summary>
    /// Computes the Base64 HMAC-SHA256 signature for a certification record.
    /// The signature field on <paramref name="record"/> is excluded from the payload.
    /// </summary>
    /// <param name="record">Record to sign.</param>
    /// <param name="hmacKey">Optional explicit key; falls back to environment or <see cref="DefaultDevKey"/>.</param>
    public static string Sign(CertificationRecordData record, string? hmacKey = null)
    {
        var payload = BuildPayload(record);
        var keyBytes = Encoding.UTF8.GetBytes(ResolveKey(hmacKey));
        using var hmac = new HMACSHA256(keyBytes);
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return Convert.ToBase64String(hash);
    }

    /// <summary>
    /// Verifies the record's <see cref="CertificationRecordData.Signature"/> against the canonical payload.
    /// Returns false when the signature is missing, malformed, does not match, or when the
    /// canonical payload itself is not in its declared shape. A verifier answers the question
    /// it was asked, so an unusable payload is a refusal here rather than an exception thrown
    /// into the host; <see cref="CertificationTrustVerifier"/> reports it as its own failure
    /// code so the two faults stay distinguishable.
    /// </summary>
    /// <param name="record">Record containing the signature to verify.</param>
    /// <param name="hmacKey">Optional explicit key; falls back to environment or <see cref="DefaultDevKey"/>.</param>
    public static bool VerifySignature(CertificationRecordData record, string? hmacKey = null)
    {
        if (string.IsNullOrWhiteSpace(record.Signature))
            return false;

        try
        {
            var expected = Sign(record with { Signature = null }, hmacKey);
            return FixedTimeEquals(
                Convert.FromBase64String(record.Signature),
                Convert.FromBase64String(expected));
        }
        catch (FormatException)
        {
            return false;
        }
        catch (CanonicalPayloadException)
        {
            return false;
        }
    }

    private static bool FixedTimeEquals(byte[] left, byte[] right)
    {
#if NET5_0_OR_GREATER
        return CryptographicOperations.FixedTimeEquals(left, right);
#else
        if (left.Length != right.Length)
            return false;
        var diff = 0;
        for (var i = 0; i < left.Length; i++)
            diff |= left[i] ^ right[i];
        return diff == 0;
#endif
    }

    private static readonly JsonSerializerOptions PayloadOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>
    /// Whether <paramref name="schemaVersion"/> selects a canonical payload lane: null is the
    /// legacy v1 lane, <see cref="CertificationRecordData.TrustLoopSchemaVersion"/> is v2, and
    /// nothing else does. The single source of truth for the signing and the verification
    /// paths, so the two cannot disagree about which versions exist. A floor
    /// (<see cref="CertificationVerifyOptions.MinimumSchemaVersion"/>) says "at least this
    /// new"; it cannot say "a version this code knows", which is what this answers.
    /// </summary>
    /// <param name="schemaVersion">The record's declared schema version, null for legacy v1.</param>
    public static bool IsKnownSchemaVersion(int? schemaVersion) =>
        schemaVersion is null || schemaVersion.Value == CertificationRecordData.TrustLoopSchemaVersion;

    /// <summary>
    /// Builds the canonical JSON payload used for signing and verification.
    /// Records without a <see cref="CertificationRecordData.SchemaVersion"/> use the
    /// legacy v1 payload byte-for-byte, so pre-trust-loop signatures stay valid.
    /// Records at <see cref="CertificationRecordData.TrustLoopSchemaVersion"/> sign the
    /// extended payload, which additionally covers <c>Gate</c>, the trust-loop evidence
    /// fields, and the Ed25519 public key. No other version selects a lane
    /// (<see cref="IsKnownSchemaVersion"/>). Both signature fields are structurally
    /// excluded. Mutant id lists and input entries are sorted for deterministic
    /// serialization; gate and attempt order is semantic and preserved.
    /// </summary>
    /// <param name="record">Record to serialize.</param>
    /// <exception cref="CanonicalPayloadException">
    /// The serializer did not produce the lane's declared shape, or the record's schema version
    /// selects no lane at all. Serialization here is reflection-based, which does not survive
    /// trimming or ahead-of-time publishing, so the payload can silently come out empty or
    /// short; and a version this code has never seen would otherwise be serialized under a
    /// shape chosen by guesswork. These bytes back every signature, so in either case they are
    /// refused rather than signed.
    /// </exception>
    public static string BuildPayload(CertificationRecordData record)
    {
        if (record.SchemaVersion is null)
            return EnsureCanonical(BuildLegacyPayload(record), versioned: false);

        if (record.SchemaVersion.Value == CertificationRecordData.TrustLoopSchemaVersion)
            return EnsureCanonical(BuildVersionedPayload(record, record.SchemaVersion.Value), versioned: true);

        // An unknown version selects no lane. Serializing it under the v2 shape would emit the
        // version verbatim inside bytes whose meaning this code cannot know, and a record so
        // stamped would clear every floor at or below its number. An unknown schema version is
        // an error, not a guess — the same exception as a degenerate payload, so it propagates
        // at mint time and is refused on every verification path.
        throw new CanonicalPayloadException(
            $"Certification record schema version {record.SchemaVersion.Value} does not select a canonical "
            + $"payload lane (known versions: none for v1, {CertificationRecordData.TrustLoopSchemaVersion} for v2), "
            + "so no signature can be computed or checked over it.");
    }

    private static string BuildVersionedPayload(CertificationRecordData record, int schemaVersion)
    {
        var clone = new VersionedPayload(
            schemaVersion,
            record.Status,
            record.Stage,
            record.Admitted,
            record.Signed,
            record.Timestamp.UtcDateTime.ToString("O"),
            record.BrickId,
            record.ContentHash,
            record.EscapeRate,
            record.TotalMutants,
            record.SurvivingMutants,
            record.KilledMutants.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            record.SurvivingMutantIds.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            record.Reason,
            record.Gate,
            record.GatesPassed.Select(g => new GatePassPayload(g.Name, g.Version, g.Configuration)).ToArray(),
            record.Inputs
                .OrderBy(i => i.Kind, StringComparer.Ordinal)
                .ThenBy(i => i.Id, StringComparer.Ordinal)
                .Select(i => new InputPayload(i.Kind, i.Id, i.Hash))
                .ToArray(),
            record.Proposer is null
                ? null
                : new ProposerPayload(
                    record.Proposer.Identity,
                    record.Proposer.Parameters
                        .OrderBy(p => p.Key, StringComparer.Ordinal)
                        .ToDictionary(p => p.Key, p => p.Value),
                    record.Proposer.Seed),
            record.Attempts.Select(a => new AttemptPayload(a.Index, a.Outcome, a.FailureCategory, a.DurationSeconds)).ToArray(),
            record.Ed25519PublicKey);
        return JsonSerializer.Serialize(clone, PayloadOptions);
    }

    private static string BuildLegacyPayload(CertificationRecordData record)
    {
        var clone = new
        {
            record.Status,
            record.Stage,
            record.Admitted,
            record.Signed,
            Timestamp = record.Timestamp.UtcDateTime.ToString("O"),
            record.BrickId,
            record.ContentHash,
            record.EscapeRate,
            record.TotalMutants,
            record.SurvivingMutants,
            KilledMutants = record.KilledMutants.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            SurvivingMutantIds = record.SurvivingMutantIds.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            record.Reason
        };
        return JsonSerializer.Serialize(clone, PayloadOptions);
    }

    // The property-NAME sequence each lane must produce. This is what makes "degenerate"
    // definable without guessing at size or emptiness: PayloadOptions carries no
    // JsonIgnoreCondition, so every declared property is emitted for every record whatever its
    // values, and a minimal legitimate record still carries every one of these names with
    // nulls. The sequence therefore depends on the payload TYPE and not on any field value —
    // there is no legitimate record it can reject — and it catches in one check everything
    // reflection-based serialization can lose when it is trimmed away or published
    // ahead-of-time: an empty object, missing properties, and a reordering, which would
    // silently invalidate every signature ever written.
    private static readonly string[] LegacyPayloadNames =
    {
        "status", "stage", "admitted", "signed", "timestamp", "brickId", "contentHash",
        "escapeRate", "totalMutants", "survivingMutants", "killedMutants", "survivingMutantIds",
        "reason"
    };

    private static readonly string[] VersionedPayloadNames =
    {
        "schemaVersion", "status", "stage", "admitted", "signed", "timestamp", "brickId",
        "contentHash", "escapeRate", "totalMutants", "survivingMutants", "killedMutants",
        "survivingMutantIds", "reason", "gate", "gatesPassed", "inputs", "proposer", "attempts",
        "ed25519PublicKey"
    };

    private static readonly string[] GatePassNames = { "name", "version", "configuration" };

    private static readonly string[] InputNames = { "kind", "id", "hash" };

    private static readonly string[] ProposerNames = { "identity", "parameters", "seed" };

    private static readonly string[] AttemptNames = { "index", "outcome", "failureCategory", "durationSeconds" };

    /// <summary>
    /// Returns <paramref name="payload"/> when it carries the exact property-name sequence its
    /// lane declares, and throws otherwise. Re-parsing with <see cref="JsonDocument"/> is
    /// deliberate: the parser is reflection-free, so it still works in exactly the publish
    /// configurations that break the serializer that produced the payload.
    /// Internal rather than private so the guard can be exercised against payloads this
    /// process cannot make the serializer emit — the publish modes that produce them are not
    /// reproducible inside a test host.
    /// </summary>
    internal static string EnsureCanonical(string payload, bool versioned)
    {
        var lane = versioned ? "v2" : "v1";
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload);
        }
        catch (JsonException ex)
        {
            throw new CanonicalPayloadException(
                $"The {lane} canonical certification payload did not serialize to parseable JSON, "
                + "so it cannot back a signature.",
                ex);
        }

        using (document)
        {
            var root = document.RootElement;
            RequireShape(root, versioned ? VersionedPayloadNames : LegacyPayloadNames, lane, "payload");

            // v1 has no nested objects; its arrays hold strings only.
            if (!versioned)
                return payload;

            // Each collection member's kind is established before it is enumerated. The name
            // sequence above establishes that these members are present, not what they are, and
            // enumerating one that is not an array throws past every handler on the verification
            // path — where the contract is a refusal, never an exception raised in the host.
            foreach (var gatePass in RequireArray(root.GetProperty("gatesPassed"), lane, "gatesPassed").EnumerateArray())
                RequireShape(gatePass, GatePassNames, lane, "gatesPassed[]");

            foreach (var input in RequireArray(root.GetProperty("inputs"), lane, "inputs").EnumerateArray())
                RequireShape(input, InputNames, lane, "inputs[]");

            // proposer.parameters is deliberately left unchecked below: its keys are
            // caller-supplied and emitted verbatim, so they are values here, not shape.
            var proposer = root.GetProperty("proposer");
            if (proposer.ValueKind != JsonValueKind.Null)
                RequireShape(proposer, ProposerNames, lane, "proposer");

            foreach (var attempt in RequireArray(root.GetProperty("attempts"), lane, "attempts").EnumerateArray())
                RequireShape(attempt, AttemptNames, lane, "attempts[]");
        }

        return payload;
    }

    private static void RequireShape(JsonElement element, string[] expected, string lane, string path)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new CanonicalPayloadException(
                $"The {lane} canonical certification payload is not its declared shape: "
                + $"'{path}' serialized as {element.ValueKind}, expected an object.");
        }

        var observed = new List<string>(expected.Length);
        foreach (var property in element.EnumerateObject())
            observed.Add(property.Name);

        if (observed.Count == expected.Length)
        {
            var matched = true;
            for (var i = 0; i < expected.Length; i++)
            {
                if (!string.Equals(observed[i], expected[i], StringComparison.Ordinal))
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
                return;
        }

        // Property names only, never values: this message reaches logs and refusal text.
        throw new CanonicalPayloadException(
            $"The {lane} canonical certification payload is not its declared shape at '{path}'. "
            + $"Expected {expected.Length} properties [{string.Join(", ", expected)}] in that order; "
            + $"got {observed.Count} [{string.Join(", ", observed)}]. "
            + "These bytes back every signature over this record, so they are refused rather than used.");
    }

    private static JsonElement RequireArray(JsonElement element, string lane, string path)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            throw new CanonicalPayloadException(
                $"The {lane} canonical certification payload is not its declared shape: "
                + $"'{path}' serialized as {element.ValueKind}, expected an array.");
        }

        return element;
    }

    private sealed record VersionedPayload(
        int SchemaVersion,
        string Status,
        string Stage,
        bool Admitted,
        bool Signed,
        string Timestamp,
        string BrickId,
        string? ContentHash,
        double? EscapeRate,
        int? TotalMutants,
        int? SurvivingMutants,
        string[] KilledMutants,
        string[] SurvivingMutantIds,
        string? Reason,
        string? Gate,
        GatePassPayload[] GatesPassed,
        InputPayload[] Inputs,
        ProposerPayload? Proposer,
        AttemptPayload[] Attempts,
        string? Ed25519PublicKey);

    private sealed record GatePassPayload(string Name, string? Version, string? Configuration);

    private sealed record InputPayload(string Kind, string Id, string Hash);

    private sealed record ProposerPayload(string Identity, Dictionary<string, string> Parameters, string? Seed);

    private sealed record AttemptPayload(int Index, string Outcome, string? FailureCategory, double? DurationSeconds);

    private static string ResolveKey(string? hmacKey)
    {
        if (!string.IsNullOrWhiteSpace(hmacKey))
            return hmacKey!;
        return Environment.GetEnvironmentVariable(HmacKeyEnvVar) ?? DefaultDevKey;
    }
}
