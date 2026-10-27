using Ashlar.Abstractions.Security;
using Ashlar.Abstractions.Security.Egress;
using FluentAssertions;
using Xunit;
using Class = Ashlar.Abstractions.Security.Egress.EgressDestinationClass;
using Label = Ashlar.Abstractions.Security.PrivacyLabel;
using Reason = Ashlar.Abstractions.Security.Egress.EgressRefusalReason;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>
/// SPEC-010 §3.3 and §3.7: the privacy rule's full decision table, the order of its steps, its refusal reasons and the
/// decision that nobody made.
///
/// <para><b>The table.</b> <see cref="DecisionTable"/> is written out by hand, one row for every destination class
/// against every subset of the four labels (5 × 16 = 80 rows), each naming the reason and the forbidding label the spec
/// gives. It is not computed from the rule, so a change to the rule that the spec does not make turns a row red.
/// <see cref="TheDecisionTable_HasOneRowForEveryClassAndEverySubset"/> keeps it complete.</para>
///
/// <para><b>The mutations these tests exist to catch</b> (SPEC-010 §8). Dropping step 3 allows the eight Unknown rows
/// whose read set lacks <c>local-only</c>; letting <c>local-only</c> through to ExternalModel changes the eight
/// ExternalModel rows whose read set holds it. Both fail <see cref="Decide_FollowsTheDecisionTable"/> by row.</para>
///
/// <para>Every read set is written as canonical text and parsed through <c>R</c>, which fails the row if the text does
/// not parse: a non-canonical spelling parses to <c>local-only</c>, and a row built on one would test that set instead
/// of the one it names.</para>
/// </summary>
[Trait("Category", "Certification")]
public sealed class PrivacyRuleDecisionTests
{
    /// <summary>The sixteen subsets of the four labels, as canonical text.</summary>
    private static readonly string[] Subsets =
    [
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
    ];

    private static readonly Class[] Classes =
        [Class.Host, Class.ExternalModel, Class.WebSearch, Class.NetworkExport, Class.Unknown];

    /// <summary>
    /// Every class against every subset: the read set, the destination class, the expected reason and the expected
    /// forbidding label (null unless the reason is <see cref="Reason.LabelForbidsDestination"/>).
    /// </summary>
    public static TheoryData<string, Class, Reason, Label?> DecisionTable => new()
    {
        // Host: step 1 allows every read set, local-only included.
        { "none", Class.Host, Reason.None, null },
        { "local-only", Class.Host, Reason.None, null },
        { "no-external-model", Class.Host, Reason.None, null },
        { "no-network-export", Class.Host, Reason.None, null },
        { "no-web", Class.Host, Reason.None, null },
        { "local-only,no-external-model", Class.Host, Reason.None, null },
        { "local-only,no-network-export", Class.Host, Reason.None, null },
        { "local-only,no-web", Class.Host, Reason.None, null },
        { "no-external-model,no-network-export", Class.Host, Reason.None, null },
        { "no-external-model,no-web", Class.Host, Reason.None, null },
        { "no-network-export,no-web", Class.Host, Reason.None, null },
        { "local-only,no-external-model,no-network-export", Class.Host, Reason.None, null },
        { "local-only,no-external-model,no-web", Class.Host, Reason.None, null },
        { "local-only,no-network-export,no-web", Class.Host, Reason.None, null },
        { "no-external-model,no-network-export,no-web", Class.Host, Reason.None, null },
        { "local-only,no-external-model,no-network-export,no-web", Class.Host, Reason.None, null },

        // ExternalModel: local-only refuses at step 2, then no-external-model at step 4.
        { "none", Class.ExternalModel, Reason.None, null },
        { "local-only", Class.ExternalModel, Reason.LocalOnlyData, null },
        { "no-external-model", Class.ExternalModel, Reason.LabelForbidsDestination, Label.NoExternalModel },
        { "no-network-export", Class.ExternalModel, Reason.None, null },
        { "no-web", Class.ExternalModel, Reason.None, null },
        { "local-only,no-external-model", Class.ExternalModel, Reason.LocalOnlyData, null },
        { "local-only,no-network-export", Class.ExternalModel, Reason.LocalOnlyData, null },
        { "local-only,no-web", Class.ExternalModel, Reason.LocalOnlyData, null },
        { "no-external-model,no-network-export", Class.ExternalModel, Reason.LabelForbidsDestination, Label.NoExternalModel },
        { "no-external-model,no-web", Class.ExternalModel, Reason.LabelForbidsDestination, Label.NoExternalModel },
        { "no-network-export,no-web", Class.ExternalModel, Reason.None, null },
        { "local-only,no-external-model,no-network-export", Class.ExternalModel, Reason.LocalOnlyData, null },
        { "local-only,no-external-model,no-web", Class.ExternalModel, Reason.LocalOnlyData, null },
        { "local-only,no-network-export,no-web", Class.ExternalModel, Reason.LocalOnlyData, null },
        { "no-external-model,no-network-export,no-web", Class.ExternalModel, Reason.LabelForbidsDestination, Label.NoExternalModel },
        { "local-only,no-external-model,no-network-export,no-web", Class.ExternalModel, Reason.LocalOnlyData, null },

        // WebSearch: local-only refuses at step 2, then no-web at step 4.
        { "none", Class.WebSearch, Reason.None, null },
        { "local-only", Class.WebSearch, Reason.LocalOnlyData, null },
        { "no-external-model", Class.WebSearch, Reason.None, null },
        { "no-network-export", Class.WebSearch, Reason.None, null },
        { "no-web", Class.WebSearch, Reason.LabelForbidsDestination, Label.NoWeb },
        { "local-only,no-external-model", Class.WebSearch, Reason.LocalOnlyData, null },
        { "local-only,no-network-export", Class.WebSearch, Reason.LocalOnlyData, null },
        { "local-only,no-web", Class.WebSearch, Reason.LocalOnlyData, null },
        { "no-external-model,no-network-export", Class.WebSearch, Reason.None, null },
        { "no-external-model,no-web", Class.WebSearch, Reason.LabelForbidsDestination, Label.NoWeb },
        { "no-network-export,no-web", Class.WebSearch, Reason.LabelForbidsDestination, Label.NoWeb },
        { "local-only,no-external-model,no-network-export", Class.WebSearch, Reason.LocalOnlyData, null },
        { "local-only,no-external-model,no-web", Class.WebSearch, Reason.LocalOnlyData, null },
        { "local-only,no-network-export,no-web", Class.WebSearch, Reason.LocalOnlyData, null },
        { "no-external-model,no-network-export,no-web", Class.WebSearch, Reason.LabelForbidsDestination, Label.NoWeb },
        { "local-only,no-external-model,no-network-export,no-web", Class.WebSearch, Reason.LocalOnlyData, null },

        // NetworkExport: local-only refuses at step 2, then no-network-export at step 4.
        { "none", Class.NetworkExport, Reason.None, null },
        { "local-only", Class.NetworkExport, Reason.LocalOnlyData, null },
        { "no-external-model", Class.NetworkExport, Reason.None, null },
        { "no-network-export", Class.NetworkExport, Reason.LabelForbidsDestination, Label.NoNetworkExport },
        { "no-web", Class.NetworkExport, Reason.None, null },
        { "local-only,no-external-model", Class.NetworkExport, Reason.LocalOnlyData, null },
        { "local-only,no-network-export", Class.NetworkExport, Reason.LocalOnlyData, null },
        { "local-only,no-web", Class.NetworkExport, Reason.LocalOnlyData, null },
        { "no-external-model,no-network-export", Class.NetworkExport, Reason.LabelForbidsDestination, Label.NoNetworkExport },
        { "no-external-model,no-web", Class.NetworkExport, Reason.None, null },
        { "no-network-export,no-web", Class.NetworkExport, Reason.LabelForbidsDestination, Label.NoNetworkExport },
        { "local-only,no-external-model,no-network-export", Class.NetworkExport, Reason.LocalOnlyData, null },
        { "local-only,no-external-model,no-web", Class.NetworkExport, Reason.LocalOnlyData, null },
        { "local-only,no-network-export,no-web", Class.NetworkExport, Reason.LocalOnlyData, null },
        { "no-external-model,no-network-export,no-web", Class.NetworkExport, Reason.LabelForbidsDestination, Label.NoNetworkExport },
        { "local-only,no-external-model,no-network-export,no-web", Class.NetworkExport, Reason.LocalOnlyData, null },

        // Unknown: local-only refuses at step 2; every other read set, none included, at step 3.
        { "none", Class.Unknown, Reason.UnknownDestination, null },
        { "local-only", Class.Unknown, Reason.LocalOnlyData, null },
        { "no-external-model", Class.Unknown, Reason.UnknownDestination, null },
        { "no-network-export", Class.Unknown, Reason.UnknownDestination, null },
        { "no-web", Class.Unknown, Reason.UnknownDestination, null },
        { "local-only,no-external-model", Class.Unknown, Reason.LocalOnlyData, null },
        { "local-only,no-network-export", Class.Unknown, Reason.LocalOnlyData, null },
        { "local-only,no-web", Class.Unknown, Reason.LocalOnlyData, null },
        { "no-external-model,no-network-export", Class.Unknown, Reason.UnknownDestination, null },
        { "no-external-model,no-web", Class.Unknown, Reason.UnknownDestination, null },
        { "no-network-export,no-web", Class.Unknown, Reason.UnknownDestination, null },
        { "local-only,no-external-model,no-network-export", Class.Unknown, Reason.LocalOnlyData, null },
        { "local-only,no-external-model,no-web", Class.Unknown, Reason.LocalOnlyData, null },
        { "local-only,no-network-export,no-web", Class.Unknown, Reason.LocalOnlyData, null },
        { "no-external-model,no-network-export,no-web", Class.Unknown, Reason.UnknownDestination, null },
        { "local-only,no-external-model,no-network-export,no-web", Class.Unknown, Reason.LocalOnlyData, null },
    };

    // ---- The table ----

    [Theory]
    [MemberData(nameof(DecisionTable))]
    public void Decide_FollowsTheDecisionTable(string readSet, Class destination, Reason reason, Label? forbiddenBy)
    {
        var decision = PrivacyRule.Decide(R(readSet), destination);

        decision.Reason.Should().Be(reason, "{0} to {1}, but got: {2}", readSet, destination, decision);
        decision.ForbiddenBy.Should().Be(forbiddenBy, "{0} to {1}, but got: {2}", readSet, destination, decision);
        decision.Allowed.Should().Be(reason == Reason.None, "Reason == None exactly when allowed: {0}", decision);
    }

    [Fact]
    public void TheDecisionTable_HasOneRowForEveryClassAndEverySubset()
    {
        var rows = ((IEnumerable<object[]>)DecisionTable).Select(row => ((string)row[0], (Class)row[1])).ToList();

        rows.Should().HaveCount(Classes.Length * Subsets.Length, "5 classes x 16 subsets");
        rows.Should().OnlyHaveUniqueItems();
        foreach (var destination in Classes)
        {
            foreach (var subset in Subsets)
                rows.Should().Contain((subset, destination), "the table needs a row for {0} to {1}", subset, destination);
        }

        Classes.Should().BeEquivalentTo(Enum.GetValues<Class>(), "the table covers every defined class");
        Subsets.Select(subset => R(subset)).Distinct().Should().HaveCount(16, "the table covers every subset of the four labels");
    }

    // ---- The order of the steps ----

    [Theory]
    [InlineData("local-only", Class.Host)]
    [InlineData("local-only,no-external-model,no-network-export,no-web", Class.Host)]
    public void Step1_HostComesFirst_SoEvenLocalOnlyDataMayStayOnTheHost(string readSet, Class destination)
    {
        PrivacyRule.Decide(R(readSet), destination).Allowed.Should().BeTrue();
    }

    [Theory]
    [InlineData("local-only,no-external-model", Class.ExternalModel)]
    [InlineData("local-only,no-web", Class.WebSearch)]
    [InlineData("local-only,no-network-export", Class.NetworkExport)]
    [InlineData("local-only", Class.Unknown)]
    [InlineData("local-only,no-external-model,no-network-export,no-web", Class.Unknown)]
    public void Step2_LocalOnlyIsReported_BeforeTheDestinationIsClassifiedOrALabelForbidsIt(string readSet, Class destination)
    {
        var decision = PrivacyRule.Decide(R(readSet), destination);

        decision.Reason.Should().Be(Reason.LocalOnlyData, "step 2 runs before steps 3 and 4: {0}", decision);
        decision.ForbiddenBy.Should().BeNull("LocalOnlyData names no forbidding label; the reason itself says local-only");
    }

    [Theory]
    [InlineData("none")]
    [InlineData("no-external-model,no-network-export,no-web")]
    public void Step3_AnUnknownDestination_IsRefused_EvenForDataNoLabelRestricts(string readSet)
    {
        var decision = PrivacyRule.Decide(R(readSet), Class.Unknown);

        decision.Reason.Should().Be(Reason.UnknownDestination, "data with no restriction still goes nowhere unknown");
        decision.ForbiddenBy.Should().BeNull();
    }

    [Theory]
    [InlineData(5)]
    [InlineData(99)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public void AClassValueTheRuleDoesNotKnow_IsRefusedAsUnknown(int value)
    {
        // A class appended later, or a corrupted value, must not fall through to step 5 and be allowed.
        var destination = (Class)value;

        PrivacyRule.Decide(PrivacyLabelSet.None, destination).Reason.Should().Be(Reason.UnknownDestination);
        PrivacyRule.Decide(R("no-external-model,no-network-export,no-web"), destination).Reason
            .Should().Be(Reason.UnknownDestination);
        PrivacyRule.Decide(PrivacyLabelSet.LocalOnly, destination).Reason.Should().Be(Reason.LocalOnlyData);
    }

    [Theory]
    [InlineData(Label.NoExternalModel, Class.ExternalModel)]
    [InlineData(Label.NoWeb, Class.WebSearch)]
    [InlineData(Label.NoNetworkExport, Class.NetworkExport)]
    public void EachNoLabel_ForbidsExactlyOneClass(Label label, Class forbidden)
    {
        var readSet = PrivacyLabelSet.Of(label);

        foreach (var destination in new[] { Class.Host, Class.ExternalModel, Class.WebSearch, Class.NetworkExport })
        {
            var decision = PrivacyRule.Decide(readSet, destination);
            if (destination == forbidden)
            {
                decision.Reason.Should().Be(Reason.LabelForbidsDestination);
                decision.ForbiddenBy.Should().Be(label);
            }
            else
            {
                decision.Allowed.Should().BeTrue("{0} forbids only {1}, not {2}", label, forbidden, destination);
            }
        }
    }

    [Fact]
    public void ForbiddenBy_IsSetExactlyForLabelForbidsDestination()
    {
        foreach (var subset in Subsets)
        {
            foreach (var destination in Classes)
            {
                var decision = PrivacyRule.Decide(R(subset), destination);
                (decision.ForbiddenBy is not null).Should().Be(
                    decision.Reason == Reason.LabelForbidsDestination, "{0} to {1}: {2}", subset, destination, decision);
                if (decision.ForbiddenBy is { } label)
                    R(subset).Contains(label).Should().BeTrue("the forbidding label is one the read set holds");
                decision.Reason.Should().NotBe(Reason.NoDecision, "the rule always decides");
            }
        }
    }

    [Fact]
    public void Decide_NullReadSet_ThrowsNamingTheParameter()
    {
        Action act = () => PrivacyRule.Decide(null!, Class.Host);

        act.Should().Throw<ArgumentNullException>().WithParameterName("readSet");
    }

    // ---- Reasons and the decision nobody made ----

    [Fact]
    public void EgressRefusalReason_ValuesArePinned_AndOnlyAppended()
    {
        // Stored reasons keep their meaning only if values never move (SPEC-010 §3.7); a new reason also needs an arm
        // in PrivacyDecision.ToString, or it prints as a bare "refused".
        Enum.GetValues<Reason>().Select(r => (int)r).Should().Equal(0, 1, 2, 3, 4);
        Enum.GetNames<Reason>().Should().Equal(
            "None", "LocalOnlyData", "LabelForbidsDestination", "UnknownDestination", "NoDecision");
        default(PrivacyDecision).ToString().Should().Contain("(NoDecision)");
    }

    [Fact]
    public void DefaultPrivacyDecision_FailsClosed()
    {
        var decision = default(PrivacyDecision);

        decision.Allowed.Should().BeFalse("default(PrivacyDecision) is not a decision anyone made");
        decision.Reason.Should().Be(Reason.NoDecision, "a refusal always names a reason, and None is reserved for allowed");
        decision.ForbiddenBy.Should().BeNull();
        decision.ToString().Should().StartWith("refused (NoDecision)");

        var allowed = PrivacyRule.Decide(PrivacyLabelSet.None, Class.Host);
        (decision == allowed).Should().BeFalse("only the rule produces an allowed decision");
        (decision != allowed).Should().BeTrue();

        // The ways a default turns up without anyone writing default(...): an unassigned array slot and the
        // parameterless struct constructor.
        var unassigned = new PrivacyDecision[2];
        unassigned.Should().OnlyContain(d => !d.Allowed && d.Reason == Reason.NoDecision);
        new PrivacyDecision().Should().Be(decision);
    }

    [Fact]
    public void Reason_IsNone_ExactlyWhenAllowed_OnEveryRowAndOnTheDefault()
    {
        foreach (var subset in Subsets)
        {
            foreach (var destination in Classes)
            {
                var decision = PrivacyRule.Decide(R(subset), destination);
                (decision.Reason == Reason.None).Should().Be(decision.Allowed, "{0} to {1}: {2}", subset, destination, decision);
            }
        }

        (default(PrivacyDecision).Reason == Reason.None).Should().BeFalse();
    }

    // ---- Equality and text ----

    [Fact]
    public void Decisions_AreEqual_ExactlyWhenReasonAndForbiddingLabelAgree()
    {
        var allowedToHost = PrivacyRule.Decide(R("local-only"), Class.Host);
        var allowedToWeb = PrivacyRule.Decide(R("no-external-model"), Class.WebSearch);
        var noWeb = PrivacyRule.Decide(R("no-web"), Class.WebSearch);
        var noWebAgain = PrivacyRule.Decide(R("no-external-model,no-web"), Class.WebSearch);
        var noModel = PrivacyRule.Decide(R("no-external-model"), Class.ExternalModel);
        var localOnly = PrivacyRule.Decide(R("local-only"), Class.WebSearch);

        (allowedToHost == allowedToWeb).Should().BeTrue("allowed decisions are equal however they were reached");
        allowedToHost.GetHashCode().Should().Be(allowedToWeb.GetHashCode());
        noWeb.Equals(noWebAgain).Should().BeTrue();
        noWeb.Equals((object)noWebAgain).Should().BeTrue();
        noWeb.GetHashCode().Should().Be(noWebAgain.GetHashCode());

        (noWeb == noModel).Should().BeFalse("same reason, different forbidding label");
        (noWeb != noModel).Should().BeTrue();
        (noWeb == localOnly).Should().BeFalse();
        (allowedToHost == localOnly).Should().BeFalse();
        noWeb.Equals(null).Should().BeFalse();
        noWeb.Equals("refused").Should().BeFalse();
    }

    [Theory]
    [InlineData("none", Class.Host, "allowed")]
    [InlineData(
        "local-only", Class.WebSearch,
        "refused (LocalOnlyData): the read set holds local-only, which nothing outside the host boundary may receive")]
    [InlineData(
        "no-external-model", Class.ExternalModel,
        "refused (LabelForbidsDestination): the read set holds no-external-model, which forbids ExternalModel destinations")]
    [InlineData(
        "no-web", Class.WebSearch,
        "refused (LabelForbidsDestination): the read set holds no-web, which forbids WebSearch destinations")]
    [InlineData(
        "no-network-export", Class.NetworkExport,
        "refused (LabelForbidsDestination): the read set holds no-network-export, which forbids NetworkExport destinations")]
    [InlineData(
        "none", Class.Unknown,
        "refused (UnknownDestination): the destination is in no known class, and such a destination receives nothing")]
    public void ToString_SaysAllowedOrNamesTheRefusal(string readSet, Class destination, string expected)
    {
        PrivacyRule.Decide(R(readSet), destination).ToString().Should().Be(expected);
    }

    private static PrivacyLabelSet R(string text)
    {
        PrivacyLabelSet.TryParse(text, out var set).Should().BeTrue(
            "'{0}' must be canonical label-set text, or the row silently tests local-only instead", text);
        return set;
    }
}
