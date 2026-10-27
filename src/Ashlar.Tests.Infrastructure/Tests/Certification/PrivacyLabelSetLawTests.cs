using Ashlar.Abstractions.Security;
using CsCheck;
using FluentAssertions;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>
/// SPEC-010 §3.1 and §3.4: the laws of <see cref="PrivacyLabelSet.Union"/>, the operation by which a subject's read set
/// grows.
///
/// <para><b>Exhaustive, not sampled.</b> Four labels make sixteen sets, so every law here is checked on every set,
/// every pair (256) and every triple (4,096), not on a sample. Each set is built with
/// <see cref="PrivacyLabelSet.Of"/> from a subset of the four labels, and the expected union is computed from the
/// two subsets with LINQ, not with the operation under test. CsCheck covers what an enumeration of subsets cannot: label
/// sequences in any order and with duplicates.</para>
///
/// <para><b>The mutation these laws exist to catch.</b> A union that loses a label lets data be sent where that label
/// forbids. SPEC-010 §8's "lose a label in the union" (the union computed as an intersection) fails
/// <see cref="None_IsTheIdentityOfUnion"/>, <see cref="Union_HoldsEveryLabelOfBothSets"/> and
/// <see cref="Union_IsTheSetOfLabelsInEither"/>.</para>
/// </summary>
[Trait("Category", "Certification")]
public sealed class PrivacyLabelSetLawTests
{
    private static readonly PrivacyLabel[] FourLabels =
        [PrivacyLabel.LocalOnly, PrivacyLabel.NoExternalModel, PrivacyLabel.NoWeb, PrivacyLabel.NoNetworkExport];

    /// <summary>Every subset of the four labels, as the labels it holds: sixteen, the empty one first.</summary>
    private static readonly PrivacyLabel[][] Subsets = Enumerable.Range(0, 1 << FourLabels.Length)
        .Select(bits => FourLabels.Where((_, i) => (bits & (1 << i)) != 0).ToArray())
        .ToArray();

    /// <summary>Any of the four labels.</summary>
    private static readonly Gen<PrivacyLabel> AnyLabel = Gen.OneOfConst(FourLabels);

    /// <summary>Zero to eight labels in any order, duplicates likely.</summary>
    private static readonly Gen<PrivacyLabel[]> AnyLabels = AnyLabel.Array[0, 8];

    [Fact]
    public void TheSixteenSubsets_AreSixteenDifferentSets()
    {
        var sets = Subsets.Select(subset => PrivacyLabelSet.Of(subset)).ToArray();

        sets.Should().HaveCount(16);
        sets.Distinct().Should().HaveCount(16, "four labels give sixteen sets, and no two subsets may build the same one");
        sets.Select(s => s.ToString()).Distinct(StringComparer.Ordinal).Should().HaveCount(16);
    }

    [Fact]
    public void Union_IsCommutative()
    {
        foreach (var a in Subsets)
        {
            foreach (var b in Subsets)
            {
                var ab = PrivacyLabelSet.Of(a).Union(PrivacyLabelSet.Of(b));
                var ba = PrivacyLabelSet.Of(b).Union(PrivacyLabelSet.Of(a));
                ab.Should().Be(ba, "{0} union {1}", Text(a), Text(b));
            }
        }
    }

    [Fact]
    public void Union_IsAssociative()
    {
        var checkedTriples = 0;
        foreach (var a in Subsets)
        {
            foreach (var b in Subsets)
            {
                foreach (var c in Subsets)
                {
                    var left = PrivacyLabelSet.Of(a).Union(PrivacyLabelSet.Of(b)).Union(PrivacyLabelSet.Of(c));
                    var right = PrivacyLabelSet.Of(a).Union(PrivacyLabelSet.Of(b).Union(PrivacyLabelSet.Of(c)));
                    if (!left.Equals(right))
                        left.Should().Be(right, "({0} union {1}) union {2}", Text(a), Text(b), Text(c));
                    checkedTriples++;
                }
            }
        }

        checkedTriples.Should().Be(4096, "every triple of the sixteen sets");
    }

    [Fact]
    public void Union_IsIdempotent()
    {
        foreach (var a in Subsets)
        {
            var set = PrivacyLabelSet.Of(a);
            set.Union(set).Should().Be(set, "{0} union itself", Text(a));
            set.Union(PrivacyLabelSet.Of(a)).Should().Be(set, "{0} union an equal set", Text(a));
        }
    }

    /// <summary>Mutation pin: a union that intersects gives <c>none</c> here for every non-empty set.</summary>
    [Fact]
    public void None_IsTheIdentityOfUnion()
    {
        PrivacyLabelSet.None.Labels.Should().BeEmpty();
        foreach (var a in Subsets)
        {
            var set = PrivacyLabelSet.Of(a);
            set.Union(PrivacyLabelSet.None).Should().Be(set, "{0} union none", Text(a));
            PrivacyLabelSet.None.Union(set).Should().Be(set, "none union {0}", Text(a));
        }
    }

    /// <summary>Mutation pin: the union is an upper bound, so it holds every label either side holds.</summary>
    [Fact]
    public void Union_HoldsEveryLabelOfBothSets()
    {
        foreach (var a in Subsets)
        {
            foreach (var b in Subsets)
            {
                var union = PrivacyLabelSet.Of(a).Union(PrivacyLabelSet.Of(b));
                foreach (var label in a.Concat(b))
                    union.Contains(label).Should().BeTrue("{0} union {1} must still hold {2}", Text(a), Text(b), label);
            }
        }
    }

    /// <summary>
    /// The union against an oracle built from the two subsets with LINQ: exactly the labels in either set, so it never
    /// loses one and never adds one.
    /// </summary>
    [Fact]
    public void Union_IsTheSetOfLabelsInEither()
    {
        foreach (var a in Subsets)
        {
            foreach (var b in Subsets)
            {
                var expected = PrivacyLabelSet.Of(a.Union(b).ToArray());
                PrivacyLabelSet.Of(a).Union(PrivacyLabelSet.Of(b)).Should().Be(expected, "{0} union {1}", Text(a), Text(b));
                expected.Labels.Should().BeEquivalentTo(a.Union(b));
            }
        }
    }

    [Fact]
    public void Union_IsTheLeastSetHoldingBoth()
    {
        // Every set that holds both a and b holds a union b.
        foreach (var a in Subsets)
        {
            foreach (var b in Subsets)
            {
                var union = PrivacyLabelSet.Of(a).Union(PrivacyLabelSet.Of(b));
                foreach (var c in Subsets.Where(c => a.All(c.Contains) && b.All(c.Contains)))
                {
                    var upper = PrivacyLabelSet.Of(c);
                    union.Labels.All(upper.Contains).Should().BeTrue(
                        "{0} holds {1} and {2}, so it holds their union", Text(c), Text(a), Text(b));
                }
            }
        }
    }

    [Fact]
    public void Contains_HoldsExactlyForTheLabelsTheSetWasBuiltFrom()
    {
        foreach (var a in Subsets)
        {
            var set = PrivacyLabelSet.Of(a);
            foreach (var label in FourLabels)
                set.Contains(label).Should().Be(a.Contains(label), "{0} and {1}", Text(a), label);
            set.Labels.Should().BeEquivalentTo(a);
        }
    }

    [Fact]
    public void Of_IgnoresOrderAndDuplicates()
    {
        AnyLabels.Sample(labels =>
        {
            var set = PrivacyLabelSet.Of(labels);
            set.Should().Be(PrivacyLabelSet.Of(labels.Distinct().ToArray()), "duplicates collapse");
            set.Should().Be(PrivacyLabelSet.Of(Enumerable.Reverse(labels).ToArray()), "order does not matter");
            set.Labels.Should().BeEquivalentTo(labels.Distinct());
        });
    }

    [Fact]
    public void Of_IsTheUnionOfItsSingletons()
    {
        AnyLabels.Sample(labels =>
        {
            var union = labels.Aggregate(PrivacyLabelSet.None, (acc, label) => acc.Union(PrivacyLabelSet.Of(label)));
            PrivacyLabelSet.Of(labels).Should().Be(union);
        });
    }

    [Fact]
    public void Union_OfLabelSequences_AgreesWithConcatenation()
    {
        Gen.Select(AnyLabels, AnyLabels).Sample((first, second) =>
        {
            PrivacyLabelSet.Of(first).Union(PrivacyLabelSet.Of(second))
                .Should().Be(PrivacyLabelSet.Of(first.Concat(second).ToArray()));
        });
    }

    [Fact]
    public void Union_NeverRemovesALabel_SoLocalOnlyOnceHeldStaysHeld()
    {
        foreach (var a in Subsets.Where(s => s.Contains(PrivacyLabel.LocalOnly)))
        {
            foreach (var b in Subsets)
            {
                PrivacyLabelSet.Of(a).Union(PrivacyLabelSet.Of(b)).Contains(PrivacyLabel.LocalOnly)
                    .Should().BeTrue("there is no downgrade: {0} union {1}", Text(a), Text(b));
            }
        }
    }

    [Fact]
    public void Union_NullArgument_ThrowsNamingTheParameter()
    {
        Action act = () => PrivacyLabelSet.None.Union(null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("other");
    }

    private static string Text(PrivacyLabel[] labels) => PrivacyLabelSet.Of(labels).ToString();
}
