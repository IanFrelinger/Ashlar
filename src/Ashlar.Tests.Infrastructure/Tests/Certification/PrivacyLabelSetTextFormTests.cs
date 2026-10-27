using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Ashlar.Abstractions.Security;
using Ashlar.Abstractions.Security.Egress;
using FluentAssertions;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>
/// SPEC-010 §3.1: the four privacy labels, the canonical text form of a <see cref="PrivacyLabelSet"/>, the converters
/// that carry it, and failing closed to <c>local-only</c>.
///
/// <para><b>Why the text form is strict.</b> Text is how a label set crosses a boundary: into a record, a log line, a
/// configuration file. Printing is canonical (labels sorted ordinally by their text, joined by commas, the empty set
/// written <c>none</c>) and <see cref="PrivacyLabelSet.TryParse"/> accepts exactly what printing produces, so text and
/// sets correspond one to one. Anything else (another case, a space, an unsorted or repeated label, an unknown name, a
/// legacy level name) is refused rather than repaired, because a set a lenient parser "fixes" is a set nobody wrote. A
/// refusal fails closed: the out set is <c>local-only</c>.</para>
///
/// <para><b>The mutation these tests exist to catch.</b> SPEC-010 §8's "make a failed parse return none" fails
/// <see cref="TryParse_RefusesNonCanonicalText_AndFailsClosedToLocalOnly"/> and
/// <see cref="ParseOrLocalOnly_MapsEveryRefusalToLocalOnly"/> on every row.</para>
///
/// <para>Hermetic: pure values, no files, no network.</para>
/// </summary>
[Trait("Category", "Certification")]
public sealed class PrivacyLabelSetTextFormTests
{
    /// <summary>
    /// The sixteen canonical texts, written out by hand: <c>none</c>, then every non-empty subset with its labels in
    /// ordinal order of their text (<c>local-only</c> &lt; <c>no-external-model</c> &lt; <c>no-network-export</c> &lt;
    /// <c>no-web</c>). The enum's own order puts <c>no-web</c> before <c>no-network-export</c>, so a printer that sorts by
    /// enum value fails the rows that hold both.
    /// </summary>
    public static TheoryData<string> CanonicalTexts => new()
    {
        "none",
        "local-only",
        "no-external-model",
        "no-network-export",
        "no-web",
        "local-only,no-external-model",
        "local-only,no-network-export",
        "local-only,no-web",
        "no-external-model,no-network-export",
        "no-external-model,no-web",
        "no-network-export,no-web",
        "local-only,no-external-model,no-network-export",
        "local-only,no-external-model,no-web",
        "local-only,no-network-export,no-web",
        "no-external-model,no-network-export,no-web",
        "local-only,no-external-model,no-network-export,no-web",
    };

    /// <summary>
    /// Text that is not the canonical form of any set. Many rows are one character away from a canonical row, which is
    /// the point: each names one rule of the grammar that a lenient parser would bend.
    /// </summary>
    public static TheoryData<string?> NonCanonicalTexts => new()
    {
        null,
        "",
        " ",
        "\t",
        // The empty set has one spelling.
        "None",
        "NONE",
        "none ",
        " none",
        "none,local-only",
        "local-only,none",
        "none,none",
        "{}",
        "[]",
        // Case, whitespace and separators.
        "Local-Only",
        "LOCAL-ONLY",
        "No-Web",
        "local-only ",
        " local-only",
        "local-only\n",
        "local-only, no-web",
        "local-only ,no-web",
        "local-only;no-web",
        "local-only|no-web",
        "local-only no-web",
        "local-only,",
        ",local-only",
        "local-only,,no-web",
        "\"local-only\"",
        // Order and duplicates. The enum declares no-web before no-network-export; the text form sorts by text.
        "no-web,local-only",
        "no-web,no-network-export",
        "no-network-export,no-external-model",
        "no-external-model,local-only",
        "no-web,no-network-export,no-external-model,local-only",
        "no-web,no-web",
        "local-only,local-only",
        "local-only,no-web,no-web",
        // Unknown and near-miss names, the member names and the numeric values.
        "localonly",
        "local_only",
        "local-only-data",
        "no-external-models",
        "no-model",
        "no-cloud",
        "no-network",
        "no-export",
        "no-websearch",
        "LocalOnly",
        "NoExternalModel",
        "NoWeb",
        "NoNetworkExport",
        "0",
        "3",
        // Look-alike characters: a Unicode hyphen (U+2010), a non-breaking hyphen (U+2011), a full-width hyphen, and a
        // trailing zero-width space.
        "local\u2010only",
        "local\u2011only",
        "local\uFF0Donly",
        "local-only\u200B",
        // SPEC-007 label text and caveat tokens are not privacy labels.
        "Public",
        "Internal",
        "Confidential",
        "Secret",
        "TopSecret",
        "SystemHigh",
        "Public//K:NOWEB",
        "NOWEB",
    };

    // ---- The four labels ----

    [Fact]
    public void ThereAreExactlyFourLabels_WithPinnedValues_AndLocalOnlyIsTheZero()
    {
        // Closed in version 1 (SPEC-010 §3.1). Stored values keep their meaning only if they never move, and the zero
        // value is local-only so an uninitialised label fails closed.
        Enum.GetValues<PrivacyLabel>().Select(l => (int)l).Should().Equal(0, 1, 2, 3);
        Enum.GetNames<PrivacyLabel>().Should().Equal("LocalOnly", "NoExternalModel", "NoWeb", "NoNetworkExport");
        default(PrivacyLabel).Should().Be(PrivacyLabel.LocalOnly);
    }

    [Theory]
    [InlineData(PrivacyLabel.LocalOnly, "local-only")]
    [InlineData(PrivacyLabel.NoExternalModel, "no-external-model")]
    [InlineData(PrivacyLabel.NoWeb, "no-web")]
    [InlineData(PrivacyLabel.NoNetworkExport, "no-network-export")]
    public void EachLabel_HasTheTextTheSpecNames(PrivacyLabel label, string text)
    {
        var set = PrivacyLabelSet.Of(label);

        set.ToString().Should().Be(text);
        set.Labels.Should().Equal(label);
        PrivacyLabelSet.TryParse(text, out var parsed).Should().BeTrue();
        parsed.Should().Be(set);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(-1)]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    public void AnUndefinedLabel_IsRefused_NeverDroppedOrWrapped(int value)
    {
        // 1 << 32 is 1 in C#, so an unchecked bit for 32 would quietly read as local-only; it must be refused instead.
        var label = (PrivacyLabel)value;

        Action of = () => PrivacyLabelSet.Of(PrivacyLabel.NoWeb, label);
        Action contains = () => PrivacyLabelSet.None.Contains(label);

        of.Should().ThrowExactly<ArgumentOutOfRangeException>().WithParameterName("labels");
        contains.Should().ThrowExactly<ArgumentOutOfRangeException>().WithParameterName("label");
    }

    [Fact]
    public void Of_NullArray_ThrowsNamingTheParameter()
    {
        Action act = () => PrivacyLabelSet.Of(null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("labels");
    }

    // ---- Printing ----

    [Fact]
    public void None_IsTheEmptySet_WrittenNone()
    {
        PrivacyLabelSet.None.ToString().Should().Be("none");
        PrivacyLabelSet.None.Labels.Should().BeEmpty();
        PrivacyLabelSet.Of().Should().Be(PrivacyLabelSet.None);
        PrivacyLabelSet.Of().ToString().Should().Be("none");
    }

    [Fact]
    public void LocalOnly_IsTheSetHoldingOnlyLocalOnly()
    {
        PrivacyLabelSet.LocalOnly.ToString().Should().Be("local-only");
        PrivacyLabelSet.LocalOnly.Labels.Should().Equal(PrivacyLabel.LocalOnly);
        PrivacyLabelSet.LocalOnly.Should().Be(PrivacyLabelSet.Of(PrivacyLabel.LocalOnly));
    }

    [Fact]
    public void EverySet_PrintsItsLabelsSortedOrdinallyByText_JoinedByCommas()
    {
        // The expected text is built here from each label's text, sorted with an ordinal comparer: the rule of §3.1
        // stated directly, so a printer that sorts any other way (by enum value, by culture) disagrees.
        var labels = Enum.GetValues<PrivacyLabel>();
        var texts = labels.ToDictionary(l => l, l => PrivacyLabelSet.Of(l).ToString());
        for (var bits = 0; bits < 1 << labels.Length; bits++)
        {
            var subset = labels.Where((_, i) => (bits & (1 << i)) != 0).ToArray();
            var expected = subset.Length == 0
                ? "none"
                : string.Join(",", subset.Select(l => texts[l]).OrderBy(t => t, StringComparer.Ordinal));

            PrivacyLabelSet.Of(subset).ToString().Should().Be(expected);
            PrivacyLabelSet.Of(Enumerable.Reverse(subset).ToArray()).ToString().Should().Be(expected, "the order given does not matter");
        }
    }

    [Fact]
    public void Labels_ComeInTheOrderOfTheTextForm()
    {
        var all = PrivacyLabelSet.Of(PrivacyLabel.NoNetworkExport, PrivacyLabel.NoWeb, PrivacyLabel.LocalOnly, PrivacyLabel.NoExternalModel);

        all.Labels.Should().Equal(PrivacyLabel.LocalOnly, PrivacyLabel.NoExternalModel, PrivacyLabel.NoNetworkExport, PrivacyLabel.NoWeb);
        all.ToString().Should().Be("local-only,no-external-model,no-network-export,no-web");
    }

    [Fact]
    public void EqualSets_BuiltDifferently_AreEqual_AndPrintIdentically()
    {
        var reference = PrivacyLabelSet.Of(PrivacyLabel.NoWeb, PrivacyLabel.NoExternalModel);
        var reordered = PrivacyLabelSet.Of(PrivacyLabel.NoExternalModel, PrivacyLabel.NoWeb, PrivacyLabel.NoWeb);
        PrivacyLabelSet.TryParse("no-external-model,no-web", out var parsed).Should().BeTrue();
        var united = PrivacyLabelSet.Of(PrivacyLabel.NoWeb).Union(PrivacyLabelSet.Of(PrivacyLabel.NoExternalModel));

        foreach (var set in new[] { reordered, parsed, united })
        {
            set.Should().Be(reference);
            set.Equals(reference).Should().BeTrue();
            set.Equals((object)reference).Should().BeTrue();
            (set == reference).Should().BeTrue();
            (set != reference).Should().BeFalse();
            set.GetHashCode().Should().Be(reference.GetHashCode());
            set.ToString().Should().Be("no-external-model,no-web");
        }
    }

    [Fact]
    public void Equality_IsFalseAgainstNullOtherSetsAndOtherTypes()
    {
        static PrivacyLabelSet NoWeb() => PrivacyLabelSet.Of(PrivacyLabel.NoWeb);

        NoWeb().Equals((PrivacyLabelSet?)null).Should().BeFalse();
        NoWeb().Equals((object?)null).Should().BeFalse();
        NoWeb().Equals("no-web").Should().BeFalse("a set is not equal to its own text");
        NoWeb().Equals(PrivacyLabelSet.None).Should().BeFalse();
        (NoWeb() == null).Should().BeFalse();
        (null == NoWeb()).Should().BeFalse();
        (NoWeb() != null).Should().BeTrue();
        ((PrivacyLabelSet?)null == null).Should().BeTrue();
        (PrivacyLabelSet.None == PrivacyLabelSet.LocalOnly).Should().BeFalse();
    }

    [Fact]
    public void Labels_CannotBeModifiedThroughACast()
    {
        var set = PrivacyLabelSet.Of(PrivacyLabel.NoWeb, PrivacyLabel.LocalOnly);
        var labels = set.Labels;

        (labels is PrivacyLabel[]).Should().BeFalse("the backing array must not be handed out");
        var list = (IList<PrivacyLabel>)labels;
        Action overwrite = () => list[0] = PrivacyLabel.NoExternalModel;
        Action add = () => list.Add(PrivacyLabel.NoExternalModel);
        Action remove = () => list.Remove(PrivacyLabel.LocalOnly);
        Action clear = () => list.Clear();

        overwrite.Should().Throw<NotSupportedException>();
        add.Should().Throw<NotSupportedException>();
        remove.Should().Throw<NotSupportedException>();
        clear.Should().Throw<NotSupportedException>();
        set.ToString().Should().Be("local-only,no-web");
        PrivacyLabelSet.Of(PrivacyLabel.NoWeb, PrivacyLabel.LocalOnly).Labels.Should().Equal(PrivacyLabel.LocalOnly, PrivacyLabel.NoWeb);
    }

    // ---- Parsing ----

    [Theory]
    [MemberData(nameof(CanonicalTexts))]
    public void TryParse_AcceptsCanonicalText_AndRoundTrips(string text)
    {
        PrivacyLabelSet.TryParse(text, out var set).Should().BeTrue();

        set.ToString().Should().Be(text, "every accepted string is exactly what ToString prints for its set");
        PrivacyLabelSet.ParseOrLocalOnly(text).Should().Be(set);
    }

    [Fact]
    public void TextAndSets_CorrespondOneToOne()
    {
        var texts = ((IEnumerable<object[]>)CanonicalTexts).Select(row => (string)row[0]).ToArray();
        texts.Should().HaveCount(16).And.OnlyHaveUniqueItems();

        var sets = texts.Select(text => PrivacyLabelSet.ParseOrLocalOnly(text)).ToArray();
        sets.Distinct().Should().HaveCount(16, "sixteen texts name sixteen different sets");

        // And every set the four labels can make prints as one of these texts.
        var labels = Enum.GetValues<PrivacyLabel>();
        for (var bits = 0; bits < 1 << labels.Length; bits++)
        {
            var set = PrivacyLabelSet.Of(labels.Where((_, i) => (bits & (1 << i)) != 0).ToArray());
            texts.Should().Contain(set.ToString());
            PrivacyLabelSet.ParseOrLocalOnly(set.ToString()).Should().Be(set);
        }
    }

    [Theory]
    [MemberData(nameof(NonCanonicalTexts))]
    public void TryParse_RefusesNonCanonicalText_AndFailsClosedToLocalOnly(string? text)
    {
        PrivacyLabelSet.TryParse(text, out var set).Should().BeFalse();

        set.Should().Be(PrivacyLabelSet.LocalOnly, "a caller that ignores the return value must still fail closed");
        set.ToString().Should().Be("local-only");
    }

    [Theory]
    [MemberData(nameof(NonCanonicalTexts))]
    public void ParseOrLocalOnly_MapsEveryRefusalToLocalOnly(string? text)
    {
        var set = PrivacyLabelSet.ParseOrLocalOnly(text);

        set.Should().Be(PrivacyLabelSet.LocalOnly);
        set.Contains(PrivacyLabel.LocalOnly).Should().BeTrue();
    }

    [Fact]
    public void UnparseableText_LeavesOnlyHostOpen()
    {
        // The fail-closed path a caller actually takes: text that is not canonical (a capitalised label here) becomes
        // local-only, and the rule then refuses every destination outside the host boundary.
        var set = PrivacyLabelSet.ParseOrLocalOnly("No-Web");

        PrivacyRule.Decide(set, EgressDestinationClass.Host).Allowed.Should().BeTrue();
        foreach (var destination in new[]
                 {
                     EgressDestinationClass.ExternalModel, EgressDestinationClass.WebSearch,
                     EgressDestinationClass.NetworkExport, EgressDestinationClass.Unknown,
                 })
        {
            PrivacyRule.Decide(set, destination).Reason.Should().Be(EgressRefusalReason.LocalOnlyData);
        }
    }

    [Fact]
    public void AnObjectNoConstructorRanOn_ReadsAsLocalOnly_NeverAsNone()
    {
        // A serializer that bypasses constructors leaves every field zero. The zero state must be the fail-closed set,
        // or such an object would carry "no restriction".
        var zeroed = (PrivacyLabelSet)RuntimeHelpers.GetUninitializedObject(typeof(PrivacyLabelSet));

        zeroed.Should().Be(PrivacyLabelSet.LocalOnly);
        zeroed.Should().NotBe(PrivacyLabelSet.None);
        zeroed.ToString().Should().Be("local-only");
        zeroed.Labels.Should().Equal(PrivacyLabel.LocalOnly);
        zeroed.Contains(PrivacyLabel.LocalOnly).Should().BeTrue();
        zeroed.Union(PrivacyLabelSet.None).Should().Be(PrivacyLabelSet.LocalOnly);
        PrivacyRule.Decide(zeroed, EgressDestinationClass.ExternalModel).Reason.Should().Be(EgressRefusalReason.LocalOnlyData);
    }

    // ---- JSON ----

    [Theory]
    [MemberData(nameof(CanonicalTexts))]
    public void Json_WritesTheCanonicalTextAndReadsItBack(string text)
    {
        var set = PrivacyLabelSet.ParseOrLocalOnly(text);

        var json = JsonSerializer.Serialize(set);

        json.Should().Be("\"" + text + "\"");
        JsonSerializer.Deserialize<PrivacyLabelSet>(json).Should().Be(set);
    }

    [Fact]
    public void Json_CarriesTheSetAsAStringInsideAnObject()
    {
        var json = JsonSerializer.Serialize(new LabelledValue { Labels = PrivacyLabelSet.Of(PrivacyLabel.NoWeb, PrivacyLabel.NoExternalModel) });

        json.Should().Be("{\"Labels\":\"no-external-model,no-web\"}");
        var read = JsonSerializer.Deserialize<LabelledValue>(json);
        read.Should().NotBeNull();
        read!.Labels.Should().Be(PrivacyLabelSet.Of(PrivacyLabel.NoExternalModel, PrivacyLabel.NoWeb));
    }

    [Theory]
    [InlineData("\"None\"")]
    [InlineData("\"no-web,no-external-model\"")]
    [InlineData("\"no-web, no-external-model\"")]
    [InlineData("\"Secret\"")]
    [InlineData("\"\"")]
    [InlineData("0")]
    [InlineData("3")]
    [InlineData("true")]
    [InlineData("null")]
    [InlineData("[\"no-web\"]")]
    [InlineData("{\"Labels\":[\"no-web\"]}")]
    public void Json_RefusesAnythingButCanonicalText(string json)
    {
        Action read = () => JsonSerializer.Deserialize<PrivacyLabelSet>(json);

        read.Should().Throw<JsonException>();
    }

    [Fact]
    public void Json_RefusesANullSetEitherWay()
    {
        // A null set is neither none nor local-only; a caller coalescing it to a default would pick the side for it.
        Action readProperty = () => JsonSerializer.Deserialize<LabelledValue>("{\"Labels\":null}");
        Action writeRoot = () => JsonSerializer.Serialize<PrivacyLabelSet>(null!);
        Action writeProperty = () => JsonSerializer.Serialize(new LabelledValue { Labels = null! });

        readProperty.Should().Throw<JsonException>();
        writeRoot.Should().Throw<JsonException>();
        writeProperty.Should().Throw<JsonException>();
    }

    // ---- TypeConverter ----

    [Theory]
    [MemberData(nameof(CanonicalTexts))]
    public void TypeConverter_ConvertsCanonicalTextBothWays(string text)
    {
        // Reflection-based serializers and configuration binders go through TypeDescriptor, not System.Text.Json.
        var converter = TypeDescriptor.GetConverter(typeof(PrivacyLabelSet));
        var set = PrivacyLabelSet.ParseOrLocalOnly(text);

        converter.CanConvertFrom(typeof(string)).Should().BeTrue();
        converter.CanConvertTo(typeof(string)).Should().BeTrue();
        converter.CanConvertFrom(typeof(int)).Should().BeFalse();
        converter.ConvertFromInvariantString(text).Should().Be(set);
        converter.ConvertToInvariantString(set).Should().Be(text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("None")]
    [InlineData("no-web,no-external-model")]
    [InlineData("local-only ")]
    [InlineData("Secret")]
    public void TypeConverter_RefusesNonCanonicalText(string text)
    {
        var converter = TypeDescriptor.GetConverter(typeof(PrivacyLabelSet));

        Action convert = () => converter.ConvertFromInvariantString(text);

        convert.Should().Throw<FormatException>();
    }

    [Fact]
    public void TypeConverter_RefusesNullEitherWay()
    {
        var converter = TypeDescriptor.GetConverter(typeof(PrivacyLabelSet));

        Action fromNull = () => converter.ConvertFrom(null!, System.Globalization.CultureInfo.InvariantCulture, null!);
        Action toText = () => converter.ConvertToInvariantString(null);

        fromNull.Should().Throw<FormatException>();
        toText.Should().Throw<NotSupportedException>();
    }

    private sealed class LabelledValue
    {
        public PrivacyLabelSet Labels { get; set; } = PrivacyLabelSet.None;
    }
}
