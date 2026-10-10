using System.Text;

namespace Ashlar.CLI.Output;

/// <summary>
/// Makes text the operator did not write safe to print on the operator's terminal.
///
/// <para>A console line is not a data structure — a terminal ACTS on some of the bytes in it. ESC
/// introduces an ANSI or OSC sequence that can move the cursor, repaint, or retitle the window; a
/// lone CR returns to column zero so what follows overwrites what was just printed; LF starts a new
/// line entirely. So a filename or a symlink target chosen by whoever wrote to a mesh store is not
/// merely ugly output — it is a way to FORGE a row. A name carrying a CR followed by
/// <c>  ✓ ADMITTED  update  · sealed by (a fingerprint the operator trusts)</c> paints a counterfeit
/// admission over its own refusal; the sealer fingerprint is the one part of a row the sender cannot
/// choose, so being able to overwrite it defeats the whole control.</para>
///
/// <para>Refusing such a name is not an option — the file is already on disk and the operator has to
/// be told WHICH file was refused. So it is printed, with every character a terminal would act on
/// replaced by U+FFFD. The row stays one line, the name stays recognisable, and the escape becomes
/// visible evidence instead of an instruction.</para>
///
/// <para>LENGTH IS THE SECOND HALF, and it is not cosmetic. Escaping alone leaves a sender able to
/// choose HOW MUCH the operator's terminal has to render. A package's <c>formatVersion</c> is a
/// required JSON string with no cap of its own, quoted straight into a refusal, so a 16&#160;MiB one
/// is a 16&#160;MiB refusal line: the row that named the file scrolls off the top of the scrollback,
/// and every legitimate row before it goes with it. Escaping turns each byte into U+FFFD without
/// making the line one character shorter, so the cap is part of the same job.</para>
///
    /// <para>SHARED ON PURPOSE. <c>pkg show</c>, <c>pkg import</c> and <c>pkg pull</c> print the same
    /// class of sender-chosen text — a package summary, proposedBy, a course name, a course detail —
    /// on the line beside the sealer fingerprint, and the import peek prints it before the gate
    /// verdict, so a refused package still paints the terminal. They call this function rather than
    /// growing a second copy that drifts. Issue #483.</para>
/// </summary>
internal static class UntrustedText
{
    /// <summary>Visible, and not itself text a terminal will act on.</summary>
    private const char Replacement = '\uFFFD';

    /// <summary>
    /// The default ceiling on one line of sender-chosen text. Comfortably above every refusal this
    /// CLI composes — the longest, SafePackageRead's symlink refusal, is about 450 characters with a
    /// short target — so an honest message is never touched, while a sender who wants a megabyte on
    /// the operator's screen gets a bounded line and a count instead.
    /// </summary>
    public const int DefaultMaxChars = 2000;

    /// <summary>
    /// Returns <paramref name="senderChosen"/> with every terminal-actionable character replaced and
    /// the result bounded to <see cref="DefaultMaxChars"/>. Null or empty becomes the empty string;
    /// text that was already clean and already short is returned unchanged.
    /// </summary>
    public static string ForConsole(string? senderChosen) => ForConsole(senderChosen, DefaultMaxChars);

    /// <summary>
    /// Returns <paramref name="senderChosen"/> with every terminal-actionable character replaced and
    /// the result bounded to <paramref name="maxChars"/>, with what was dropped NAMED rather than
    /// silently lost — an operator who cannot see the whole value must at least see that there was
    /// more, or a truncated line reads as the sender's actual text.
    /// </summary>
    /// <param name="senderChosen">Text this operator did not write.</param>
    /// <param name="maxChars">The ceiling on the text itself, before the truncation note.</param>
    public static string ForConsole(string? senderChosen, int maxChars)
    {
        if (string.IsNullOrEmpty(senderChosen))
        {
            return string.Empty;
        }

        // Bound BEFORE escaping, so a hostile 16 MiB string never becomes a hostile 16 MiB
        // StringBuilder on the way to being refused.
        if (senderChosen.Length > maxChars)
        {
            var cut = maxChars < 0 ? 0 : maxChars;
            // Never split a surrogate pair: half of one renders as a replacement glyph anyway, which
            // would look like an escape this function had defused — a lie about the input.
            if (cut > 0 && cut < senderChosen.Length && char.IsHighSurrogate(senderChosen[cut - 1]))
            {
                cut--;
            }
            return Escape(senderChosen[..cut])
                 + $"… [truncated: {senderChosen.Length:N0} characters, limit {maxChars:N0}]";
        }

        return Escape(senderChosen);
    }

    private static string Escape(string senderChosen)
    {
        // Allocates only when there is something to replace — which is never, for an honest filename.
        StringBuilder? clean = null;
        for (var i = 0; i < senderChosen.Length; i++)
        {
            var c = senderChosen[i];
            if (!IsTerminalActionable(c))
            {
                clean?.Append(c);
                continue;
            }
            clean ??= new StringBuilder(senderChosen.Length).Append(senderChosen, 0, i);
            clean.Append(Replacement);
        }
        return clean?.ToString() ?? senderChosen;
    }

    private static bool IsTerminalActionable(char c) =>
        // C0 — NUL, BEL, backspace, TAB, CR, LF, and ESC, which introduces every ANSI/OSC sequence.
        c < '\u0020'
        // DEL.
        || c == '\u007F'
        // C1. U+009B is CSI in its own right: an escape sequence with no ESC in front of it, which
        // is why stripping ESC alone would not be enough.
        || (c >= '\u0080' && c <= '\u009F')
        // Bidi overrides and isolates. These move nothing; they REORDER what is already on the line,
        // so a hostile name can be made to read left-to-right as an honest one.
        || c == '\u200E' || c == '\u200F'
        || (c >= '\u202A' && c <= '\u202E')
        || (c >= '\u2066' && c <= '\u2069');
}
