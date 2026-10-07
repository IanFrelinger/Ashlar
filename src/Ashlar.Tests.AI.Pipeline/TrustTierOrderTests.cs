using FluentAssertions;
using Ashlar.AI.Pipeline.Rag;
using Xunit;

namespace Ashlar.Tests.AI.Pipeline;

/// <summary>
/// A caller clearance and a record label fail closed in OPPOSITE directions. The single
/// <c>Rank</c> these replace applied one rule to both and was open both ways: a blank name ranked
/// Public (so an unlabelled record reached everyone) and an unknown name ranked TopSecret (so a
/// misspelt or "Unclassified" caller clearance reached everything).
/// </summary>
public sealed class TrustTierOrderTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Unclassified")]
    [InlineData("Secrte")]
    public void An_omitted_or_unrecognised_caller_clearance_ranks_at_the_floor(string? caller)
    {
        TrustTierOrder.CallerRank(caller).Should().Be(TrustTierOrder.CallerRank(TrustTierOrder.Floor));
        TrustTierOrder.CallerRank(caller).Should().Be(0);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Unclassified")]
    [InlineData("Bogus")]
    public void An_unlabelled_or_unrecognised_record_ranks_at_the_top(string? record)
    {
        TrustTierOrder.RecordRank(record).Should().Be(TrustTierOrder.RecordRank(TrustTierOrder.MostRestrictive));
        TrustTierOrder.RecordRank(record).Should().Be(4);
    }

    // Positive control for both theories above: a KNOWN name ranks the same on both sides, so
    // neither fallback can be satisfied by ranking everything at one end.
    [Theory]
    [InlineData("Public", 0)]
    [InlineData("internal", 1)]
    [InlineData(" Confidential ", 2)]
    [InlineData("SECRET", 3)]
    [InlineData("TopSecret", 4)]
    [InlineData("top-secret", 4)]
    public void A_known_tier_ranks_the_same_for_a_caller_and_a_record(string tier, int rank)
    {
        TrustTierOrder.CallerRank(tier).Should().Be(rank);
        TrustTierOrder.RecordRank(tier).Should().Be(rank);
    }

    [Theory]
    [InlineData(null, "Public", "omitted")]
    [InlineData("  ", "Public", "omitted")]
    [InlineData("Unclassified", "Public", "unrecognised")]
    [InlineData("secret", "Secret", "explicit")]
    [InlineData("top-secret", "TopSecret", "explicit")]
    public void ResolveCaller_names_the_clearance_applied_and_why(string? caller, string applied, string basis)
    {
        TrustTierOrder.ResolveCaller(caller).Should().Be((applied, basis));
    }

    [Theory]
    // Unlabelled and unknown records: only the top clearance.
    [InlineData(null, "TopSecret", true)]
    [InlineData(null, "Secret", false)]
    [InlineData("", null, false)]
    [InlineData("Bogus", "Secret", false)]
    [InlineData("Bogus", "TopSecret", true)]
    // Omitted and unknown callers: only the floor.
    [InlineData("Public", null, true)]
    [InlineData("Internal", null, false)]
    [InlineData("Secret", "Unclassified", false)]
    [InlineData("Public", "Unclassified", true)]
    // Known on both sides: the plain order.
    [InlineData("Confidential", "Secret", true)]
    [InlineData("Secret", "Confidential", false)]
    public void IsAllowed_applies_each_fallback_to_its_own_side(string? record, string? caller, bool allowed)
    {
        TrustTierOrder.IsAllowed(record, caller).Should().Be(allowed);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("   ", "")]
    [InlineData("secret", "Secret")]
    [InlineData("top-secret", "TopSecret")]
    [InlineData(" PartnerOnly ", "PartnerOnly")]
    public void NormalizeRecordTier_keeps_unlabelled_unlabelled_and_invents_no_tier(string? input, string stored)
    {
        TrustTierOrder.NormalizeRecordTier(input).Should().Be(stored);
    }
}
