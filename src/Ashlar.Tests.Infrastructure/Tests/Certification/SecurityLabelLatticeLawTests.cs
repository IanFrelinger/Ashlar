using Ashlar.Abstractions.Security;
using CsCheck;
using FluentAssertions;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>
/// The lattice laws of <see cref="SecurityLabel"/> as CsCheck properties, and the agreement with them of
/// <see cref="ReferenceMonitor"/>, <see cref="HighWaterMark"/> and the canonical text form.
/// </summary>
/// <remarks>
/// <para>Every property runs over <see cref="SecurityLabelGenerators"/>, which mix in
/// <see cref="SecurityLabel.SystemHigh"/>, so the laws are checked on the lattice with its distinguished top and not
/// only on the product of the level chain and the two token sets.</para>
/// <para>The SPEC-007 mutation checks are pinned by name. A level comparison weakened from <c>&lt;=</c> to
/// <c>&lt;</c> fails <see cref="Dominates_IsReflexive"/>; the compartment subset test turned round fails
/// <see cref="Dominates_HoldsExactlyWhenJoinGivesTheUpperLabel"/> and
/// <see cref="Public_IsDominatedByEveryLabel"/> (which the level change fails too); a Join that intersects caveats
/// fails <see cref="Join_IsAnUpperBoundOfBothLabels"/>; and CanWrite with its operands flipped fails
/// <see cref="CanWrite_AllowsExactlyWhenTheDestinationDominatesTheCurrentMark"/>.</para>
/// </remarks>
[Trait("Category", "Certification")]
public sealed class SecurityLabelLatticeLawTests
{
    /// <summary>
    /// Mutation pin: with the level comparison weakened from <c>&lt;=</c> to <c>&lt;</c>, no ordinary label
    /// dominates itself.
    /// </summary>
    [Fact]
    public void Dominates_IsReflexive()
    {
        SecurityLabelGenerators.Label.Sample(label =>
        {
            label.Dominates(label).Should().BeTrue("every label dominates itself");
        });
    }

    [Fact]
    public void Dominates_IsAntisymmetric()
    {
        SecurityLabelGenerators.Pair.Sample((a, b) =>
        {
            (a.Dominates(b) && b.Dominates(a)).Should().Be(
                a.Equals(b), "two labels dominate each other exactly when they are equal");
        });
    }

    [Fact]
    public void Dominates_IsTransitive()
    {
        SecurityLabelGenerators.Triple.Sample((a, b, c) =>
        {
            if (b.Dominates(a) && c.Dominates(b))
                c.Dominates(a).Should().BeTrue("a <= b and b <= c imply a <= c");
        });
    }

    /// <summary>
    /// The generators build ordered labels with the constructor alone, so this checks Dominates against an oracle
    /// that does not go through Join or Meet. It is also what keeps the guarded laws in this class from passing
    /// vacuously: their premises hold on every draw from <see cref="SecurityLabelGenerators.Chain"/> and
    /// <see cref="SecurityLabelGenerators.Bounded"/>.
    /// </summary>
    [Fact]
    public void Dominates_HoldsBetweenLabelsBuiltAboveOneAnother()
    {
        SecurityLabelGenerators.Chain.Sample((low, middle, high) =>
        {
            middle.Dominates(low).Should().BeTrue("middle was built from low's tokens and more, at no lower a level");
            high.Dominates(middle).Should().BeTrue("high was built from middle's tokens and more, at no lower a level");
            high.Dominates(low).Should().BeTrue("high was built from low's tokens and more, at no lower a level");
        });

        SecurityLabelGenerators.Bounded.Sample((a, b, upper, lower) =>
        {
            upper.Dominates(a).Should().BeTrue("upper was built above a");
            upper.Dominates(b).Should().BeTrue("upper was built above b");
            a.Dominates(lower).Should().BeTrue("lower was built below a");
            b.Dominates(lower).Should().BeTrue("lower was built below b");
        });
    }

    /// <summary>
    /// <c>a &lt;= b</c> exactly when <c>a ⊔ b = b</c>. Mutation pin: with the compartment subset test turned round,
    /// or the level comparison weakened, the order and the join disagree.
    /// </summary>
    [Fact]
    public void Dominates_HoldsExactlyWhenJoinGivesTheUpperLabel()
    {
        SecurityLabelGenerators.Pair.Sample((a, b) =>
        {
            b.Dominates(a).Should().Be(a.Join(b).Equals(b), "a <= b exactly when a join b is b");
        });
    }

    /// <summary><c>a &lt;= b</c> exactly when <c>a ⊓ b = a</c>.</summary>
    [Fact]
    public void Dominates_HoldsExactlyWhenMeetGivesTheLowerLabel()
    {
        SecurityLabelGenerators.Pair.Sample((a, b) =>
        {
            b.Dominates(a).Should().Be(a.Meet(b).Equals(a), "a <= b exactly when a meet b is a");
        });
    }

    [Fact]
    public void Join_IsCommutative()
    {
        SecurityLabelGenerators.Pair.Sample((a, b) =>
        {
            a.Join(b).Should().Be(b.Join(a));
        });
    }

    [Fact]
    public void Join_IsAssociative()
    {
        SecurityLabelGenerators.Triple.Sample((a, b, c) =>
        {
            a.Join(b).Join(c).Should().Be(a.Join(b.Join(c)));
        });
    }

    [Fact]
    public void Join_IsIdempotent()
    {
        SecurityLabelGenerators.Label.Sample(label =>
        {
            label.Join(label).Should().Be(label);
        });
    }

    /// <summary>
    /// Mutation pin: a Join that intersects the caveats instead of uniting them drops a caveat that only one side
    /// carries, and the result no longer dominates that side.
    /// </summary>
    [Fact]
    public void Join_IsAnUpperBoundOfBothLabels()
    {
        SecurityLabelGenerators.Pair.Sample((a, b) =>
        {
            var join = a.Join(b);
            join.Dominates(a).Should().BeTrue("a join b dominates a");
            join.Dominates(b).Should().BeTrue("a join b dominates b");
        });
    }

    [Fact]
    public void Join_IsTheLeastUpperBound()
    {
        SecurityLabelGenerators.Bounded.Sample((a, b, upper, _) =>
        {
            if (upper.Dominates(a) && upper.Dominates(b))
                upper.Dominates(a.Join(b)).Should().BeTrue("every upper bound of a and b dominates a join b");
        });
    }

    [Fact]
    public void Meet_IsCommutative()
    {
        SecurityLabelGenerators.Pair.Sample((a, b) =>
        {
            a.Meet(b).Should().Be(b.Meet(a));
        });
    }

    [Fact]
    public void Meet_IsAssociative()
    {
        SecurityLabelGenerators.Triple.Sample((a, b, c) =>
        {
            a.Meet(b).Meet(c).Should().Be(a.Meet(b.Meet(c)));
        });
    }

    [Fact]
    public void Meet_IsIdempotent()
    {
        SecurityLabelGenerators.Label.Sample(label =>
        {
            label.Meet(label).Should().Be(label);
        });
    }

    [Fact]
    public void Meet_IsALowerBoundOfBothLabels()
    {
        SecurityLabelGenerators.Pair.Sample((a, b) =>
        {
            var meet = a.Meet(b);
            a.Dominates(meet).Should().BeTrue("a dominates a meet b");
            b.Dominates(meet).Should().BeTrue("b dominates a meet b");
        });
    }

    [Fact]
    public void Meet_IsTheGreatestLowerBound()
    {
        SecurityLabelGenerators.Bounded.Sample((a, b, _, lower) =>
        {
            if (a.Dominates(lower) && b.Dominates(lower))
                a.Meet(b).Dominates(lower).Should().BeTrue("a meet b dominates every lower bound of a and b");
        });
    }

    [Fact]
    public void JoinAndMeet_AbsorbEachOther()
    {
        SecurityLabelGenerators.Pair.Sample((a, b) =>
        {
            a.Join(a.Meet(b)).Should().Be(a, "a join (a meet b) is a");
            a.Meet(a.Join(b)).Should().Be(a, "a meet (a join b) is a");
        });
    }

    [Fact]
    public void SystemHigh_IsTheTopElement()
    {
        // SystemHigh is a top of its own, not the TopSecret label that carries every token.
        var fullest = new SecurityLabel(
            SecurityLevel.TopSecret,
            new[] { "ALPHA", "BRAVO", "CHARLIE", "DELTA" },
            new[] { "NOWEB", "NOFORN", "ORCON" });
        fullest.Dominates(SecurityLabel.SystemHigh).Should().BeFalse("only SystemHigh dominates SystemHigh");
        SecurityLabel.SystemHigh.Should().NotBe(new SecurityLabel(SecurityLevel.TopSecret));

        SecurityLabelGenerators.Label.Sample(label =>
        {
            SecurityLabel.SystemHigh.Dominates(label).Should().BeTrue("SystemHigh dominates every label");
            label.Dominates(SecurityLabel.SystemHigh).Should().Be(
                label.IsSystemHigh, "only SystemHigh dominates SystemHigh");
            label.Join(SecurityLabel.SystemHigh).Should().Be(SecurityLabel.SystemHigh);
            SecurityLabel.SystemHigh.Join(label).Should().Be(SecurityLabel.SystemHigh);
            label.Meet(SecurityLabel.SystemHigh).Should().Be(label);
            SecurityLabel.SystemHigh.Meet(label).Should().Be(label);
        });
    }

    /// <summary>
    /// Mutation pin: with the compartment subset test turned round, a label that carries a compartment no longer
    /// dominates Public; with the level comparison weakened, no Public-level label does.
    /// </summary>
    [Fact]
    public void Public_IsDominatedByEveryLabel()
    {
        SecurityLabelGenerators.Label.Sample(label =>
        {
            label.Dominates(SecurityLabel.Public).Should().BeTrue("every label dominates Public, the bottom");
            SecurityLabel.Public.Dominates(label).Should().Be(
                label.Equals(SecurityLabel.Public), "Public dominates nothing but itself");
            label.Join(SecurityLabel.Public).Should().Be(label);
            SecurityLabel.Public.Join(label).Should().Be(label);
            label.Meet(SecurityLabel.Public).Should().Be(SecurityLabel.Public);
            SecurityLabel.Public.Meet(label).Should().Be(SecurityLabel.Public);
        });
    }

    [Fact]
    public void StaticJoin_IsTheFoldOfPairwiseJoins()
    {
        SecurityLabelGenerators.Label.Array[0, 6].Sample(labels =>
        {
            var joined = SecurityLabel.Join(labels);
            var fold = labels.Length == 0
                ? SecurityLabel.Public
                : labels.Aggregate((acc, next) => acc.Join(next));

            joined.Should().Be(fold, "the join of a sequence is its pairwise joins folded together");
            SecurityLabel.Join(Enumerable.Reverse(labels)).Should().Be(joined, "the order of the labels does not matter");
            foreach (var label in labels)
                joined.Dominates(label).Should().BeTrue("the join dominates every label in the sequence");
        });
    }

    [Fact]
    public void StaticJoin_OfNoLabels_IsPublic()
    {
        SecurityLabel.Join(Array.Empty<SecurityLabel>()).Should().Be(SecurityLabel.Public);
        SecurityLabel.Join(Enumerable.Empty<SecurityLabel>()).Should().Be(SecurityLabel.Public);
    }

    [Fact]
    public void Operations_RefuseANullOperand_EvenOnTheShortCircuitedExtremes()
    {
        // SystemHigh and Public short-circuit Dominates, Join and Meet, so the null guard is the only thing standing
        // between a null operand and a silent answer.
        foreach (var receiver in new[] { SecurityLabel.SystemHigh, SecurityLabel.Public })
        {
            Action dominates = () => _ = receiver.Dominates(null!);
            Action join = () => _ = receiver.Join(null!);
            Action meet = () => _ = receiver.Meet(null!);

            dominates.Should().ThrowExactly<ArgumentNullException>().WithParameterName("other");
            join.Should().ThrowExactly<ArgumentNullException>().WithParameterName("other");
            meet.Should().ThrowExactly<ArgumentNullException>().WithParameterName("other");
        }

        Action staticJoin = () => _ = SecurityLabel.Join((IEnumerable<SecurityLabel>)null!);
        staticJoin.Should().ThrowExactly<ArgumentNullException>().WithParameterName("labels");
    }

    [Fact]
    public void StaticJoin_RefusesANullElementWhereverItSits()
    {
        Gen.Select(SecurityLabelGenerators.Label.Array[0, 3], SecurityLabelGenerators.Label.Array[0, 3])
            .Sample((before, after) =>
            {
                var labels = new List<SecurityLabel>(before) { null! };
                labels.AddRange(after);

                Action act = () => SecurityLabel.Join(labels);

                act.Should().ThrowExactly<ArgumentException>("a null label is refused, even after SystemHigh")
                    .WithParameterName("labels");
            });
    }

    [Fact]
    public void CanRead_AllowsExactlyWhenTheClearanceDominatesTheData()
    {
        SecurityLabelGenerators.Pair.Sample((clearance, data) =>
        {
            var decision = ReferenceMonitor.CanRead(clearance, data);

            decision.Allowed.Should().Be(
                clearance.Dominates(data), "no read up: a read is allowed exactly when the clearance dominates the data");
            ShouldBeExplained(decision);
        });
    }

    /// <summary>
    /// Mutation pin: with CanWrite's operands flipped, a write up to a dominating destination is refused and a write
    /// down is allowed, so the decision stops agreeing with the order.
    /// </summary>
    [Fact]
    public void CanWrite_AllowsExactlyWhenTheDestinationDominatesTheCurrentMark()
    {
        SecurityLabelGenerators.Pair.Sample((current, destination) =>
        {
            var decision = ReferenceMonitor.CanWrite(current, destination);

            decision.Allowed.Should().Be(
                destination.Dominates(current),
                "no write down: a write is allowed exactly when the destination dominates the current mark");
            ShouldBeExplained(decision);
        });
    }

    [Fact]
    public void Refusals_NameTheFirstFailingRuleAndWhatIsMissing()
    {
        SecurityLabelGenerators.Pair.Sample((receiver, source) =>
        {
            ShouldExplain(ReferenceMonitor.CanRead(clearance: receiver, data: source), source, receiver);
            ShouldExplain(ReferenceMonitor.CanWrite(current: source, destination: receiver), source, receiver);
        });
    }

    [Fact]
    public void Construction_CollapsesDuplicatesAndSortsTokensOrdinally()
    {
        Gen.Select(
                SecurityLabelGenerators.Level,
                SecurityLabelGenerators.Token.Array[0, 4],
                SecurityLabelGenerators.Token.Array[0, 3])
            .Sample((level, compartments, caveats) =>
            {
                // Every token goes in twice, the second time in reverse order.
                var label = new SecurityLabel(
                    level,
                    compartments.Concat(Enumerable.Reverse(compartments)),
                    caveats.Concat(Enumerable.Reverse(caveats)));

                label.Level.Should().Be(level);
                label.IsSystemHigh.Should().BeFalse("only SecurityLabel.SystemHigh is the top element");
                label.Compartments.Should().Equal(OrdinalSet(compartments));
                label.Caveats.Should().Equal(OrdinalSet(caveats));
            });
    }

    [Fact]
    public void Text_IsCanonicalAndRoundTripsThroughTryParse()
    {
        Gen.OneOf(SecurityLabelGenerators.Label, SecurityLabelGenerators.WideLabel).Sample(label =>
        {
            var text = label.ToString();
            text.Should().Be(Render(label, label.Compartments, label.Caveats), "ToString prints the canonical form");

            SecurityLabel.TryParse(text, out var parsed).Should().BeTrue("canonical text parses");
            parsed.Should().Be(label);
            parsed.ToString().Should().Be(text);
            parsed.GetHashCode().Should().Be(label.GetHashCode(), "equal labels hash alike");
            parsed.IsSystemHigh.Should().Be(label.IsSystemHigh);
            SecurityLabel.ParseOrSystemHigh(text).Should().Be(label);

            ShouldBeOrdinalSet(label.Compartments, "compartments");
            ShouldBeOrdinalSet(label.Caveats, "caveats");
        });
    }

    /// <summary>
    /// Text and labels correspond one to one: whatever <see cref="SecurityLabel.TryParse"/> accepts,
    /// <see cref="SecurityLabel.ToString"/> prints back exactly, and whatever it refuses comes back as
    /// <see cref="SecurityLabel.SystemHigh"/>. None of these perturbations lands on another canonical form, so every
    /// one that changes the text must be refused.
    /// </summary>
    [Fact]
    public void TryParse_AcceptsOnlyCanonicalText()
    {
        Gen.Select(
                Gen.OneOf(SecurityLabelGenerators.Label, SecurityLabelGenerators.WideLabel),
                Gen.Enum<Perturbation>(),
                Gen.Int[0, 4095])
            .Sample((label, perturbation, pick) =>
            {
                var canonical = label.ToString();
                var text = Perturb(label, perturbation, pick);

                var accepted = SecurityLabel.TryParse(text, out var parsed);

                if (accepted)
                {
                    parsed.ToString().Should().Be(text, "TryParse accepts only the canonical text of the label it returns");
                }
                else
                {
                    parsed.IsSystemHigh.Should().BeTrue("a refused parse fails closed to SystemHigh");
                    SecurityLabel.ParseOrSystemHigh(text).IsSystemHigh.Should().BeTrue(
                        "ParseOrSystemHigh fails closed on the same text");
                }

                accepted.Should().Be(text == canonical, "a perturbation that changes canonical text leaves canonical form");
            });
    }

    [Fact]
    public void HighWaterMark_IsTheJoinOfTheFloorAndEverythingObserved()
    {
        Gen.Select(
                Gen.Bool,
                SecurityLabelGenerators.Label,
                SecurityLabelGenerators.Label.Array[0, 6],
                SecurityLabelGenerators.Label)
            .Sample((startAtDefault, floor, observed, destination) =>
            {
                var start = startAtDefault ? SecurityLabel.Public : floor;
                var mark = startAtDefault ? new HighWaterMark() : new HighWaterMark(floor);
                mark.Current.Should().Be(start, "a mark starts at its floor, which is Public by default");

                foreach (var label in observed)
                {
                    var before = mark.Current;
                    mark.Observe(label);
                    mark.Current.Should().Be(before.Join(label), "observing a label joins it in");
                    mark.Current.Dominates(before).Should().BeTrue("the mark only rises");
                }

                mark.Current.Should().Be(
                    SecurityLabel.Join(new[] { start }.Concat(observed)),
                    "the mark is the join of its floor and everything observed");
                mark.Current.Dominates(start).Should().BeTrue("the mark never falls below its floor");
                foreach (var label in observed)
                    mark.Current.Dominates(label).Should().BeTrue("the mark dominates everything observed");

                mark.CanWriteTo(destination).Should().Be(
                    ReferenceMonitor.CanWrite(mark.Current, destination),
                    "CanWriteTo is CanWrite against the current mark");
            });
    }

    private static void ShouldBeExplained(AccessDecision decision)
    {
        if (decision.Allowed)
        {
            decision.Reason.Should().Be(AccessDenialReason.None, "an allowed decision has no refusal reason");
            decision.Detail.Should().BeEmpty("an allowed decision has nothing to explain");
        }
        else
        {
            decision.Reason.Should().NotBe(AccessDenialReason.None, "every refusal names a reason");
            decision.Detail.Should().NotBeNullOrWhiteSpace("every refusal is explained");
        }
    }

    // `source` flows to `receiver`: the data to the clearance for a read, the current mark to the destination for
    // a write. The expected reason is the first rule that fails, in the documented order.
    private static void ShouldExplain(AccessDecision decision, SecurityLabel source, SecurityLabel receiver)
    {
        var expected = FirstFailingRule(source, receiver);
        decision.Reason.Should().Be(expected, "a refusal reports the first rule that fails, in the documented order");
        decision.Allowed.Should().Be(expected == AccessDenialReason.None);

        switch (expected)
        {
            case AccessDenialReason.SystemHighData:
                decision.Detail.Should().Contain("SystemHigh");
                break;
            case AccessDenialReason.LevelTooLow:
                decision.Detail.Should().Contain(receiver.Level.ToString()).And.Contain(source.Level.ToString());
                break;
            case AccessDenialReason.MissingCompartment:
                decision.Detail.Should().EndWith(
                    ": " + string.Join(",", Missing(source.Compartments, receiver.Compartments)),
                    "the detail names every missing compartment, in canonical order");
                break;
            case AccessDenialReason.MissingCaveat:
                decision.Detail.Should().EndWith(
                    ": " + string.Join(",", Missing(source.Caveats, receiver.Caveats)),
                    "the detail names every missing caveat, in canonical order");
                break;
            default:
                decision.Detail.Should().BeEmpty();
                break;
        }
    }

    private static AccessDenialReason FirstFailingRule(SecurityLabel source, SecurityLabel receiver)
    {
        if (receiver.IsSystemHigh)
            return AccessDenialReason.None;
        if (source.IsSystemHigh)
            return AccessDenialReason.SystemHighData;
        if (source.Level > receiver.Level)
            return AccessDenialReason.LevelTooLow;
        if (Missing(source.Compartments, receiver.Compartments).Length > 0)
            return AccessDenialReason.MissingCompartment;
        if (Missing(source.Caveats, receiver.Caveats).Length > 0)
            return AccessDenialReason.MissingCaveat;

        return AccessDenialReason.None;
    }

    private static string[] Missing(IReadOnlyList<string> required, IReadOnlyList<string> held) =>
        required.Where(token => !held.Contains(token, StringComparer.Ordinal)).ToArray();

    private static string[] OrdinalSet(IEnumerable<string> tokens) =>
        tokens.Distinct(StringComparer.Ordinal).OrderBy(token => token, StringComparer.Ordinal).ToArray();

    private static void ShouldBeOrdinalSet(IReadOnlyList<string> tokens, string what)
    {
        for (var i = 1; i < tokens.Count; i++)
        {
            string.CompareOrdinal(tokens[i - 1], tokens[i]).Should().BeLessThan(
                0, "the {0} are sorted ordinally and hold no duplicates", what);
        }
    }

    // The canonical grammar, restated rather than borrowed from ToString: the level name (or SystemHigh), then
    // "//C:" and the compartments, then "//K:" and the caveats, each list comma-separated and left out when empty.
    private static string Render(
        SecurityLabel label,
        IReadOnlyList<string> compartments,
        IReadOnlyList<string> caveats,
        bool caveatsFirst = false)
    {
        var head = label.IsSystemHigh ? "SystemHigh" : label.Level.ToString();
        var compartmentSegment = compartments.Count == 0 ? string.Empty : "//C:" + string.Join(",", compartments);
        var caveatSegment = caveats.Count == 0 ? string.Empty : "//K:" + string.Join(",", caveats);
        return caveatsFirst ? head + caveatSegment + compartmentSegment : head + compartmentSegment + caveatSegment;
    }

    private enum Perturbation
    {
        InsertSpace,
        LowercaseOneCharacter,
        DuplicateToken,
        SwapTwoTokens,
        InsertEmptyToken,
        AppendSeparator,
        SwapSegments,
    }

    // Applies one small perturbation to the canonical text of `label`, using `pick` to choose where. Some leave the
    // text unchanged (lowercasing a character that is not an uppercase letter, swapping tokens in a list of one).
    private static string Perturb(SecurityLabel label, Perturbation perturbation, int pick)
    {
        var text = label.ToString();
        var compartments = label.Compartments.ToList();
        var caveats = label.Caveats.ToList();

        // The token perturbations act on the compartments on an even pick (when there are any), else on the caveats.
        var tokens = (pick % 2 == 0 && compartments.Count > 0) || caveats.Count == 0 ? compartments : caveats;

        switch (perturbation)
        {
            case Perturbation.InsertSpace:
                return text.Insert(pick % (text.Length + 1), " ");

            case Perturbation.LowercaseOneCharacter:
            {
                var at = pick % text.Length;
                return text.Substring(0, at) + char.ToLowerInvariant(text[at]) + text.Substring(at + 1);
            }

            case Perturbation.DuplicateToken:
            {
                if (tokens.Count == 0)
                    return text;

                var at = pick % tokens.Count;
                tokens.Insert(at, tokens[at]);
                return Render(label, compartments, caveats);
            }

            case Perturbation.SwapTwoTokens:
            {
                if (tokens.Count < 2)
                    return text;

                var i = pick % tokens.Count;
                var j = (i + 1 + ((pick / tokens.Count) % (tokens.Count - 1))) % tokens.Count;
                (tokens[i], tokens[j]) = (tokens[j], tokens[i]);
                return Render(label, compartments, caveats);
            }

            case Perturbation.InsertEmptyToken:
                // On a label with no tokens this leaves an empty segment, such as "Secret//C:".
                tokens.Insert(pick % (tokens.Count + 1), string.Empty);
                return Render(label, compartments, caveats);

            case Perturbation.AppendSeparator:
                return text + "//";

            case Perturbation.SwapSegments:
                return Render(label, compartments, caveats, caveatsFirst: true);

            default:
                throw new ArgumentOutOfRangeException(nameof(perturbation), perturbation, null);
        }
    }
}
