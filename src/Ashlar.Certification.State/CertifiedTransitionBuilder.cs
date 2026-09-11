using System.Text;
using System.Text.Json;

namespace Ashlar.Certification.State;

/// <summary>
/// Computes certified transition entry hashes and assembles transitions.
/// </summary>
public sealed class CertifiedTransitionBuilder
{
    public string ComputeEntryHash(
        string priorStateHash,
        string action,
        string behaviorCertContentHash,
        string resultingStateHash,
        string prevEntryHash) =>
        Contracts.BrickContentHasher.ComputeSha256(BuildCanonicalPayload(
            priorStateHash,
            action,
            behaviorCertContentHash,
            resultingStateHash,
            prevEntryHash));

    // The largest payload in transition-entry-hashes.golden.json is 345 bytes. A starting
    // size, not a limit.
    private const int PayloadBufferHint = 512;

    // The bytes StateLogVerifier's decision rests on: it recomputes this hash for every entry
    // and refuses the log when it does not match the stored one. Internal rather than inlined
    // above so CertifiedTransitionEntryHashGoldenTests can pin the bytes themselves — a pin
    // that could only observe the hash would report "the hash moved" rather than which byte
    // moved it.
    //
    // The payload is WRITTEN, not serialized from an anonymous type. Reflection-based
    // serialization does not survive trimming or ahead-of-time publishing: under a trimmed
    // publish the payload could serialize to an empty object, and every entry hash in every
    // attested state log would then be the hash of the same empty object. This assembly ships
    // netstandard2.0, net8.0 and net10.0, so the same source runs against three different
    // System.Text.Json builds; written this way the byte order is the statement order below.
    // Utf8JsonWriter still does the encoding — compact output and the default JavaScript
    // encoder, which is what escapes the "+" in a Base64 hash and leaves "/" and "=" alone —
    // so the bytes are unchanged; transition-entry-hashes.golden.json is what says so.
    internal static string BuildCanonicalPayload(
        string priorStateHash,
        string action,
        string behaviorCertContentHash,
        string resultingStateHash,
        string prevEntryHash)
    {
        using var buffer = new MemoryStream(PayloadBufferHint);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();

            // This order IS the byte order, and it is not the parameter order: it is the member
            // order of the anonymous type this replaced. Do not tidy it into parameter order —
            // a reordering loses no information but changes the hash, and every attested state
            // log already written would stop verifying.
            WriteStringOrNull(writer, "action", action);
            WriteStringOrNull(writer, "behaviorCertContentHash", behaviorCertContentHash);
            WriteStringOrNull(writer, "prevEntryHash", prevEntryHash);
            WriteStringOrNull(writer, "priorStateHash", priorStateHash);
            WriteStringOrNull(writer, "resultingStateHash", resultingStateHash);

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    // The literal null rather than an omitted property, so the five names are carried whatever
    // the values are. The genesis prev-entry hash is the empty string, not null, and is written
    // as one.
    private static void WriteStringOrNull(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
            writer.WriteNull(name);
        else
            writer.WriteString(name, value);
    }

    public CertifiedTransition Create(
        string priorStateHash,
        string action,
        string behaviorCertContentHash,
        string resultingStateHash,
        string prevEntryHash)
    {
        var entryHash = ComputeEntryHash(
            priorStateHash,
            action,
            behaviorCertContentHash,
            resultingStateHash,
            prevEntryHash);

        return new CertifiedTransition
        {
            PriorStateHash = priorStateHash,
            Action = action,
            BehaviorCertContentHash = behaviorCertContentHash,
            ResultingStateHash = resultingStateHash,
            PrevEntryHash = prevEntryHash,
            EntryHash = entryHash
        };
    }
}
