using System.Globalization;
using System.Text;

namespace Ashlar.Manifest.Admission;

/// <summary>
/// Validation for new human decision labels, not authentication. Never applied to historical
/// records: changing their actor changes the bytes covered by existing signatures.
/// </summary>
internal static class GateDecisionActor
{
    internal const string AutomaticActor = "gate";
    internal const int MaxChars = 256;

    // In invariant globalization mode Normalize is a no-op. Check a non-ASCII compatibility
    // spelling once per process; silently accepting that mode would unreserve the system label.
    private static readonly bool NormalizationSupported = "ｇａｔｅ".Normalize(NormalizationForm.FormKC) == AutomaticActor;

    internal static void Validate(string? actor)
    {
        if (string.IsNullOrWhiteSpace(actor))
        {
            throw new ArgumentException("A decision needs an actor: the record must say who seated or refused.", nameof(actor));
        }
        if (!NormalizationSupported)
        {
            throw new InvalidOperationException("Human gate decisions require Unicode normalization support. Disable invariant globalization and provide the runtime globalization data before deciding; automatic outcomes and historical reads remain available.");
        }
        if (actor.Length > MaxChars)
        {
            throw new ArgumentException($"A human decision actor must be at most {MaxChars} UTF-16 code units.", nameof(actor));
        }
        if (char.IsWhiteSpace(actor[0]) || char.IsWhiteSpace(actor[^1]))
        {
            throw new ArgumentException("A human decision actor cannot have leading or trailing whitespace.", nameof(actor));
        }

        foreach (var c in actor)
        {
            if (IsTerminalControl(c))
            {
                // Do not echo the unsafe input in an exception a caller may print.
                throw new ArgumentException("A human decision actor cannot contain terminal or directional controls.", nameof(actor));
            }
        }

        // Compare only: never normalize, trim or otherwise rewrite the accepted actor. NFKC
        // catches compatibility spellings (e.g. fullwidth); ignoring format and variation
        // selectors here reserves g<ZWJ>ate without banning ZWJ/ZWNJ in legitimate names.
        // This is deliberately not a general Unicode-confusable or identity-authentication check.
        string comparison;
        try
        {
            // Normalize also rejects malformed UTF-16 and unsupported Unicode noncharacters.
            comparison = actor.Normalize(NormalizationForm.FormKC);
        }
        catch (ArgumentException)
        {
            throw new ArgumentException("A human decision actor must contain valid normalizable Unicode text.", nameof(actor));
        }
        var visible = new StringBuilder(comparison.Length);
        foreach (var rune in comparison.EnumerateRunes())
        {
            if (!IsReservedNameIgnorable(rune)) visible.Append(rune.ToString());
        }
        var reservedName = visible.ToString().Trim();
        if (reservedName.Length == 0)
        {
            throw new ArgumentException("A human decision actor cannot consist only of whitespace, format or variation-selector characters.", nameof(actor));
        }
        if (string.Equals(reservedName, AutomaticActor, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The actor 'gate' is reserved for automatic outcomes; use a human decision identity.", nameof(actor));
        }
    }

    private static bool IsTerminalControl(char c) =>
        char.IsControl(c)
        || c is '\u061C' or '\u200E' or '\u200F' or '\u2028' or '\u2029'
        || c is >= '\u202A' and <= '\u202E'
        || c is >= '\u2066' and <= '\u2069';

    private static bool IsReservedNameIgnorable(Rune rune) =>
        Rune.GetUnicodeCategory(rune) == UnicodeCategory.Format
        || rune.Value is >= 0x180B and <= 0x180D
        || rune.Value == 0x180F
        || rune.Value is >= 0xFE00 and <= 0xFE0F
        || rune.Value is >= 0xE0100 and <= 0xE01EF;
}
