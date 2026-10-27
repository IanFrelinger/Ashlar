using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json.Serialization;

namespace Ashlar.Abstractions.Security;

/// <summary>
/// The privacy labels a piece of data carries: a set of zero or more <see cref="PrivacyLabel"/>s, each naming a kind
/// of destination the data must not reach (SPEC-010 §3.1). The empty set, written <c>none</c>, means no restriction.
/// </summary>
/// <remarks>
/// <para><b>Text form.</b> <see cref="ToString"/> prints the labels sorted ordinally by their text and joined by
/// commas, for example <c>no-external-model,no-network-export</c>, and the empty set as <c>none</c>.
/// <see cref="TryParse"/> accepts exactly those sixteen strings, so text and sets correspond one to one.</para>
/// <para><b>Fail closed.</b> Unlabelled data, an unknown label name and text that does not parse are all treated as
/// <see cref="LocalOnly"/> (<see cref="ParseOrLocalOnly"/>). So is an instance no constructor ran on, such as a
/// zeroed object a serializer made: it reads as <see cref="LocalOnly"/>, never as <see cref="None"/>.</para>
/// <para><b>Union.</b> A subject that has read several pieces of data holds the union of their sets
/// (<see cref="Union(PrivacyLabelSet)"/>, <see cref="ReadSet"/>). Union is commutative, associative and idempotent,
/// and <see cref="None"/> is its identity. Nothing here removes a label.</para>
/// <para><b>Serialisation.</b> The canonical text is the only wire form: System.Text.Json writes a set as that string
/// and refuses to read anything else, JSON <c>null</c> included, and a TypeConverter does the same for
/// reflection-based serializers and binders.</para>
/// <para>Instances are immutable, and each of the sixteen sets has one shared instance.</para>
/// </remarks>
[Experimental(SecurityExperimental.DiagnosticId, UrlFormat = SecurityExperimental.UrlFormat)]
[JsonConverter(typeof(PrivacyLabelSetJsonConverter))]
[TypeConverter(typeof(PrivacyLabelSetTypeConverter))]
public sealed class PrivacyLabelSet : IEquatable<PrivacyLabelSet>
{
    private const string NoneText = "none";
    private const int LabelCount = 4;
    private const int SetCount = 1 << LabelCount;

    // The local-only bit is stored inverted, so the all-zero state of an object no constructor ran on reads as
    // local-only, the fail-closed set, and never as none.
    private const int LocalOnlyBit = 1 << (int)PrivacyLabel.LocalOnly;

    // Every label in the order of the text form: ordinal by text, which is not the order of the enum values.
    private static readonly PrivacyLabel[] TextOrder =
        { PrivacyLabel.LocalOnly, PrivacyLabel.NoExternalModel, PrivacyLabel.NoNetworkExport, PrivacyLabel.NoWeb };

    // Indexed by the set's mask: bit (1 << (int)label) is set when the set contains the label.
    private static readonly string[] Texts = BuildTexts();
    private static readonly ReadOnlyCollection<PrivacyLabel>[] Lists = BuildLists();
    private static readonly PrivacyLabelSet[] Sets = BuildSets();
    private static readonly Dictionary<string, PrivacyLabelSet> ByText = BuildIndex();

    private readonly int _stored;

    private PrivacyLabelSet(int mask)
    {
        _stored = mask ^ LocalOnlyBit;
    }

    /// <summary>The empty set, written <c>none</c>: no restriction. The identity of <see cref="Union(PrivacyLabelSet)"/>.</summary>
    public static PrivacyLabelSet None => Sets[0];

    /// <summary>
    /// The set holding only <see cref="PrivacyLabel.LocalOnly"/>, written <c>local-only</c>. Unlabelled data, an unknown
    /// label name and text that does not parse are treated as this set.
    /// </summary>
    public static PrivacyLabelSet LocalOnly => Sets[LocalOnlyBit];

    /// <summary>The labels in the set, in the order of the text form. Empty for <see cref="None"/>.</summary>
    public IReadOnlyList<PrivacyLabel> Labels => Lists[Mask];

    private int Mask => _stored ^ LocalOnlyBit;

    /// <summary>The set holding exactly <paramref name="labels"/>; duplicates collapse and order does not matter.</summary>
    /// <param name="labels">The labels. None at all gives <see cref="None"/>.</param>
    /// <returns>The set of those labels.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="labels"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A label is not one of the four defined labels.</exception>
    public static PrivacyLabelSet Of(params PrivacyLabel[] labels)
    {
        SecurityGuard.ThrowIfNull(labels, nameof(labels));
        var mask = 0;
        foreach (var label in labels)
            mask |= Bit(label, nameof(labels));

        return Sets[mask];
    }

    /// <summary><see langword="true"/> when the set holds <paramref name="label"/>.</summary>
    /// <param name="label">The label to look for.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="label"/> is not one of the four defined labels.</exception>
    public bool Contains(PrivacyLabel label) => (Mask & Bit(label, nameof(label))) != 0;

    /// <summary>
    /// The union: every label either set holds. A subject that has read data carrying this set and data carrying
    /// <paramref name="other"/> holds the union.
    /// </summary>
    /// <param name="other">The set to unite with.</param>
    public PrivacyLabelSet Union(PrivacyLabelSet other)
    {
        SecurityGuard.ThrowIfNull(other, nameof(other));
        return Sets[Mask | other.Mask];
    }

    /// <summary>
    /// Parses the canonical text form printed by <see cref="ToString"/>.
    /// </summary>
    /// <remarks>
    /// <para>The canonical forms are <c>none</c>, and one to four distinct label texts (<c>local-only</c>,
    /// <c>no-external-model</c>, <c>no-network-export</c>, <c>no-web</c>) in ascending ordinal order, joined by commas,
    /// in exact case, with no whitespace anywhere. Exactly those sixteen strings are accepted, so every accepted string
    /// is what <see cref="ToString"/> prints for the set it parses to.</para>
    /// <para>On failure <paramref name="labels"/> is <see cref="LocalOnly"/>, so a caller that ignores the return value
    /// still fails closed.</para>
    /// </remarks>
    /// <param name="text">The text to parse.</param>
    /// <param name="labels">The parsed set, or <see cref="LocalOnly"/> when the text is not canonical.</param>
    /// <returns><see langword="true"/> when <paramref name="text"/> is the canonical text of a set.</returns>
    public static bool TryParse(string? text, out PrivacyLabelSet labels)
    {
        var parsed = text is not null && ByText.TryGetValue(text, out var found) ? found : null;
        labels = parsed ?? LocalOnly;
        return parsed is not null;
    }

    /// <summary>
    /// Parses <paramref name="text"/> as <see cref="TryParse"/> does, and fails closed: null, blank, unknown or
    /// malformed text is <see cref="LocalOnly"/>.
    /// </summary>
    /// <param name="text">The text to parse.</param>
    public static PrivacyLabelSet ParseOrLocalOnly(string? text)
    {
        _ = TryParse(text, out var labels);
        return labels;
    }

    /// <summary>
    /// The canonical text form: the labels sorted ordinally by their text and joined by commas, for example
    /// <c>no-external-model,no-network-export</c>, or <c>none</c> for the empty set. <see cref="TryParse"/> reads it
    /// back to an equal set.
    /// </summary>
    public override string ToString() => Texts[Mask];

    /// <inheritdoc />
    public bool Equals(PrivacyLabelSet? other) => other is not null && Mask == other.Mask;

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as PrivacyLabelSet);

    /// <inheritdoc />
    public override int GetHashCode() => Mask;

    /// <summary>Value equality, the same as <see cref="Equals(PrivacyLabelSet)"/>.</summary>
    public static bool operator ==(PrivacyLabelSet? left, PrivacyLabelSet? right) =>
        left is null ? right is null : left.Equals(right);

    /// <summary>Value inequality, the negation of <c>==</c>.</summary>
    public static bool operator !=(PrivacyLabelSet? left, PrivacyLabelSet? right) => !(left == right);

    // The text of one label.
    internal static string TextOf(PrivacyLabel label) => label switch
    {
        PrivacyLabel.LocalOnly => "local-only",
        PrivacyLabel.NoExternalModel => "no-external-model",
        PrivacyLabel.NoWeb => "no-web",
        PrivacyLabel.NoNetworkExport => "no-network-export",
        _ => throw new ArgumentOutOfRangeException(nameof(label), "Not one of the four privacy labels."),
    };

    private static int Bit(PrivacyLabel label, string paramName) =>
        label is >= PrivacyLabel.LocalOnly and <= PrivacyLabel.NoNetworkExport
            ? 1 << (int)label
            : throw new ArgumentOutOfRangeException(
                paramName,
                "A privacy label must be LocalOnly, NoExternalModel, NoWeb or NoNetworkExport.");

    private static string[] BuildTexts()
    {
        var texts = new string[SetCount];
        for (var mask = 0; mask < SetCount; mask++)
        {
            var builder = new StringBuilder();
            foreach (var label in TextOrder)
            {
                if ((mask & (1 << (int)label)) == 0)
                    continue;
                if (builder.Length > 0)
                    builder.Append(',');
                builder.Append(TextOf(label));
            }

            texts[mask] = mask == 0 ? NoneText : builder.ToString();
        }

        return texts;
    }

    private static ReadOnlyCollection<PrivacyLabel>[] BuildLists()
    {
        var lists = new ReadOnlyCollection<PrivacyLabel>[SetCount];
        for (var mask = 0; mask < SetCount; mask++)
        {
            var labels = new List<PrivacyLabel>(LabelCount);
            foreach (var label in TextOrder)
            {
                if ((mask & (1 << (int)label)) != 0)
                    labels.Add(label);
            }

            lists[mask] = Array.AsReadOnly(labels.ToArray());
        }

        return lists;
    }

    private static PrivacyLabelSet[] BuildSets()
    {
        var sets = new PrivacyLabelSet[SetCount];
        for (var mask = 0; mask < SetCount; mask++)
            sets[mask] = new PrivacyLabelSet(mask);

        return sets;
    }

    private static Dictionary<string, PrivacyLabelSet> BuildIndex()
    {
        var index = new Dictionary<string, PrivacyLabelSet>(SetCount, StringComparer.Ordinal);
        for (var mask = 0; mask < SetCount; mask++)
            index.Add(Texts[mask], Sets[mask]);

        return index;
    }
}
