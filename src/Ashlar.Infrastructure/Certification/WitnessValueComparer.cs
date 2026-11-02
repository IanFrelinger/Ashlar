using System.Text.Json;

namespace Ashlar.Infrastructure.Certification;

/// <summary>Compares expected and actual witness output values during certification.</summary>
internal static class WitnessValueComparer
{
    /// <summary>
    /// Exact value equality after unwrapping primitive JSON transport values. Integral widths
    /// retain the existing numeric equality contract; other CLR kinds must have the same type.
    /// </summary>
    public static bool AreEqual(object? expected, object? actual)
    {
        expected = NormalizeJson(expected);
        actual = NormalizeJson(actual);
        if (expected is null || actual is null)
            return expected is null && actual is null;

        var expectedIsInt = expected is int or long or short or byte;
        var actualIsInt = actual is int or long or short or byte;
        if (expectedIsInt || actualIsInt)
            return expectedIsInt && actualIsInt && Convert.ToInt64(expected) == Convert.ToInt64(actual);

        // Preserve the JSON kind for aggregates: an object/array must never equal a string
        // containing its JSON. Raw equality is conservative (no structural CLR coercion).
        if (expected is JsonElement || actual is JsonElement)
            return expected is JsonElement ej && actual is JsonElement aj
                && ej.ValueKind == aj.ValueKind && ej.GetRawText() == aj.GetRawText();

        // ToString equality conflates doubles with strings, null with empty strings, and
        // unrelated object types. Correctness and mutation use this same comparison path.
        return expected.GetType() == actual.GetType() && expected.Equals(actual);
    }

    private static object? NormalizeJson(object? value) => value is JsonElement element
        ? element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number when element.TryGetInt64(out var integer) => integer,
            JsonValueKind.Number => element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => element
        }
        : value;
}
