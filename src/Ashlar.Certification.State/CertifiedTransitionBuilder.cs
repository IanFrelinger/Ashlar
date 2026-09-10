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

    // The bytes StateLogVerifier's decision rests on: it recomputes this hash for every entry
    // and refuses the log when it does not match the stored one. Internal rather than inlined
    // above so CertifiedTransitionEntryHashGoldenTests can pin the bytes themselves — a pin
    // that could only observe the hash would report "the hash moved" rather than which byte
    // moved it.
    internal static string BuildCanonicalPayload(
        string priorStateHash,
        string action,
        string behaviorCertContentHash,
        string resultingStateHash,
        string prevEntryHash)
    {
        var payload = new
        {
            action,
            behaviorCertContentHash,
            prevEntryHash,
            priorStateHash,
            resultingStateHash
        };

        return JsonSerializer.Serialize(
            payload,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
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
