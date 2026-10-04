using Ashlar.Abstractions.Security;
using Ashlar.BackgroundAgents.DataSensitivity;
using FluentAssertions;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>
/// SPEC-007: <see cref="SecurityLevel"/> generalises the five primitive levels in
/// <see cref="DataSensitivityLevels"/>, and the two must keep the same names, values and order.
///
/// <para><b>Why this is a test and not a type reference.</b> <see cref="SecurityLevel"/> lives in
/// Ashlar.Abstractions, which Ashlar.BackgroundAgents references, so the enum cannot be defined in terms of the
/// primitives without a cycle. Nothing at compile time notices if one side gains a level, renumbers one or
/// renames one; a level that crosses between the two models by name or by number would then quietly mean
/// something else on the other side. This suite is the link.</para>
///
/// <para>Hermetic: pure values, no files, no network.</para>
/// </summary>
[Trait("Category", "Certification")]
public sealed class SecurityLabelLevelParityTests
{
    /// <summary>Each level beside the primitive it mirrors, written out so a mismatch names both sides.</summary>
    private static readonly (SecurityLevel Level, IDataSensitivityLevel Primitive)[] Pairs =
    {
        (SecurityLevel.Public, DataSensitivityLevels.Public),
        (SecurityLevel.Internal, DataSensitivityLevels.Internal),
        (SecurityLevel.Confidential, DataSensitivityLevels.Confidential),
        (SecurityLevel.Secret, DataSensitivityLevels.Secret),
        (SecurityLevel.TopSecret, DataSensitivityLevels.TopSecret),
    };

    [Fact]
    public void ThePairTable_CoversEveryLevelAndEveryPrimitive_InOrder()
    {
        // Without this the assertions below could pass over a table that silently skips a level.
        Pairs.Select(pair => pair.Level).Should().Equal(Enum.GetValues<SecurityLevel>());
        Pairs.Select(pair => pair.Primitive).Should().Equal(DataSensitivityLevels.All);
    }

    [Fact]
    public void EachLevel_HasTheSensitivityValueOfItsPrimitive()
    {
        foreach (var (level, primitive) in Pairs)
        {
            ((int)level).Should().Be(
                primitive.SensitivityValue,
                "SecurityLevel.{0} must keep the SensitivityValue of DataSensitivityLevels.{1}",
                level,
                primitive.Value);
        }
    }

    [Fact]
    public void EachLevel_HasTheNameOfItsPrimitive()
    {
        foreach (var (level, primitive) in Pairs)
        {
            // Value, not Display: the primitive TopSecret displays as "Top Secret", and Value is the name both
            // models read (DataSensitivityLevels.FromName and SecurityLabel.TryParse).
            level.ToString().Should().Be(primitive.Value);
            new SecurityLabel(level).ToString().Should().Be(
                primitive.Value, "the canonical text form spells a level the way the primitive names it");
        }
    }

    [Fact]
    public void EachPrimitiveName_ParsesToTheMatchingLevel_AndBack()
    {
        foreach (var (level, primitive) in Pairs)
        {
            SecurityLabel.TryParse(primitive.Value, out var label).Should().BeTrue(
                "the primitive name {0} is canonical label text", primitive.Value);
            label.Level.Should().Be(level);
            label.IsSystemHigh.Should().BeFalse();

            DataSensitivityLevels.FromName(level.ToString()).Should().BeSameAs(primitive);
        }
    }

    [Fact]
    public void TheEnum_HasExactlyFiveMembers_ValuedZeroToFour()
    {
        Enum.GetValues<SecurityLevel>().Select(level => (int)level).Should().Equal(0, 1, 2, 3, 4);
        Enum.GetNames<SecurityLevel>().Should().Equal("Public", "Internal", "Confidential", "Secret", "TopSecret");
    }

    [Fact]
    public void All_ListsTheSameLevels_InTheSameOrder()
    {
        var primitives = DataSensitivityLevels.All;

        primitives.Should().HaveCount(Enum.GetValues<SecurityLevel>().Length);
        primitives.Select(primitive => primitive.SensitivityValue)
            .Should().Equal(Enum.GetValues<SecurityLevel>().Select(level => (int)level));
        primitives.Select(primitive => primitive.Value)
            .Should().Equal(Enum.GetNames<SecurityLevel>());
    }

    [Fact]
    public void Dominance_OverBareLevels_FollowsTheSensitivityOrder()
    {
        // With no compartments or caveats a label is just its level, so the lattice order must be the
        // primitives' numeric order: one more place where renumbering either side would show.
        foreach (var (higher, higherPrimitive) in Pairs)
        {
            foreach (var (lower, lowerPrimitive) in Pairs)
            {
                new SecurityLabel(higher).Dominates(new SecurityLabel(lower)).Should().Be(
                    higherPrimitive.SensitivityValue >= lowerPrimitive.SensitivityValue,
                    "{0} dominates {1} exactly when its SensitivityValue is at least as high",
                    higher,
                    lower);
            }
        }
    }
}
