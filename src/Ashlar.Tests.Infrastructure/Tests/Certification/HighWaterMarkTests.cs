using Ashlar.Abstractions.Security;
using FluentAssertions;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>
/// The SPEC-007 high-water mark: a session's running join of every label it has read, against which the
/// star property (no write down) is decided.
///
/// <para>What is pinned: the mark starts at its floor (<see cref="SecurityLabel.Public"/> by default); it
/// only rises, to the join of what was read and never to the last label alone; once it reaches
/// <see cref="SecurityLabel.SystemHigh"/> nothing short of a SystemHigh destination may receive a
/// write; and <see cref="HighWaterMark.CanWriteTo"/> is exactly
/// <see cref="ReferenceMonitor.CanWrite"/> of the current mark, with the operands in that order, and
/// does not move the mark.</para>
///
/// <para>Labels are written as canonical text and parsed through <c>L</c>, which fails the row if the
/// text does not parse, for the reason given in <see cref="ReferenceMonitorDecisionTests"/>.</para>
/// </summary>
[Trait("Category", "Certification")]
public sealed class HighWaterMarkTests
{
    [Fact]
    public void SpecExample_AfterReadingSecretAlpha_OnlyADestinationHoldingAlphaMayReceive()
    {
        var mark = new HighWaterMark();
        mark.Observe(L("Secret//C:ALPHA"));

        var toSecret = mark.CanWriteTo(L("Secret"));
        toSecret.Allowed.Should().BeFalse("plain Secret lacks ALPHA, so the write would move ALPHA data down");
        toSecret.Reason.Should().Be(AccessDenialReason.MissingCompartment);
        toSecret.Detail.Should().Contain("ALPHA");

        var toTopSecretAlpha = mark.CanWriteTo(L("TopSecret//C:ALPHA"));
        toTopSecretAlpha.Allowed.Should().BeTrue("TopSecret//C:ALPHA dominates the mark, but got: {0}", toTopSecretAlpha);
    }

    [Fact]
    public void NewMark_StartsAtPublic()
    {
        new HighWaterMark().Current.Should().Be(SecurityLabel.Public);
    }

    [Theory]
    [InlineData("Public")]
    [InlineData("Public//K:NOWEB")]
    [InlineData("Internal")]
    [InlineData("Secret//C:ALPHA")]
    [InlineData("TopSecret//C:ALPHA,BRAVO//K:NOWEB")]
    [InlineData("SystemHigh")]
    public void NewMark_MayWriteAnywhere(string destination)
    {
        var decision = new HighWaterMark().CanWriteTo(L(destination));

        decision.Allowed.Should().BeTrue("a session that has read nothing has nothing to leak, but got: {0}", decision);
    }

    [Fact]
    public void MarkWithAFloor_StartsThere()
    {
        var floor = L("Confidential//C:ALPHA");
        var mark = new HighWaterMark(floor);

        mark.Current.Should().Be(floor);
        mark.CanWriteTo(L("Confidential")).Reason.Should().Be(AccessDenialReason.MissingCompartment);
        mark.CanWriteTo(L("Internal//C:ALPHA")).Reason.Should().Be(AccessDenialReason.LevelTooLow);
        mark.CanWriteTo(L("Confidential//C:ALPHA")).Allowed.Should().BeTrue();

        mark.Observe(SecurityLabel.Public);
        mark.Current.Should().Be(floor, "observing the bottom leaves a mark where it is");
    }

    [Theory]
    [InlineData("Public")]
    [InlineData("Internal")]
    [InlineData("Secret")]
    [InlineData("Confidential//C:ALPHA")]
    [InlineData("Secret//C:ALPHA")]
    public void ObservingALabelTheMarkAlreadyDominates_LeavesItUnchanged(string lower)
    {
        var high = L("Secret//C:ALPHA");
        high.Dominates(L(lower)).Should().BeTrue("the row must be at or below the mark: {0}", lower);

        var mark = new HighWaterMark();
        mark.Observe(high);
        mark.Observe(L(lower));

        mark.Current.Should().Be(high, "the mark only rises, so reading {0} after {1} does not lower it", lower, high);
    }

    [Fact]
    public void ObservingIncomparableLabels_GivesTheirJoin()
    {
        var mark = new HighWaterMark();
        mark.Observe(L("Secret//C:ALPHA"));
        mark.Observe(L("Confidential//C:BRAVO//K:NOWEB"));

        mark.Current.Should().Be(L("Secret//C:ALPHA,BRAVO//K:NOWEB"));
        mark.Current.ToString().Should().Be("Secret//C:ALPHA,BRAVO//K:NOWEB");

        // Neither label read is enough on its own to receive what the session now holds.
        mark.CanWriteTo(L("Secret//C:ALPHA")).Reason.Should().Be(AccessDenialReason.MissingCompartment);
        mark.CanWriteTo(L("Secret//C:ALPHA,BRAVO")).Reason.Should().Be(AccessDenialReason.MissingCaveat);
        mark.CanWriteTo(L("Confidential//C:ALPHA,BRAVO//K:NOWEB")).Reason.Should().Be(AccessDenialReason.LevelTooLow);
        mark.CanWriteTo(L("Secret//C:ALPHA,BRAVO//K:NOWEB")).Allowed.Should().BeTrue();
    }

    [Fact]
    public void EachObservation_RaisesOrKeepsTheMark_AndTheMarkCoversEverythingRead()
    {
        var reads = new[] { "Internal", "Public//K:NOWEB", "Secret//C:ALPHA", "Confidential", "Secret//C:BRAVO", "Public" };
        var mark = new HighWaterMark();
        foreach (var text in reads)
        {
            var before = mark.Current;
            var read = L(text);

            mark.Observe(read);

            mark.Current.Dominates(before).Should().BeTrue("the mark never falls, including after reading {0}", text);
            mark.Current.Dominates(read).Should().BeTrue("the mark covers every label read, including {0}", text);
        }

        mark.Current.Should().Be(L("Secret//C:ALPHA,BRAVO//K:NOWEB"));
    }

    [Fact]
    public void TheMark_DoesNotDependOnTheOrderOfObservation()
    {
        var labels = new[] { "Confidential//C:BRAVO//K:NOWEB", "Internal", "Secret//C:ALPHA", "Public//K:NOFORN" };

        var forward = new HighWaterMark();
        foreach (var text in labels)
            forward.Observe(L(text));

        var backward = new HighWaterMark();
        for (var i = labels.Length - 1; i >= 0; i--)
            backward.Observe(L(labels[i]));

        forward.Current.Should().Be(backward.Current);
        forward.Current.Should().Be(L("Secret//C:ALPHA,BRAVO//K:NOFORN,NOWEB"));
    }

    [Fact]
    public void ObservingSystemHigh_PinsTheMarkAtTheTop()
    {
        var mark = new HighWaterMark();
        mark.Observe(L("Secret//C:ALPHA"));
        mark.Observe(SecurityLabel.SystemHigh);

        mark.Current.IsSystemHigh.Should().BeTrue();
        mark.Current.Should().Be(SecurityLabel.SystemHigh);

        mark.Observe(L("TopSecret//C:BRAVO//K:NOWEB"));
        mark.Current.Should().Be(SecurityLabel.SystemHigh, "SystemHigh absorbs every later observation");
    }

    [Theory]
    [InlineData("Public")]
    [InlineData("Secret//C:ALPHA")]
    [InlineData("TopSecret")]
    [InlineData("TopSecret//C:ALPHA,BRAVO//K:NOFORN,NOWEB")]
    public void AfterSystemHigh_OnlyASystemHighDestinationIsAllowed(string destination)
    {
        var mark = new HighWaterMark();
        mark.Observe(SecurityLabel.SystemHigh);

        var decision = mark.CanWriteTo(L(destination));
        decision.Allowed.Should().BeFalse("a session that has read SystemHigh data may not write it to {0}", destination);
        decision.Reason.Should().Be(AccessDenialReason.SystemHighData);

        mark.CanWriteTo(SecurityLabel.SystemHigh).Allowed.Should().BeTrue();
    }

    [Fact]
    public void ObservingAnUnparseableLabel_FailsClosedToSystemHigh()
    {
        // Label text that is not canonical (a lowercase level name here) parses to SystemHigh, so a
        // session that reads data so labelled can no longer write anywhere below the top.
        var mark = new HighWaterMark();
        mark.Observe(SecurityLabel.ParseOrSystemHigh("secret//C:ALPHA"));

        mark.Current.Should().Be(SecurityLabel.SystemHigh);
        mark.CanWriteTo(L("TopSecret//C:ALPHA")).Reason.Should().Be(AccessDenialReason.SystemHighData);
    }

    [Theory]
    [InlineData("Public")]
    [InlineData("Internal")]
    [InlineData("Secret")]
    [InlineData("Secret//C:ALPHA")]
    [InlineData("Secret//C:ALPHA,BRAVO")]
    [InlineData("Secret//C:ALPHA,BRAVO//K:NOWEB")]
    [InlineData("TopSecret//C:ALPHA,BRAVO//K:NOWEB")]
    [InlineData("SystemHigh")]
    public void CanWriteTo_IsTheReferenceMonitorsWriteDecisionOnTheCurrentMark(string destination)
    {
        // The mark ends at Secret//C:ALPHA,BRAVO//K:NOWEB, so the rows span every refusal short of
        // SystemHighData as well as allowed writes. Equality is structural, so the reason and the
        // detail must match too, not only the verdict.
        var mark = new HighWaterMark(L("Internal//K:NOWEB"));
        mark.Observe(L("Secret//C:ALPHA"));
        mark.Observe(L("Confidential//C:BRAVO"));
        var target = L(destination);

        mark.CanWriteTo(target).Should().Be(ReferenceMonitor.CanWrite(mark.Current, target));
    }

    [Fact]
    public void CanWriteTo_DoesNotMoveTheMark()
    {
        var mark = new HighWaterMark();
        mark.Observe(L("Confidential//C:ALPHA"));
        var before = mark.Current;

        _ = mark.CanWriteTo(L("TopSecret//C:ALPHA,BRAVO//K:NOWEB"));
        _ = mark.CanWriteTo(L("Public"));
        _ = mark.CanWriteTo(SecurityLabel.SystemHigh);

        mark.Current.Should().Be(before, "deciding a write is not reading");
    }

    [Fact]
    public void NullArguments_ThrowNamingTheParameter()
    {
        var floor = L("Secret");
        var mark = new HighWaterMark(floor);

        Action nullFloor = () => _ = new HighWaterMark(null!);
        Action nullObservation = () => mark.Observe(null!);
        Action nullDestination = () => _ = mark.CanWriteTo(null!);

        nullFloor.Should().Throw<ArgumentNullException>().WithParameterName("floor");
        nullObservation.Should().Throw<ArgumentNullException>().WithParameterName("label");
        nullDestination.Should().Throw<ArgumentNullException>().WithParameterName("destination");

        mark.Current.Should().Be(floor, "a refused observation leaves the mark where it was");
    }

    private static SecurityLabel L(string text)
    {
        SecurityLabel.TryParse(text, out var label).Should().BeTrue(
            "'{0}' must be canonical label text, or the row silently tests SystemHigh instead", text);
        return label;
    }
}
