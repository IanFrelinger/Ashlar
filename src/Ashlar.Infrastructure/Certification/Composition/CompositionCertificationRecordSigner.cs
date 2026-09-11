using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Ashlar.Certification.Contracts;
using Ashlar.Core.Application.Certification.Models;

namespace Ashlar.Infrastructure.Certification.Composition;

/// <summary>
/// HMAC signer for composition certification records. Resolves its key from an explicit
/// parameter, then <c>ASHLAR_CERT_DEV_HMAC_KEY</c>, then the committed dev key. Warns when
/// the dev key is in effect. This fixes limitation 9 from certification-evidence.md by
/// honoring explicit keys.
/// </summary>
public sealed class CompositionCertificationRecordSigner
{
    private readonly byte[] _keyBytes;

    /// <summary>Initializes a new composition certification record signer.</summary>
    /// <param name="brickSigner">Unused; kept for API compatibility.</param>
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
        _ = brickSigner; // Kept for API compatibility but not used for key resolution
        
        var key = string.IsNullOrWhiteSpace(hmacKey)
            ? Environment.GetEnvironmentVariable(CertificationRecordSigning.HmacKeyEnvVar)
              ?? CertificationRecordSigner.DefaultDevKey
            : hmacKey;
        
        _keyBytes = Encoding.UTF8.GetBytes(key);
        UsesDevKey = CertificationRecordSigning.UsesDevKey(hmacKey);
        if (UsesDevKey)
            CertificationRecordSigner.WarnDevKey(logger, nameof(CompositionCertificationRecordSigner));
    }

    /// <summary>
    /// True when composition records are signed with the committed development key
    /// (no explicit key, <c>ASHLAR_CERT_DEV_HMAC_KEY</c> unset): every signature this instance
    /// mints or accepts is forgeable by anyone with the source.
    /// </summary>
    public bool UsesDevKey { get; }

    /// <summary>Sign.</summary>
    public string Sign(CompositionCertificationRecord record)
    {
        var payload = BuildPayload(record);
        using var hmac = new HMACSHA256(_keyBytes);
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
    // deliberately — compact output, the default JavaScript encoder, and each target's own
    // decimal form for a double — so the bytes are unchanged; composition-payloads.golden.json
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

    // WriteNumber(double) rather than a formatted string, so the target's own decimal form is
    // reproduced rather than replaced. Choosing one form here would move signatures already
    // written; the golden corpus restricts doubles to exactly representable values for the same
    // reason.
    private static void WriteNumberOrNull(Utf8JsonWriter writer, string name, double? value)
    {
        if (value is null)
            writer.WriteNull(name);
        else
            writer.WriteNumber(name, value.Value);
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
