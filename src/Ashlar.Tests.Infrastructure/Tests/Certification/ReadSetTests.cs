using System.Collections.Concurrent;
using System.Diagnostics;
using Ashlar.Abstractions.Security;
using Ashlar.Abstractions.Security.Egress;
using FluentAssertions;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>
/// SPEC-010 §3.4: a subject's read set, the union of every label set it has observed, against which a send is decided.
///
/// <para>What is pinned: a read set starts at its floor (<c>none</c> by default); it only grows, to the union of what
/// was read, never to the last set alone and never losing a label; observing a set it already holds changes nothing;
/// once it holds <c>local-only</c>, only the host may receive a send; and concurrent observations are never lost, the
/// property <c>HighWaterMarkTests.ConcurrentObservations_AreNeverLost</c> pins for the mark it replaces.</para>
///
/// <para>Sets are written as canonical text and parsed through <c>R</c>, which fails the row if the text does not
/// parse, for the reason given in <see cref="PrivacyRuleDecisionTests"/>.</para>
/// </summary>
[Trait("Category", "Certification")]
public sealed class ReadSetTests
{
    private static readonly PrivacyLabel[] FourLabels =
        [PrivacyLabel.LocalOnly, PrivacyLabel.NoExternalModel, PrivacyLabel.NoWeb, PrivacyLabel.NoNetworkExport];

    [Fact]
    public void NewReadSet_StartsAtNone()
    {
        new ReadSet().Current.Should().Be(PrivacyLabelSet.None);
    }

    [Fact]
    public void AReadSetWithAFloor_StartsThere_AndNothingLowersIt()
    {
        var floor = R("local-only,no-web");
        var readSet = new ReadSet(floor);

        readSet.Current.Should().Be(floor);
        readSet.Observe(PrivacyLabelSet.None);
        readSet.Observe(R("no-web"));
        readSet.Current.Should().Be(floor, "observing what the set already holds leaves it where it is");
    }

    [Fact]
    public void Observe_GivesTheUnionOfEverythingObserved()
    {
        var readSet = new ReadSet();
        readSet.Observe(R("no-web"));
        readSet.Observe(R("no-external-model"));

        readSet.Current.Should().Be(R("no-external-model,no-web"));
        readSet.Current.ToString().Should().Be("no-external-model,no-web");
    }

    [Fact]
    public void EachObservation_AddsWhatWasRead_AndRemovesNothing()
    {
        var reads = new[] { "none", "no-web", "no-network-export,no-web", "none", "no-external-model", "local-only", "no-web" };
        var readSet = new ReadSet();
        foreach (var text in reads)
        {
            var before = readSet.Current;
            var read = R(text);

            readSet.Observe(read);

            readSet.Current.Should().Be(before.Union(read), "after reading {0} the set is what it held plus what was read", text);
            before.Labels.All(readSet.Current.Contains).Should().BeTrue("the set never loses a label, here after {0}", text);
            read.Labels.All(readSet.Current.Contains).Should().BeTrue("the set holds everything read, here {0}", text);
        }

        readSet.Current.Should().Be(R("local-only,no-external-model,no-network-export,no-web"));
    }

    [Fact]
    public void ObservingTheSameLabelsAgain_KeepsThem()
    {
        var readSet = new ReadSet();
        readSet.Observe(R("no-external-model,no-web"));
        readSet.Observe(R("no-external-model,no-web"));
        readSet.Observe(R("no-web"));

        readSet.Current.Should().Be(R("no-external-model,no-web"), "a label read twice is still held");
    }

    [Fact]
    public void TheReadSet_DoesNotDependOnTheOrderOfObservation()
    {
        var sets = new[] { "no-web", "none", "no-network-export", "no-external-model,no-web" };

        var forward = new ReadSet();
        foreach (var text in sets)
            forward.Observe(R(text));

        var backward = new ReadSet();
        for (var i = sets.Length - 1; i >= 0; i--)
            backward.Observe(R(sets[i]));

        forward.Current.Should().Be(backward.Current);
        forward.Current.Should().Be(R("no-external-model,no-network-export,no-web"));
    }

    [Fact]
    public void AfterObservingLocalOnly_OnlyTheHostMayReceive()
    {
        var readSet = new ReadSet();
        readSet.Observe(R("no-web"));
        readSet.Observe(PrivacyLabelSet.LocalOnly);
        readSet.Observe(R("no-external-model"));

        readSet.Current.Contains(PrivacyLabel.LocalOnly).Should().BeTrue("there is no downgrade");
        PrivacyRule.Decide(readSet.Current, EgressDestinationClass.Host).Allowed.Should().BeTrue();
        foreach (var destination in new[]
                 {
                     EgressDestinationClass.ExternalModel, EgressDestinationClass.WebSearch,
                     EgressDestinationClass.NetworkExport, EgressDestinationClass.Unknown,
                 })
        {
            PrivacyRule.Decide(readSet.Current, destination).Reason.Should().Be(EgressRefusalReason.LocalOnlyData);
        }
    }

    [Fact]
    public void ObservingUnparseableLabelText_FailsClosedToLocalOnly()
    {
        // Label text that is not canonical (unsorted here) parses to local-only, so a session that reads data so
        // labelled can no longer send anywhere outside the host boundary.
        var readSet = new ReadSet();
        readSet.Observe(PrivacyLabelSet.ParseOrLocalOnly("no-web,no-external-model"));

        readSet.Current.Should().Be(PrivacyLabelSet.LocalOnly);
        PrivacyRule.Decide(readSet.Current, EgressDestinationClass.WebSearch).Reason.Should().Be(EgressRefusalReason.LocalOnlyData);
    }

    [Fact]
    public void NullArguments_ThrowNamingTheParameter()
    {
        var floor = R("no-web");
        var readSet = new ReadSet(floor);

        Action nullFloor = () => _ = new ReadSet(null!);
        Action nullObservation = () => readSet.Observe(null!);

        nullFloor.Should().Throw<ArgumentNullException>().WithParameterName("floor");
        nullObservation.Should().Throw<ArgumentNullException>().WithParameterName("labels");
        readSet.Current.Should().Be(floor, "a refused observation leaves the set where it was");
    }

    [Fact]
    public void ConcurrentObservations_AreNeverLost()
    {
        // A lost union leaves the read set without a label the subject read, which permits a send that label forbids.
        // Four workers, one per label, wait at a start line; a coordinator releases them together onto the same fresh
        // read set, round after round, and each observes its own label. With only four labels a set changes at most
        // four times, so the overlap has to be made, not waited for: a read-union-write without compare-and-swap keeps
        // only the last writer's label whenever two of them overlap, which over these rounds happens many times on
        // any multi-core run. Every round's set must end holding all four.
        const int Rounds = 20_000;
        var deadline = TimeSpan.FromSeconds(60);
        var sets = Enumerable.Range(0, Rounds).Select(_ => new ReadSet()).ToArray();
        var arrived = 0;
        var released = -1;
        var aborted = 0;
        var failures = new ConcurrentQueue<Exception>();

        var workers = FourLabels.Select(label => new Thread(() =>
            {
                try
                {
                    var observed = PrivacyLabelSet.Of(label);
                    for (var round = 0; round < Rounds; round++)
                    {
                        Interlocked.Increment(ref arrived);
                        var spinner = default(SpinWait);
                        while (Volatile.Read(ref released) < round)
                        {
                            if (Volatile.Read(ref aborted) != 0)
                                return;
                            spinner.SpinOnce(sleep1Threshold: -1);
                        }

                        sets[round].Observe(observed);
                    }
                }
                catch (Exception ex)
                {
                    failures.Enqueue(ex);
                    Volatile.Write(ref aborted, 1);
                }
            })
            { IsBackground = true })
            .ToArray();

        foreach (var worker in workers)
            worker.Start();

        var clock = Stopwatch.StartNew();
        for (var round = 0; round < Rounds && Volatile.Read(ref aborted) == 0; round++)
        {
            // Every worker is at this round's start line before any is released into it.
            var spinner = default(SpinWait);
            while (Volatile.Read(ref arrived) < FourLabels.Length * (round + 1))
            {
                if (Volatile.Read(ref aborted) != 0 || clock.Elapsed > deadline)
                {
                    Volatile.Write(ref aborted, 1);
                    break;
                }

                spinner.SpinOnce(sleep1Threshold: -1);
            }

            Volatile.Write(ref released, round);
        }

        foreach (var worker in workers)
            worker.Join(deadline).Should().BeTrue("every worker finishes its rounds");

        failures.Should().BeEmpty();
        Volatile.Read(ref aborted).Should().Be(0, "the rounds ran to the end within {0}", deadline);
        var full = PrivacyLabelSet.Of(FourLabels);
        var lost = sets.Count(s => !s.Current.Equals(full));
        lost.Should().Be(0, "no observation may be lost: {0} of {1} rounds ended without all four labels", lost, Rounds);
    }

    private static PrivacyLabelSet R(string text)
    {
        PrivacyLabelSet.TryParse(text, out var set).Should().BeTrue(
            "'{0}' must be canonical label-set text, or the row silently tests local-only instead", text);
        return set;
    }
}
