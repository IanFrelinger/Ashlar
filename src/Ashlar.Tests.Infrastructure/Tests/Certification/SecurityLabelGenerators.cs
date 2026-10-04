using Ashlar.Abstractions.Security;
using CsCheck;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>
/// CsCheck generators for the <see cref="SecurityLabel"/> property tests.
/// </summary>
/// <remarks>
/// <para><see cref="Label"/> draws its tokens from deliberately small alphabets, so subsets, overlaps and equal
/// labels turn up often and the lattice laws are exercised on related labels, not mostly on incomparable ones.
/// <see cref="WideLabel"/> draws from the whole token grammar instead, for the text form and the ordinal sort.</para>
/// <para>Every label is built with the public constructor, duplicates and all: collapsing them is part of what
/// is under test. <see cref="SecurityLabel.SystemHigh"/> is mixed in at one draw in eight.</para>
/// </remarks>
internal static class SecurityLabelGenerators
{
    private const int MaxTokenLength = 64;
    private const string TokenFirstChars = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";

    // '-' (0x2D) sorts before the digits, and '_' (0x5F) after the letters, so tokens built from these characters
    // put the ordinal sort to work in a way that letters alone would not.
    private const string TokenLaterChars = TokenFirstChars + "_-";

    /// <summary>Every defined level, <see cref="SecurityLevel.Public"/> (0) to <see cref="SecurityLevel.TopSecret"/> (4).</summary>
    public static readonly Gen<SecurityLevel> Level = Gen.Int[0, 4].Select(i => (SecurityLevel)i);

    /// <summary>Zero to three compartments from a four-token alphabet, duplicates allowed.</summary>
    public static readonly Gen<string[]> Compartments =
        Gen.OneOfConst("ALPHA", "BRAVO", "CHARLIE", "DELTA").Array[0, 3];

    /// <summary>Zero to two caveats from a three-token alphabet, duplicates allowed.</summary>
    public static readonly Gen<string[]> Caveats = Gen.OneOfConst("NOWEB", "NOFORN", "ORCON").Array[0, 2];

    /// <summary>A label over the small alphabets, or <see cref="SecurityLabel.SystemHigh"/>.</summary>
    public static readonly Gen<SecurityLabel> Label = WithSystemHigh(
        Gen.Select(Level, Compartments, Caveats, (level, compartments, caveats) =>
            new SecurityLabel(level, compartments, caveats)));

    /// <summary>
    /// Any valid token: a letter or digit, then 0 to 63 letters, digits, <c>_</c> or <c>-</c>. Most are one to
    /// three characters long, so equal tokens and tokens that are prefixes of one another both turn up.
    /// </summary>
    public static readonly Gen<string> Token = Gen.Select(
        Gen.Char[TokenFirstChars],
        Gen.Frequency<char[]>(
            (3, Gen.Char[TokenLaterChars].Array[0, 2]),
            (1, Gen.Char[TokenLaterChars].Array[0, MaxTokenLength - 1])),
        (first, rest) => first + new string(rest));

    /// <summary>
    /// A label with zero to four compartments and zero to three caveats drawn from <see cref="Token"/>, or
    /// <see cref="SecurityLabel.SystemHigh"/>.
    /// </summary>
    public static readonly Gen<SecurityLabel> WideLabel = WithSystemHigh(
        Gen.Select(Level, Token.Array[0, 4], Token.Array[0, 3], (level, compartments, caveats) =>
            new SecurityLabel(level, compartments, caveats)));

    /// <summary>
    /// Three labels with <c>low &lt;= middle &lt;= high</c> by construction: each adds tokens to the one before
    /// and its level is no lower. Built with the constructor, not with <see cref="SecurityLabel.Join(SecurityLabel)"/>,
    /// so the order holds whatever the lattice operations do. The top is sometimes
    /// <see cref="SecurityLabel.SystemHigh"/>.
    /// </summary>
    public static readonly Gen<(SecurityLabel, SecurityLabel, SecurityLabel)> Chain =
        Gen.Select(Level.Array[3], Compartments.Array[3], Caveats.Array[3], Gen.Int[0, 7], BuildChain);

    /// <summary>
    /// Two labels <c>a</c> and <c>b</c> with a common upper bound (above both) and a common lower bound (below
    /// both), again by construction: the bounds are built from the union and the intersection of the tokens and
    /// from the higher and the lower level, plus some slack. <c>a</c> and <c>b</c> themselves are drawn
    /// independently, so they are usually incomparable.
    /// </summary>
    public static readonly Gen<(SecurityLabel, SecurityLabel, SecurityLabel, SecurityLabel)> Bounded =
        Gen.Select(Level.Array[4], Compartments.Array[4], Caveats.Array[4], Gen.Int[0, 9], BuildBounded);

    /// <summary>
    /// Two labels: independent, ordered either way round (from <see cref="Chain"/>), or equal (distinct instances,
    /// except for the <see cref="SecurityLabel.SystemHigh"/> singleton).
    /// </summary>
    public static readonly Gen<(SecurityLabel, SecurityLabel)> Pair = Gen.Frequency<(SecurityLabel, SecurityLabel)>(
        (3, Gen.Select(Label, Label)),
        (1, Chain.Select((low, _, high) => (low, high))),
        (1, Chain.Select((low, _, high) => (high, low))),
        (1, Label.Select(label => (label, Rebuild(label)))));

    /// <summary>Three labels: independent, or a <see cref="Chain"/> in ascending order.</summary>
    public static readonly Gen<(SecurityLabel, SecurityLabel, SecurityLabel)> Triple =
        Gen.Frequency<(SecurityLabel, SecurityLabel, SecurityLabel)>(
            (2, Gen.Select(Label, Label, Label)),
            (1, Chain));

    private static Gen<SecurityLabel> WithSystemHigh(Gen<SecurityLabel> ordinary) =>
        Gen.Frequency<SecurityLabel>((7, ordinary), (1, Gen.Const(SecurityLabel.SystemHigh)));

    // An equal label that is a different instance, built from its tokens in reverse order.
    private static SecurityLabel Rebuild(SecurityLabel label) =>
        label.IsSystemHigh
            ? label
            : new SecurityLabel(label.Level, Enumerable.Reverse(label.Compartments), Enumerable.Reverse(label.Caveats));

    private static (SecurityLabel, SecurityLabel, SecurityLabel) BuildChain(
        SecurityLevel[] levels, string[][] compartments, string[][] caveats, int top)
    {
        var ascending = levels.OrderBy(level => level).ToArray();
        var low = new SecurityLabel(ascending[0], compartments[0], caveats[0]);
        var middle = new SecurityLabel(
            ascending[1],
            compartments[0].Concat(compartments[1]),
            caveats[0].Concat(caveats[1]));
        var high = new SecurityLabel(
            ascending[2],
            compartments[0].Concat(compartments[1]).Concat(compartments[2]),
            caveats[0].Concat(caveats[1]).Concat(caveats[2]));

        return top switch
        {
            0 => (low, middle, SecurityLabel.SystemHigh),
            1 => (low, SecurityLabel.SystemHigh, SecurityLabel.SystemHigh),
            _ => (low, middle, high),
        };
    }

    private static (SecurityLabel, SecurityLabel, SecurityLabel, SecurityLabel) BuildBounded(
        SecurityLevel[] levels, string[][] compartments, string[][] caveats, int systemHigh)
    {
        var a = new SecurityLabel(levels[0], compartments[0], caveats[0]);
        var b = new SecurityLabel(levels[1], compartments[1], caveats[1]);
        var upper = new SecurityLabel(
            Max(Max(levels[0], levels[1]), levels[2]),
            compartments[0].Concat(compartments[1]).Concat(compartments[2]),
            caveats[0].Concat(caveats[1]).Concat(caveats[2]));
        var lower = new SecurityLabel(
            Min(Min(levels[0], levels[1]), levels[3]),
            compartments[0].Intersect(compartments[1]).Intersect(compartments[3]),
            caveats[0].Intersect(caveats[1]).Intersect(caveats[3]));

        // Only SystemHigh is above SystemHigh, and anything below b is below SystemHigh too.
        return systemHigh switch
        {
            0 => (SecurityLabel.SystemHigh, b, SecurityLabel.SystemHigh, b),
            1 => (a, b, SecurityLabel.SystemHigh, lower),
            2 => (SecurityLabel.SystemHigh, SecurityLabel.SystemHigh, SecurityLabel.SystemHigh, lower),
            _ => (a, b, upper, lower),
        };
    }

    private static SecurityLevel Max(SecurityLevel x, SecurityLevel y) => x >= y ? x : y;

    private static SecurityLevel Min(SecurityLevel x, SecurityLevel y) => x <= y ? x : y;
}
