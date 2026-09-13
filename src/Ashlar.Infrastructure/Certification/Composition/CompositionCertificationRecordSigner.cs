using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Ashlar.Certification.Contracts;
using Ashlar.Core.Application.Certification.Models;

namespace Ashlar.Infrastructure.Certification.Composition;

/// <summary>
/// HMAC signer for composition certification records.
///
/// <para><b>Key ladder, most specific first:</b> an explicit <c>hmacKey</c>; else the injected
/// <see cref="CertificationRecordSigner"/>, which becomes this lane's KEY HOLDER — this class then
/// computes no MAC of its own and stores no key material, so whatever key the brick lane is under,
/// the composition lane is under the same one; else <c>ASHLAR_CERT_DEV_HMAC_KEY</c>; else the
/// committed dev key. Warns when the dev key is in effect.</para>
///
/// <para>The injected signer is the rung that closes limitation 9: it is the one the shipped DI
/// registration fills, so a host that does the single thing SPEC-006 S-4 tells it to do — supply a
/// <see cref="CertificationRecordSigner"/> holding a real key — mints composition records under that
/// key, with no host code change. Do not reinstate a discard here; deleting the delegation compiles
/// silently and re-opens the defect.</para>
/// </summary>
public sealed class CompositionCertificationRecordSigner
{
    // Exactly one of these is non-null, decided in the constructor. _keyHolder means "this signer
    // holds no key; ask the brick signer", which is the whole of the limitation 9 fix and the reason
    // this type is no longer a second resident copy of the operator key. _keyBytes is the standalone
    // path. CompositionSignerKeyPathConventionTests pins _keyBytes null when delegating.
    private readonly CertificationRecordSigner? _keyHolder;
    private readonly byte[]? _keyBytes;

    /// <summary>Initializes a new composition certification record signer.</summary>
    /// <param name="brickSigner">
    /// The brick lane's signer. When supplied without an explicit <paramref name="hmacKey"/> it
    /// becomes this lane's key holder: signing is delegated to it, so the operator's key reaches
    /// composition records without any key material crossing this boundary. This parameter is
    /// LOAD-BEARING; it was formerly discarded (limitation 9).
    /// </param>
    /// <param name="logger">Optional logger; receives the dev-key warning when the committed key is in effect.</param>
    /// <param name="hmacKey">
    /// Optional explicit HMAC key. When provided, composition records are signed with this key
    /// instead of reading from the environment. This allows hosts to pass a real key and have
    /// it honored (limitation 9 fix).
    /// </param>
    public CompositionCertificationRecordSigner(
        CertificationRecordSigner? brickSigner = null,
        ILogger<CompositionCertificationRecordSigner>? logger = null,
        string? hmacKey = null)
    {
        if (brickSigner is not null && string.IsNullOrWhiteSpace(hmacKey))
        {
            // Limitation 9, operative half. No key is copied out of the brick signer; the MAC is
            // computed by it. UsesDevKey is the brick lane's answer because it IS the brick lane's
            // key — the two flags are now the same fact rather than two coincidences.
            _keyHolder = brickSigner;
            UsesDevKey = brickSigner.UsesDevKey;
        }
        else
        {
            // An explicit key is the most specific statement available and outranks an injected
            // signer; with neither, the pre-existing ladder, unchanged in effect. The flag is now
            // computed from the SAME resolved string as the bytes — the previous code derived them
            // from two independent reads of the environment, which could disagree if the variable
            // changed between them.
            var key = string.IsNullOrWhiteSpace(hmacKey)
                ? Environment.GetEnvironmentVariable(CertificationRecordSigning.HmacKeyEnvVar)
                  ?? CertificationRecordSigner.DefaultDevKey
                : hmacKey!;

            _keyBytes = Encoding.UTF8.GetBytes(key);
            UsesDevKey = CertificationRecordSigning.UsesDevKey(key);
        }

        if (UsesDevKey)
            CertificationRecordSigner.WarnDevKey(logger, nameof(CompositionCertificationRecordSigner));
    }

    /// <summary>
    /// True when composition records are signed with the committed development key
    /// (no explicit key, <c>ASHLAR_CERT_DEV_HMAC_KEY</c> unset): every signature this instance
    /// mints or accepts is forgeable by anyone with the source.
    /// </summary>
    public bool UsesDevKey { get; }

    /// <summary>
    /// True when this signer derives its key from <paramref name="brickSigner"/>, so both lanes are
    /// under one key and stay under it — delegation inherits the brick lane's late binding, not just
    /// its current value.
    ///
    /// <para><b>Reference identity, deliberately. This compares NO key material and must never be
    /// described as a key comparison.</b> It is exact for the shipped DI path, where one
    /// <see cref="CertificationRecordSigner"/> singleton is injected into both lanes. It is a FALSE
    /// POSITIVE for a host that deliberately built two signers holding the same explicit key — that
    /// host is correctly configured and its only consumer warns anyway, which is why that consumer
    /// warns and never refuses. Answering it exactly would mean comparing keys, and
    /// not doing that is the whole design of this class.</para>
    /// </summary>
    internal bool SharesKeyHolderWith(CertificationRecordSigner brickSigner)
        => ReferenceEquals(_keyHolder, brickSigner);

    /// <summary>Sign.</summary>
    public string Sign(CompositionCertificationRecord record)
    {
        var payload = BuildPayload(record);
        if (_keyHolder is not null)
            return _keyHolder.ComputeCanonicalHmac(payload);

        using var hmac = new HMACSHA256(_keyBytes!);
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return Convert.ToBase64String(hash);
    }

    /// <summary>Verify.</summary>
    public bool Verify(CompositionCertificationRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.Signature))
            return false;

        try
        {
            var expected = Sign(record with { Signature = null });
            return CryptographicOperations.FixedTimeEquals(
                Convert.FromBase64String(record.Signature),
                Convert.FromBase64String(expected));
        }
        catch (FormatException)
        {
            return false;
        }
        catch (CanonicalPayloadException)
        {
            // A verifier answers the question it was asked. Signing propagates this — a loud
            // failure at mint time is the correct outcome for bytes nobody can vouch for — while
            // verification refuses without throwing into its host, which is what the brick
            // lane's CertificationRecordSigning.VerifySignature already does for the same
            // exception.
            return false;
        }
    }

    // Every property name in the payload, spelled once and read twice: the emitter writes them
    // and the shape check re-reads them off the emitted bytes, so the two cannot disagree.
    private static class Names
    {
        internal const string Status = "status";
        internal const string Stage = "stage";
        internal const string Admitted = "admitted";
        internal const string Signed = "signed";
        internal const string Timestamp = "timestamp";
        internal const string CompositionId = "compositionId";
        internal const string CompositionEscapeRate = "compositionEscapeRate";
        internal const string TotalStructuralMutants = "totalStructuralMutants";
        internal const string SurvivingStructuralMutants = "survivingStructuralMutants";
        internal const string KilledStructuralMutantIds = "killedStructuralMutantIds";
        internal const string SurvivingStructuralMutantIds = "survivingStructuralMutantIds";
        internal const string Reason = "reason";
    }

    // The property-NAME sequence the payload must carry. Nothing is ever omitted — a minimal
    // legitimate record still carries all twelve names, with nulls and empty arrays — so the
    // sequence depends on the payload and not on any field value, and there is no legitimate
    // record it can reject. It catches an empty object, a missing property and a reordering in
    // one check; a reordering loses no information but changes the bytes, which would silently
    // invalidate every composition signature already written.
    private static readonly string[] PayloadNames =
    {
        Names.Status, Names.Stage, Names.Admitted, Names.Signed, Names.Timestamp,
        Names.CompositionId, Names.CompositionEscapeRate, Names.TotalStructuralMutants,
        Names.SurvivingStructuralMutants, Names.KilledStructuralMutantIds,
        Names.SurvivingStructuralMutantIds, Names.Reason
    };

    // The largest payload in composition-payloads.golden.json is 436 bytes. A starting size,
    // not a limit.
    private const int PayloadBufferHint = 1024;

    // Internal rather than private so CompositionCanonicalPayloadGoldenTests can pin these
    // bytes directly. They are the message every composition admission signature is computed
    // over, and a pin that could only observe them through a signature would report "the
    // signature moved" rather than which byte did.
    //
    // The payload is WRITTEN, not serialized from an object graph. Reflection-based
    // serialization does not survive trimming or ahead-of-time publishing: under a trimmed
    // publish the payload could serialize to an empty object, and bytes that back a signature
    // must never be silently empty. Written this way the byte order is the statement order
    // below and the names are the constants above. Utf8JsonWriter still does the encoding,
    // deliberately - compact output and the default JavaScript encoder - but not the decimal form
    // of a double: WriteNumberOrNull below chooses that, so every target writes the same text at
    // the same width, with the one tie residual that method states. composition-payloads.golden.json
    // is what says so.
    internal static string BuildPayload(CompositionCertificationRecord record)
    {
        using var buffer = new MemoryStream(PayloadBufferHint);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            WriteStringOrNull(writer, Names.Status, record.Status);
            WriteStringOrNull(writer, Names.Stage, record.Stage);
            writer.WriteBoolean(Names.Admitted, record.Admitted);
            writer.WriteBoolean(Names.Signed, record.Signed);

            // The round-trip form of the UTC DateTime, written as a STRING. Utf8JsonWriter's own
            // DateTimeOffset overload emits "+00:00" and trims trailing fractional zeros, which
            // is a different message for the same instant.
            WriteStringOrNull(writer, Names.Timestamp, record.Timestamp.UtcDateTime.ToString("O"));
            WriteStringOrNull(writer, Names.CompositionId, record.CompositionId);
            WriteNumberOrNull(writer, Names.CompositionEscapeRate, record.CompositionEscapeRate);
            WriteNumberOrNull(writer, Names.TotalStructuralMutants, record.TotalStructuralMutants);
            WriteNumberOrNull(writer, Names.SurvivingStructuralMutants, record.SurvivingStructuralMutants);
            WriteOrdinalSorted(writer, Names.KilledStructuralMutantIds, record.KilledStructuralMutantIds);
            WriteOrdinalSorted(writer, Names.SurvivingStructuralMutantIds, record.SurvivingStructuralMutantIds);
            WriteStringOrNull(writer, Names.Reason, record.Reason);
            writer.WriteEndObject();
        }

        return EnsureCanonical(Encoding.UTF8.GetString(buffer.ToArray()));
    }

    // A post-condition on the emitter above, not a check on the runtime: no record can make it
    // write another name sequence, so what this catches is a mistyped name or a swapped pair of
    // write statements. Re-parsing with JsonDocument keeps the check reflection-free, so it
    // holds in every publish mode the emitter itself holds in.
    private static string EnsureCanonical(string payload)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload);
        }
        catch (JsonException ex)
        {
            throw new CanonicalPayloadException(
                "The canonical composition certification payload is not parseable JSON, so it "
                + "cannot back a signature.",
                ex);
        }

        List<string> observed;
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new CanonicalPayloadException(
                    "The canonical composition certification payload is not its declared shape: it "
                    + $"was written as {document.RootElement.ValueKind}, expected an object.");
            }

            observed = new List<string>(PayloadNames.Length);
            foreach (var property in document.RootElement.EnumerateObject())
                observed.Add(property.Name);
        }

        if (observed.Count == PayloadNames.Length)
        {
            var matched = true;
            for (var i = 0; i < PayloadNames.Length; i++)
            {
                if (!string.Equals(observed[i], PayloadNames[i], StringComparison.Ordinal))
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
                return payload;
        }

        // Property names only, never values: this message reaches logs and refusal text.
        throw new CanonicalPayloadException(
            "The canonical composition certification payload is not its declared shape. "
            + $"Expected {PayloadNames.Length} properties [{string.Join(", ", PayloadNames)}] in that "
            + $"order; got {observed.Count} [{string.Join(", ", observed)}]. These bytes back every "
            + "signature over this record, so they are refused rather than used.");
    }

    // Writes the literal null rather than omitting the property. Nothing here is conditional on
    // a value, which is what keeps the name sequence an invariant of the payload rather than of
    // the record.
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

    // The same canonical decimal form the brick lane uses, for the same reason and with the same
    // three decisions: G17 on the invariant culture because it is the width at which a binary64
    // always round-trips - the width holds on every target this payload is produced or
    // re-produced on, the rounding of an exact tie at the 17th significant digit does not, which
    // is the one residual the brick lane documents in full; both zeros collapsed onto "0" because
    // emitting "-0" leaves the writers agreeing and the readers not; NaN and the infinities
    // refused as a canonical-payload fault rather than left to throw ArgumentException past the
    // verifier's catch. The full reasoning is on CertificationRecordSigning.WriteNumberOrNull -
    // the two emitters must not drift apart, because a composition record carries a brick-shaped
    // escape rate.
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
                $"The canonical composition certification payload cannot be written: '{name}' is "
                + $"{number.ToString(CultureInfo.InvariantCulture)}, and JSON has no number for NaN "
                + "or infinity, so there are no bytes for a signature to cover.");
        }

        writer.WritePropertyName(name);
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
}
