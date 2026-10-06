using System.Globalization;
using System.Reflection;
using Ashlar.Abstractions.Security;
using Ashlar.Abstractions.Security.Egress;
using FluentAssertions;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>
/// SPEC-007 PR 4.4, read scopes: <see cref="EgressSubject.BeginRead"/> and <see cref="ReadScope"/>. A read counts
/// at what it reported only when it completed and reported; otherwise it counts as <see cref="SecurityLabel.SystemHigh"/>.
/// </summary>
/// <remarks>
/// <para><b>What is pinned.</b> A completed read with no report observes SystemHigh (the unreported-read rule, SPEC-007
/// §7: unlabelled data fails closed upward). A completed read observes the join of its reports and nothing else, and a
/// report of Public ("read nothing") raises no mark. <see cref="EgressSubject.Observe"/> never satisfies a scope, so
/// code that can reach it cannot launder a result by observing a low label on a side value. A read that ends by an
/// exception observes SystemHigh even after it reported, because it may have fetched data its message carries. The
/// scope observes into every live frame of the chain it was begun on, from whatever flow it ends on; a report after it
/// ended is never lost; and with no frame it changes nothing.</para>
/// <para><b>Process-global state.</b> None. Frames live on each test's own async flow, the guard has an explicit
/// profile and reads no environment variable, and each decision is read from the value <c>Evaluate</c> returns.</para>
/// <para>Hermetic: no network, no files, no environment.</para>
/// </remarks>
[Trait("Category", "Certification")]
public sealed class EgressSubjectReadScopeTests
{
    private const string NoSubject = "no-subject";
    private const string SubjectPrefix = "subject:";
    private const string Remote = "https://remote.example/v1/chat";

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    /// <summary>A guard with an explicit profile, so no decision here reads the environment.</summary>
    private static readonly EgressGuard Guard = new("full");

    private static readonly SecurityLabel Internal = new(SecurityLevel.Internal);
    private static readonly SecurityLabel Confidential = new(SecurityLevel.Confidential);
    private static readonly SecurityLabel Secret = new(SecurityLevel.Secret);

    [Fact]
    public void A_completed_read_with_no_report_observes_SystemHigh()
    {
        var mark = new HighWaterMark();
        using (EgressSubject.Enter("read-unreported", mark))
        {
            using (var read = EgressSubject.BeginRead())
            {
                Decide(EgressFamilies.ModelMeai).Current.Should().Be(SecurityLabel.Public, "the read has not ended yet");
                read.Complete();
            }

            mark.Current.Should().Be(SecurityLabel.SystemHigh, "an unreported read is unlabelled data, which fails closed to the top");

            var decision = Decide(EgressFamilies.ModelMeai);
            decision.CurrentBasis.Should().Be(SubjectPrefix + "read-unreported");
            decision.Current.Should().Be(SecurityLabel.SystemHigh);
            decision.Access.Reason.Should().Be(AccessDenialReason.SystemHighData);
        }
    }

    [Fact]
    public void A_completed_read_observes_only_what_it_reported()
    {
        var mark = new HighWaterMark();
        using (EgressSubject.Enter("read-reported", mark))
        {
            using (var read = EgressSubject.BeginRead())
            {
                read.Report(Internal);
                read.Report(Confidential);
                read.Complete();
            }

            mark.Current.Should().Be(Confidential, "the join of the reports, and nothing else");

            Decide(EgressFamilies.WebSearch).Access.Allowed.Should().BeTrue("Confidential data may go to web search");
            Decide(EgressFamilies.ModelMeai).Access.Reason.Should().Be(AccessDenialReason.LevelTooLow);
        }
    }

    [Fact]
    public void A_report_of_Public_reads_nothing_and_raises_no_mark()
    {
        var mark = new HighWaterMark(Internal);
        using (EgressSubject.Enter("read-nothing", mark))
        {
            using (var read = EgressSubject.BeginRead())
            {
                read.Report(SecurityLabel.Public);
                read.Complete();
            }

            mark.Current.Should().Be(Internal, "\"read nothing\" satisfies the scope without raising");
            Decide(EgressFamilies.ModelMeai).Access.Allowed.Should().BeTrue("Internal data may go to an Internal model");
        }
    }

    [Fact]
    public void Observe_alone_does_not_satisfy_a_read_scope()
    {
        var mark = new HighWaterMark();
        using (EgressSubject.Enter("read-laundered", mark))
        {
            using (var read = EgressSubject.BeginRead())
            {
                // A reader that observes a low label on a side value and returns something else.
                EgressSubject.Observe(SecurityLabel.Public);
                read.Complete();
            }

            mark.Current.Should().Be(SecurityLabel.SystemHigh, "Observe only raises; it never reports the read");
            Decide(EgressFamilies.ModelMeai).Access.Reason.Should().Be(AccessDenialReason.SystemHighData);
        }
    }

    [Fact]
    public void A_read_that_throws_observes_SystemHigh_even_after_it_reported()
    {
        var mark = new HighWaterMark();
        using (EgressSubject.Enter("read-threw", mark))
        {
            var read = () =>
            {
                using var scope = EgressSubject.BeginRead();
                scope.Report(SecurityLabel.Public);
                throw new InvalidOperationException("the read fetched data, then threw");
            };

            read.Should().Throw<InvalidOperationException>();

            mark.Current.Should().Be(
                SecurityLabel.SystemHigh, "a read can fetch data and then throw, and the exception's message can carry it");
            Decide(EgressFamilies.ModelMeai).Access.Reason.Should().Be(AccessDenialReason.SystemHighData);
        }
    }

    [Fact]
    public async Task An_awaited_read_that_throws_observes_SystemHigh()
    {
        var mark = new HighWaterMark(Internal);
        using (EgressSubject.Enter("read-threw-async", mark))
        {
            async Task ReadAsync()
            {
                using var scope = EgressSubject.BeginRead();
                await Task.Yield();
                scope.Report(Internal);
                await Task.Delay(1);
                throw new InvalidOperationException("the read fetched data, then threw");
            }

            await ((Func<Task>)ReadAsync).Should().ThrowAsync<InvalidOperationException>();

            mark.Current.Should().Be(SecurityLabel.SystemHigh);
        }
    }

    [Fact]
    public async Task A_read_observes_into_every_live_frame_of_its_chain_from_whatever_flow_it_ends_on()
    {
        var outerMark = new HighWaterMark();
        var innerMark = new HighWaterMark();

        using (EgressSubject.Enter("read-chain-outer", outerMark))
        using (EgressSubject.Enter("read-chain-inner", innerMark))
        {
            var read = EgressSubject.BeginRead();
            await Task.Run(() =>
            {
                read.Report(Secret);
                read.Complete();
                read.Dispose();
            }).WaitAsync(Patience);

            innerMark.Current.Should().Be(Secret);
            outerMark.Current.Should().Be(Secret, "every live frame of the chain the read was begun on");
            Decide(EgressFamilies.ModelMeai).Access.Reason.Should().Be(AccessDenialReason.LevelTooLow);
        }
    }

    [Fact]
    public async Task A_read_ended_on_a_flow_without_its_chain_still_raises_the_chain_it_was_begun_on()
    {
        var outerMark = new HighWaterMark();
        var innerMark = new HighWaterMark();
        var suppressedLabel = new SecurityLabel(SecurityLevel.Internal, ["SUPPRESSED"]);
        var threadLabel = new SecurityLabel(SecurityLevel.Internal, ["THREAD"]);
        var detachedLabel = new SecurityLabel(SecurityLevel.Internal, ["DETACHED"]);
        var lateLabel = new SecurityLabel(SecurityLevel.Internal, ["LATE"]);

        using (EgressSubject.Enter("read-offchain-outer", outerMark))
        using (EgressSubject.Enter("read-offchain-inner", innerMark))
        {
            // 1. Ended in a task that does not inherit the execution context.
            var suppressed = EgressSubject.BeginRead();
            Task<EgressDecision> suppressedEnd;
            using (ExecutionContext.SuppressFlow())
            {
                suppressedEnd = Task.Run(() =>
                {
                    var there = Decide(EgressFamilies.ModelMeai);
                    suppressed.Report(suppressedLabel);
                    suppressed.Complete();
                    suppressed.Dispose();
                    return there;
                });
            }

            (await suppressedEnd.WaitAsync(Patience)).CurrentBasis.Should().Be(
                NoSubject, "the task ran without the begin chain: no frame flowed into it");
            innerMark.Current.Should().Be(suppressedLabel, "the scope observes into the chain it was begun on");
            outerMark.Current.Should().Be(suppressedLabel, "every frame of that chain, not the ending flow's");

            // 2. Ended on a new thread started without the execution context.
            var threaded = EgressSubject.BeginRead();
            EgressDecision? onThread = null;
            var thread = new Thread(() =>
            {
                onThread = Decide(EgressFamilies.ModelMeai);
                threaded.Report(threadLabel);
                threaded.Complete();
                threaded.Dispose();
            });
            thread.UnsafeStart();
            thread.Join(Patience).Should().BeTrue("the thread ends the read");

            onThread!.CurrentBasis.Should().Be(NoSubject, "the thread has no frame of its own");
            outerMark.Current.Should().Be(suppressedLabel.Join(threadLabel));

            // 3. Ended under a detachment on this flow, then reported late from there.
            var detached = EgressSubject.BeginRead();
            using (Detach())
            {
                Decide(EgressFamilies.ModelMeai).CurrentBasis.Should().Be(NoSubject, "the detachment leaves every frame");
                detached.Report(detachedLabel);
                detached.Complete();
                detached.Dispose();
                detached.Report(lateLabel);
            }

            var expected = suppressedLabel.Join(threadLabel).Join(detachedLabel).Join(lateLabel);
            innerMark.Current.Should().Be(expected, "a late report from a flow without the chain still reaches it");
            outerMark.Current.Should().Be(expected);
        }
    }

    [Fact]
    public void A_report_after_the_read_ended_is_never_lost()
    {
        var mark = new HighWaterMark();
        using (EgressSubject.Enter("read-late", mark))
        {
            var read = EgressSubject.BeginRead();
            read.Report(Internal);
            read.Complete();
            read.Dispose();
            mark.Current.Should().Be(Internal);

            read.Report(Secret);
            mark.Current.Should().Be(Secret, "a late report goes to the frames itself");

            read.Dispose();
            mark.Current.Should().Be(Secret, "disposing twice does nothing");
        }
    }

    [Fact]
    public async Task Concurrent_reports_all_count()
    {
        var mark = new HighWaterMark();
        using (EgressSubject.Enter("read-concurrent", mark))
        {
            using (var read = EgressSubject.BeginRead())
            {
                var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var reporters = Enumerable.Range(0, 8).Select(i => Task.Run(async () =>
                {
                    await start.Task.WaitAsync(Patience);
                    read.Report(new SecurityLabel(SecurityLevel.Internal, ["R" + i.ToString(CultureInfo.InvariantCulture)]));
                })).ToArray();

                start.SetResult();
                await Task.WhenAll(reporters).WaitAsync(Patience);
                read.Complete();
            }

            mark.Current.Should().Be(
                new SecurityLabel(SecurityLevel.Internal, Enumerable.Range(0, 8).Select(i => "R" + i.ToString(CultureInfo.InvariantCulture))),
                "no concurrent report is lost");
        }
    }

    [Fact]
    public void With_no_frame_a_read_changes_nothing()
    {
        using (var read = EgressSubject.BeginRead())
        {
            read.Complete();
        }

        var decision = Decide(EgressFamilies.ModelMeai);
        decision.CurrentBasis.Should().Be(NoSubject);
        decision.Current.Should().Be(SecurityLabel.SystemHigh);

        using var scope = EgressSubject.BeginRead();
        ((Action)(() => scope.Report(null!))).Should().Throw<ArgumentNullException>().WithParameterName("label");
    }

    /// <summary>The internal <c>EgressSubject.Detach</c>, by reflection, as <see cref="EgressSubjectDetachTests"/> reaches it.</summary>
    private static IDisposable Detach()
    {
        var method = typeof(EgressSubject).GetMethod("Detach", BindingFlags.NonPublic | BindingFlags.Static, binder: null, Type.EmptyTypes, modifiers: null);
        method.Should().NotBeNull("EgressSubject.Detach is the internal, raise-only way to leave every frame (SPEC-007 PR 4.4)");
        return (IDisposable)method!.Invoke(null, null)!;
    }

    private static EgressDecision Decide(string family) =>
        Guard.Evaluate(new EgressRequest(family, "twin:read:" + Guid.NewGuid().ToString("N"), new Uri(Remote)));
}
