using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using Ashlar.Abstractions.Security;
using Ashlar.Abstractions.Security.Egress;
using FluentAssertions;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>
/// SPEC-007 PR 4.4, monotone nesting of <see cref="EgressSubject"/> frames: a nested frame never decides below a
/// frame it was entered inside.
/// </summary>
/// <remarks>
/// <para><b>What is pinned.</b> A decision's current label is the join of the innermost live frame's mark and the
/// mark of every frame it was entered inside, live or disposed, and its basis names the innermost subject. Disposing a
/// frame observes its mark into every frame it was entered inside, also when the frame was entered in a child task
/// whose frames never flow back out. <see cref="EgressSubject.Observe"/> joins into every frame on the chain at once,
/// so an enclosing flow that decides while a nested frame is still running in another task already counts what it
/// read, and it only raises. Before 4.4 the guard read only the innermost live frame, so entering a fresh Public frame
/// inside a Secret one declassified everything the enclosing subject had read, and a nested subject's reads were lost
/// when it was disposed.</para>
/// <para><b>Disposed ancestors (fail closed).</b> A frame disposed out of order still counts for the frame running
/// inside it. A parent frame that ends while a fire-and-forget task started inside it still runs counts for every
/// frame that task enters, then or later, and is still raised by what the task reads, in a frame of its own or with
/// none, so a sibling task started inside the same parent counts it. Otherwise a parent frame that ends first would
/// declassify a child task still running inside it, whose closures may hold what the parent read.</para>
/// <para><b>Unwinding.</b> A flow that disposes its own frames out of order goes back to where in-order <c>using</c>
/// blocks would have left it, past the enclosing frame it disposed first, so no chain of disposed frames builds up on
/// it, also across the iterations of an async loop. A task started inside that enclosing frame does not go past it.</para>
/// <para><b>Process-global state.</b> None. Frames live on each test's own async flow, the guard has an explicit
/// profile and reads no environment variable, and each decision is read from the value <c>Evaluate</c> returns.</para>
/// <para>Hermetic: no network, no files, no environment.</para>
/// </remarks>
[Trait("Category", "Certification")]
public sealed class EgressSubjectNestingTests
{
    private const string NoSubject = "no-subject";
    private const string SubjectPrefix = "subject:";
    private const string Remote = "https://remote.example/v1/chat";
    private const string UnknownFamily = "not-a-family";

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    /// <summary>A guard with an explicit profile, so no decision here reads the environment.</summary>
    private static readonly EgressGuard Guard = new("full");

    private static readonly SecurityLabel Internal = new(SecurityLevel.Internal);
    private static readonly SecurityLabel Confidential = new(SecurityLevel.Confidential);
    private static readonly SecurityLabel Secret = new(SecurityLevel.Secret);

    [Fact]
    public void A_Public_frame_inside_a_Secret_frame_decides_Secret()
    {
        using (EgressSubject.Enter("nest-outer", new HighWaterMark(Secret)))
        using (EgressSubject.Enter("nest-inner", new HighWaterMark()))
        {
            var decision = Decide(EgressFamilies.ModelMeai);

            decision.CurrentBasis.Should().Be(SubjectPrefix + "nest-inner", "the basis names the innermost subject");
            decision.Current.Should().Be(Secret, "a nested frame never decides below a frame it was entered inside");
            decision.Access.Allowed.Should().BeFalse("Secret data may not go to an Internal model: {0}", decision.Access);
            decision.Access.Reason.Should().Be(AccessDenialReason.LevelTooLow);
        }

        Decide(EgressFamilies.ModelMeai).CurrentBasis.Should().Be(NoSubject);
    }

    [Fact]
    public async Task Every_live_frame_on_the_chain_is_joined_across_await()
    {
        var outer = new SecurityLabel(SecurityLevel.Internal, ["NEST"]);

        using (EgressSubject.Enter("chain-outer", new HighWaterMark(outer)))
        {
            await Task.Yield();
            using (EgressSubject.Enter("chain-middle", new HighWaterMark()))
            {
                await Task.Delay(1);
                using (EgressSubject.Enter("chain-inner", new HighWaterMark(Confidential)))
                {
                    await Task.Yield();

                    var decision = Decide(UnknownFamily);
                    decision.CurrentBasis.Should().Be(SubjectPrefix + "chain-inner");
                    decision.Current.Should().Be(
                        new SecurityLabel(SecurityLevel.Confidential, ["NEST"]),
                        "the current label is the join of Internal//C:NEST, Public and Confidential");
                }
            }
        }
    }

    [Fact]
    public void A_frame_disposed_out_of_order_still_counts_for_the_frame_entered_inside_it()
    {
        var outer = EgressSubject.Enter("order-outer", new HighWaterMark(Secret));
        var inner = EgressSubject.Enter("order-inner", new HighWaterMark(Internal));
        try
        {
            outer.Dispose();

            var decision = Decide(EgressFamilies.ModelMeai);
            decision.CurrentBasis.Should().Be(SubjectPrefix + "order-inner", "the basis still names the innermost live subject");
            decision.Current.Should().Be(
                Secret, "the inner frame was entered inside the Secret one, and disposing that one first does not declassify it");
            decision.Access.Allowed.Should().BeFalse("Secret data may not go to an Internal model: {0}", decision.Access);
            decision.Access.Reason.Should().Be(AccessDenialReason.LevelTooLow);
        }
        finally
        {
            inner.Dispose();
            outer.Dispose();
        }

        Decide(EgressFamilies.ModelMeai).CurrentBasis.Should().Be(NoSubject);
    }

    [Fact]
    public async Task A_child_task_that_outlives_its_parent_frame_still_decides_at_the_parent_mark()
    {
        var childEntered = new TaskCompletionSource<EgressDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        var parentDisposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<(EgressDecision After, EgressDecision Late)> child;

        using (EgressSubject.Enter("orphan-parent", new HighWaterMark(Secret)))
        {
            // Fire and forget: the parent never awaits the child, whose closure may hold what the parent read.
            child = Task.Run(async () =>
            {
                EgressDecision after;
                using (EgressSubject.Enter("orphan-child", new HighWaterMark()))
                {
                    childEntered.SetResult(Decide(EgressFamilies.ModelMeai));
                    await parentDisposed.Task.WaitAsync(Patience);
                    after = Decide(EgressFamilies.ModelMeai);
                }

                // A frame entered only after the parent was disposed was still started inside it.
                using (EgressSubject.Enter("orphan-late", new HighWaterMark()))
                    return (after, Decide(EgressFamilies.ModelMeai));
            });

            var before = await childEntered.Task.WaitAsync(Patience);
            before.CurrentBasis.Should().Be(SubjectPrefix + "orphan-child");
            before.Current.Should().Be(Secret, "while the parent frame is live, the child decides at its mark");
        }

        Decide(EgressFamilies.ModelMeai).CurrentBasis.Should().Be(NoSubject, "the parent's flow is restored when its frame ends");
        parentDisposed.SetResult();
        var (afterDispose, late) = await child.WaitAsync(Patience);

        afterDispose.CurrentBasis.Should().Be(SubjectPrefix + "orphan-child");
        afterDispose.Current.Should().Be(Secret, "disposing the parent frame first must not declassify a child still running inside it");
        afterDispose.Access.Allowed.Should().BeFalse("Secret data may not go to an Internal model: {0}", afterDispose.Access);
        afterDispose.Access.Reason.Should().Be(AccessDenialReason.LevelTooLow);

        late.CurrentBasis.Should().Be(SubjectPrefix + "orphan-late");
        late.Current.Should().Be(Secret, "a frame entered after the parent ended was still entered inside it");
        late.Access.Reason.Should().Be(AccessDenialReason.LevelTooLow);
    }

    [Fact]
    public async Task A_read_after_the_parent_frame_ended_still_reaches_a_sibling_task_entered_inside_it()
    {
        var observed = new SecurityLabel(SecurityLevel.Internal, ["OBSERVED"]);
        var propagated = new SecurityLabel(SecurityLevel.Internal, ["PROPAGATED"]);
        var parentDisposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readerObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var siblingDecided = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readerDisposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task reader;
        Task<(EgressDecision AfterObserve, EgressDecision AfterDispose)> sibling;

        using (EgressSubject.Enter("sibling-parent", new HighWaterMark()))
        {
            // Two fire-and-forget tasks started inside the parent frame, which can share what its closure holds.
            reader = Task.Run(async () =>
            {
                var readerMark = new HighWaterMark();
                using (EgressSubject.Enter("sibling-reader", readerMark))
                {
                    await parentDisposed.Task.WaitAsync(Patience);
                    EgressSubject.Observe(observed);
                    readerObserved.SetResult();
                    await siblingDecided.Task.WaitAsync(Patience);

                    // Straight into the reader's own mark: only disposing its frame carries this one outward.
                    readerMark.Observe(propagated);
                }

                readerDisposed.SetResult();
            });

            sibling = Task.Run(async () =>
            {
                using (EgressSubject.Enter("sibling-egress", new HighWaterMark()))
                {
                    await readerObserved.Task.WaitAsync(Patience);
                    var afterObserve = Decide(UnknownFamily);
                    siblingDecided.SetResult();
                    await readerDisposed.Task.WaitAsync(Patience);
                    return (afterObserve, Decide(UnknownFamily));
                }
            });
        }

        parentDisposed.SetResult();
        var (afterObserve, afterDispose) = await sibling.WaitAsync(Patience);
        await reader.WaitAsync(Patience);

        afterObserve.CurrentBasis.Should().Be(SubjectPrefix + "sibling-egress");
        afterObserve.Current.Should().Be(
            observed, "a read observed after the parent frame ended still raises it, and the sibling still counts it");
        afterDispose.Current.Should().Be(
            observed.Join(propagated), "a frame disposed after the parent ended still carries its mark into the parent");
    }

    [Fact]
    public async Task Observe_in_a_task_with_no_frame_of_its_own_after_the_parent_ended_still_reaches_a_sibling_task()
    {
        var observed = new SecurityLabel(SecurityLevel.Internal, ["FRAMELESS"]);
        var parentDisposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readerObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<EgressDecision> reader;
        Task<EgressDecision> sibling;

        using (EgressSubject.Enter("frameless-parent", new HighWaterMark()))
        {
            // The reader enters no frame: the only frame on its flow is the parent's.
            reader = Task.Run(async () =>
            {
                await parentDisposed.Task.WaitAsync(Patience);
                var own = Decide(UnknownFamily);
                EgressSubject.Observe(observed);
                readerObserved.SetResult();
                return own;
            });

            sibling = Task.Run(async () =>
            {
                using (EgressSubject.Enter("frameless-sibling", new HighWaterMark()))
                {
                    await readerObserved.Task.WaitAsync(Patience);
                    return Decide(UnknownFamily);
                }
            });
        }

        parentDisposed.SetResult();
        var own = await reader.WaitAsync(Patience);
        var decision = await sibling.WaitAsync(Patience);

        own.CurrentBasis.Should().Be(
            NoSubject, "a disposed frame is never the innermost one, so a task with no frame of its own decides with no subject");
        decision.CurrentBasis.Should().Be(SubjectPrefix + "frameless-sibling");
        decision.Current.Should().Be(
            observed, "what the reader observes still raises the parent it was started inside, which the sibling counts");
    }

    [Fact]
    public async Task Frames_disposed_out_of_order_on_one_flow_unwind_as_in_order_using_blocks_would()
    {
        var outer = EgressSubject.Enter("unwind-outer", new HighWaterMark(Secret));
        var inner = EgressSubject.Enter("unwind-inner", new HighWaterMark());
        outer.Dispose();
        inner.Dispose();

        Decide(EgressFamilies.ModelMeai).CurrentBasis.Should().Be(NoSubject);
        using (EgressSubject.Enter("unwind-after", new HighWaterMark()))
        {
            var after = Decide(UnknownFamily);
            after.CurrentBasis.Should().Be(SubjectPrefix + "unwind-after");
            after.Current.Should().Be(
                SecurityLabel.Public,
                "this flow disposed both frames, so it unwinds past the outer one as in-order using blocks would, and a frame entered afterwards is inside neither");
        }

        // The same in an async loop, whose flow is never restored between iterations: no chain builds up.
        for (var i = 0; i < 50; i++)
        {
            var loopOuter = EgressSubject.Enter("unwind-loop-outer", new HighWaterMark(Secret));
            var loopInner = EgressSubject.Enter("unwind-loop-inner", new HighWaterMark());
            loopOuter.Dispose();
            loopInner.Dispose();
            await Task.Yield();

            using (EgressSubject.Enter("unwind-loop-probe", new HighWaterMark()))
                Decide(UnknownFamily).Current.Should().Be(SecurityLabel.Public, "iteration {0} leaves no disposed frame behind", i);
        }

        var enclosingMark = new HighWaterMark();
        using (EgressSubject.Enter("unwind-enclosing", enclosingMark))
        {
            var nestedOuter = EgressSubject.Enter("unwind-nested-outer", new HighWaterMark(Secret));
            var nestedInner = EgressSubject.Enter("unwind-nested-inner", new HighWaterMark());
            nestedOuter.Dispose();
            nestedInner.Dispose();

            var back = Decide(EgressFamilies.ModelMeai);
            back.CurrentBasis.Should().Be(SubjectPrefix + "unwind-enclosing", "the flow is back in the frame both were entered inside");
            back.Current.Should().Be(
                Secret, "unwinding past the disposed frame loses nothing: its mark was observed into the enclosing frame");
            back.Access.Reason.Should().Be(AccessDenialReason.LevelTooLow);
        }

        Decide(EgressFamilies.ModelMeai).CurrentBasis.Should().Be(NoSubject, "the enclosing frame's using restores this flow");
    }

    [Fact]
    public async Task A_parent_frame_disposed_out_of_order_on_its_own_flow_still_holds_a_task_started_inside_it()
    {
        var parentDisposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<(EgressDecision Own, EgressDecision Late)> child;

        var parent = EgressSubject.Enter("unwound-parent", new HighWaterMark(Secret));

        // Fire and forget, started inside the parent frame only.
        child = Task.Run(async () =>
        {
            await parentDisposed.Task.WaitAsync(Patience);
            EgressDecision own;
            using (EgressSubject.Enter("unwound-child", new HighWaterMark()))
                own = Decide(EgressFamilies.ModelMeai);

            using (EgressSubject.Enter("unwound-late", new HighWaterMark()))
                return (own, Decide(EgressFamilies.ModelMeai));
        });

        // This flow enters a frame after starting the task, then disposes the parent first.
        var next = EgressSubject.Enter("unwound-next", new HighWaterMark());
        parent.Dispose();
        next.Dispose();

        using (EgressSubject.Enter("unwound-after", new HighWaterMark()))
            Decide(UnknownFamily).Current.Should().Be(SecurityLabel.Public, "this flow disposed both, so it unwinds past the parent");

        parentDisposed.SetResult();
        var (own, late) = await child.WaitAsync(Patience);

        own.CurrentBasis.Should().Be(SubjectPrefix + "unwound-child");
        own.Current.Should().Be(Secret, "the task was started inside the parent, which still counts for its frames");
        late.CurrentBasis.Should().Be(SubjectPrefix + "unwound-late");
        late.Current.Should().Be(
            Secret, "only the flow that disposed the parent out of order unwinds past it; the task's own frame ends back inside it");
        late.Access.Reason.Should().Be(AccessDenialReason.LevelTooLow);
    }

    [Fact]
    public async Task A_task_handed_a_child_scope_stays_inside_a_parent_frame_that_another_flow_ended()
    {
        var parentDisposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<(EgressDecision NoFrame, EgressDecision Late)> task;

        using (EgressSubject.Enter("handed-parent", new HighWaterMark(Secret)))
        {
            var child = EgressSubject.Enter("handed-child", new HighWaterMark());

            // Fire and forget, handed the child scope: the task owns its lifetime and ends it.
            task = Task.Run(async () =>
            {
                await parentDisposed.Task.WaitAsync(Patience);
                child.Dispose();
                var noFrame = Decide(EgressFamilies.ModelMeai);
                using (EgressSubject.Enter("handed-late", new HighWaterMark()))
                    return (noFrame, Decide(EgressFamilies.ModelMeai));
            });
        } // The parent's using ends while the child is still innermost on this flow: out of order here.

        parentDisposed.SetResult();
        var (noFrame, late) = await task.WaitAsync(Patience);

        noFrame.CurrentBasis.Should().Be(NoSubject, "the task's only frames have ended, and a disposed frame is never the innermost one");
        late.CurrentBasis.Should().Be(SubjectPrefix + "handed-late");
        late.Current.Should().Be(
            Secret, "the task was started inside the Secret parent and never disposed it, so a frame it enters later still counts it");
        late.Access.Reason.Should().Be(AccessDenialReason.LevelTooLow);
    }

    [Fact]
    public async Task A_flow_whose_parent_frame_a_background_task_ended_stays_inside_it()
    {
        var parent = EgressSubject.Enter("background-parent", new HighWaterMark(Secret));
        var child = EgressSubject.Enter("background-child", new HighWaterMark());

        // Fire and forget: the parent's owner ends it in the background. This flow never disposes the parent.
        var parentEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Task.Run(async () =>
        {
            await Task.Yield();
            parent.Dispose();
            parentEnded.SetResult();
        });
        await parentEnded.Task.WaitAsync(Patience);
        child.Dispose();

        using (EgressSubject.Enter("background-after", new HighWaterMark()))
        {
            var after = Decide(EgressFamilies.ModelMeai);
            after.CurrentBasis.Should().Be(SubjectPrefix + "background-after");
            after.Current.Should().Be(
                Secret, "this flow entered the Secret parent and never disposed it, so it is still inside it (fail closed)");
            after.Access.Reason.Should().Be(AccessDenialReason.LevelTooLow);
        }
    }

    [Fact]
    public async Task A_flow_that_awaits_its_parent_frame_disposed_on_another_flow_stays_inside_it()
    {
        var parent = EgressSubject.Enter("awaited-parent", new HighWaterMark(Secret));
        var child = EgressSubject.Enter("awaited-child", new HighWaterMark());
        await Task.Run(parent.Dispose).WaitAsync(Patience);
        child.Dispose();

        using (EgressSubject.Enter("awaited-after", new HighWaterMark()))
        {
            var after = Decide(EgressFamilies.ModelMeai);
            after.CurrentBasis.Should().Be(SubjectPrefix + "awaited-after");
            after.Current.Should().Be(
                Secret, "the parent was disposed on another flow, which does not take this flow out of it (fail closed)");
            after.Access.Reason.Should().Be(AccessDenialReason.LevelTooLow);
        }
    }

    [Fact]
    public async Task A_task_handed_a_child_scope_counts_a_later_raise_of_the_mark_of_a_parent_another_flow_ended()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var parentMark = new HighWaterMark();

        using (EgressSubject.Enter("raised-enclosing", new HighWaterMark()))
        {
            var parent = EgressSubject.Enter("raised-parent", parentMark);
            var child = EgressSubject.Enter("raised-child", new HighWaterMark());

            // Fire and forget, handed the child scope.
            var task = Task.Run(async () =>
            {
                await release.Task.WaitAsync(Patience);
                child.Dispose();
                using (EgressSubject.Enter("raised-late", new HighWaterMark()))
                    return Decide(UnknownFamily);
            });

            // Out of order on this flow, which still has the child innermost. Then the parent's subject reads Secret
            // somewhere else: only the parent's mark rises, not the enclosing frame its mark was observed into.
            parent.Dispose();
            parentMark.Observe(Secret); // Straight into the parent's mark, as a producer holding it would.
            release.SetResult();
            var late = await task.WaitAsync(Patience);

            late.CurrentBasis.Should().Be(SubjectPrefix + "raised-late");
            late.Current.Should().Be(
                Secret,
                "the task never disposed the parent, so it is still inside it and reads its mark when it decides, also what it rose to after the parent ended");
            late.Access.Reason.Should().Be(AccessDenialReason.LevelTooLow);
        }
    }

    [Fact]
    public async Task A_flow_whose_parent_frame_another_flow_ended_counts_a_later_raise_of_the_parent_mark()
    {
        var parentMark = new HighWaterMark();

        using (EgressSubject.Enter("raised-awaited-enclosing", new HighWaterMark()))
        {
            var parent = EgressSubject.Enter("raised-awaited-parent", parentMark);
            var child = EgressSubject.Enter("raised-awaited-child", new HighWaterMark());
            await Task.Run(parent.Dispose).WaitAsync(Patience);
            await RaiseThroughAnotherFrame(parentMark, Secret);
            child.Dispose();

            using (EgressSubject.Enter("raised-awaited-late", new HighWaterMark()))
            {
                var late = Decide(UnknownFamily);
                late.CurrentBasis.Should().Be(SubjectPrefix + "raised-awaited-late");
                late.Current.Should().Be(
                    Secret, "this flow entered the parent and never disposed it, so it still reads the parent's mark when it decides");
            }
        }
    }

    [Fact]
    public async Task A_task_that_ends_the_frames_around_a_frame_another_flow_ended_stays_inside_that_frame()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var outermost = EgressSubject.Enter("around-outermost", new HighWaterMark());
        var parent = EgressSubject.Enter("around-parent", new HighWaterMark(Secret));
        var child = EgressSubject.Enter("around-child", new HighWaterMark());

        // Fire and forget, handed the child and the outermost frame, which it ends innermost first.
        var task = Task.Run(async () =>
        {
            await release.Task.WaitAsync(Patience);
            child.Dispose();
            outermost.Dispose();
            using (EgressSubject.Enter("around-late", new HighWaterMark()))
                return Decide(EgressFamilies.ModelMeai);
        });

        parent.Dispose(); // Out of order on this flow: the child is still innermost here.
        release.SetResult();
        var late = await task.WaitAsync(Patience);

        late.CurrentBasis.Should().Be(SubjectPrefix + "around-late");
        late.Current.Should().Be(
            Secret, "the task was started inside the Secret parent and never disposed it; ending the frames around it does not take the task out");
        late.Access.Reason.Should().Be(AccessDenialReason.LevelTooLow);
    }

    [Fact]
    public async Task A_flow_that_ends_the_frames_around_a_frame_another_flow_ended_stays_inside_that_frame()
    {
        var outermost = EgressSubject.Enter("around-awaited-outermost", new HighWaterMark());
        var parent = EgressSubject.Enter("around-awaited-parent", new HighWaterMark(Secret));
        var child = EgressSubject.Enter("around-awaited-child", new HighWaterMark());
        await Task.Run(parent.Dispose).WaitAsync(Patience);
        child.Dispose();
        outermost.Dispose();

        using (EgressSubject.Enter("around-awaited-late", new HighWaterMark()))
        {
            var late = Decide(EgressFamilies.ModelMeai);
            late.CurrentBasis.Should().Be(SubjectPrefix + "around-awaited-late");
            late.Current.Should().Be(
                Secret, "this flow entered the Secret parent and never disposed it; ending the frames around it does not take the flow out");
            late.Access.Reason.Should().Be(AccessDenialReason.LevelTooLow);
        }
    }

    [Fact]
    public void Every_dispose_order_of_three_frames_on_one_flow_decides_as_in_order_using_blocks_would()
    {
        string[] names = ["orders-a", "orders-b", "orders-c"];
        SecurityLabel[] labels =
        [
            new(SecurityLevel.Internal, ["ORDER-A"]),
            new(SecurityLevel.Internal, ["ORDER-B"]),
            new(SecurityLevel.Internal, ["ORDER-C"]),
        ];
        var all = labels[0].Join(labels[1]).Join(labels[2]);
        var failures = new List<string>();

        // One synchronous flow for every order, so anything an order leaves behind shows in the next.
        foreach (var order in Permutations(3))
        {
            var tag = string.Join(",", order.Select(i => names[i]));
            using (EgressSubject.Enter("orders-enclosing", new HighWaterMark()))
            {
                var frames = new IDisposable[3];
                for (var i = 0; i < 3; i++)
                    frames[i] = EgressSubject.Enter(names[i], new HighWaterMark(labels[i]));

                var live = new[] { true, true, true };
                foreach (var k in order)
                {
                    frames[k].Dispose();
                    live[k] = false;

                    // In-order using blocks would leave the flow in the innermost frame not yet disposed.
                    var innermost = Array.LastIndexOf(live, true);
                    var basis = SubjectPrefix + (innermost < 0 ? "orders-enclosing" : names[innermost]);
                    var decision = Decide(UnknownFamily);
                    if (decision.CurrentBasis != basis || !decision.Current.Equals(all))
                        failures.Add($"[{tag}] after {names[k]}: {decision.CurrentBasis} at {decision.Current}, not {basis} at {all}");
                }
            }

            var after = Decide(UnknownFamily);
            if (after.CurrentBasis != NoSubject)
                failures.Add($"[{tag}] after the enclosing frame ended: {after.CurrentBasis}, not {NoSubject}");

            using (EgressSubject.Enter("orders-fresh", new HighWaterMark()))
            {
                var fresh = Decide(UnknownFamily);
                if (!fresh.Current.Equals(SecurityLabel.Public))
                    failures.Add($"[{tag}] a frame entered afterwards decides {fresh.Current}, not Public");
            }
        }

        failures.Should().BeEmpty(
            "a flow that disposes its own frames, in any order, ends where in-order using blocks would; every failure: {0}",
            string.Join(" || ", failures));
    }

    [Fact]
    public async Task A_task_with_no_frame_of_its_own_never_decides_below_a_frame_while_it_is_being_disposed()
    {
        // Labels this large make the join that observes the parent's mark outward take measurable time: the window in
        // which the parent is ending and the enclosing frame does not hold its mark yet.
        var enclosingFloor = new SecurityLabel(
            SecurityLevel.Internal, Enumerable.Range(0, 60_000).Select(i => "E" + i.ToString("D6", CultureInfo.InvariantCulture)));
        var parentLabel = new SecurityLabel(
            SecurityLevel.Secret, Enumerable.Range(0, 60_000).Select(i => "P" + i.ToString("D6", CultureInfo.InvariantCulture)));
        var resolve = ResolveOnThisFlow();
        const int Iterations = 60;
        var below = 0;
        string? example = null;

        for (var i = 0; i < Iterations; i++)
        {
            var hit = await DisposeWhileAFramelessTaskDecides(enclosingFloor, parentLabel, resolve);
            if (hit is not null)
            {
                below++;
                example ??= hit;
            }
        }

        below.Should().Be(
            0,
            "a task started inside a frame decides at that frame until the frame's mark has been observed into the enclosing frame; {0} of {1} disposals let it decide below, for example: {2}",
            below,
            Iterations,
            example);
    }

    [Fact]
    public async Task Tasks_started_at_every_level_keep_every_frame_they_were_started_inside_in_every_dispose_order()
    {
        var failures = new List<string>();
        foreach (var order in Permutations(3))
            failures.AddRange(await DisposeInOrderWithTasksAtEveryLevel(order));

        failures.Should().BeEmpty(
            "a task never decides below a frame it was started inside and did not dispose; every failing order: {0}",
            string.Join(" || ", failures));
    }

    [Fact]
    public void An_unwound_frame_no_longer_keeps_the_frame_inside_it_reachable()
    {
        // The pair outermost on this flow, then inside a live enclosing frame.
        var (outermostOuter, outermostInner) = EnterPairAndDispose(outOfOrder: true);
        using (EgressSubject.Enter("retain-enclosing", new HighWaterMark()))
        {
            var (outOfOrderOuter, outOfOrderInner) = EnterPairAndDispose(outOfOrder: true);
            var (inOrderOuter, inOrderInner) = EnterPairAndDispose(outOfOrder: false);
            for (var i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            inOrderInner.IsAlive.Should().BeFalse("the control: an in-order pair keeps nothing, so the collection did run");
            outermostInner.IsAlive.Should().BeFalse(
                "once the flow has unwound past the outer frame, nothing keeps the inner frame or its mark reachable from it");
            outOfOrderInner.IsAlive.Should().BeFalse("nor inside an enclosing frame");
            GC.KeepAlive(outermostOuter);
            GC.KeepAlive(outOfOrderOuter);
            GC.KeepAlive(inOrderOuter);
        }
    }

    [Fact]
    public void Disposing_a_frame_observes_its_mark_into_every_enclosing_live_frame()
    {
        var outerMark = new HighWaterMark();
        var middleMark = new HighWaterMark(Internal);
        var innerMark = new HighWaterMark();

        using (EgressSubject.Enter("dispose-outer", outerMark))
        {
            using (EgressSubject.Enter("dispose-middle", middleMark))
            {
                using (EgressSubject.Enter("dispose-inner", innerMark))
                {
                    // Straight into the inner mark, as a producer holding it would: only disposal carries it outward.
                    innerMark.Observe(Secret);
                    outerMark.Current.Should().Be(SecurityLabel.Public, "nothing has propagated before the inner frame ends");
                }

                middleMark.Current.Should().Be(Secret, "what the inner subject read leaves with its output");
                outerMark.Current.Should().Be(Secret, "every enclosing frame is raised, not only the nearest");

                var middle = Decide(EgressFamilies.ModelMeai);
                middle.CurrentBasis.Should().Be(SubjectPrefix + "dispose-middle");
                middle.Current.Should().Be(Secret);
                middle.Access.Reason.Should().Be(AccessDenialReason.LevelTooLow, "Secret may not go to an Internal model");
            }

            var outer = Decide(EgressFamilies.ModelMeai);
            outer.CurrentBasis.Should().Be(SubjectPrefix + "dispose-outer");
            outer.Current.Should().Be(Secret);
            outer.Access.Reason.Should().Be(AccessDenialReason.LevelTooLow);
        }
    }

    [Fact]
    public async Task A_frame_entered_in_a_child_task_reaches_the_parent_frame_when_it_is_disposed()
    {
        using (EgressSubject.Enter("child-parent", new HighWaterMark()))
        {
            await Task.Run(() =>
            {
                var innerMark = new HighWaterMark();
                using (EgressSubject.Enter("child-inner", innerMark))
                    innerMark.Observe(Secret);
            }).WaitAsync(Patience);

            var decision = Decide(EgressFamilies.ModelMeai);
            decision.CurrentBasis.Should().Be(SubjectPrefix + "child-parent", "the child's frame never flows back out");
            decision.Current.Should().Be(Secret, "but what the child's subject read does, when its frame is disposed");
            decision.Access.Reason.Should().Be(AccessDenialReason.LevelTooLow);
        }
    }

    [Fact]
    public void Observe_joins_into_every_live_frame_on_the_chain_at_once()
    {
        var outerMark = new HighWaterMark();
        var innerMark = new HighWaterMark();

        using (EgressSubject.Enter("observe-outer", outerMark))
        using (EgressSubject.Enter("observe-inner", innerMark))
        {
            EgressSubject.Observe(Confidential);

            innerMark.Current.Should().Be(Confidential);
            outerMark.Current.Should().Be(Confidential, "every frame on the chain is raised at once, before the inner frame ends");
        }
    }

    [Fact]
    public async Task Observe_reaches_an_enclosing_flow_while_the_nested_frame_is_still_running()
    {
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using (EgressSubject.Enter("live-outer", new HighWaterMark()))
        {
            var child = Task.Run(async () =>
            {
                using (EgressSubject.Enter("live-inner", new HighWaterMark()))
                {
                    EgressSubject.Observe(Secret);
                    observed.SetResult();
                    await release.Task.WaitAsync(Patience);
                    return Decide(EgressFamilies.ModelMeai);
                }
            });

            await observed.Task.WaitAsync(Patience);

            // The child's frame is still live: only the live join can have raised this flow's frame.
            var parent = Decide(EgressFamilies.ModelMeai);
            release.SetResult();
            var inner = await child.WaitAsync(Patience);

            parent.CurrentBasis.Should().Be(SubjectPrefix + "live-outer");
            parent.Current.Should().Be(Secret, "an enclosing flow that egresses while the nested one runs already counts its reads");
            parent.Access.Reason.Should().Be(AccessDenialReason.LevelTooLow);
            inner.CurrentBasis.Should().Be(SubjectPrefix + "live-inner");
            inner.Current.Should().Be(Secret);
        }
    }

    [Fact]
    public void Observe_only_raises_and_with_no_frame_does_nothing()
    {
        EgressSubject.Observe(SecurityLabel.Public);
        var none = Decide(EgressFamilies.ModelMeai);
        none.CurrentBasis.Should().Be(NoSubject, "with no frame there is nothing to raise: no subject is already the top");
        none.Current.Should().Be(SecurityLabel.SystemHigh);

        var mark = new HighWaterMark(Secret);
        using (EgressSubject.Enter("observe-raises", mark))
        {
            EgressSubject.Observe(SecurityLabel.Public);
            EgressSubject.Observe(Internal);

            mark.Current.Should().Be(Secret, "a lower label never lowers a mark");
            Decide(EgressFamilies.ModelMeai).Access.Reason.Should().Be(AccessDenialReason.LevelTooLow);
        }

        ((Action)(() => EgressSubject.Observe(null!))).Should().Throw<ArgumentNullException>().WithParameterName("label");
    }

    private static EgressDecision Decide(string family) =>
        Guard.Evaluate(new EgressRequest(family, "twin:nesting:" + Guid.NewGuid().ToString("N"), new Uri(Remote)));

    // The same subject's mark entered again on a flow that carries none of this test's frames, as a second session of
    // that subject would: what it reads there raises that mark and nothing else.
    private static async Task RaiseThroughAnotherFrame(HighWaterMark mark, SecurityLabel label)
    {
        Task raise;
        using (ExecutionContext.SuppressFlow())
        {
            raise = Task.Run(() =>
            {
                using (EgressSubject.Enter("same-subject-elsewhere", mark))
                    EgressSubject.Observe(label);
            });
        }

        await raise.WaitAsync(Patience);
        mark.Current.Should().Be(label, "the read elsewhere raised the shared mark");
    }

    // EgressSubject.Resolve is internal: reached by reflection, as the detachment tests reach Detach. It is what a
    // decision reads, without building a decision around labels this large.
    private static Func<(SecurityLabel Current, string Basis)> ResolveOnThisFlow()
    {
        var method = typeof(EgressSubject).GetMethod(
            "Resolve", BindingFlags.NonPublic | BindingFlags.Static, binder: null, Type.EmptyTypes, modifiers: null);
        method.Should().NotBeNull("EgressSubject.Resolve is the internal read of the current label");
        return method!.CreateDelegate<Func<(SecurityLabel Current, string Basis)>>();
    }

    // Its own async method, so its frames never flow back to the caller. Enclosing > parent on this flow, and a task
    // started inside the parent, with no frame of its own, that decides in a loop while this flow disposes the parent
    // in order, as a using block would. Returns the first decision the task made at the enclosing frame below the
    // parent's mark, if any.
    private static async Task<string?> DisposeWhileAFramelessTaskDecides(
        SecurityLabel enclosingFloor, SecurityLabel parentLabel, Func<(SecurityLabel Current, string Basis)> resolve)
    {
        using (EgressSubject.Enter("disposing-enclosing", new HighWaterMark(enclosingFloor)))
        {
            var parent = EgressSubject.Enter("disposing-parent", new HighWaterMark(parentLabel));
            using var started = new ManualResetEventSlim(false);
            var stop = 0;
            var task = Task.Factory.StartNew<string?>(
                () =>
                {
                    var first = resolve();
                    started.Set();
                    if (first.Basis != SubjectPrefix + "disposing-parent" || !first.Current.Dominates(parentLabel))
                        return $"before the dispose: {first.Basis} at level {first.Current.Level}, not inside the parent";

                    while (Volatile.Read(ref stop) == 0)
                    {
                        var (current, basis) = resolve();
                        if (basis == SubjectPrefix + "disposing-enclosing" && !current.Dominates(parentLabel))
                            return $"{basis} at level {current.Level}, without the parent's mark";
                    }

                    return null;
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);

            started.Wait(Patience).Should().BeTrue("the task must start deciding inside the parent before it is disposed");
            Thread.SpinWait(2000);
            parent.Dispose();
            Thread.SpinWait(2000);
            Volatile.Write(ref stop, 1);
            return await task.WaitAsync(Patience);
        }
    }

    // Its own async method, so the frames it enters never flow back to the caller: each order starts with no frame.
    // A > B > C on this flow, a fire-and-forget task started at each level, and one more started inside C and handed C.
    // The flow disposes the three in `order`, except that when C comes last the handed task disposes it instead.
    private static async Task<List<string>> DisposeInOrderWithTasksAtEveryLevel(int[] order)
    {
        string[] names = ["levels-a", "levels-b", "levels-c"];
        SecurityLabel[] labels =
        [
            new(SecurityLevel.Internal, ["LEVEL-A"]),
            new(SecurityLevel.Internal, ["LEVEL-B"]),
            new(SecurityLevel.Internal, ["LEVEL-C"]),
        ];
        var tag = string.Join(",", order.Select(i => names[i]));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var frames = new IDisposable[3];
        var tasks = new Task<EgressDecision>[3];
        for (var i = 0; i < 3; i++)
        {
            frames[i] = EgressSubject.Enter(names[i], new HighWaterMark(labels[i]));
            var level = i;
            tasks[i] = Task.Run(async () =>
            {
                await release.Task.WaitAsync(Patience);
                using (EgressSubject.Enter("levels-late-" + level, new HighWaterMark()))
                    return Decide(UnknownFamily);
            });
        }

        var c = frames[2];
        var handed = Task.Run(async () =>
        {
            await release.Task.WaitAsync(Patience);
            c.Dispose();
            using (EgressSubject.Enter("levels-handed-late", new HighWaterMark()))
                return Decide(UnknownFamily);
        });

        var handedDisposesC = order[2] == 2;
        foreach (var k in order)
        {
            if (k != 2 || !handedDisposesC)
                frames[k].Dispose();
        }

        release.SetResult();
        var failures = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var late = await tasks[i].WaitAsync(Patience);
            var floor = labels.Take(i + 1).Aggregate((x, y) => x.Join(y));
            if (!late.Current.Dominates(floor))
                failures.Add($"[{tag}] the task started at level {i} decides {late.Current}, below {floor}");
        }

        var handedLate = await handed.WaitAsync(Patience);
        var all = labels[0].Join(labels[1]).Join(labels[2]);
        if (!handedLate.Current.Dominates(all))
            failures.Add($"[{tag}] the task handed C (C disposed by the {(handedDisposesC ? "task" : "flow")}) decides {handedLate.Current}, below {all}");

        return failures;
    }

    private static IEnumerable<int[]> Permutations(int n)
    {
        if (n == 1)
        {
            yield return [0];
            yield break;
        }

        foreach (var shorter in Permutations(n - 1))
        {
            for (var at = 0; at <= shorter.Length; at++)
            {
                var longer = shorter.ToList();
                longer.Insert(at, n - 1);
                yield return longer.ToArray();
            }
        }
    }

    // Not inlined, so no local of the caller keeps the inner frame alive. The flow is inside a live enclosing frame,
    // so an out-of-order pair unwinds past its outer frame to the enclosing one.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (IDisposable Outer, WeakReference InnerMark) EnterPairAndDispose(bool outOfOrder)
    {
        var outer = EgressSubject.Enter("retain-outer", new HighWaterMark());
        var innerMark = new HighWaterMark();
        var inner = EgressSubject.Enter("retain-inner", innerMark);
        if (outOfOrder)
            outer.Dispose();

        inner.Dispose();
        outer.Dispose();
        return (outer, new WeakReference(innerMark));
    }
}
