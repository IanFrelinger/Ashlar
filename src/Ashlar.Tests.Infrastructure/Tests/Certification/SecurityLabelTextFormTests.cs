using System.Text.Json;
using Ashlar.Abstractions.Security;
using FluentAssertions;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>
/// SPEC-007: the canonical text form of a <see cref="SecurityLabel"/>, and the construction rules it rests on.
///
/// <para><b>Why the text form is strict.</b> Text is how a label crosses a boundary: into a record, a header,
/// a configuration file. Printing is canonical (tokens sorted ordinally, duplicates collapsed) and
/// <see cref="SecurityLabel.TryParse"/> accepts exactly what printing produces, so text and labels correspond
/// one to one. Anything else — a lowercase level, a stray space, an unsorted or repeated token, segments out
/// of order — is refused rather than repaired, because a label a lenient parser "fixes" is a label nobody
/// wrote. A refusal fails closed: the out label is <see cref="SecurityLabel.SystemHigh"/>, which only a
/// SystemHigh clearance may read.</para>
///
/// <para>Hermetic: pure values, no files, no network.</para>
/// </summary>
[Trait("Category", "Certification")]
public sealed class SecurityLabelTextFormTests
{
    /// <summary>The five examples the spec gives for the text form.</summary>
    public static TheoryData<string> SpecExamples => new()
    {
        "Secret",
        "Secret//C:ALPHA,BRAVO",
        "Secret//C:ALPHA//K:NOWEB",
        "Public//K:NOWEB",
        "SystemHigh",
    };

    /// <summary>Canonical text beyond the spec examples: every level, both segments alone and together, the
    /// shortest and longest tokens, and token lists whose order only an ordinal sort gets right.</summary>
    public static TheoryData<string> CanonicalTexts => new()
    {
        "Public",
        "Internal",
        "Confidential",
        "Secret",
        "TopSecret",
        "SystemHigh",
        "Secret//C:ALPHA,BRAVO",
        "Secret//C:ALPHA//K:NOWEB",
        "Public//K:NOWEB",
        "Public//C:ALPHA",
        "Confidential//C:A",
        "Internal//K:9",
        "TopSecret//C:A-1,A0,AB,A_//K:NOFORN,NOWEB",
        "Secret//C:" + new string('A', 64),
    };

    /// <summary>
    /// Text that is not the canonical form of any label. Several rows are one character away from a canonical
    /// row, which is the point: each names one rule of the grammar that a lenient parser would bend.
    /// </summary>
    public static TheoryData<string?> NonCanonicalTexts => new()
    {
        null,
        "",
        " ",
        "secret",
        "SECRET",
        "Unclassified",
        // The enum's numeric value: Enum.Parse would accept it, and the grammar names levels, not numbers.
        "3",
        // DataSensitivityLevels.TopSecret's display name, not its value.
        "Top Secret",
        "TopSecret ",
        " Secret",
        "Secret\t",
        "Secret //C:ALPHA",
        "Secret//C: ALPHA",
        "Secret//C:alpha",
        "Secret//c:ALPHA",
        "Secret//C:",
        "Secret//K:",
        "Secret//C:ALPHA,,BRAVO",
        "Secret//C:,ALPHA",
        "Secret//C:ALPHA,",
        "Secret//C:BRAVO,ALPHA",
        // Unsorted ordinally ('0' is 0x30, '-' is 0x2D), whatever a culture-aware sort would say.
        "Secret//C:A0,A-1",
        "Secret//C:ALPHA,ALPHA",
        "Secret//K:NOWEB//C:ALPHA",
        "Secret//C:ALPHA//C:BRAVO",
        "Secret//K:NOFORN//K:NOWEB",
        "Secret//X:ALPHA",
        "Secret//",
        "Secret//C:ALPHA//",
        "Secret//C:ALPHA//K:NOWEB ",
        "Secret/C:ALPHA",
        "Secret///C:ALPHA",
        "SystemHigh//C:ALPHA",
        "SystemHigh ",
        "systemhigh",
        "SYSTEMHIGH",
        "Secret//C:" + new string('A', 65),
        "Secret//C:_ALPHA",
        "Secret//C:-ALPHA",
        "Secret//C:\u00C4LPHA",
    };

    /// <summary>
    /// Tokens the constructor must refuse: wrong case, blank, whitespace, too long, a bad first character,
    /// non-ASCII, and the characters the text form itself uses as separators.
    /// </summary>
    public static TheoryData<string> InvalidTokens => new()
    {
        "alpha",
        "Alpha",
        "",
        " ",
        "AL PHA",
        "ALPHA ",
        " ALPHA",
        "\tALPHA",
        new string('A', 65),
        "_ALPHA",
        "-ALPHA",
        "\u00C4LPHA",
        "AL,PHA",
        "AL/PHA",
        "AL:PHA",
        "AL.PHA",
    };

    /// <summary><see cref="SecurityLabel.IsValidToken"/> on the edges of <c>^[A-Z0-9][A-Z0-9_-]{0,63}$</c>.</summary>
    public static TheoryData<string?, bool> TokenBoundaries => new()
    {
        { "A", true },
        { "7", true },
        { new string('A', 64), true },
        { "A" + new string('_', 62) + "-", true },
        { "A-_9", true },
        { "REL-TO_USA", true },
        { null, false },
        { "", false },
        { new string('A', 65), false },
        { "_A", false },
        { "-A", false },
        { "a", false },
        { "Ab", false },
        { " A", false },
        { "A ", false },
        { "A B", false },
        { "A.B", false },
        { "A,B", false },
        { "A/B", false },
        { "A:B", false },
        // Letters and digits outside ASCII: char.IsUpper or char.IsDigit says yes to each of these.
        { "\u00C4", false },
        { "\u0130", false },
        { "\uFF21", false },
        { "\u0661", false },
    };

    // ---- Printing ----

    [Theory]
    [MemberData(nameof(SpecExamples))]
    public void SpecExample_PrintsItsCanonicalText(string text)
    {
        SpecExample(text).ToString().Should().Be(text);
    }

    [Fact]
    public void Tokens_PrintSorted_WhateverOrderTheyWereGivenIn()
    {
        var label = new SecurityLabel(SecurityLevel.Secret, ["BRAVO", "ALPHA"], ["NOWEB", "NOFORN"]);

        label.ToString().Should().Be("Secret//C:ALPHA,BRAVO//K:NOFORN,NOWEB");
        label.Compartments.Should().Equal("ALPHA", "BRAVO");
        label.Caveats.Should().Equal("NOFORN", "NOWEB");
    }

    [Fact]
    public void DuplicateTokens_Collapse()
    {
        var label = new SecurityLabel(
            SecurityLevel.Secret, ["ALPHA", "BRAVO", "ALPHA", "BRAVO"], ["NOWEB", "NOWEB"]);

        label.ToString().Should().Be("Secret//C:ALPHA,BRAVO//K:NOWEB");
        label.Compartments.Should().Equal("ALPHA", "BRAVO");
        label.Caveats.Should().Equal("NOWEB");
    }

    [Fact]
    public void Tokens_SortOrdinally_NotByCulture()
    {
        // '-' (0x2D) < '0' (0x30) < 'B' (0x42) < '_' (0x5F). A culture-aware comparison weighs punctuation
        // differently, and the order it picks would then depend on the host's culture data, which is exactly
        // what a canonical form cannot depend on.
        var label = new SecurityLabel(SecurityLevel.Secret, ["A_", "AB", "A0", "A-1"]);

        label.ToString().Should().Be("Secret//C:A-1,A0,AB,A_");
        label.Compartments.Should().Equal("A-1", "A0", "AB", "A_");
    }

    [Fact]
    public void EqualLabels_BuiltDifferently_PrintIdentically_AndAreEqual()
    {
        var reference = new SecurityLabel(SecurityLevel.Secret, ["BRAVO", "ALPHA", "BRAVO"], ["NOWEB"]);
        var fromSortedInput = new SecurityLabel(SecurityLevel.Secret, ["ALPHA", "BRAVO"], ["NOWEB"]);
        SecurityLabel.TryParse("Secret//C:ALPHA,BRAVO//K:NOWEB", out var fromText).Should().BeTrue();
        var fromJoin = new SecurityLabel(SecurityLevel.Confidential, ["ALPHA"])
            .Join(new SecurityLabel(SecurityLevel.Secret, ["BRAVO"], ["NOWEB"]));

        foreach (var label in new[] { fromSortedInput, fromText, fromJoin })
        {
            label.ToString().Should().Be(reference.ToString());
            label.Equals(reference).Should().BeTrue();
            label.Should().Be(reference);
            label.GetHashCode().Should().Be(reference.GetHashCode());
        }
    }

    [Fact]
    public void ACompartmentAndACaveat_WithTheSameToken_AreDifferentLabels()
    {
        var compartment = new SecurityLabel(SecurityLevel.Secret, compartments: ["NOWEB"]);
        var caveat = new SecurityLabel(SecurityLevel.Secret, caveats: ["NOWEB"]);

        compartment.ToString().Should().Be("Secret//C:NOWEB");
        caveat.ToString().Should().Be("Secret//K:NOWEB");
        compartment.Should().NotBe(caveat);
    }

    // ---- Parsing ----

    [Theory]
    [MemberData(nameof(CanonicalTexts))]
    public void TryParse_AcceptsCanonicalText_AndRoundTrips(string text)
    {
        SecurityLabel.TryParse(text, out var label).Should().BeTrue();

        label.ToString().Should().Be(text, "every accepted string is exactly what ToString prints for its label");
        label.IsSystemHigh.Should().Be(text == "SystemHigh");
    }

    [Theory]
    [MemberData(nameof(SpecExamples))]
    public void TryParse_ReadsEachSpecExample_AsTheLabelItNames(string text)
    {
        var expected = SpecExample(text);

        SecurityLabel.TryParse(text, out var label).Should().BeTrue();

        label.Should().Be(expected);
        label.Level.Should().Be(expected.Level);
        label.Compartments.Should().Equal(expected.Compartments);
        label.Caveats.Should().Equal(expected.Caveats);
        label.IsSystemHigh.Should().Be(expected.IsSystemHigh);
    }

    [Theory]
    [MemberData(nameof(NonCanonicalTexts))]
    public void TryParse_RefusesNonCanonicalText_AndFailsClosedToSystemHigh(string? text)
    {
        SecurityLabel.TryParse(text, out var label).Should().BeFalse();

        label.IsSystemHigh.Should().BeTrue("a caller that ignores the return value must still fail closed");
        label.Should().Be(SecurityLabel.SystemHigh);
    }

    [Theory]
    [MemberData(nameof(NonCanonicalTexts))]
    public void ParseOrSystemHigh_MapsEveryRefusalToSystemHigh(string? text)
    {
        var label = SecurityLabel.ParseOrSystemHigh(text);

        label.IsSystemHigh.Should().BeTrue();
        label.Should().Be(SecurityLabel.SystemHigh);
    }

    [Theory]
    [MemberData(nameof(SpecExamples))]
    public void ParseOrSystemHigh_ReadsEachSpecExample_AsTheLabelItNames(string text)
    {
        var label = SecurityLabel.ParseOrSystemHigh(text);

        label.Should().Be(SpecExample(text));
        label.ToString().Should().Be(text);
    }

    // ---- Construction ----

    [Theory]
    [MemberData(nameof(InvalidTokens))]
    public void Constructor_RefusesAnInvalidCompartment_NamingTheParameter(string token)
    {
        Action act = () => _ = new SecurityLabel(SecurityLevel.Secret, compartments: ["ALPHA", token]);

        act.Should().ThrowExactly<ArgumentException>().WithParameterName("compartments");
    }

    [Theory]
    [MemberData(nameof(InvalidTokens))]
    public void Constructor_RefusesAnInvalidCaveat_NamingTheParameter(string token)
    {
        Action act = () => _ = new SecurityLabel(SecurityLevel.Secret, ["ALPHA"], caveats: ["NOWEB", token]);

        act.Should().ThrowExactly<ArgumentException>().WithParameterName("caveats");
    }

    [Fact]
    public void Constructor_RefusesANullToken_NamingTheParameter()
    {
        Action nullCompartment = () => _ = new SecurityLabel(
            SecurityLevel.Secret, compartments: new string[] { "ALPHA", null! });
        Action nullCaveat = () => _ = new SecurityLabel(
            SecurityLevel.Secret, caveats: new string[] { null!, "NOWEB" });

        nullCompartment.Should().ThrowExactly<ArgumentException>().WithParameterName("compartments");
        nullCaveat.Should().ThrowExactly<ArgumentException>().WithParameterName("caveats");
    }

    [Theory]
    [InlineData(5)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    public void Constructor_RefusesAnUndefinedLevel(int value)
    {
        Action act = () => _ = new SecurityLabel((SecurityLevel)value);

        act.Should().ThrowExactly<ArgumentOutOfRangeException>().WithParameterName("level");
    }

    [Fact]
    public void Constructor_TreatsNullTokenSequencesAsNone()
    {
        var label = new SecurityLabel(SecurityLevel.Secret, compartments: null, caveats: null);

        label.Compartments.Should().BeEmpty();
        label.Caveats.Should().BeEmpty();
        label.ToString().Should().Be("Secret");
        label.Should().Be(new SecurityLabel(SecurityLevel.Secret));
        label.Should().Be(new SecurityLabel(SecurityLevel.Secret, Array.Empty<string>(), Array.Empty<string>()));
    }

    [Fact]
    public void Constructor_CopiesTheTokens_SoTheCallersArrayCannotChangeTheLabel()
    {
        var compartments = new[] { "ALPHA" };
        var label = new SecurityLabel(SecurityLevel.Secret, compartments);

        compartments[0] = "ZULU";

        label.Compartments.Should().Equal("ALPHA");
        label.ToString().Should().Be("Secret//C:ALPHA");
    }

    [Theory]
    [MemberData(nameof(TokenBoundaries))]
    public void IsValidToken_AgreesWithTheTokenGrammar(string? token, bool expected)
    {
        SecurityLabel.IsValidToken(token).Should().Be(expected);
    }

    [Fact]
    public void ATokenOfSixtyFourCharacters_IsAcceptedEverywhere_AndOneOfSixtyFiveNowhere()
    {
        var longest = new string('Z', 64);
        var tooLong = new string('Z', 65);

        var label = new SecurityLabel(SecurityLevel.Secret, [longest], [longest]);
        label.ToString().Should().Be("Secret//C:" + longest + "//K:" + longest);
        SecurityLabel.TryParse(label.ToString(), out var parsed).Should().BeTrue();
        parsed.Should().Be(label);

        Action act = () => _ = new SecurityLabel(SecurityLevel.Secret, caveats: [tooLong]);
        act.Should().ThrowExactly<ArgumentException>().WithParameterName("caveats");
        SecurityLabel.TryParse("Secret//K:" + tooLong, out _).Should().BeFalse();
    }

    // ---- The distinguished labels ----

    [Fact]
    public void SystemHigh_IsNotTheTopSecretLabel()
    {
        // SystemHigh reports TopSecret as its level, so a check on Level alone cannot tell the two apart.
        // They must not compare equal or print the same, or unlabelled data would read as plain TopSecret.
        var topSecret = new SecurityLabel(SecurityLevel.TopSecret);

        SecurityLabel.SystemHigh.Level.Should().Be(SecurityLevel.TopSecret);
        SecurityLabel.SystemHigh.IsSystemHigh.Should().BeTrue();
        topSecret.IsSystemHigh.Should().BeFalse();

        SecurityLabel.SystemHigh.Should().NotBe(topSecret);
        topSecret.Should().NotBe(SecurityLabel.SystemHigh);
        SecurityLabel.SystemHigh.ToString().Should().Be("SystemHigh");
        topSecret.ToString().Should().Be("TopSecret");

        SecurityLabel.SystemHigh.Compartments.Should().BeEmpty();
        SecurityLabel.SystemHigh.Caveats.Should().BeEmpty();
        SecurityLabel.ParseOrSystemHigh("TopSecret").IsSystemHigh.Should().BeFalse();
    }

    [Fact]
    public void Public_IsTheConstructedPublicLabel()
    {
        var constructed = new SecurityLabel(SecurityLevel.Public);

        SecurityLabel.Public.Should().Be(constructed);
        SecurityLabel.Public.GetHashCode().Should().Be(constructed.GetHashCode());
        SecurityLabel.Public.ToString().Should().Be("Public");
        SecurityLabel.Public.Level.Should().Be(SecurityLevel.Public);
        SecurityLabel.Public.IsSystemHigh.Should().BeFalse();
        SecurityLabel.Public.Compartments.Should().BeEmpty();
        SecurityLabel.Public.Caveats.Should().BeEmpty();

        // The zero value of the enum is the bottom of the order.
        new SecurityLabel(default).Should().Be(SecurityLabel.Public);
    }

    [Fact]
    public void Equality_IsFalseAgainstNullAndOtherTypes()
    {
        // A fresh label per call: the compiler treats x.Equals(null) as a null test and would mark a shared local
        // maybe-null afterwards.
        static SecurityLabel Secret() => new(SecurityLevel.Secret);

        Secret().Equals((SecurityLabel?)null).Should().BeFalse();
        Secret().Equals((object?)null).Should().BeFalse();
        Secret().Equals("Secret").Should().BeFalse("a label is not equal to its own text");
        (Secret() == null).Should().BeFalse();
        (null == Secret()).Should().BeFalse();
        (Secret() != null).Should().BeTrue();
    }

    // ---- Immutability ----

    [Fact]
    public void CompartmentsAndCaveats_CannotBeModifiedThroughACast()
    {
        var label = new SecurityLabel(SecurityLevel.Secret, ["ALPHA", "BRAVO"], ["NOWEB"]);

        AssertCannotBeModified(label.Compartments);
        AssertCannotBeModified(label.Caveats);

        label.Compartments.Should().Equal("ALPHA", "BRAVO");
        label.Caveats.Should().Equal("NOWEB");
        label.ToString().Should().Be("Secret//C:ALPHA,BRAVO//K:NOWEB");
    }

    private static void AssertCannotBeModified(IReadOnlyList<string> tokens)
    {
        // An array would pass every check here but two: arrays are fixed-size, so Add, Insert, Remove and
        // Clear throw NotSupportedException on an array too, but an element of an array can be overwritten
        // in place. The type check and the indexer are the assertions that tell a wrapper from the array.
        (tokens is string[]).Should().BeFalse("the backing array must not be handed out");

        var list = (IList<string>)tokens;
        var collection = (ICollection<string>)tokens;
        var first = tokens[0];

        Action overwrite = () => list[0] = "ZULU";
        Action insert = () => list.Insert(0, "ZULU");
        Action removeAt = () => list.RemoveAt(0);
        Action add = () => collection.Add("ZULU");
        Action remove = () => collection.Remove(first);
        Action clear = () => collection.Clear();

        overwrite.Should().Throw<NotSupportedException>();
        insert.Should().Throw<NotSupportedException>();
        removeAt.Should().Throw<NotSupportedException>();
        add.Should().Throw<NotSupportedException>();
        remove.Should().Throw<NotSupportedException>();
        clear.Should().Throw<NotSupportedException>();

        tokens[0].Should().Be(first);
    }

    // ---- JSON ----

    [Theory]
    [MemberData(nameof(SpecExamples))]
    public void Json_WritesTheCanonicalTextAndReadsItBack(string text)
    {
        var label = SpecExample(text);

        var json = JsonSerializer.Serialize(label);

        json.Should().Be("\"" + text + "\"");
        JsonSerializer.Deserialize<SecurityLabel>(json).Should().Be(label);
    }

    [Fact]
    public void Json_KeepsSystemHighAsTheTopInsideAnObject()
    {
        // A structural projection could not rebuild SystemHigh through the public constructor, so it would come
        // back as a lower label. The canonical string cannot.
        var json = JsonSerializer.Serialize(new LabelledValue { Label = SecurityLabel.SystemHigh });

        json.Should().Be("{\"Label\":\"SystemHigh\"}");
        var read = JsonSerializer.Deserialize<LabelledValue>(json);
        read.Should().NotBeNull();
        read!.Label.IsSystemHigh.Should().BeTrue();
    }

    [Theory]
    [InlineData("\"secret\"")]
    [InlineData("\"Secret//C:BRAVO,ALPHA\"")]
    [InlineData("\"\"")]
    [InlineData("4")]
    [InlineData("true")]
    [InlineData("[\"Secret\"]")]
    [InlineData("{\"Level\":4,\"Compartments\":[],\"Caveats\":[],\"IsSystemHigh\":true}")]
    public void Json_RefusesAnythingButCanonicalText(string json)
    {
        // No guess either way: a converter cannot tell a label on data (fails closed to SystemHigh) from a
        // clearance or destination (fails closed to Public), so it refuses.
        Action read = () => JsonSerializer.Deserialize<SecurityLabel>(json);

        read.Should().Throw<JsonException>();
    }

    private sealed class LabelledValue
    {
        public SecurityLabel Label { get; set; } = SecurityLabel.Public;
    }

    /// <summary>Each spec example's label, built by the constructor rather than parsed.</summary>
    private static SecurityLabel SpecExample(string text) => text switch
    {
        "Secret" => new SecurityLabel(SecurityLevel.Secret),
        "Secret//C:ALPHA,BRAVO" => new SecurityLabel(SecurityLevel.Secret, ["ALPHA", "BRAVO"]),
        "Secret//C:ALPHA//K:NOWEB" => new SecurityLabel(SecurityLevel.Secret, ["ALPHA"], ["NOWEB"]),
        "Public//K:NOWEB" => new SecurityLabel(SecurityLevel.Public, caveats: ["NOWEB"]),
        "SystemHigh" => SecurityLabel.SystemHigh,
        _ => throw new ArgumentOutOfRangeException(nameof(text), text, "Not one of the spec examples."),
    };
}
