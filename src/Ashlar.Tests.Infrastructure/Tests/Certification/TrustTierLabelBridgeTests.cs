using System.Text;
using Ashlar.Abstractions.Security;
using Ashlar.AI.Pipeline.Rag;
using CsCheck;
using FluentAssertions;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>
/// SPEC-007 PR 2: <see cref="TrustTierOrder.RecordLabel"/> and <see cref="TrustTierOrder.CallerLabel"/> map the RAG
/// trust tiers onto <see cref="SecurityLabel"/> without changing a single decision the tiers make today.
/// </summary>
/// <remarks>
/// <para><b>What is held together.</b> A known tier, in any spelling <see cref="TrustTierOrder.TryRank"/> accepts,
/// becomes the label at the level <see cref="TrustTierOrder.RecordRank"/> and <see cref="TrustTierOrder.CallerRank"/>
/// give it, prints as the canonical tier name and carries no compartments or caveats. A blank or unknown name becomes
/// <see cref="SecurityLabel.SystemHigh"/> for a record and <see cref="SecurityLabel.Public"/> for a caller: the two
/// fallback directions the rank functions already have.</para>
/// <para><b>The one intended difference.</b> The rank functions put an unlabelled record at TopSecret, which a
/// TopSecret caller may read; the bridge puts it at SystemHigh, which only a SystemHigh clearance may read, and no
/// tier name produces a SystemHigh clearance. So exactly one family of cells disagrees: an unlabelled record against a
/// TopSecret caller (and, for the re-index downgrade rule, an unlabelled stored record re-indexed at TopSecret). Those
/// cells are pinned on both sides here, so the difference cannot widen, vanish or move without a test going red. The
/// bridge is not wired into <see cref="TrustTierOrder.IsAllowed"/>, the search filter or the downgrade check; these
/// tests pin the semantics a later caller inherits, not enforcement anywhere.</para>
/// <para><b>Oracle.</b> The expected ranks come from the tables in this file, not from the code under test, so a
/// change to <see cref="TrustTierOrder.TryRank"/> that moved a spelling would show here as well as in its own
/// tests.</para>
/// <para>Hermetic: pure values, no files, no network.</para>
/// </remarks>
[Trait("Category", "Certification")]
public sealed class TrustTierLabelBridgeTests
{
    private const int TopRank = 4;

    /// <summary>The canonical tier names, indexed by rank: the oracle for <see cref="SecurityLabel.ToString"/>.</summary>
    private static readonly string[] CanonicalNames = { "Public", "Internal", "Confidential", "Secret", "TopSecret" };

    /// <summary>
    /// Every spelling of a known tier this suite exercises, with its rank: each canonical name exactly, in lower,
    /// upper and mixed case, and inside surrounding whitespace, plus the <c>top-secret</c> alias.
    /// </summary>
    private static readonly (string Text, int Rank)[] KnownSpellings = BuildKnownSpellings();

    /// <summary>
    /// Names that are not a known tier, so a record carrying one is unlabelled and a caller naming one is floored.
    /// Several look like a tier or like label text: <c>SystemHigh</c> and canonical label text with compartments are
    /// exactly what label parsing would accept, and they must not be read that way here. U+200B is not whitespace,
    /// so it is not trimmed.
    /// </summary>
    private static readonly string?[] UnknownNames =
    {
        null,
        "",
        "   ",
        "\t\r\n",
        "Bogus",
        "Unclassified",
        "Secrte",
        "PartnerOnly",
        "SystemHigh",
        "systemhigh",
        "Secret//C:ALPHA",
        "TopSecret//K:NOWEB",
        "Top Secret",
        "top_secret",
        "\u200BSecret",
    };

    /// <summary>Every tier name in the matrices: the known spellings with their rank, and the unknown names with none.</summary>
    private static readonly (string? Text, int? Rank)[] AllNames =
        KnownSpellings.Select(known => ((string?)known.Text, (int?)known.Rank))
            .Concat(UnknownNames.Select(unknown => (unknown, (int?)null)))
            .ToArray();

    public static TheoryData<string, int> KnownSpellingCases()
    {
        var data = new TheoryData<string, int>();
        foreach (var (text, rank) in KnownSpellings)
            data.Add(text, rank);
        return data;
    }

    public static TheoryData<string?> UnknownNameCases()
    {
        var data = new TheoryData<string?>();
        foreach (var name in UnknownNames)
            data.Add(name);
        return data;
    }

    [Fact]
    public void TheSpellingTable_CoversEveryTier_WithSpellingsLabelParsingWouldMisread()
    {
        // Without this the parity theory could pass over a table that skips a tier, or one made only of canonical
        // text, on which a bridge that parsed label text would look correct.
        KnownSpellings.Select(known => known.Rank).Distinct().OrderBy(rank => rank)
            .Should().Equal(Enum.GetValues<SecurityLevel>().Select(level => (int)level));
        KnownSpellings.Select(known => known.Text).Should().Contain(new[] { "top-secret", "TOP-SECRET" });

        foreach (var rank in Enumerable.Range(0, CanonicalNames.Length))
        {
            KnownSpellings.Count(known => known.Rank == rank && !SecurityLabel.TryParse(known.Text, out _))
                .Should().BeGreaterThanOrEqualTo(
                    4,
                    "tier {0} needs spellings TrustTierOrder accepts and exact-case label parsing refuses",
                    CanonicalNames[rank]);
        }

        foreach (var name in UnknownNames)
            TrustTierOrder.TryRank(name, out _).Should().BeFalse("{0} is listed as an unknown name", Show(name));
    }

    [Theory]
    [MemberData(nameof(KnownSpellingCases))]
    public void AKnownSpelling_MapsToTheLevelBothRanksGiveIt_OnBothSides(string tier, int rank)
    {
        var record = TrustTierOrder.RecordLabel(tier);
        var caller = TrustTierOrder.CallerLabel(tier);

        TrustTierOrder.RecordRank(tier).Should().Be(rank);
        TrustTierOrder.CallerRank(tier).Should().Be(rank);
        ((int)record.Level).Should().Be(rank, "RecordLabel({0}) must keep the rank RecordRank gives it", Show(tier));
        ((int)caller.Level).Should().Be(rank, "CallerLabel({0}) must keep the rank CallerRank gives it", Show(tier));

        record.IsSystemHigh.Should().BeFalse("{0} is a known tier, not an unlabelled record", Show(tier));
        caller.IsSystemHigh.Should().BeFalse("no tier name may produce a SystemHigh clearance");
        record.Should().Be(caller, "a known tier labels a record and a clearance identically");
        record.Should().Be(new SecurityLabel((SecurityLevel)rank));

        // A known spelling must NOT go through label parsing, which is exact-case and would make it SystemHigh.
        if (!string.Equals(tier, CanonicalNames[rank], StringComparison.Ordinal))
            SecurityLabel.ParseOrSystemHigh(tier).IsSystemHigh.Should().BeTrue("the spelling is not canonical label text");
    }

    [Theory]
    [MemberData(nameof(KnownSpellingCases))]
    public void AKnownSpelling_PrintsAsTheCanonicalTier_AndCarriesNoCompartmentsOrCaveats(string tier, int rank)
    {
        var record = TrustTierOrder.RecordLabel(tier);
        var caller = TrustTierOrder.CallerLabel(tier);

        record.ToString().Should().Be(CanonicalNames[rank]);
        caller.ToString().Should().Be(CanonicalNames[rank]);
        TrustTierOrder.NormalizeRecordTier(tier).Should().Be(record.ToString(), "the stored tier and the label agree");
        TrustTierOrder.ResolveCaller(tier).Should().Be((caller.ToString(), TrustTierOrder.BasisExplicit));

        // Level only: a tier carries no need-to-know tokens and no flags-as-caveats.
        record.Compartments.Should().BeEmpty();
        record.Caveats.Should().BeEmpty();
        caller.Compartments.Should().BeEmpty();
        caller.Caveats.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(UnknownNameCases))]
    public void AnUnlabelledRecord_IsSystemHigh_AndAnUnknownCaller_IsPublic(string? name)
    {
        var record = TrustTierOrder.RecordLabel(name);
        var caller = TrustTierOrder.CallerLabel(name);

        record.IsSystemHigh.Should().BeTrue("an unlabelled or unrecognised record {0} fails closed to the top", Show(name));
        record.Should().Be(SecurityLabel.SystemHigh);
        caller.Should().Be(SecurityLabel.Public, "an omitted or unrecognised clearance {0} floors", Show(name));
        caller.IsSystemHigh.Should().BeFalse("a caller naming {0} must not be cleared for everything", Show(name));
        caller.ToString().Should().Be(TrustTierOrder.ResolveCaller(name).Applied);

        // Stricter than the rank function, never looser: the same record ranks only at TopSecret there.
        TrustTierOrder.RecordRank(name).Should().Be(TopRank);
        record.Dominates(TrustTierOrder.RecordLabel(TrustTierOrder.MostRestrictive)).Should().BeTrue();
        TrustTierOrder.RecordLabel(TrustTierOrder.MostRestrictive).Dominates(record).Should().BeFalse();
    }

    [Fact]
    public void TheNamedEnds_MapToTopSecretAndPublic_NotToSystemHigh()
    {
        var mostRestrictive = TrustTierOrder.RecordLabel(TrustTierOrder.MostRestrictive);
        mostRestrictive.Level.Should().Be(SecurityLevel.TopSecret);
        mostRestrictive.IsSystemHigh.Should().BeFalse("a record labelled TopSecret is labelled, so it is not SystemHigh");
        mostRestrictive.Should().Be(new SecurityLabel(SecurityLevel.TopSecret));
        TrustTierOrder.CallerLabel(TrustTierOrder.MostRestrictive).IsSystemHigh.Should().BeFalse();

        TrustTierOrder.CallerLabel(TrustTierOrder.Floor).Should().Be(SecurityLabel.Public);
        TrustTierOrder.RecordLabel(TrustTierOrder.Floor).Should().Be(SecurityLabel.Public);
    }

    /// <summary>
    /// Every record name against every caller name: the reference monitor over the bridged labels decides as
    /// <see cref="TrustTierOrder.IsAllowed"/> does in every cell but one family, an unlabelled record against a
    /// TopSecret caller. The expected answers come from this file's tables, so the cell counts are exact.
    /// </summary>
    [Fact]
    public void ReadDecisions_MatchIsAllowed_InEveryCellButTheUnlabelledTopSecretOne()
    {
        var mismatches = new List<string>();
        int cells = 0, divergent = 0, bothAllowed = 0, bothRefused = 0;

        foreach (var (recordText, recordRank) in AllNames)
        {
            foreach (var (callerText, callerRank) in AllNames)
            {
                cells++;
                var cell = "record " + Show(recordText) + " x caller " + Show(callerText);
                var legacyExpected = (recordRank ?? TopRank) <= (callerRank ?? 0);
                var legacy = TrustTierOrder.IsAllowed(recordText, callerText);
                var decision = ReferenceMonitor.CanRead(
                    TrustTierOrder.CallerLabel(callerText), TrustTierOrder.RecordLabel(recordText));

                if (legacy != legacyExpected)
                    mismatches.Add(cell + ": IsAllowed changed behaviour, expected " + legacyExpected);

                var isDivergenceCell = recordRank is null && callerRank == TopRank;
                if (isDivergenceCell)
                {
                    divergent++;
                    if (!legacy || decision.Allowed || decision.Reason != AccessDenialReason.SystemHighData)
                    {
                        mismatches.Add(cell + ": expected IsAllowed true and CanRead refused with SystemHighData, got "
                            + legacy + " and " + decision);
                    }

                    continue;
                }

                if (decision.Allowed != legacy)
                {
                    mismatches.Add(cell + ": IsAllowed " + legacy + " but CanRead " + decision);
                    continue;
                }

                var expectedReason = decision.Allowed
                    ? AccessDenialReason.None
                    : recordRank is null ? AccessDenialReason.SystemHighData : AccessDenialReason.LevelTooLow;
                if (decision.Reason != expectedReason)
                    mismatches.Add(cell + ": expected reason " + expectedReason + ", got " + decision);

                if (decision.Allowed)
                    bothAllowed++;
                else
                    bothRefused++;
            }
        }

        mismatches.Should().BeEmpty();
        cells.Should().Be(AllNames.Length * AllNames.Length);
        divergent.Should().Be(
            UnknownNames.Length * KnownSpellings.Count(known => known.Rank == TopRank),
            "exactly the unlabelled-record x TopSecret-caller cells diverge");
        bothAllowed.Should().BeGreaterThan(0);
        bothRefused.Should().BeGreaterThan(0);
        (divergent + bothAllowed + bothRefused).Should().Be(cells);
    }

    [Theory]
    [MemberData(nameof(UnknownNameCases))]
    public void TheDivergenceCell_AnUnlabelledRecord_IsServedToTopSecretToday_ButRefusedOverLabels(string? record)
    {
        foreach (var topSecret in new[] { "TopSecret", "top-secret", " TOPSECRET " })
        {
            TrustTierOrder.IsAllowed(record, topSecret).Should().BeTrue(
                "today an unlabelled record {0} ranks TopSecret and a {1} caller reads it", Show(record), Show(topSecret));

            var decision = ReferenceMonitor.CanRead(
                TrustTierOrder.CallerLabel(topSecret), TrustTierOrder.RecordLabel(record));
            decision.Allowed.Should().BeFalse("over labels an unlabelled record is SystemHigh");
            decision.Reason.Should().Be(AccessDenialReason.SystemHighData);
        }

        // Control: the same caller reads a record that IS labelled TopSecret on both sides.
        TrustTierOrder.IsAllowed(TrustTierOrder.MostRestrictive, "TopSecret").Should().BeTrue();
        ReferenceMonitor.CanRead(
                TrustTierOrder.CallerLabel("TopSecret"),
                TrustTierOrder.RecordLabel(TrustTierOrder.MostRestrictive))
            .Allowed.Should().BeTrue();
    }

    /// <summary>
    /// The re-index downgrade rule of <c>VectorDataRagService.IndexAsync</c> (refuse when
    /// <c>RecordRank(NormalizeRecordTier(new)) &lt; RecordRank(stored)</c>) against its label form (refuse when the new
    /// label does not dominate the stored one). They agree everywhere except a stored unlabelled record re-indexed at
    /// TopSecret, which ranks allow (TopSecret is not below TopSecret) and labels refuse (TopSecret is below
    /// SystemHigh).
    /// </summary>
    [Fact]
    public void DowngradeDecisions_MatchTheRankRule_InEveryCellButUnlabelledStoredTopSecretNew()
    {
        var mismatches = new List<string>();
        int cells = 0, divergent = 0, bothRefused = 0, bothAccepted = 0;

        foreach (var (storedText, storedRank) in AllNames)
        {
            foreach (var (newText, newRank) in AllNames)
            {
                cells++;
                var cell = "stored " + Show(storedText) + " x new " + Show(newText);
                var normalized = TrustTierOrder.NormalizeRecordTier(newText);
                var legacyExpected = (newRank ?? TopRank) < (storedRank ?? TopRank);
                var legacyRefuses = TrustTierOrder.RecordRank(normalized) < TrustTierOrder.RecordRank(storedText);
                var labelRefuses = !TrustTierOrder.RecordLabel(normalized)
                    .Dominates(TrustTierOrder.RecordLabel(storedText));

                if (legacyRefuses != legacyExpected)
                    mismatches.Add(cell + ": the rank rule changed behaviour, expected refuse=" + legacyExpected);

                if (storedRank is null && newRank == TopRank)
                {
                    divergent++;
                    if (legacyRefuses || !labelRefuses)
                    {
                        mismatches.Add(cell + ": expected ranks to accept and labels to refuse, got refuse="
                            + legacyRefuses + " and refuse=" + labelRefuses);
                    }

                    continue;
                }

                if (labelRefuses != legacyRefuses)
                {
                    mismatches.Add(cell + ": ranks refuse=" + legacyRefuses + " but labels refuse=" + labelRefuses);
                    continue;
                }

                if (labelRefuses)
                    bothRefused++;
                else
                    bothAccepted++;
            }
        }

        mismatches.Should().BeEmpty();
        cells.Should().Be(AllNames.Length * AllNames.Length);
        divergent.Should().Be(UnknownNames.Length * KnownSpellings.Count(known => known.Rank == TopRank));
        bothRefused.Should().BeGreaterThan(0);
        bothAccepted.Should().BeGreaterThan(0);
        (divergent + bothRefused + bothAccepted).Should().Be(cells);
    }

    [Fact]
    public void TheDowngradeCell_ReindexingAnUnlabelledRecordAtTopSecret_IsAcceptedByRanks_AndRefusedByLabels()
    {
        foreach (var stored in new[] { string.Empty, "PartnerOnly" })
        {
            var requested = TrustTierOrder.NormalizeRecordTier("top-secret");

            (TrustTierOrder.RecordRank(requested) < TrustTierOrder.RecordRank(stored)).Should().BeFalse(
                "today TopSecret is not below an unlabelled record's TopSecret rank, so the re-index is accepted");
            TrustTierOrder.RecordLabel(requested).Dominates(TrustTierOrder.RecordLabel(stored)).Should().BeFalse(
                "over labels TopSecret is below SystemHigh, so the re-index would lower the label");
        }
    }

    /// <summary>
    /// Arbitrary strings, and tier names with case, whitespace and other noise: a record label is SystemHigh or sits
    /// exactly at <see cref="TrustTierOrder.RecordRank"/>, so it is never below what the rank function says, and a
    /// caller label is never SystemHigh and sits exactly at <see cref="TrustTierOrder.CallerRank"/>.
    /// </summary>
    [Fact]
    public void AnyString_RecordLabelIsNeverBelowItsRank_AndCallerLabelIsExactlyItsRank()
    {
        var known = 0;
        var unknown = 0;

        TierNames.Sample(
            name =>
            {
                var isKnown = TrustTierOrder.TryRank(name, out _);
                if (isKnown)
                    Interlocked.Increment(ref known);
                else
                    Interlocked.Increment(ref unknown);

                var record = TrustTierOrder.RecordLabel(name);
                var caller = TrustTierOrder.CallerLabel(name);

                record.IsSystemHigh.Should().Be(!isKnown, "only a blank or unknown record name is SystemHigh");
                if (!record.IsSystemHigh)
                {
                    ((int)record.Level).Should().Be(TrustTierOrder.RecordRank(name));
                    record.ToString().Should().Be(TrustTierOrder.NormalizeRecordTier(name));
                }

                record.Dominates(new SecurityLabel((SecurityLevel)TrustTierOrder.RecordRank(name))).Should().BeTrue(
                    "a record label is never below the rank TrustTierOrder gives it");
                TrustTierOrder.RecordLabel(TrustTierOrder.NormalizeRecordTier(name)).Should().Be(
                    record, "storing the normalised tier keeps the label");

                caller.IsSystemHigh.Should().BeFalse("no caller name produces a SystemHigh clearance");
                ((int)caller.Level).Should().Be(TrustTierOrder.CallerRank(name));
                caller.ToString().Should().Be(TrustTierOrder.ResolveCaller(name).Applied);

                record.Compartments.Should().BeEmpty();
                record.Caveats.Should().BeEmpty();
                caller.Compartments.Should().BeEmpty();
                caller.Caveats.Should().BeEmpty();
            },
            iter: 5000,
            print: Show);

        known.Should().BeGreaterThan(0, "the generator must reach known tiers");
        unknown.Should().BeGreaterThan(0, "the generator must reach unknown names");
    }

    /// <summary>The read-decision parity of the matrix, over generated pairs of names.</summary>
    [Fact]
    public void AnyPair_ReadDecisionMatchesIsAllowed_ExceptAnUnlabelledRecordAgainstATopSecretCaller()
    {
        var divergent = 0;

        Gen.Select(TierNames, TierNames).Sample(
            (record, caller) =>
            {
                var legacy = TrustTierOrder.IsAllowed(record, caller);
                var decision = ReferenceMonitor.CanRead(
                    TrustTierOrder.CallerLabel(caller), TrustTierOrder.RecordLabel(record));

                if (!TrustTierOrder.TryRank(record, out _) && TrustTierOrder.CallerRank(caller) == TopRank)
                {
                    Interlocked.Increment(ref divergent);
                    legacy.Should().BeTrue();
                    decision.Allowed.Should().BeFalse();
                    decision.Reason.Should().Be(AccessDenialReason.SystemHighData);
                }
                else
                {
                    decision.Allowed.Should().Be(legacy);
                }
            },
            iter: 5000,
            print: pair => Show(pair.Item1) + " x " + Show(pair.Item2));

        divergent.Should().BeGreaterThan(0, "the generator must reach the divergence cell");
    }

    /// <summary>
    /// Tier-shaped names: a tier name or the <c>top-secret</c> alias with each letter's case drawn at random and
    /// whitespace or near-whitespace on either side (U+00A0 and U+2003 are trimmed, U+200B, <c>_</c> and <c>-</c> are
    /// not), plus the unknown names above, arbitrary strings and null.
    /// </summary>
    private static readonly Gen<string?> TierNames = Gen.Frequency<string?>(
        (6, Gen.Select(
                Gen.OneOfConst("Public", "Internal", "Confidential", "Secret", "TopSecret", "top-secret")
                    .SelectMany(name => Gen.Bool.Array[name.Length].Select(upper => RandomCase(name, upper))),
                Gen.OneOfConst("", "", " ", "  ", "\t", "\r\n", "\u00A0", "\u2003", "\u200B", "_", "-", "x"),
                Gen.OneOfConst("", "", " ", "  ", "\t", "\r\n", "\u00A0", "\u2003", "\u200B", "_", "-", "s"),
                (name, prefix, suffix) => prefix + name + suffix)
            .Select(name => (string?)name)),
        (2, Gen.OneOfConst(UnknownNames)),
        (2, Gen.String.Select(text => (string?)text)));

    private static string RandomCase(string name, bool[] upper)
    {
        var chars = name.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
            chars[i] = upper[i] ? char.ToUpperInvariant(chars[i]) : char.ToLowerInvariant(chars[i]);
        return new string(chars);
    }

    private static (string Text, int Rank)[] BuildKnownSpellings()
    {
        var spellings = new List<(string, int)>();
        for (var rank = 0; rank < CanonicalNames.Length; rank++)
        {
            var name = CanonicalNames[rank];
            spellings.Add((name, rank));
            spellings.Add((name.ToLowerInvariant(), rank));
            spellings.Add((name.ToUpperInvariant(), rank));
            spellings.Add((Alternate(name), rank));
            spellings.Add((" " + name + " ", rank));
            spellings.Add(("\t" + name.ToLowerInvariant() + "\r\n", rank));
            // Trim removes Unicode white space too: no-break space and em space pad a known name.
            spellings.Add(("\u00A0" + name + "\u2003", rank));
        }

        spellings.Add(("top-secret", TopRank));
        spellings.Add(("TOP-SECRET", TopRank));
        spellings.Add((" Top-Secret ", TopRank));
        return spellings.ToArray();
    }

    // "Secret" -> "sEcReT": a mixed-case spelling that is neither the canonical, lower nor upper form.
    private static string Alternate(string name)
    {
        var chars = name.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
            chars[i] = i % 2 == 0 ? char.ToLowerInvariant(chars[i]) : char.ToUpperInvariant(chars[i]);
        return new string(chars);
    }

    // A readable, unambiguous rendering of a name for an assertion message: null, quotes, and escaped control and
    // invisible characters.
    private static string Show(string? text)
    {
        if (text is null)
            return "(null)";

        var builder = new StringBuilder("\"");
        foreach (var c in text)
        {
            if (c is >= ' ' and <= '~')
                builder.Append(c);
            else
                builder.Append("\\u").Append(((int)c).ToString("X4", System.Globalization.CultureInfo.InvariantCulture));
        }

        return builder.Append('"').ToString();
    }
}
