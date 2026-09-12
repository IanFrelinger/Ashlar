using System.Text.Json;
using Ashlar.Core.Domain.Execution;

namespace Ashlar.Infrastructure.Certification;

/// <summary>Serializes brick outputs to canonical JSON for witness comparison.</summary>
/// <remarks>
/// <para><b>Why this checks its own output.</b> The determinism leg runs a brick twice and
/// compares these two strings. <c>BrickOutput</c> holds arbitrary <c>object</c> values that brick
/// code sets, so serializing them needs reflection and there is no closed type set a source
/// generator could cover. Under a trimmed or ahead-of-time publish, reflection-based
/// serialization does not fail loudly — the payload can come back empty or short a property, with
/// no exception and nothing in the calling code able to tell (this is the defect #584 pinned for
/// the canonical signing payload, in the same serializer).</para>
///
/// <para>An equality comparison is the worst possible consumer of that. Two <em>different</em>
/// outputs that both degrade to <c>{}</c> compare equal, so the determinism leg reports
/// deterministic and the certificate records a check that never ran. The failure is silent and
/// it is fail-OPEN, which is why the shape is established before the bytes are returned rather
/// than trusted.</para>
///
/// <para>Reflection-free by construction: <see cref="EnsureCanonicalShape"/> re-reads with
/// <c>JsonDocument</c>, which still works in exactly the configurations where the serializer
/// does not.</para>
/// </remarks>
internal static class BrickOutputSerializer
{
    /// <summary>To canonical json.</summary>
    /// <exception cref="CanonicalOutputException">
    /// The serialized payload is not the shape this output declares, so nothing can be concluded
    /// from comparing it.
    /// </exception>
    public static string ToCanonicalJson(BrickOutput output)
    {
        var payload = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["summary"] = output.Summary
        };

        foreach (var (key, value) in output.ToDictionary().OrderBy(k => k.Key, StringComparer.Ordinal))
            payload[key] = Normalize(value);

        // payload.Count, not the dictionary's: a brick output whose own key is "summary"
        // collapses onto the summary slot, and the count has to follow the payload it describes.
        return EnsureCanonicalShape(JsonSerializer.Serialize(payload, CanonicalOptions), payload.Count);
    }

    /// <summary>
    /// Refuses a payload that is not the object of <paramref name="expectedProperties"/> members
    /// the caller composed. Counting members is the value-independent invariant here: the values
    /// are open-world, so nothing about them can be asserted, but the NUMBER of top-level keys is
    /// fixed by the dictionary that was handed to the serializer.
    /// </summary>
    /// <remarks>
    /// Separate and <c>internal</c> so it can be exercised against payloads this process cannot
    /// make the serializer emit — the publish modes that produce them are not reproducible inside
    /// a test host. Same reason <c>CertificationRecordSigning.EnsureCanonical</c> is shaped this way.
    /// </remarks>
    internal static string EnsureCanonicalShape(string payload, int expectedProperties)
    {
        int observed;
        try
        {
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new CanonicalOutputException(
                    "The canonical brick output is not its declared shape: serialized as "
                    + $"{document.RootElement.ValueKind}, expected an object. Two outputs compared "
                    + "through bytes like these would agree without having been compared.");
            }

            observed = 0;
            foreach (var _ in document.RootElement.EnumerateObject())
                observed++;
        }
        catch (JsonException ex)
        {
            throw new CanonicalOutputException(
                $"The canonical brick output could not be re-read as JSON: {ex.Message}", ex);
        }

        if (observed != expectedProperties)
        {
            throw new CanonicalOutputException(
                $"The canonical brick output declares {expectedProperties} properties and serialized "
                + $"{observed}. Reflection-based serialization degrades silently under a trimmed or "
                + "ahead-of-time publish; a determinism comparison over a degraded payload reports "
                + "agreement it never established.");
        }

        return payload;
    }

    private static object? Normalize(object? value) => value switch
    {
        null => null,
        JsonElement el => FromJsonElement(el),
        _ => value
    };

    private static object FromJsonElement(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString()!,
        JsonValueKind.Number when el.TryGetInt64(out var l) => l,
        JsonValueKind.Number => el.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Object => el.EnumerateObject()
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ToDictionary(p => p.Name, p => FromJsonElement(p.Value), StringComparer.Ordinal),
        JsonValueKind.Array => el.EnumerateArray().Select(FromJsonElement).ToArray(),
        _ => el.GetRawText()
    };

    private static readonly JsonSerializerOptions CanonicalOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = null
    };
}
