using System.Globalization;
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
    /// The emitter did not produce the lane's declared shape, the record's schema version
    /// selects no lane at all, or a proposer parameter key repeats so the payload would carry a
    /// duplicate property name and describe no single record. A version this code has never
    /// seen would otherwise be written under a shape chosen by guesswork. These bytes back
    /// every signature, so in each case they are refused rather than signed.
    /// </exception>
    public static string BuildPayload(CertificationRecordData record)
    {
        if (record.SchemaVersion is null)
            return EnsureCanonical(WritePayload(record, schemaVersion: null), versioned: false);

        if (record.SchemaVersion.Value == CertificationRecordData.TrustLoopSchemaVersion)
            return EnsureCanonical(WritePayload(record, record.SchemaVersion.Value), versioned: true);

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

    // The largest payload in the golden corpus is 1213 bytes. A starting size, not a limit.
    private const int PayloadBufferHint = 2048;

    // The canonical payload is WRITTEN, not serialized from an object graph. Reflection-based
    // serialization does not survive trimming or ahead-of-time publishing: under a trimmed
    // publish the payload could serialize to an empty object, and bytes that back a signature
    // must never be silently empty. Written this way the byte order is the statement order
    // below and the names are the constants above, so the canonical form is a property of this
    // file rather than of whichever System.Text.Json build a consumer resolves — this package
    // ships netstandard2.0, net8.0 and net10.0, which are three different serializer builds
    // behind one "canonical bytes" claim.
    //
    // Utf8JsonWriter still does the encoding, deliberately: its defaults are compact output and
    // the default JavaScript encoder, and WriteNumber(double) keeps whatever decimal form the
    // target produces. This change is about how the bytes are ORDERED and NAMED and must not
    // move one of them; canonical-payloads.golden.json is what says it did not.
    private static string WritePayload(CertificationRecordData record, int? schemaVersion)
    {
        using var buffer = new MemoryStream(PayloadBufferHint);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();

            // v2 is v1's names in the same order with schemaVersion prepended and six appended,
            // so the two lanes are one method under two brackets rather than two transcriptions
            // that could drift apart in the thirteen names they share.
            if (schemaVersion is not null)
                writer.WriteNumber(Names.SchemaVersion, schemaVersion.Value);

            WriteStringOrNull(writer, Names.Status, record.Status);
            WriteStringOrNull(writer, Names.Stage, record.Stage);
            writer.WriteBoolean(Names.Admitted, record.Admitted);
            writer.WriteBoolean(Names.Signed, record.Signed);

            // The round-trip form of the UTC DateTime, written as a STRING. Utf8JsonWriter's own
            // DateTimeOffset overload emits "+00:00" and trims trailing fractional zeros, which
            // is a different message for the same instant.
            WriteStringOrNull(writer, Names.Timestamp, record.Timestamp.UtcDateTime.ToString("O"));
            WriteStringOrNull(writer, Names.BrickId, record.BrickId);
            WriteStringOrNull(writer, Names.ContentHash, record.ContentHash);
            WriteNumberOrNull(writer, Names.EscapeRate, record.EscapeRate);
            WriteNumberOrNull(writer, Names.TotalMutants, record.TotalMutants);
            WriteNumberOrNull(writer, Names.SurvivingMutants, record.SurvivingMutants);
            WriteOrdinalSorted(writer, Names.KilledMutants, record.KilledMutants);
            WriteOrdinalSorted(writer, Names.SurvivingMutantIds, record.SurvivingMutantIds);
            WriteStringOrNull(writer, Names.Reason, record.Reason);

            if (schemaVersion is not null)
            {
                WriteStringOrNull(writer, Names.Gate, record.Gate);

                // Gate and attempt order is semantic and preserved; inputs and mutant ids are
                // sorted, because their caller order carries nothing and would otherwise be the
                // thing that decides the bytes.
                writer.WriteStartArray(Names.GatesPassed);
                foreach (var gatePass in record.GatesPassed)
                {
                    writer.WriteStartObject();
                    WriteStringOrNull(writer, Names.Name, gatePass.Name);
                    WriteStringOrNull(writer, Names.Version, gatePass.Version);
                    WriteStringOrNull(writer, Names.Configuration, gatePass.Configuration);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();

                writer.WriteStartArray(Names.Inputs);
                foreach (var input in record.Inputs
                    .OrderBy(i => i.Kind, StringComparer.Ordinal)
                    .ThenBy(i => i.Id, StringComparer.Ordinal))
                {
                    writer.WriteStartObject();
                    WriteStringOrNull(writer, Names.Kind, input.Kind);
                    WriteStringOrNull(writer, Names.Id, input.Id);
                    WriteStringOrNull(writer, Names.Hash, input.Hash);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();

                WriteProposer(writer, record.Proposer);

                writer.WriteStartArray(Names.Attempts);
                foreach (var attempt in record.Attempts)
                {
                    writer.WriteStartObject();
                    writer.WriteNumber(Names.Index, attempt.Index);
                    WriteStringOrNull(writer, Names.Outcome, attempt.Outcome);
                    WriteStringOrNull(writer, Names.FailureCategory, attempt.FailureCategory);
                    WriteNumberOrNull(writer, Names.DurationSeconds, attempt.DurationSeconds);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();

                WriteStringOrNull(writer, Names.Ed25519PublicKey, record.Ed25519PublicKey);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    // proposer.parameters is the one place in the payload whose property names are supplied by
    // the caller: no key policy is configured, so keys are emitted verbatim in ordinal order.
    // They are written straight from the sorted sequence — the intermediate dictionary the
    // previous code built to carry them relied on Dictionary<,> preserving insertion
    // order, which is a property of that type rather than a statement this payload makes.
    private static void WriteProposer(Utf8JsonWriter writer, CertificationProposer? proposer)
    {
        if (proposer is null)
        {
            // The literal null, never an empty object: a record with no proposer and a record
            // with an empty one are different records and must not share a signature.
            writer.WriteNull(Names.Proposer);
            return;
        }

        writer.WriteStartObject(Names.Proposer);
        WriteStringOrNull(writer, Names.Identity, proposer.Identity);

        writer.WriteStartObject(Names.Parameters);
        string? previousKey = null;
        foreach (var parameter in proposer.Parameters.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            // The sort is ordinal, so a repeated key arrives next to its twin. Utf8JsonWriter
            // does not reject a duplicate property name, and a payload carrying one describes no
            // single record, so it is refused in the same vehicle every other unusable payload
            // uses. The key itself is not named: this message reaches logs and refusal text, and
            // these keys are caller data rather than shape.
            if (string.Equals(previousKey, parameter.Key, StringComparison.Ordinal))
            {
                throw new CanonicalPayloadException(
                    "The v2 canonical certification payload cannot be written: the proposer supplied "
                    + "the same parameter key more than once, so the payload would carry a duplicate "
                    + "property name and describe no single record.");
            }

            previousKey = parameter.Key;
            WriteStringOrNull(writer, parameter.Key, parameter.Value);
        }

        writer.WriteEndObject();

        WriteStringOrNull(writer, Names.Seed, proposer.Seed);
        writer.WriteEndObject();
    }

    // Every helper below writes the literal null rather than omitting the property, at every
    // level. Nothing here is conditional on a value, which is what keeps the name sequence an
    // invariant of the lane rather than of the record — and therefore what lets EnsureCanonical
    // define a degenerate payload as a shape rather than as a size.
    private static void WriteStringOrNull(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
            writer.WriteNull(name);
        else
            writer.WriteString(name, value);
    }

    private static void WriteNumberOrNull(Utf8JsonWriter writer, string name, int? value)
    {
        if (value is null)
            writer.WriteNull(name);
        else
            writer.WriteNumber(name, value.Value);
    }

    // The decimal form of a double is CHOSEN here rather than delegated. WriteNumber(double)
    // reproduces whatever form the target's own formatter produces, and those forms are not the
    // same on every target this package ships for: the netstandard2.0 asset writes 1/3 as
    // 0.33333333333333331 where net8.0 and net10.0 write 0.3333333333333333. These bytes back
    // every signature over the record, so one form has to hold on all of them.
    //
    // G17 on the invariant culture is that form. 17 significant digits is the width at which a
    // binary64 always survives a round trip - a width, not a heuristic - and it was measured
    // character-for-character identical, and re-parsing to the identical bit pattern, on net8.0,
    // net10.0 and the netstandard2.0 asset under Mono. "R" is not usable: the targets disagree on
    // it (1/3 and double.Epsilon among others) and its meaning changed at .NET Core 3.0. A fixed
    // format is not usable either: no fractional width spans double.Epsilon to double.MaxValue,
    // and a form that loses precision cannot back a signature.
    //
    // Both zeros collapse onto "0" DELIBERATELY - do not restore the sign here. Emitting "-0"
    // makes the three writers agree and leaves the system disagreeing, because the netstandard2.0
    // asset reads -0 back as +0 and would re-emit "0", computing different bytes from the same
    // record file. Collapsing gives up only the sign bit of a zero (-0.0 == 0.0) and is stable
    // through emit, read and re-emit on every target.
    //
    // NaN and the infinities have no JSON number at all, so refusing them IS the canonical answer.
    // Utf8JsonWriter already refuses them - but with an ArgumentException, which VerifySignature
    // does not catch and therefore throws into its host. Refused here through the vehicle every
    // other canonical refusal uses, so a verifier answers rather than throws.
    private static void WriteNumberOrNull(Utf8JsonWriter writer, string name, double? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
            return;
        }

        var number = value.Value;
        if (double.IsNaN(number) || double.IsInfinity(number))
        {
            throw new CanonicalPayloadException(
                $"The canonical certification payload cannot be written: '{name}' is "
                + $"{number.ToString(CultureInfo.InvariantCulture)}, and JSON has no number for NaN "
                + "or infinity, so there are no bytes for a signature to cover.");
        }

        writer.WritePropertyName(name);

        // Validation is left ON: WriteRawValue re-reads the text as a JSON number, which is a
        // second and independent check that what the formatter produced is a number at all.
        writer.WriteRawValue(
            number == 0d ? "0" : number.ToString("G17", CultureInfo.InvariantCulture),
            skipInputValidation: false);
    }

    private static void WriteOrdinalSorted(Utf8JsonWriter writer, string name, IReadOnlyList<string> values)
    {
        writer.WriteStartArray(name);
        foreach (string? value in values.OrderBy(x => x, StringComparer.Ordinal))
        {
            if (value is null)
                writer.WriteNullValue();
            else
                writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
    }

    // Every property name in the payload, spelled once and read twice: the emitter writes them
    // and the shape guard below checks them, so the two cannot disagree about a spelling. Two of
    // these are not what inspection suggests — camel casing lowercases a leading RUN, which is
    // why it is "brickId" and not "brickID", and "ed25519PublicKey" and not "ed25519publicKey" —
    // and a one-character difference in either changes every signature in that lane.
    private static class Names
    {
        internal const string SchemaVersion = "schemaVersion";
        internal const string Status = "status";
        internal const string Stage = "stage";
        internal const string Admitted = "admitted";
        internal const string Signed = "signed";
        internal const string Timestamp = "timestamp";
        internal const string BrickId = "brickId";
        internal const string ContentHash = "contentHash";
        internal const string EscapeRate = "escapeRate";
        internal const string TotalMutants = "totalMutants";
        internal const string SurvivingMutants = "survivingMutants";
        internal const string KilledMutants = "killedMutants";
        internal const string SurvivingMutantIds = "survivingMutantIds";
        internal const string Reason = "reason";
        internal const string Gate = "gate";
        internal const string GatesPassed = "gatesPassed";
        internal const string Inputs = "inputs";
        internal const string Proposer = "proposer";
        internal const string Attempts = "attempts";
        internal const string Ed25519PublicKey = "ed25519PublicKey";
        internal const string Name = "name";
        internal const string Version = "version";
        internal const string Configuration = "configuration";
        internal const string Kind = "kind";
        internal const string Id = "id";
        internal const string Hash = "hash";
        internal const string Identity = "identity";
        internal const string Parameters = "parameters";
        internal const string Seed = "seed";
        internal const string Index = "index";
        internal const string Outcome = "outcome";
        internal const string FailureCategory = "failureCategory";
        internal const string DurationSeconds = "durationSeconds";
    }

    // The property-NAME sequence each lane must produce. This is what makes "degenerate"
    // definable without guessing at size or emptiness: nothing is ever omitted, so every
    // declared property is emitted for every record whatever its values, and a minimal
    // legitimate record still carries every one of these names with nulls. The sequence
    // therefore depends on the lane and not on any field value — there is no legitimate record
    // it can reject — and it catches in one check an empty object, a missing property, and a
    // reordering, which loses no information but changes the bytes and would silently
    // invalidate every signature ever written.
    //
    // The emitter above leaves the serializer no room to decide any of that, which makes this a
    // post-condition an editing mistake trips rather than a deployment fault it detects. It is
    // kept for exactly that reason: it is the one thing standing between a mistyped name or a
    // swapped pair of statements and a silently re-shaped signed payload.
    private static readonly string[] LegacyPayloadNames =
    {
        Names.Status, Names.Stage, Names.Admitted, Names.Signed, Names.Timestamp, Names.BrickId,
        Names.ContentHash, Names.EscapeRate, Names.TotalMutants, Names.SurvivingMutants,
        Names.KilledMutants, Names.SurvivingMutantIds, Names.Reason
    };

    private static readonly string[] VersionedPayloadNames =
    {
        Names.SchemaVersion, Names.Status, Names.Stage, Names.Admitted, Names.Signed,
        Names.Timestamp, Names.BrickId, Names.ContentHash, Names.EscapeRate, Names.TotalMutants,
        Names.SurvivingMutants, Names.KilledMutants, Names.SurvivingMutantIds, Names.Reason,
        Names.Gate, Names.GatesPassed, Names.Inputs, Names.Proposer, Names.Attempts,
        Names.Ed25519PublicKey
    };

    private static readonly string[] GatePassNames = { Names.Name, Names.Version, Names.Configuration };

    private static readonly string[] InputNames = { Names.Kind, Names.Id, Names.Hash };

    private static readonly string[] ProposerNames = { Names.Identity, Names.Parameters, Names.Seed };

    private static readonly string[] AttemptNames = { Names.Index, Names.Outcome, Names.FailureCategory, Names.DurationSeconds };

    /// <summary>
    /// Returns <paramref name="payload"/> when it carries the exact property-name sequence its
    /// lane declares, and throws otherwise. No input to the emitter can produce another
    /// sequence, so this is a post-condition on the emitter rather than a check on the runtime:
    /// it asserts that the bytes about to back a signature are the shape this file says they
    /// are, which is what a mistyped name or a swapped pair of write statements would break.
    /// Re-parsing with <see cref="JsonDocument"/> keeps the check reflection-free, so it holds
    /// in every publish mode the emitter itself holds in.
    /// Internal rather than private so it can be exercised directly against payloads no input
    /// to the emitter can produce.
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

    private static string ResolveKey(string? hmacKey)
    {
        if (!string.IsNullOrWhiteSpace(hmacKey))
            return hmacKey!;
        return Environment.GetEnvironmentVariable(HmacKeyEnvVar) ?? DefaultDevKey;
    }
}
