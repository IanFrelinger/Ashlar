using System.Text;
using System.Text.Json.Serialization;

namespace Ashlar.Abstractions.Security;

/// <summary>
/// A Bell-LaPadula security label: a <see cref="SecurityLevel"/>, a set of compartments (need-to-know tokens)
/// and a set of caveats (dissemination restrictions such as <c>NOWEB</c>), plus a distinguished top element,
/// <see cref="SystemHigh"/>.
/// </summary>
/// <remarks>
/// <para><b>Order.</b> <c>a &lt;= b</c> (<c>b.Dominates(a)</c>) holds iff <c>a.Level &lt;= b.Level</c>, every
/// compartment of <c>a</c> is a compartment of <c>b</c>, and every caveat of <c>a</c> is a caveat of <c>b</c>.
/// <see cref="SystemHigh"/> dominates every label and only itself dominates it. With
/// <see cref="Join(SecurityLabel)"/> (the high-water mark) and <see cref="Meet(SecurityLabel)"/> the labels form
/// a lattice whose bottom is <see cref="Public"/> and whose top is <see cref="SystemHigh"/>.</para>
/// <para><b>Caveats</b> are modelled exactly like compartments: whatever receives the data must carry every
/// caveat the data carries, so a restriction such as "no web" is one lattice rule rather than a second
/// mechanism.</para>
/// <para><b>Tokens</b> match <c>^[A-Z0-9][A-Z0-9_-]{0,63}$</c> and compare ordinally; an invalid token is
/// refused at construction. Instances are immutable, and <see cref="Compartments"/> and <see cref="Caveats"/>
/// are sorted (ordinal) and free of duplicates.</para>
/// <para><b>Text form.</b> <see cref="ToString"/> prints the one canonical form of a label, and
/// <see cref="TryParse"/> accepts exactly the canonical forms (see <see cref="TryParse"/>), so text and labels
/// correspond one to one.</para>
/// <para><b>Fail closed.</b> Unlabelled <b>data</b>, an unknown level name and a data label that does not parse
/// are all treated as <see cref="SystemHigh"/> (<see cref="ParseOrSystemHigh"/>), which only a
/// <see cref="SystemHigh"/> clearance may read. A clearance or a write destination fails closed the other way;
/// see <see cref="ParseOrSystemHigh"/>.</para>
/// <para><b>Serialisation.</b> The canonical text is the only wire form: System.Text.Json writes a label as that
/// string and refuses to read anything else, so <see cref="SystemHigh"/> cannot come back as a lower label. Never
/// rebuild a label field by field (SystemHigh would become TopSecret), and never filter or bridge on
/// <see cref="Level"/> alone.</para>
/// <para>This provides classification-style controls inside the runtime. It is not an accredited
/// cross-domain solution.</para>
/// </remarks>
[JsonConverter(typeof(SecurityLabelJsonConverter))]
public sealed class SecurityLabel : IEquatable<SecurityLabel>
{
    private const string SystemHighText = "SystemHigh";
    private const string CompartmentsPrefix = "//C:";
    private const string CaveatsPrefix = "//K:";
    private const int MaxTokenLength = 64;

    // Declared before the static label properties, so they are initialised first.
    private static readonly string[] SegmentSeparator = { "//" };
    private static readonly char[] TokenSeparator = { ',' };

    private readonly string[] _compartments;
    private readonly string[] _caveats;
    private readonly string _canonical;

    /// <summary>
    /// Creates a label. Compartments and caveats are sets: duplicates collapse and order does not matter.
    /// </summary>
    /// <param name="level">The classification level.</param>
    /// <param name="compartments">Need-to-know tokens, or <see langword="null"/> for none.</param>
    /// <param name="caveats">Dissemination-restriction tokens, or <see langword="null"/> for none.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="level"/> is not a defined level.</exception>
    /// <exception cref="ArgumentException">A token is null or does not match
    /// <c>^[A-Z0-9][A-Z0-9_-]{0,63}$</c>.</exception>
    public SecurityLabel(
        SecurityLevel level,
        IEnumerable<string>? compartments = null,
        IEnumerable<string>? caveats = null)
        : this(
            RequireDefined(level),
            NormalizeTokens(compartments, nameof(compartments)),
            NormalizeTokens(caveats, nameof(caveats)),
            isSystemHigh: false)
    {
    }

    // Trusted: the token arrays are already valid, sorted (ordinal), duplicate-free, and never mutated after this.
    private SecurityLabel(SecurityLevel level, string[] compartments, string[] caveats, bool isSystemHigh)
    {
        Level = level;
        IsSystemHigh = isSystemHigh;
        _compartments = compartments;
        _caveats = caveats;
        Compartments = Array.AsReadOnly(compartments);
        Caveats = Array.AsReadOnly(caveats);
        _canonical = isSystemHigh ? SystemHighText : Format(level, compartments, caveats);
    }

    /// <summary>The bottom element: <see cref="SecurityLevel.Public"/> with no compartments and no caveats.</summary>
    public static SecurityLabel Public { get; } =
        new(SecurityLevel.Public, Array.Empty<string>(), Array.Empty<string>(), isSystemHigh: false);

    /// <summary>
    /// The top element. It dominates every label, only it dominates itself, and joining anything with it gives
    /// it. Unlabelled or unparseable data is treated as this label.
    /// </summary>
    /// <remarks>
    /// It is the most restrictive label on the data side and the most permissive on the receiving side: a
    /// SystemHigh clearance reads everything and a SystemHigh destination receives everything.
    /// </remarks>
    public static SecurityLabel SystemHigh { get; } =
        new(SecurityLevel.TopSecret, Array.Empty<string>(), Array.Empty<string>(), isSystemHigh: true);

    /// <summary>
    /// The classification level. For <see cref="SystemHigh"/> this reports <see cref="SecurityLevel.TopSecret"/>;
    /// check <see cref="IsSystemHigh"/> rather than the level to recognise the top element.
    /// </summary>
    public SecurityLevel Level { get; }

    /// <summary>The compartments, sorted (ordinal) and free of duplicates. Empty for <see cref="SystemHigh"/>.</summary>
    public IReadOnlyList<string> Compartments { get; }

    /// <summary>The caveats, sorted (ordinal) and free of duplicates. Empty for <see cref="SystemHigh"/>.</summary>
    public IReadOnlyList<string> Caveats { get; }

    /// <summary><see langword="true"/> only for <see cref="SystemHigh"/>, the top element.</summary>
    public bool IsSystemHigh { get; }

    /// <summary>
    /// <see langword="true"/> when this label dominates <paramref name="other"/> (<c>other &lt;= this</c>): its
    /// level is at least <paramref name="other"/>'s and it carries every compartment and caveat
    /// <paramref name="other"/> carries. <see cref="SystemHigh"/> dominates everything and is dominated only by
    /// itself.
    /// </summary>
    /// <param name="other">The label to compare against.</param>
    public bool Dominates(SecurityLabel other)
    {
        SecurityGuard.ThrowIfNull(other, nameof(other));
        if (IsSystemHigh)
            return true;
        if (other.IsSystemHigh)
            return false;

        return other.Level <= Level
            && IsSubset(other._compartments, _compartments)
            && IsSubset(other._caveats, _caveats);
    }

    /// <summary>
    /// The least upper bound (the high-water mark, used for derivative labelling): the higher level and the
    /// union of the compartments and of the caveats. Anything joined with <see cref="SystemHigh"/> is
    /// <see cref="SystemHigh"/>.
    /// </summary>
    /// <param name="other">The label to join with.</param>
    public SecurityLabel Join(SecurityLabel other)
    {
        SecurityGuard.ThrowIfNull(other, nameof(other));
        if (IsSystemHigh || other.IsSystemHigh)
            return SystemHigh;

        return new SecurityLabel(
            Level >= other.Level ? Level : other.Level,
            Union(_compartments, other._compartments),
            Union(_caveats, other._caveats),
            isSystemHigh: false);
    }

    /// <summary>
    /// The greatest lower bound: the lower level and the intersection of the compartments and of the caveats.
    /// <c>x.Meet(SystemHigh)</c> is <c>x</c>.
    /// </summary>
    /// <param name="other">The label to meet with.</param>
    public SecurityLabel Meet(SecurityLabel other)
    {
        SecurityGuard.ThrowIfNull(other, nameof(other));
        if (IsSystemHigh)
            return other;
        if (other.IsSystemHigh)
            return this;

        return new SecurityLabel(
            Level <= other.Level ? Level : other.Level,
            Intersect(_compartments, other._compartments),
            Intersect(_caveats, other._caveats),
            isSystemHigh: false);
    }

    /// <summary>
    /// The join of every label in <paramref name="labels"/>: the label of something derived from all of them.
    /// The join of no labels is <see cref="Public"/>, the bottom, so an unlabelled source must be included as
    /// <see cref="SystemHigh"/>, never left out: leaving it out lowers the result. Use this only when the inputs
    /// are known to be complete; output whose inputs are unknown is <see cref="SystemHigh"/>.
    /// </summary>
    /// <param name="labels">The labels to join.</param>
    /// <exception cref="ArgumentException"><paramref name="labels"/> contains a null element.</exception>
    public static SecurityLabel Join(IEnumerable<SecurityLabel> labels)
    {
        SecurityGuard.ThrowIfNull(labels, nameof(labels));
        var result = Public;
        foreach (var label in labels)
        {
            if (label is null)
                throw new ArgumentException("The sequence contains a null label.", nameof(labels));
            result = result.Join(label);
        }

        return result;
    }

    /// <summary>
    /// <see langword="true"/> when <paramref name="token"/> is a valid compartment or caveat token: 1 to 64
    /// characters, uppercase ASCII letters and digits, with <c>_</c> and <c>-</c> allowed after the first.
    /// </summary>
    /// <param name="token">The candidate token.</param>
    public static bool IsValidToken(string? token)
    {
        if (token is null || token.Length == 0 || token.Length > MaxTokenLength)
            return false;
        if (!IsUpperAlphanumeric(token[0]))
            return false;

        for (var i = 1; i < token.Length; i++)
        {
            var c = token[i];
            if (!IsUpperAlphanumeric(c) && c != '_' && c != '-')
                return false;
        }

        return true;
    }

    /// <summary>
    /// Parses the canonical text form printed by <see cref="ToString"/>.
    /// </summary>
    /// <remarks>
    /// <para>The grammar is <c>SystemHigh</c>, or a level name (<c>Public</c>, <c>Internal</c>,
    /// <c>Confidential</c>, <c>Secret</c>, <c>TopSecret</c>, exact case) followed by an optional
    /// <c>//C:</c> segment of compartments and then an optional <c>//K:</c> segment of caveats, each a
    /// comma-separated list of tokens, for example <c>Secret//C:ALPHA,BRAVO//K:NOWEB</c>.</para>
    /// <para>Only canonical text is accepted: no whitespace anywhere, no empty segment or token, no lowercase
    /// token, segments in the order <c>C</c> then <c>K</c>, and tokens in ascending ordinal order with no
    /// duplicates. Every accepted string is therefore exactly what <see cref="ToString"/> prints for the label it
    /// parses to.</para>
    /// <para>On failure <paramref name="label"/> is set to <see cref="SystemHigh"/>. That fails closed only for a
    /// label on data (or on a write source, such as a high-water-mark floor). A caller parsing a clearance or a
    /// write destination must check the return value and fall back to <see cref="Public"/>, because a
    /// <see cref="SystemHigh"/> clearance reads everything and a <see cref="SystemHigh"/> destination receives
    /// everything.</para>
    /// </remarks>
    /// <param name="text">The text to parse.</param>
    /// <param name="label">The parsed label, or <see cref="SystemHigh"/> when the text is not canonical.</param>
    /// <returns><see langword="true"/> when <paramref name="text"/> is a canonical label.</returns>
    public static bool TryParse(string? text, out SecurityLabel label)
    {
        var parsed = ParseCanonical(text);
        label = parsed ?? SystemHigh;
        return parsed is not null;
    }

    /// <summary>
    /// Parses <paramref name="text"/> as <see cref="TryParse"/> does, and fails closed: a null, blank, unknown or
    /// malformed label is <see cref="SystemHigh"/>.
    /// </summary>
    /// <remarks>
    /// Use this for a label on <b>data</b> (what is being read). A <b>clearance</b> or a <b>write
    /// destination</b> that cannot be read fails closed in the opposite direction, to <see cref="Public"/>:
    /// treating it as <see cref="SystemHigh"/> would widen what it may read or receive. (TrustTierOrder in the
    /// RAG pipeline draws the same line between a record and a caller.)
    /// </remarks>
    /// <param name="text">The text to parse.</param>
    public static SecurityLabel ParseOrSystemHigh(string? text) => ParseCanonical(text) ?? SystemHigh;

    /// <summary>
    /// The canonical text form, for example <c>Secret</c>, <c>Secret//C:ALPHA,BRAVO</c>,
    /// <c>Secret//C:ALPHA//K:NOWEB</c>, <c>Public//K:NOWEB</c> or <c>SystemHigh</c>. Equal labels print
    /// identically, and <see cref="TryParse"/> reads the text back to an equal label.
    /// </summary>
    public override string ToString() => _canonical;

    /// <inheritdoc />
    public bool Equals(SecurityLabel? other) =>
        other is not null
        && (ReferenceEquals(this, other) || string.Equals(_canonical, other._canonical, StringComparison.Ordinal));

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as SecurityLabel);

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(_canonical);

    /// <summary>Value equality, the same as <see cref="Equals(SecurityLabel)"/>; never a reference comparison.</summary>
    public static bool operator ==(SecurityLabel? left, SecurityLabel? right) =>
        left is null ? right is null : left.Equals(right);

    /// <summary>Value inequality, the negation of <c>==</c>.</summary>
    public static bool operator !=(SecurityLabel? left, SecurityLabel? right) => !(left == right);

    // What `source` carries that `receiver` lacks, sorted (ordinal). The backing arrays stay private to this type.
    internal static string[] MissingCompartments(SecurityLabel source, SecurityLabel receiver) =>
        Missing(source._compartments, receiver._compartments);

    internal static string[] MissingCaveats(SecurityLabel source, SecurityLabel receiver) =>
        Missing(source._caveats, receiver._caveats);

    // Quotes caller-supplied text for an exception message: bounded length, control characters replaced.
    internal static string Quote(string? text)
    {
        if (text is null)
            return "(null)";

        const int MaxQuoted = 80;
        var builder = new StringBuilder("'");
        for (var i = 0; i < text.Length && i < MaxQuoted; i++)
            builder.Append(char.IsControl(text[i]) ? '?' : text[i]);
        if (text.Length > MaxQuoted)
            builder.Append("...");
        return builder.Append('\'').ToString();
    }

    internal static string LevelName(SecurityLevel level) => level switch
    {
        SecurityLevel.Public => "Public",
        SecurityLevel.Internal => "Internal",
        SecurityLevel.Confidential => "Confidential",
        SecurityLevel.Secret => "Secret",
        SecurityLevel.TopSecret => "TopSecret",
        _ => throw new ArgumentOutOfRangeException(nameof(level)),
    };

    // The tokens of `required` that `held` lacks, in order. Both arrays are sorted (ordinal) and duplicate-free.
    private static string[] Missing(string[] required, string[] held)
    {
        var missing = new List<string>();
        var j = 0;
        foreach (var token in required)
        {
            while (j < held.Length && string.CompareOrdinal(held[j], token) < 0)
                j++;

            if (j < held.Length && string.Equals(held[j], token, StringComparison.Ordinal))
                j++;
            else
                missing.Add(token);
        }

        return missing.ToArray();
    }

    internal static string JoinTokens(string[] tokens)
    {
        var builder = new StringBuilder();
        AppendTokens(builder, tokens);
        return builder.ToString();
    }

    private static SecurityLevel RequireDefined(SecurityLevel level) =>
        level is >= SecurityLevel.Public and <= SecurityLevel.TopSecret
            ? level
            : throw new ArgumentOutOfRangeException(
                nameof(level),
                "A security level must be Public, Internal, Confidential, Secret or TopSecret.");

    private static string[] NormalizeTokens(IEnumerable<string>? tokens, string paramName)
    {
        if (tokens is null)
            return Array.Empty<string>();

        var list = new List<string>();
        foreach (var token in tokens)
        {
            if (!IsValidToken(token))
            {
                throw new ArgumentException(
                    "Invalid security label token " + Quote(token)
                    + ": a token is 1 to 64 uppercase ASCII letters, digits, '_' or '-', starting with an uppercase letter or digit.",
                    paramName);
            }

            list.Add(token);
        }

        if (list.Count == 0)
            return Array.Empty<string>();

        list.Sort(StringComparer.Ordinal);
        var distinct = new List<string>(list.Count);
        foreach (var token in list)
        {
            if (distinct.Count == 0 || !string.Equals(distinct[distinct.Count - 1], token, StringComparison.Ordinal))
                distinct.Add(token);
        }

        return distinct.ToArray();
    }

    private static bool IsUpperAlphanumeric(char c) => c is (>= 'A' and <= 'Z') or (>= '0' and <= '9');

    private static bool IsSubset(string[] subset, string[] superset) =>
        subset.Length <= superset.Length && Missing(subset, superset).Length == 0;

    private static string[] Union(string[] a, string[] b)
    {
        if (a.Length == 0)
            return b;
        if (b.Length == 0)
            return a;

        var result = new List<string>(a.Length + b.Length);
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            var order = string.CompareOrdinal(a[i], b[j]);
            if (order < 0)
            {
                result.Add(a[i++]);
            }
            else if (order > 0)
            {
                result.Add(b[j++]);
            }
            else
            {
                result.Add(a[i++]);
                j++;
            }
        }

        while (i < a.Length)
            result.Add(a[i++]);
        while (j < b.Length)
            result.Add(b[j++]);

        return result.ToArray();
    }

    private static string[] Intersect(string[] a, string[] b)
    {
        if (a.Length == 0 || b.Length == 0)
            return Array.Empty<string>();

        var result = new List<string>(Math.Min(a.Length, b.Length));
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            var order = string.CompareOrdinal(a[i], b[j]);
            if (order < 0)
            {
                i++;
            }
            else if (order > 0)
            {
                j++;
            }
            else
            {
                result.Add(a[i]);
                i++;
                j++;
            }
        }

        return result.ToArray();
    }

    private static string Format(SecurityLevel level, string[] compartments, string[] caveats)
    {
        var builder = new StringBuilder(LevelName(level));
        if (compartments.Length > 0)
        {
            builder.Append(CompartmentsPrefix);
            AppendTokens(builder, compartments);
        }

        if (caveats.Length > 0)
        {
            builder.Append(CaveatsPrefix);
            AppendTokens(builder, caveats);
        }

        return builder.ToString();
    }

    private static void AppendTokens(StringBuilder builder, string[] tokens)
    {
        for (var i = 0; i < tokens.Length; i++)
        {
            if (i > 0)
                builder.Append(',');
            builder.Append(tokens[i]);
        }
    }

    // Null unless `text` is exactly the canonical form of some label.
    private static SecurityLabel? ParseCanonical(string? text)
    {
        if (text is null || text.Length == 0)
            return null;

        var segments = text.Split(SegmentSeparator, StringSplitOptions.None);
        if (string.Equals(segments[0], SystemHighText, StringComparison.Ordinal))
            return segments.Length == 1 ? SystemHigh : null;

        if (!TryParseLevel(segments[0], out var level))
            return null;

        var index = 1;
        var compartments = Array.Empty<string>();
        var caveats = Array.Empty<string>();

        if (index < segments.Length && HasTag(segments[index], 'C'))
        {
            var parsed = ParseTokenList(segments[index]);
            if (parsed is null)
                return null;
            compartments = parsed;
            index++;
        }

        if (index < segments.Length && HasTag(segments[index], 'K'))
        {
            var parsed = ParseTokenList(segments[index]);
            if (parsed is null)
                return null;
            caveats = parsed;
            index++;
        }

        // Anything left over is an unknown, repeated, empty or out-of-order segment.
        if (index != segments.Length)
            return null;

        return compartments.Length == 0 && caveats.Length == 0 && level == SecurityLevel.Public
            ? Public
            : new SecurityLabel(level, compartments, caveats, isSystemHigh: false);
    }

    private static bool HasTag(string segment, char tag) =>
        segment.Length >= 2 && segment[0] == tag && segment[1] == ':';

    // Null unless the list after "C:" or "K:" is non-empty, every token is valid, and tokens strictly ascend.
    private static string[]? ParseTokenList(string segment)
    {
        var tokens = segment.Substring(2).Split(TokenSeparator);
        for (var i = 0; i < tokens.Length; i++)
        {
            if (!IsValidToken(tokens[i]))
                return null;
            if (i > 0 && string.CompareOrdinal(tokens[i - 1], tokens[i]) >= 0)
                return null;
        }

        return tokens;
    }

    private static bool TryParseLevel(string text, out SecurityLevel level)
    {
        switch (text)
        {
            case "Public":
                level = SecurityLevel.Public;
                return true;
            case "Internal":
                level = SecurityLevel.Internal;
                return true;
            case "Confidential":
                level = SecurityLevel.Confidential;
                return true;
            case "Secret":
                level = SecurityLevel.Secret;
                return true;
            case "TopSecret":
                level = SecurityLevel.TopSecret;
                return true;
            default:
                level = SecurityLevel.Public;
                return false;
        }
    }
}
