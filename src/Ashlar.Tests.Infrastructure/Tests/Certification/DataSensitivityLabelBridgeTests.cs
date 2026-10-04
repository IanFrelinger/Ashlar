using Ashlar.Abstractions.Security;
using Ashlar.BackgroundAgents.DataSensitivity;
using CsCheck;
using FluentAssertions;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>
/// SPEC-007 PR 2: <see cref="DataSensitivityLabelBridge"/> maps an <see cref="IDataSensitivityLevel"/> onto a
/// <see cref="SecurityLabel"/> without changing a single existing decision.
/// </summary>
/// <remarks>
/// <para><b>Parity.</b> For every level whose <see cref="IDataSensitivityLevel.SensitivityValue"/> is in 0 to 4,
/// <see cref="DataSensitivityRegistry.CanAccess"/> and <see cref="ReferenceMonitor.CanRead"/> over the bridged labels
/// give the same answer. Outside that range the bridge is never wider than the registry: it refuses exactly what the
/// registry refuses, plus a clearance below 0 (which no label can express) and data above 4 (SystemHigh).</para>
/// <para><b>Fail closed, in opposite directions.</b> Unlabelled data is SystemHigh and an omitted clearance is
/// Public; out-of-range data rises (above 4 to SystemHigh, below 0 to Public) and an out-of-range clearance narrows
/// (above 4 to TopSecret, below 0 to no clearance at all).</para>
/// <para><b>Level only.</b> Every bridged label is bare, and the four flags do not move it.</para>
/// <para><b>The one intended divergence</b> is pinned on both sides by
/// <see cref="IntendedDivergence_LegacyResolvesUnlabelledData_ToALevelATopSecretAgentCanRead"/> and
/// <see cref="IntendedDivergence_BridgeLabelsUnlabelledData_SystemHigh_WhichATopSecretClearanceCannotRead"/>: the
/// legacy path resolves unlabelled data to TopSecret, which a TopSecret agent reads, while the bridge makes it
/// SystemHigh, which it does not. Nothing enforces through the bridge yet. The change that switches enforcement to
/// it must flip the legacy assertion deliberately, by name.</para>
/// <para>Hermetic: pure values and a fresh <see cref="DataSensitivityRegistry"/> per test, no files, no network.</para>
/// </remarks>
[Trait("Category", "Certification")]
public sealed class DataSensitivityLabelBridgeTests
{
    /// <summary>Each primitive beside the level it maps to, written out so a mismatch names both sides.</summary>
    private static readonly (IDataSensitivityLevel Primitive, SecurityLevel Level)[] Primitives =
    {
        (DataSensitivityLevels.Public, SecurityLevel.Public),
        (DataSensitivityLevels.Internal, SecurityLevel.Internal),
        (DataSensitivityLevels.Confidential, SecurityLevel.Confidential),
        (DataSensitivityLevels.Secret, SecurityLevel.Secret),
        (DataSensitivityLevels.TopSecret, SecurityLevel.TopSecret),
    };

    /// <summary>The edges of both ranges and of <see cref="int"/>, for the exhaustive grid.</summary>
    private static readonly int[] EdgeValues =
    {
        int.MinValue, int.MinValue + 1, -2, -1, 0, 1, 2, 3, 4, 5, 6, 10, int.MaxValue - 1, int.MaxValue,
    };

    /// <summary>
    /// Any <see cref="int"/>, weighted so the in-range values 0 to 4 and the edges just outside them turn up
    /// often.
    /// </summary>
    private static readonly Gen<int> AnyValue = Gen.Frequency<int>(
        (4, Gen.Int[-2, 6]),
        (2, Gen.Int),
        (1, Gen.OneOfConst(int.MinValue, int.MinValue + 1, -1, 0, 4, 5, int.MaxValue - 1, int.MaxValue)));

    /// <summary>A custom level at <see cref="AnyValue"/> with four independent random flags.</summary>
    private static readonly Gen<IDataSensitivityLevel> AnyLevel =
        Gen.Select(AnyValue, Gen.Bool.Array[4], (value, flags) => Level(value, flags));

    [Fact]
    public void ThePrimitiveTable_CoversEveryPrimitive_InOrder()
    {
        // Without this the assertions below could pass over a table that silently skips a level.
        Primitives.Select(pair => pair.Primitive).Should().Equal(DataSensitivityLevels.All);
        Primitives.Select(pair => pair.Level).Should().Equal(Enum.GetValues<SecurityLevel>());
    }

    [Fact]
    public void EachPrimitive_AsData_MapsToTheBareLabelOfTheSameLevel()
    {
        foreach (var (primitive, level) in Primitives)
        {
            var label = primitive.ToDataLabel();

            label.Should().Be(new SecurityLabel(level), "the data label of {0} is the bare {1}", primitive.Value, level);
            label.IsSystemHigh.Should().BeFalse();
            label.Compartments.Should().BeEmpty();
            label.Caveats.Should().BeEmpty();
        }
    }

    [Fact]
    public void EachPrimitive_AsClearance_MapsToTheBareLabelOfTheSameLevel()
    {
        foreach (var (primitive, level) in Primitives)
        {
            primitive.TryToClearance(out var clearance).Should().BeTrue("{0} is in range", primitive.Value);

            clearance.Should().Be(new SecurityLabel(level), "the clearance of {0} is the bare {1}", primitive.Value, level);
            clearance!.IsSystemHigh.Should().BeFalse();
            clearance.Compartments.Should().BeEmpty();
            clearance.Caveats.Should().BeEmpty();
        }
    }

    /// <summary>
    /// Level only: whatever the value and the flags, a bridged label is bare, and a bridged clearance is never
    /// SystemHigh.
    /// </summary>
    [Fact]
    public void BridgedLabels_NeverCarryCompartmentsOrCaveats_AndAClearanceIsNeverSystemHigh()
    {
        AnyLevel.Sample(level =>
        {
            var data = level.ToDataLabel();
            data.Compartments.Should().BeEmpty();
            data.Caveats.Should().BeEmpty();
            if (!data.IsSystemHigh)
            {
                data.Should().Be(new SecurityLabel(data.Level), "a data label below SystemHigh is a bare level");
            }

            if (level.TryToClearance(out var clearance))
            {
                clearance.IsSystemHigh.Should().BeFalse("SystemHigh reads everything, and no level grants that");
                clearance.Should().Be(new SecurityLabel(clearance.Level), "a clearance is a bare level");
            }
        });
    }

    /// <summary>
    /// The 25 primitive pairs: the registry and the reference monitor over the bridged labels agree on every one,
    /// and 15 of them are allowed (data at or below the agent).
    /// </summary>
    [Fact]
    public void Decision_OverEveryPrimitivePair_MatchesCanAccess()
    {
        var registry = new DataSensitivityRegistry();
        var allowed = 0;

        foreach (var (agent, _) in Primitives)
        {
            foreach (var (data, _) in Primitives)
            {
                var legacy = registry.CanAccess(agent, data);
                var bridged = BridgeAllows(agent, data);

                bridged.Should().Be(
                    legacy, "CanAccess({0}, {1}) is {2}, and the bridge must agree", agent.Value, data.Value, legacy);
                allowed += legacy ? 1 : 0;
            }
        }

        allowed.Should().Be(15, "five levels give 5 + 4 + 3 + 2 + 1 pairs with the data at or below the agent");
    }

    /// <summary>
    /// Custom levels registered with the registry and resolved through its own <see cref="DataSensitivityRegistry.GetByName"/>,
    /// with flags looser and stricter than the primitive at the same value, agree with the registry against every
    /// primitive and every other custom level, in both roles.
    /// </summary>
    [Fact]
    public void Decision_OverInRangeCustomLevels_MatchesCanAccess()
    {
        var registry = new DataSensitivityRegistry();
        registry.Register(Level("Restricted", 2, loose: true));
        registry.Register(Level("Sealed", 2, loose: false));
        registry.Register(Level("Staff", 1, loose: false));
        registry.Register(Level("Embargoed", 0, loose: false));
        registry.Register(Level("Vault", 4, loose: true));

        var levels = new[] { "Public", "Internal", "Confidential", "Secret", "TopSecret" }
            .Concat(new[] { "Restricted", "Sealed", "Staff", "Embargoed", "Vault" })
            .Select(name => registry.GetByName(name) ?? throw new InvalidOperationException(name + " did not resolve"))
            .ToArray();
        levels.Should().HaveCount(registry.GetAll().Count, "every registered level takes part");

        int allowed = 0, refused = 0;
        foreach (var agent in levels)
        {
            foreach (var data in levels)
            {
                var legacy = registry.CanAccess(agent, data);
                BridgeAllows(agent, data).Should().Be(
                    legacy, "CanAccess({0}, {1}) is {2}, and the bridge must agree", agent.Value, data.Value, legacy);
                _ = legacy ? allowed++ : refused++;
            }
        }

        allowed.Should().BePositive();
        refused.Should().BePositive();
    }

    /// <summary>
    /// Two levels with the same <see cref="IDataSensitivityLevel.SensitivityValue"/> and opposite flags map to
    /// equal labels in both roles, and <see cref="DataSensitivityRegistry.CanAccess"/> treats them as equal too.
    /// </summary>
    [Fact]
    public void Flags_DoNotChangeTheMapping()
    {
        var registry = new DataSensitivityRegistry();
        for (var value = 0; value <= 4; value++)
        {
            var loose = Level("Loose" + value, value, loose: true);
            var strict = Level("Strict" + value, value, loose: false);
            var primitive = DataSensitivityLevels.All[value];

            loose.ToDataLabel().Should().Be(strict.ToDataLabel());
            loose.ToDataLabel().Should().Be(primitive.ToDataLabel());
            loose.TryToClearance(out var looseClearance).Should().BeTrue();
            strict.TryToClearance(out var strictClearance).Should().BeTrue();
            looseClearance.Should().Be(strictClearance);

            foreach (var other in DataSensitivityLevels.All)
            {
                registry.CanAccess(loose, other).Should().Be(registry.CanAccess(strict, other));
                registry.CanAccess(other, loose).Should().Be(registry.CanAccess(other, strict));
            }
        }

        Gen.Select(AnyValue, Gen.Bool.Array[4], Gen.Bool.Array[4]).Sample((value, a, b) =>
        {
            var first = Level(value, a);
            var second = Level(value, b);

            first.ToDataLabel().Should().Be(second.ToDataLabel());
            first.TryToClearance(out var firstClearance).Should().Be(second.TryToClearance(out var secondClearance));
            firstClearance.Should().Be(secondClearance);
        });
    }

    /// <summary>
    /// The mapping reads <see cref="IDataSensitivityLevel.SensitivityValue"/> and nothing else: a custom level
    /// named like a label, or displayed like a primitive, maps by its value.
    /// </summary>
    [Fact]
    public void ALevelNamedLikeALabel_MapsByItsValue_NotItsName()
    {
        var registry = new DataSensitivityRegistry();
        registry.Register(new ConfigurableSensitivityLevel("SystemHigh", "SystemHigh", 1, true, true, false, true, "named like the top"));
        registry.Register(new ConfigurableSensitivityLevel("Unclassified", "Top Secret", 0, true, true, false, true, "displayed like a primitive"));

        var namedTop = registry.GetByName("SystemHigh");
        namedTop.ToDataLabel().Should().Be(new SecurityLabel(SecurityLevel.Internal));
        namedTop.TryToClearance(out var namedTopClearance).Should().BeTrue();
        namedTopClearance.Should().Be(new SecurityLabel(SecurityLevel.Internal));

        var displayedTop = registry.GetByName("Unclassified");
        displayedTop.ToDataLabel().Should().Be(SecurityLabel.Public);
        displayedTop.TryToClearance(out var displayedTopClearance).Should().BeTrue();
        displayedTopClearance.Should().Be(SecurityLabel.Public);

        new ConfigurableSensitivityLevel("TopSecret", "TopSecret", 0, false, false, true, false, "unregistered")
            .ToDataLabel().Should().Be(SecurityLabel.Public, "the name TopSecret does not outrank the value 0");
    }

    /// <summary>
    /// Names go through the registry's own resolver, which accepts spellings the canonical label parser refuses:
    /// parsed as label text, each of these would have become SystemHigh.
    /// </summary>
    [Theory]
    [InlineData("secret", SecurityLevel.Secret)]
    [InlineData("SECRET", SecurityLevel.Secret)]
    [InlineData("top-secret", SecurityLevel.TopSecret)]
    [InlineData("topsecret", SecurityLevel.TopSecret)]
    [InlineData("confidential", SecurityLevel.Confidential)]
    public void ARegistrySpelling_ResolvesThroughGetByName_ToItsLevel(string name, SecurityLevel expected)
    {
        var registry = new DataSensitivityRegistry();

        registry.GetByName(name).ToDataLabel().Should().Be(new SecurityLabel(expected));
        registry.GetByName(name).TryToClearance(out var clearance).Should().BeTrue();
        clearance.Should().Be(new SecurityLabel(expected));

        SecurityLabel.TryParse(name, out var parsed).Should().BeFalse("{0} is not canonical label text", name);
        parsed.IsSystemHigh.Should().BeTrue("which is why the bridge never parses a level name as a label");
    }

    [Theory]
    [InlineData(5)]
    [InlineData(10)]
    [InlineData(int.MaxValue)]
    public void DataAboveTopSecret_IsSystemHigh(int value)
    {
        Level(value).ToDataLabel().Should().Be(SecurityLabel.SystemHigh);
        Level(value).ToDataLabel().IsSystemHigh.Should().BeTrue();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-2)]
    [InlineData(int.MinValue)]
    public void DataBelowPublic_IsPublic(int value)
    {
        Level(value).ToDataLabel().Should().Be(SecurityLabel.Public, "raising a data label to the bottom only narrows");
    }

    [Theory]
    [InlineData(5)]
    [InlineData(10)]
    [InlineData(int.MaxValue)]
    public void AClearanceAboveTopSecret_IsNarrowedToTopSecret_NeverSystemHigh(int value)
    {
        Level(value).TryToClearance(out var clearance).Should().BeTrue();

        clearance.Should().Be(new SecurityLabel(SecurityLevel.TopSecret));
        clearance!.IsSystemHigh.Should().BeFalse("a SystemHigh clearance would read everything, unlabelled data included");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-2)]
    [InlineData(int.MinValue)]
    public void AClearanceBelowPublic_IsRefused(int value)
    {
        Level(value).TryToClearance(out var clearance).Should().BeFalse("no label lies below Public");
        clearance.Should().BeNull();
    }

    /// <summary>
    /// A custom level above TopSecret that the registry knows: SystemHigh as data, and as a clearance narrowed to
    /// TopSecret, so it cannot read data at its own level. The registry lets it; the bridge is narrower, never wider.
    /// </summary>
    [Fact]
    public void ACustomLevelAboveTopSecret_IsSystemHighAsData_AndTopSecretAsAClearance()
    {
        var registry = new DataSensitivityRegistry();
        registry.Register(Level("Codeword", 7, loose: false));
        var codeword = registry.GetByName("Codeword");

        codeword.ToDataLabel().Should().Be(SecurityLabel.SystemHigh);
        codeword.TryToClearance(out var clearance).Should().BeTrue();
        clearance.Should().Be(new SecurityLabel(SecurityLevel.TopSecret));

        registry.CanAccess(codeword!, codeword!).Should().BeTrue("the registry compares 7 <= 7");
        BridgeAllows(codeword, codeword).Should().BeFalse("SystemHigh data needs a SystemHigh clearance");
        BridgeAllows(codeword, DataSensitivityLevels.TopSecret).Should().BeTrue();
    }

    /// <summary>
    /// Every pair from the edge grid: the bridge allows exactly what the registry allows, minus a clearance below 0
    /// and data above 4. So it is never wider, and over 0 to 4 it is equal.
    /// </summary>
    [Fact]
    public void Decision_OverTheEdgeGrid_IsNeverWiderThanCanAccess_AndEqualInRange()
    {
        var registry = new DataSensitivityRegistry();
        int bridgeAllowed = 0, legacyOnly = 0, inRange = 0;

        foreach (var agentValue in EdgeValues)
        {
            foreach (var dataValue in EdgeValues)
            {
                var agent = Level(agentValue);
                var data = Level(dataValue);
                var legacy = registry.CanAccess(agent, data);
                var bridged = BridgeAllows(agent, data);

                AssertSound(agentValue, dataValue, legacy, bridged);
                bridgeAllowed += bridged ? 1 : 0;
                legacyOnly += legacy && !bridged ? 1 : 0;
                inRange += InRange(agentValue) && InRange(dataValue) ? 1 : 0;
            }
        }

        // The grid must exercise both outcomes and the divergence, or the assertions above prove little.
        bridgeAllowed.Should().BePositive();
        legacyOnly.Should().BePositive();
        inRange.Should().Be(25);
    }

    /// <summary>
    /// Soundness over arbitrary <see cref="int"/> pairs and flags: whenever the bridge allows, the registry allows,
    /// and for values in 0 to 4 the two decisions are equal.
    /// </summary>
    [Fact]
    public void Decision_OverArbitraryValues_IsNeverWiderThanCanAccess_AndEqualInRange()
    {
        var registry = new DataSensitivityRegistry();
        Gen.Select(AnyLevel, AnyLevel).Sample(
            (agent, data) =>
                AssertSound(
                    agent.SensitivityValue,
                    data.SensitivityValue,
                    registry.CanAccess(agent, data),
                    BridgeAllows(agent, data)),
            iter: 10_000);
    }

    [Fact]
    public void UnlabelledData_IsSystemHigh()
    {
        var registry = new DataSensitivityRegistry();

        DataSensitivityLabelBridge.ToDataLabel(null).Should().Be(SecurityLabel.SystemHigh);
        registry.GetByName("NoSuchLevel").ToDataLabel().Should().Be(SecurityLabel.SystemHigh);
        registry.GetByName(null).ToDataLabel().Should().Be(SecurityLabel.SystemHigh);
        registry.GetByName("").ToDataLabel().Should().Be(SecurityLabel.SystemHigh);
        registry.GetByName("   ").ToDataLabel().Should().Be(SecurityLabel.SystemHigh);
    }

    [Fact]
    public void AnOmittedClearance_FloorsToPublic()
    {
        var registry = new DataSensitivityRegistry();

        DataSensitivityLabelBridge.TryToClearance(null, out var omitted).Should().BeTrue();
        omitted.Should().Be(SecurityLabel.Public);

        registry.GetByName("NoSuchLevel").TryToClearance(out var unknown).Should().BeTrue();
        unknown.Should().Be(SecurityLabel.Public);

        ReferenceMonitor.CanRead(unknown!, DataSensitivityLevels.Public.ToDataLabel()).Allowed.Should().BeTrue();
        ReferenceMonitor.CanRead(unknown!, DataSensitivityLevels.Internal.ToDataLabel()).Allowed.Should().BeFalse();
    }

    /// <summary>
    /// THE INTENDED DIVERGENCE, legacy side. Today an unlabelled or unknown data label resolves through
    /// <see cref="DataSensitivityFallbacks.ResolveLabel"/> to the most restrictive level the registry knows, TopSecret
    /// for the primitives, and <see cref="DataSensitivityRegistry.CanAccess"/> lets a TopSecret agent read it (this is
    /// the RAG read decision's body). The change that switches enforcement to the bridge flips this assertion, by
    /// name, on purpose.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("NoSuchLevel")]
    public void IntendedDivergence_LegacyResolvesUnlabelledData_ToALevelATopSecretAgentCanRead(string? label)
    {
        var registry = new DataSensitivityRegistry();

        var resolved = registry.ResolveLabel(label);
        resolved.Should().BeSameAs(DataSensitivityLevels.TopSecret);
        registry.CanAccess(DataSensitivityLevels.TopSecret, resolved).Should().BeTrue(
            "legacy enforcement serves unlabelled data to a TopSecret agent; switching enforcement must change this");

        // The trap the bridge documents: feed the legacy fallback into the bridge and SystemHigh is lost.
        resolved.ToDataLabel().Should().Be(new SecurityLabel(SecurityLevel.TopSecret));
        resolved.ToDataLabel().IsSystemHigh.Should().BeFalse();
    }

    /// <summary>
    /// THE INTENDED DIVERGENCE, bridge side. Resolved with <see cref="DataSensitivityRegistry.GetByName"/>, an
    /// unlabelled or unknown data label is no level, which the bridge makes SystemHigh, and a TopSecret clearance is
    /// refused it with <see cref="AccessDenialReason.SystemHighData"/>.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("NoSuchLevel")]
    public void IntendedDivergence_BridgeLabelsUnlabelledData_SystemHigh_WhichATopSecretClearanceCannotRead(string? label)
    {
        var registry = new DataSensitivityRegistry();

        var dataLabel = registry.GetByName(label).ToDataLabel();
        dataLabel.Should().Be(SecurityLabel.SystemHigh);

        DataSensitivityLevels.TopSecret.TryToClearance(out var clearance).Should().BeTrue();
        var decision = ReferenceMonitor.CanRead(clearance!, dataLabel);
        decision.Allowed.Should().BeFalse("only a SystemHigh clearance reads SystemHigh data, and no level maps to one");
        decision.Reason.Should().Be(AccessDenialReason.SystemHighData);
    }

    [Fact]
    public void BothMethods_AcceptANullReceiver()
    {
        IDataSensitivityLevel? level = null;

        level.ToDataLabel().Should().Be(SecurityLabel.SystemHigh);
        level.TryToClearance(out var clearance).Should().BeTrue();
        clearance.Should().Be(SecurityLabel.Public);
    }

    /// <summary>
    /// The value is read once, so a level whose value changes between reads cannot have one value decide the range
    /// and another pick the label.
    /// </summary>
    [Fact]
    public void BothMethods_ReadTheValueOnce()
    {
        var data = new ShiftingLevel(2, 99);
        data.ToDataLabel().Should().Be(new SecurityLabel(SecurityLevel.Confidential));
        data.Reads.Should().Be(1);

        var agent = new ShiftingLevel(2, -1);
        agent.TryToClearance(out var clearance).Should().BeTrue();
        clearance.Should().Be(new SecurityLabel(SecurityLevel.Confidential));
        agent.Reads.Should().Be(1);
    }

    /// <summary>The bridged read decision: no clearance at all is a refusal.</summary>
    private static bool BridgeAllows(IDataSensitivityLevel? agent, IDataSensitivityLevel? data) =>
        agent.TryToClearance(out var clearance) && ReferenceMonitor.CanRead(clearance, data.ToDataLabel()).Allowed;

    private static void AssertSound(int agentValue, int dataValue, bool legacy, bool bridged)
    {
        if (bridged)
        {
            legacy.Should().BeTrue(
                "the bridge allowed agent {0} to read data {1}, so CanAccess must too: the bridge is never wider",
                agentValue,
                dataValue);
        }

        if (InRange(agentValue) && InRange(dataValue))
        {
            bridged.Should().Be(legacy, "agent {0} and data {1} are both in 0 to 4", agentValue, dataValue);
        }

        // The exact shape of the difference: the registry's answer, minus the two cases no label can express.
        bridged.Should().Be(
            legacy && agentValue >= 0 && dataValue <= 4,
            "the bridge refuses what CanAccess refuses, plus a negative clearance and data above 4 (agent {0}, data {1})",
            agentValue,
            dataValue);
    }

    private static bool InRange(int value) => value is >= 0 and <= 4;

    private static IDataSensitivityLevel Level(int value) => Level("L" + value, value, loose: true);

    private static IDataSensitivityLevel Level(int value, bool[] flags) =>
        new ConfigurableSensitivityLevel("L" + value, "L" + value, value, flags[0], flags[1], flags[2], flags[3], "generated");

    private static ConfigurableSensitivityLevel Level(string name, int value, bool loose) =>
        new(
            name,
            name,
            value,
            AllowsExternalLLM: loose,
            AllowsWebSearch: loose,
            RequiresLocalOnly: !loose,
            AllowsNetworkExports: loose,
            loose ? "looser flags than the primitive" : "stricter flags than the primitive");

    /// <summary>A level whose value moves after the first read, and which counts its reads.</summary>
    private sealed class ShiftingLevel : IDataSensitivityLevel
    {
        private readonly int _first;
        private readonly int _rest;

        public ShiftingLevel(int first, int rest)
        {
            _first = first;
            _rest = rest;
        }

        public int Reads { get; private set; }

        public int SensitivityValue => Reads++ == 0 ? _first : _rest;

        public string Value => "Shifting";

        public string Display => "Shifting";

        public bool AllowsExternalLLM => false;

        public bool AllowsWebSearch => false;

        public bool RequiresLocalOnly => true;

        public bool AllowsNetworkExports => false;

        public string Description => "changes value after the first read";
    }
}
