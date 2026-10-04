using Ashlar.Abstractions.Security;
using FluentAssertions;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>
/// Decision tables for the SPEC-007 reference monitor: which reads and writes it allows, the reason it
/// gives for each refusal, the order it checks its rules in, and the sentence it hands a person.
///
/// <para><b>The flip these tables exist to catch.</b> <see cref="ReferenceMonitor.CanRead"/> and
/// <see cref="ReferenceMonitor.CanWrite"/> are the same order test with the operands in opposite roles:
/// a read is allowed when <c>data &lt;= clearance</c>, a write when <c>current &lt;= destination</c>. A
/// write check with its operands swapped still returns the right verdict for every pair of equal labels
/// and every incomparable pair. Only a strictly ordered pair tells the two apart, because exactly one
/// direction of each operation is allowed for it; the asymmetry rows are those pairs.</para>
///
/// <para>Every label is written as canonical text and parsed through <c>L</c>, which fails the row if
/// the text does not parse. A non-canonical spelling parses to <see cref="SecurityLabel.SystemHigh"/>,
/// and a row built on one would be testing the top element instead of the label it names.</para>
/// </summary>
[Trait("Category", "Certification")]
public sealed class ReferenceMonitorDecisionTests
{
    /// <summary>
    /// Nine labels spanning the order: bottom, top, a caveat-only label, two incomparable compartments
    /// at one level, and a label carrying both kinds of token.
    /// </summary>
    private static readonly string[] Lattice =
    {
        "Public",
        "Public//K:NOWEB",
        "Internal",
        "Confidential//C:ALPHA",
        "Secret//C:ALPHA",
        "Secret//C:BRAVO",
        "Secret//C:ALPHA,BRAVO//K:NOWEB",
        "TopSecret",
        "SystemHigh",
    };

    [Theory]
    [InlineData("Public", "Public")]
    [InlineData("Secret", "Secret")]
    [InlineData("Secret//C:ALPHA//K:NOWEB", "Secret//C:ALPHA//K:NOWEB")]
    [InlineData("Internal", "Public")]
    [InlineData("TopSecret", "Confidential")]
    [InlineData("Confidential//C:ALPHA", "Public")]
    [InlineData("Secret//C:ALPHA,BRAVO", "Secret//C:ALPHA")]
    [InlineData("Secret//K:NOFORN,NOWEB", "Secret//K:NOWEB")]
    [InlineData("TopSecret//C:ALPHA,BRAVO//K:NOWEB", "Confidential//C:BRAVO")]
    [InlineData("SystemHigh", "SystemHigh")]
    [InlineData("SystemHigh", "TopSecret//C:ALPHA,BRAVO//K:NOWEB")]
    [InlineData("SystemHigh", "Public")]
    public void CanRead_Allows_WhenTheClearanceDominatesTheData(string clearance, string data)
    {
        var decision = ReferenceMonitor.CanRead(L(clearance), L(data));

        decision.Allowed.Should().BeTrue("a {0} clearance dominates {1} data, but got: {2}", clearance, data, decision);
        decision.Reason.Should().Be(AccessDenialReason.None);
        decision.Detail.Should().BeEmpty("an allowed decision has nothing to explain");
    }

    [Theory]
    [InlineData("Confidential", "Secret", AccessDenialReason.LevelTooLow)]
    [InlineData("Public", "Internal", AccessDenialReason.LevelTooLow)]
    [InlineData("Secret//C:ALPHA", "TopSecret//C:ALPHA", AccessDenialReason.LevelTooLow)]
    [InlineData("Secret//C:ALPHA", "Secret//C:ALPHA,BRAVO", AccessDenialReason.MissingCompartment)]
    [InlineData("TopSecret", "Public//C:ALPHA", AccessDenialReason.MissingCompartment)]
    [InlineData("Secret", "Secret//K:NOWEB", AccessDenialReason.MissingCaveat)]
    [InlineData("TopSecret//C:ALPHA//K:NOFORN", "Public//K:NOWEB", AccessDenialReason.MissingCaveat)]
    [InlineData("TopSecret//C:ALPHA//K:NOWEB", "SystemHigh", AccessDenialReason.SystemHighData)]
    public void CanRead_Refuses_WithTheReasonOfTheRuleThatFailed(
        string clearance, string data, AccessDenialReason reason)
    {
        var decision = ReferenceMonitor.CanRead(L(clearance), L(data));

        decision.Allowed.Should().BeFalse("a {0} clearance does not dominate {1} data", clearance, data);
        decision.Reason.Should().Be(reason);
        decision.Detail.Should().NotBeNullOrWhiteSpace("a refusal is explained, never silent");
    }

    [Fact]
    public void CanRead_Refusals_NameWhatIsMissing()
    {
        var compartment = ReferenceMonitor.CanRead(L("Secret//C:ALPHA"), L("Secret//C:ALPHA,BRAVO"));
        compartment.Reason.Should().Be(AccessDenialReason.MissingCompartment);
        compartment.Detail.Should().Contain("BRAVO")
            .And.NotContain("ALPHA", "the clearance holds ALPHA, so naming it would send the reader to the wrong token");

        var caveat = ReferenceMonitor.CanRead(L("Secret"), L("Secret//K:NOWEB"));
        caveat.Reason.Should().Be(AccessDenialReason.MissingCaveat);
        caveat.Detail.Should().Contain("NOWEB");

        var level = ReferenceMonitor.CanRead(L("Confidential"), L("Secret"));
        level.Reason.Should().Be(AccessDenialReason.LevelTooLow);
        level.Detail.Should().Contain("Confidential").And.Contain("Secret");

        var systemHigh = ReferenceMonitor.CanRead(L("TopSecret//C:ALPHA//K:NOWEB"), SecurityLabel.SystemHigh);
        systemHigh.Reason.Should().Be(AccessDenialReason.SystemHighData);
        systemHigh.Detail.Should().Contain("SystemHigh");
    }

    [Fact]
    public void CanRead_UnparseableDataLabel_IsReadableOnlyWithASystemHighClearance()
    {
        // The fail-closed path a caller actually takes: label text that is not canonical (a lowercase
        // token here) parses to SystemHigh, and no clearance short of SystemHigh can read it.
        var data = SecurityLabel.ParseOrSystemHigh("Secret//C:alpha");

        ReferenceMonitor.CanRead(L("TopSecret//C:ALPHA//K:NOWEB"), data).Reason
            .Should().Be(AccessDenialReason.SystemHighData);
        ReferenceMonitor.CanRead(SecurityLabel.SystemHigh, data).Allowed.Should().BeTrue();
    }

    [Theory]
    [InlineData("Public", "Public")]
    [InlineData("Secret", "Secret")]
    [InlineData("Internal", "Secret")]
    [InlineData("Public", "TopSecret//C:ALPHA//K:NOWEB")]
    [InlineData("Secret//C:ALPHA", "Secret//C:ALPHA,BRAVO")]
    [InlineData("Public//K:NOWEB", "Confidential//K:NOFORN,NOWEB")]
    [InlineData("Public", "SystemHigh")]
    [InlineData("TopSecret//C:ALPHA,BRAVO//K:NOWEB", "SystemHigh")]
    [InlineData("SystemHigh", "SystemHigh")]
    public void CanWrite_Allows_WhenTheDestinationDominatesTheCurrentMark(string current, string destination)
    {
        var decision = ReferenceMonitor.CanWrite(L(current), L(destination));

        decision.Allowed.Should().BeTrue(
            "a {0} destination dominates a {1} mark, but got: {2}", destination, current, decision);
        decision.Reason.Should().Be(AccessDenialReason.None);
        decision.Detail.Should().BeEmpty("an allowed decision has nothing to explain");
    }

    [Theory]
    [InlineData("Secret", "Internal", AccessDenialReason.LevelTooLow)]
    [InlineData("TopSecret//C:ALPHA", "Secret//C:ALPHA", AccessDenialReason.LevelTooLow)]
    [InlineData("Secret//C:ALPHA", "Secret", AccessDenialReason.MissingCompartment)]
    [InlineData("Public//C:ALPHA", "TopSecret//C:BRAVO", AccessDenialReason.MissingCompartment)]
    [InlineData("Public//K:NOWEB", "Public", AccessDenialReason.MissingCaveat)]
    [InlineData("Secret//C:ALPHA//K:NOWEB", "TopSecret//C:ALPHA,BRAVO", AccessDenialReason.MissingCaveat)]
    [InlineData("SystemHigh", "TopSecret", AccessDenialReason.SystemHighData)]
    [InlineData("SystemHigh", "TopSecret//C:ALPHA,BRAVO//K:NOFORN,NOWEB", AccessDenialReason.SystemHighData)]
    public void CanWrite_Refuses_WithTheReasonOfTheRuleThatFailed(
        string current, string destination, AccessDenialReason reason)
    {
        var decision = ReferenceMonitor.CanWrite(L(current), L(destination));

        decision.Allowed.Should().BeFalse("a {0} destination does not dominate a {1} mark", destination, current);
        decision.Reason.Should().Be(reason);
        decision.Detail.Should().NotBeNullOrWhiteSpace("a refusal is explained, never silent");
    }

    [Fact]
    public void CanWrite_Refusals_NameWhatIsMissing()
    {
        var compartment = ReferenceMonitor.CanWrite(L("Secret//C:ALPHA"), L("Secret"));
        compartment.Reason.Should().Be(AccessDenialReason.MissingCompartment);
        compartment.Detail.Should().Contain("ALPHA");

        var caveat = ReferenceMonitor.CanWrite(L("Public//K:NOWEB"), L("Public"));
        caveat.Reason.Should().Be(AccessDenialReason.MissingCaveat);
        caveat.Detail.Should().Contain("NOWEB");

        var level = ReferenceMonitor.CanWrite(L("Secret"), L("Internal"));
        level.Reason.Should().Be(AccessDenialReason.LevelTooLow);
        level.Detail.Should().Contain("Secret").And.Contain("Internal");
    }

    [Theory]
    [InlineData("Internal", "Secret")]
    [InlineData("Secret//C:ALPHA", "Secret//C:ALPHA,BRAVO")]
    [InlineData("Public", "Public//K:NOWEB")]
    [InlineData("Confidential//C:ALPHA", "TopSecret//C:ALPHA//K:NOWEB")]
    [InlineData("Public", "SystemHigh")]
    [InlineData("TopSecret//C:ALPHA,BRAVO//K:NOWEB", "SystemHigh")]
    public void StrictlyOrderedPair_IsReadDownAndWrittenUp_NeverTheReverse(string lowerText, string higherText)
    {
        var lower = L(lowerText);
        var higher = L(higherText);
        higher.Dominates(lower).Should().BeTrue("the row must be ordered: {0} <= {1}", lowerText, higherText);
        lower.Dominates(higher).Should().BeFalse("the row must be strictly ordered: {0} < {1}", lowerText, higherText);

        ReferenceMonitor.CanRead(higher, lower).Allowed
            .Should().BeTrue("a {0} clearance reads {1} data", higherText, lowerText);
        ReferenceMonitor.CanWrite(lower, higher).Allowed
            .Should().BeTrue("a {0} mark writes up to {1}", lowerText, higherText);

        var readUp = ReferenceMonitor.CanRead(lower, higher);
        var writeDown = ReferenceMonitor.CanWrite(higher, lower);
        readUp.Allowed.Should().BeFalse("no read up: a {0} clearance may not read {1} data", lowerText, higherText);
        writeDown.Allowed.Should().BeFalse("no write down: a {0} mark may not write to {1}", higherText, lowerText);
        writeDown.Reason.Should().Be(readUp.Reason, "both refusals test the same order, {0} <= {1}", higherText, lowerText);
    }

    [Theory]
    [InlineData("Secret//C:ALPHA", "Secret//C:BRAVO")]
    [InlineData("TopSecret", "Public//K:NOWEB")]
    [InlineData("Secret//C:ALPHA", "Confidential//C:ALPHA,BRAVO")]
    public void IncomparablePair_NeitherReadsNorWritesTheOther(string firstText, string secondText)
    {
        var first = L(firstText);
        var second = L(secondText);

        ReferenceMonitor.CanRead(first, second).Allowed.Should().BeFalse();
        ReferenceMonitor.CanRead(second, first).Allowed.Should().BeFalse();
        ReferenceMonitor.CanWrite(first, second).Allowed.Should().BeFalse();
        ReferenceMonitor.CanWrite(second, first).Allowed.Should().BeFalse();
    }

    [Fact]
    public void EveryDecision_AgreesWithDominance_AcrossASmallLattice()
    {
        foreach (var sourceText in Lattice)
        {
            foreach (var receiverText in Lattice)
            {
                var source = L(sourceText);
                var receiver = L(receiverText);
                var dominated = receiver.Dominates(source);

                var read = ReferenceMonitor.CanRead(receiver, source);
                var write = ReferenceMonitor.CanWrite(source, receiver);

                read.Allowed.Should().Be(
                    dominated,
                    "a {0} clearance reading {1} data follows the order, but got: {2}",
                    receiverText,
                    sourceText,
                    read);
                write.Allowed.Should().Be(
                    dominated,
                    "a {0} mark writing to {1} follows the order, but got: {2}",
                    sourceText,
                    receiverText,
                    write);
                write.Reason.Should().Be(read.Reason, "both decisions test {0} <= {1}", sourceText, receiverText);
                (read.Reason == AccessDenialReason.None).Should().Be(
                    read.Allowed, "a refusal always names its reason and an allowance never does: {0}", read);
            }
        }
    }

    [Theory]
    // Level too low and a compartment missing.
    [InlineData("Confidential", "Secret//C:ALPHA", AccessDenialReason.LevelTooLow)]
    // Level too low and a caveat missing.
    [InlineData("Internal//C:ALPHA", "Secret//K:NOWEB", AccessDenialReason.LevelTooLow)]
    // Level too low, a compartment missing and a caveat missing.
    [InlineData("Public", "TopSecret//C:ALPHA//K:NOWEB", AccessDenialReason.LevelTooLow)]
    // A compartment missing and a caveat missing.
    [InlineData("Secret", "Secret//C:ALPHA//K:NOWEB", AccessDenialReason.MissingCompartment)]
    [InlineData("TopSecret//K:NOFORN", "Confidential//C:ALPHA,BRAVO//K:NOWEB", AccessDenialReason.MissingCompartment)]
    // SystemHigh data reports TopSecret, so it is also above every level these clearances hold.
    [InlineData("Public", "SystemHigh", AccessDenialReason.SystemHighData)]
    [InlineData("Internal//C:ALPHA//K:NOWEB", "SystemHigh", AccessDenialReason.SystemHighData)]
    public void CanRead_WhenSeveralRulesFail_ReportsTheFirstInPrecedenceOrder(
        string clearance, string data, AccessDenialReason reason)
    {
        ReferenceMonitor.CanRead(L(clearance), L(data)).Reason.Should().Be(
            reason, "the order is SystemHighData, LevelTooLow, MissingCompartment, MissingCaveat");
    }

    [Theory]
    [InlineData("Secret//C:ALPHA//K:NOWEB", "Internal", AccessDenialReason.LevelTooLow)]
    [InlineData("TopSecret//C:BRAVO", "Secret//C:ALPHA", AccessDenialReason.LevelTooLow)]
    [InlineData("Secret//C:ALPHA//K:NOWEB", "Secret", AccessDenialReason.MissingCompartment)]
    [InlineData("Public//C:ALPHA//K:NOWEB", "TopSecret//K:NOFORN", AccessDenialReason.MissingCompartment)]
    [InlineData("SystemHigh", "Public", AccessDenialReason.SystemHighData)]
    public void CanWrite_WhenSeveralRulesFail_ReportsTheFirstInPrecedenceOrder(
        string current, string destination, AccessDenialReason reason)
    {
        ReferenceMonitor.CanWrite(L(current), L(destination)).Reason.Should().Be(
            reason, "the order is SystemHighData, LevelTooLow, MissingCompartment, MissingCaveat");
    }

    [Fact]
    public void Detail_NamesOnlyTheRuleItReports()
    {
        var levelFirst = ReferenceMonitor.CanRead(L("Confidential"), L("Secret//C:ALPHA//K:NOWEB"));
        levelFirst.Reason.Should().Be(AccessDenialReason.LevelTooLow);
        levelFirst.Detail.Should().NotContain("ALPHA").And.NotContain("NOWEB");

        var compartmentFirst = ReferenceMonitor.CanRead(L("Secret"), L("Secret//C:ALPHA//K:NOWEB"));
        compartmentFirst.Reason.Should().Be(AccessDenialReason.MissingCompartment);
        compartmentFirst.Detail.Should().Contain("ALPHA").And.NotContain("NOWEB");
    }

    [Fact]
    public void Detail_ListsEveryMissingTokenOfTheReportedKind_CommaSeparatedInOrder()
    {
        // Built through the constructor in reverse order: the detail follows the label's canonical
        // order, not the order a caller happened to supply the tokens in.
        var data = new SecurityLabel(SecurityLevel.Secret, new[] { "CHARLIE", "BRAVO", "ALPHA" });
        var clearance = new SecurityLabel(SecurityLevel.Secret, new[] { "CHARLIE" });

        var decision = ReferenceMonitor.CanRead(clearance, data);

        decision.Reason.Should().Be(AccessDenialReason.MissingCompartment);
        decision.Detail.Should().Contain("ALPHA,BRAVO").And.NotContain("CHARLIE");
    }

    [Fact]
    public void Detail_OrdersTokensOrdinally()
    {
        // Ordinal order puts '-' before digits, digits before letters, and '_' after letters. A
        // culture-aware sort places '_' before letters, so it would print B_1 ahead of BA.
        var current = new SecurityLabel(SecurityLevel.Public, caveats: new[] { "B_1", "BA", "B1", "B-1" });

        var decision = ReferenceMonitor.CanWrite(current, SecurityLabel.Public);

        decision.Reason.Should().Be(AccessDenialReason.MissingCaveat);
        decision.Detail.Should().Contain("B-1,B1,BA,B_1");
    }

    [Theory]
    [InlineData("Internal", "Confidential", "clearance level Internal is below data level Confidential")]
    [InlineData("Secret//C:ALPHA", "Secret//C:ALPHA,BRAVO", "clearance lacks compartment(s): BRAVO")]
    [InlineData("Secret", "Secret//K:NOFORN,NOWEB", "clearance lacks caveat(s): NOFORN,NOWEB")]
    [InlineData(
        "TopSecret",
        "SystemHigh",
        "the data is SystemHigh (for example unlabelled or unparseable), which only a SystemHigh clearance may read")]
    public void CanRead_Detail_IsTheSentenceAPersonReads(string clearance, string data, string expected)
    {
        ReferenceMonitor.CanRead(L(clearance), L(data)).Detail.Should().Be(expected);
    }

    [Theory]
    [InlineData("Confidential", "Internal", "destination level Internal is below current level Confidential")]
    [InlineData("Secret//C:ALPHA", "Secret", "destination lacks compartment(s): ALPHA")]
    [InlineData("Public//K:NOFORN,NOWEB", "Public", "destination lacks caveat(s): NOFORN,NOWEB")]
    [InlineData(
        "SystemHigh",
        "TopSecret",
        "the subject's current label is SystemHigh, which only a SystemHigh destination may receive")]
    public void CanWrite_Detail_IsTheSentenceAPersonReads(string current, string destination, string expected)
    {
        // A write refusal names the destination, never a clearance: the reader has to know which side
        // of the write to change.
        ReferenceMonitor.CanWrite(L(current), L(destination)).Detail.Should().Be(expected);
    }

    [Fact]
    public void CanRead_NullArgument_ThrowsNamingTheParameter()
    {
        var label = L("Secret");

        Action nullClearance = () => _ = ReferenceMonitor.CanRead(null!, label);
        Action nullData = () => _ = ReferenceMonitor.CanRead(label, null!);

        nullClearance.Should().Throw<ArgumentNullException>().WithParameterName("clearance");
        nullData.Should().Throw<ArgumentNullException>().WithParameterName("data");
    }

    [Fact]
    public void CanWrite_NullArgument_ThrowsNamingTheParameter()
    {
        var label = L("Secret");

        Action nullCurrent = () => _ = ReferenceMonitor.CanWrite(null!, label);
        Action nullDestination = () => _ = ReferenceMonitor.CanWrite(label, null!);

        nullCurrent.Should().Throw<ArgumentNullException>().WithParameterName("current");
        nullDestination.Should().Throw<ArgumentNullException>().WithParameterName("destination");
    }

    [Fact]
    public void DefaultAccessDecision_FailsClosed()
    {
        var decision = default(AccessDecision);

        decision.Allowed.Should().BeFalse("default(AccessDecision) is not a decision anyone made");
        decision.Reason.Should().Be(
            AccessDenialReason.NoDecision, "a refusal always names a reason, and None is reserved for allowed");
        decision.Detail.Should().NotBeNullOrWhiteSpace("even the decision nobody made explains why it refuses");
        decision.ToString().Should().StartWith("refused (NoDecision)");

        var allowed = ReferenceMonitor.CanRead(L("Secret"), L("Secret"));
        (decision == allowed).Should().BeFalse("only the reference monitor produces an allowed decision");
        (decision != allowed).Should().BeTrue();

        // The ways a default turns up without anyone writing default(...): an unassigned array slot and
        // the parameterless struct constructor.
        var unassigned = new AccessDecision[2];
        unassigned.Should().OnlyContain(d => !d.Allowed);
        new AccessDecision().Should().Be(decision);
    }

    [Fact]
    public void AccessDenialReason_ValuesArePinned_AndOnlyAppended()
    {
        // Stored reasons keep their meaning only if values never move; a new reason also needs an arm in
        // AccessDecision.ToString, or it prints as "Unknown".
        Enum.GetValues<AccessDenialReason>().Select(r => (int)r).Should().Equal(0, 1, 2, 3, 4, 5);
        Enum.GetNames<AccessDenialReason>().Should().Equal(
            "None", "LevelTooLow", "MissingCompartment", "MissingCaveat", "SystemHighData", "NoDecision");
        default(AccessDecision).ToString().Should().Contain("(NoDecision)");
    }

    [Fact]
    public void AccessDecision_IdenticalRefusals_AreEqual()
    {
        // Two separate calls, so the two Detail strings are distinct instances with the same text.
        var first = ReferenceMonitor.CanRead(L("Secret//C:ALPHA"), L("Secret//C:ALPHA,BRAVO"));
        var second = ReferenceMonitor.CanRead(L("Secret//C:ALPHA"), L("Secret//C:ALPHA,BRAVO"));

        first.Equals(second).Should().BeTrue();
        first.Equals((object)second).Should().BeTrue();
        (first == second).Should().BeTrue();
        (first != second).Should().BeFalse();
        first.GetHashCode().Should().Be(second.GetHashCode());
    }

    [Fact]
    public void AccessDecision_AllowedDecisions_AreEqualHoweverTheyWereReached()
    {
        var read = ReferenceMonitor.CanRead(L("Secret"), L("Internal"));
        var write = ReferenceMonitor.CanWrite(L("Public"), L("Secret//C:ALPHA"));

        (read == write).Should().BeTrue();
        read.GetHashCode().Should().Be(write.GetHashCode());
    }

    [Fact]
    public void AccessDecision_AllowedAndRefused_AreNotEqual()
    {
        var allowed = ReferenceMonitor.CanRead(L("Secret"), L("Secret"));
        var refused = ReferenceMonitor.CanRead(L("Secret"), L("Secret//C:ALPHA"));

        allowed.Equals(refused).Should().BeFalse();
        allowed.Equals((object)refused).Should().BeFalse();
        (allowed == refused).Should().BeFalse();
        (allowed != refused).Should().BeTrue();
    }

    [Fact]
    public void AccessDecision_RefusalsDifferingInDetailOrReason_AreNotEqual()
    {
        var lacksAlpha = ReferenceMonitor.CanRead(L("Secret"), L("Secret//C:ALPHA"));
        var lacksBravo = ReferenceMonitor.CanRead(L("Secret"), L("Secret//C:BRAVO"));
        lacksAlpha.Reason.Should().Be(lacksBravo.Reason, "the pair must differ only in its detail");
        (lacksAlpha == lacksBravo).Should().BeFalse();
        (lacksAlpha != lacksBravo).Should().BeTrue();

        var levelTooLow = ReferenceMonitor.CanRead(L("Internal"), L("Secret"));
        (lacksAlpha == levelTooLow).Should().BeFalse();

        lacksAlpha.Equals(null).Should().BeFalse();
        lacksAlpha.Equals("refused").Should().BeFalse();
    }

    [Fact]
    public void AccessDecision_ToString_SaysAllowedOrNamesTheRefusal()
    {
        ReferenceMonitor.CanRead(L("Secret"), L("Internal")).ToString().Should().Be("allowed");

        var refused = ReferenceMonitor.CanRead(L("Secret//C:ALPHA"), L("Secret//C:ALPHA,BRAVO"));
        refused.ToString().Should().StartWith("refused (MissingCompartment)")
            .And.Be("refused (MissingCompartment): " + refused.Detail);
    }

    [Theory]
    [InlineData("Internal", "Secret", "refused (LevelTooLow): ")]
    [InlineData("Secret", "Secret//C:ALPHA", "refused (MissingCompartment): ")]
    [InlineData("Secret", "Secret//K:NOWEB", "refused (MissingCaveat): ")]
    [InlineData("TopSecret", "SystemHigh", "refused (SystemHighData): ")]
    public void AccessDecision_ToString_NamesEveryRefusalReason(string clearance, string data, string prefix)
    {
        var decision = ReferenceMonitor.CanRead(L(clearance), L(data));

        decision.ToString().Should().Be(prefix + decision.Detail);
    }

    private static SecurityLabel L(string text)
    {
        SecurityLabel.TryParse(text, out var label).Should().BeTrue(
            "'{0}' must be canonical label text, or the row silently tests SystemHigh instead", text);
        return label;
    }
}
