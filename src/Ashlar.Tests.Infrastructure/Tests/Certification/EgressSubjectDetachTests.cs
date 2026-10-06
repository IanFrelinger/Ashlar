using System.Globalization;
using System.Reflection;
using Ashlar.Abstractions.Security;
using Ashlar.Abstractions.Security.Egress;
using Ashlar.Core.Application.Paths;
using Ashlar.Orchestration.Communication;
using Ashlar.Orchestration.Communication.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>
/// SPEC-007 PR 4.4: work handed to another component is not decided at the caller's mark. The internal, raise-only
/// <c>EgressSubject.RunDetached</c> runs a callback with no subject, and <see cref="AgentBus"/> starts each subscriber
/// inside it.
/// </summary>
/// <remarks>
/// <para><b>Why.</b> <see cref="AgentBus.PublishAsync"/> runs every subscriber's handler through <c>Task.Run</c>, which
/// captures the publisher's execution context, so before 4.4 a subscriber's egress was decided at the publisher's
/// mark: another component's sends attributed to, and allowed or refused by, what the publisher had read.</para>
/// <para><b>What is pinned.</b> A subscriber decides with no subject (<see cref="SecurityLabel.SystemHigh"/>) while the
/// publisher is inside a frame, and the publisher's own frame is restored when <c>PublishAsync</c> returns, also for a
/// task inside a parent frame that has ended, which a frame it enters afterwards still counts. While the callback runs
/// a decision has no subject, and a frame entered inside it starts a chain of its own whose reads never reach the
/// caller's frames. When the callback returns, or throws, the calling flow is back in exactly the caller's frame, and
/// when it throws, before any exception filter of the caller runs: the callback's own filters and finally blocks run
/// detached, and a filter of the caller decides at the caller's mark and what it reads raises the caller's frame, also
/// around a nested callback; a frame such a filter enters stays on the caller's flow until the flow disposes it. A
/// frame the callback enters and leaves undisposed is dropped from the calling flow, which decides at its own mark,
/// also after it disposes that frame. Work the callback creates keeps no subject for its whole life: a task it starts,
/// after the callback returns, after the caller's frame ends, and when the frame it was handed ends after the callback
/// returned; and a cold task, a timer, a cancellation registration and a continuation it creates, also when the caller
/// starts or triggers them. Each captures the flow where it is created, so one of those the caller created keeps the
/// caller's frame although the callback starts or triggers it. No caller holds the detachment, so none can end it out
/// of order or hand it to a task, and no program on one flow leaves the flow detached, or below a live frame it
/// entered outside every callback: not the 24 that dispose three frames entered inside the callback in every order, 0
/// to 3 of them before it returns and the rest after it, and not any of the 2,092 programs of an enclosing frame, up
/// to two more frames and up to two callbacks, nested or in turn, each checked step by step against a model of the
/// rule. The convention fact pins every call site of <c>RunDetached</c> in the repository's C#, every tree but build
/// output, dot directories and nested checkouts, and that the bus starts each subscriber inside the callback.</para>
/// <para><b>Internal surface.</b> This assembly is not in <c>Ashlar.Abstractions</c>' InternalsVisibleTo, so
/// <c>RunDetached</c> is reached by reflection, as <see cref="EgressGuardDecisionTests"/> reads the core's counters.</para>
/// <para><b>Process-global state.</b> None: frames live on each test's own flow (the program twin runs each program on
/// a copy of it), the guard has an explicit profile, and each bus is a fresh instance with a message type unique to the
/// test. The convention fact is a pure file read. It is a tripwire, not a proof: a call spelled through an alias, a
/// delegate or reflection is not seen.</para>
/// </remarks>
[Trait("Category", "Certification")]
public sealed class EgressSubjectDetachTests
{
    private const string NoSubject = "no-subject";
    private const string SubjectPrefix = "subject:";
    private const string Remote = "https://remote.example/v1/chat";
    private const string UnknownFamily = "not-a-family";

    /// <summary>The call as written, split so that this file is not one of the sites it counts.</summary>
    private const string DetachCall = "EgressSubject" + ".RunDetached(";

    private const string AgentBusPath = "src/Ashlar.Orchestration/Communication/AgentBus.cs";

    /// <summary>Every production dispatch point that must not inherit its caller's subject (SPEC-007 PR 4.4).</summary>
    private static readonly string[] ExpectedSites = [AgentBusPath + " x1"];

    /// <summary>Below this many scanned files the scan is taken to have lost its reach, not to have found nothing.</summary>
    private const int ScannedFileFloor = 1000;

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    /// <summary>A guard with an explicit profile, so no decision here reads the environment.</summary>
    private static readonly EgressGuard Guard = new("full");

    private static readonly SecurityLabel Internal = new(SecurityLevel.Internal);
    private static readonly SecurityLabel Confidential = new(SecurityLevel.Confidential);
    private static readonly SecurityLabel Secret = new(SecurityLevel.Secret);

    [Fact]
    public async Task An_AgentBus_subscriber_runs_with_no_subject_whatever_the_publisher_has_read()
    {
        var bus = new AgentBus(NullLogger<AgentBus>.Instance);
        var messageType = "egress-detach-" + Guid.NewGuid().ToString("N");
        var decided = new TaskCompletionSource<EgressDecision>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var subscription = await bus.SubscribeAsync(messageType, (_, _) =>
        {
            decided.TrySetResult(Decide(EgressFamilies.ModelMeai));
            return Task.CompletedTask;
        });

        using (EgressSubject.Enter("bus-publisher", new HighWaterMark(Internal)))
        {
            await bus.PublishAsync(new OutputEmitted
            {
                MessageId = Guid.NewGuid().ToString("N"),
                FromAgentId = "bus-publisher",
                MessageType = messageType,
                Output = "a result the publisher computed",
            });

            var publisher = Decide(EgressFamilies.ModelMeai);
            publisher.CurrentBasis.Should().Be(SubjectPrefix + "bus-publisher", "publishing restores the publisher's own frame");
            publisher.Current.Should().Be(Internal);

            // Waited for while the publisher's frame is still live, so a subscriber that inherited it would show it.
            var subscriber = await decided.Task.WaitAsync(Patience);
            subscriber.CurrentBasis.Should().Be(NoSubject, "a subscriber is another component, never decided at the publisher's mark");
            subscriber.Current.Should().Be(SecurityLabel.SystemHigh);
            subscriber.Access.Reason.Should().Be(AccessDenialReason.SystemHighData, "no subject fails closed upward");
        }
    }

    [Fact]
    public async Task A_publish_from_a_task_inside_an_ended_parent_frame_keeps_the_parent_for_a_frame_entered_after_it()
    {
        var bus = new AgentBus(NullLogger<AgentBus>.Instance);
        var parentDisposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<EgressDecision> child;

        using (EgressSubject.Enter("bus-ended-parent", new HighWaterMark(Secret)))
        {
            child = Task.Run(async () =>
            {
                await parentDisposed.Task.WaitAsync(Patience);

                // The bus detaches this flow while it dispatches, then restores the frame the flow was in.
                await bus.PublishAsync(new OutputEmitted
                {
                    MessageId = Guid.NewGuid().ToString("N"),
                    FromAgentId = "bus-ended-parent",
                    MessageType = "egress-detach-" + Guid.NewGuid().ToString("N"),
                    Output = "a result the task computed inside the parent",
                });

                using (EgressSubject.Enter("bus-ended-late", new HighWaterMark()))
                    return Decide(EgressFamilies.ModelMeai);
            });
        }

        parentDisposed.SetResult();
        var late = await child.WaitAsync(Patience);

        late.CurrentBasis.Should().Be(SubjectPrefix + "bus-ended-late");
        late.Current.Should().Be(
            Secret, "the detachment restores exactly the frame the task was in, the ended parent, so a later frame still counts it");
        late.Access.Reason.Should().Be(AccessDenialReason.LevelTooLow);
    }

    [Fact]
    public void RunDetached_leaves_every_frame_while_the_callback_runs_and_then_restores_the_callers()
    {
        var callerMark = new HighWaterMark(Internal);

        using (EgressSubject.Enter("detach-caller", callerMark))
        {
            RunDetached(() =>
            {
                var detached = Decide(EgressFamilies.ModelMeai);
                detached.CurrentBasis.Should().Be(NoSubject);
                detached.Current.Should().Be(SecurityLabel.SystemHigh, "it only raises: no subject is the top");
                detached.Access.Reason.Should().Be(AccessDenialReason.SystemHighData);

                var innerMark = new HighWaterMark();
                using (EgressSubject.Enter("detach-inner", innerMark))
                {
                    var inner = Decide(UnknownFamily);
                    inner.CurrentBasis.Should().Be(SubjectPrefix + "detach-inner");
                    inner.Current.Should().Be(SecurityLabel.Public, "a frame entered under a detachment starts a chain of its own");

                    innerMark.Observe(Secret);
                }

                Decide(UnknownFamily).CurrentBasis.Should().Be(NoSubject, "disposing the inner frame returns to the detachment");
            });

            callerMark.Current.Should().Be(Internal, "what was read under the detachment never reaches the caller's frames");
            EgressSubjectNestingTests.RestorePathLength().Should().Be(1, "the calling flow is back in exactly the caller's frame");

            var restored = Decide(EgressFamilies.ModelMeai);
            restored.CurrentBasis.Should().Be(SubjectPrefix + "detach-caller", "the caller's frame is restored when the callback returns");
            restored.Current.Should().Be(Internal);
            restored.Access.Allowed.Should().BeTrue("Internal data may go to an Internal model: {0}", restored.Access);
        }
    }

    [Fact]
    public void RunDetached_restores_the_callers_frame_when_the_callback_throws()
    {
        var thrown = new InvalidOperationException("the work handed off failed");
        IDisposable? left = null;

        using (EgressSubject.Enter("detach-throw-caller", new HighWaterMark(Internal)))
        {
            var run = () => RunDetached(() =>
            {
                left = EgressSubject.Enter("detach-throw-left", new HighWaterMark(Secret));
                throw thrown;
            });

            run.Should().Throw<InvalidOperationException>().Which.Should().BeSameAs(thrown, "the callback's exception reaches the caller");
            EgressSubjectNestingTests.RestorePathLength().Should().Be(1, "the calling flow is back in exactly the caller's frame");
            var back = Decide(EgressFamilies.ModelMeai);
            back.CurrentBasis.Should().Be(SubjectPrefix + "detach-throw-caller", "the caller's frame is restored on every path");
            back.Current.Should().Be(Internal, "the frame the callback entered before it threw is not on the calling flow");
            left!.Dispose();
        }
    }

    [Fact]
    public void RunDetached_writes_the_flows_frame_only_inside_a_try_that_catches_everything_or_in_that_catch()
    {
        // An asynchronous abort, such as the one ControlledExecution.Run raises on .NET 8 and later, can arrive between
        // any two instructions outside an exception handler. One that arrives inside a try that catches everything goes
        // to that catch, which puts the caller's frame back, and the runtime holds it back while a catch runs. A write of
        // the flow's frame outside both, such as a restore after the try, leaves a window in which the abort leaves
        // RunDetached with the flow detached: a filter of the caller for it decides below the caller's mark, what it
        // reads misses the caller's frames, and the flow stays detached afterwards. The abort cannot be raised here
        // without risking the test host, so the compiled method is read instead.
        using var module = Mono.Cecil.ModuleDefinition.ReadModule(typeof(EgressSubject).Assembly.Location);
        var method = module.GetType(typeof(EgressSubject).FullName).Methods.Single(m => m.Name == "RunDetached");

        var catchAll = method.Body.ExceptionHandlers
            .Where(h => h.HandlerType == Mono.Cecil.Cil.ExceptionHandlerType.Catch && h.CatchType.FullName == "System.Object")
            .ToList();
        catchAll.Should().ContainSingle("RunDetached puts the caller's frame back in one catch that matches every exception");
        var clause = catchAll[0];

        var writes = method.Body.Instructions
            .Where(i => i.Operand is Mono.Cecil.MethodReference { Name: "set_Value" } called
                && called.DeclaringType.Name == "AsyncLocal`1")
            .ToList();
        writes.Should().HaveCount(3, "it detaches the flow once and puts the caller's frame back twice: when the callback returns, and in the catch");

        var unprotected = writes
            .Where(i => !Within(i, clause.TryStart, clause.TryEnd) && !Within(i, clause.HandlerStart, clause.HandlerEnd))
            .Select(i => $"IL_{i.Offset:x4}")
            .ToList();
        string.Join(", ", unprotected).Should().BeEmpty(
            "an asynchronous abort next to a write of the flow's frame outside the try and its catch would leave RunDetached with the flow detached");
    }

    [Fact]
    public void A_callers_exception_filter_runs_in_the_callers_frame_when_the_callback_throws()
    {
        // An exception's first pass runs every filter up the stack before any finally block, so a restore made only in a
        // finally would come after the caller's filter. The filter is the caller's code, inside the caller's live frame.
        var seen = new List<(EgressDecision Decision, int Length)>();

        using (EgressSubject.Enter("filter-caller", new HighWaterMark(Secret)))
        {
            try
            {
                RunDetached(() =>
                {
                    using (EgressSubject.Enter("filter-callback", new HighWaterMark()))
                        throw new InvalidOperationException("the work handed off failed");
                });
            }
            catch (InvalidOperationException) when (Record(seen, EgressFamilies.ModelMeai))
            {
            }

            Decide(UnknownFamily).CurrentBasis.Should().Be(SubjectPrefix + "filter-caller");
        }

        // The same for a frame entered inside a callback, around a nested callback that throws.
        RunDetached(() =>
        {
            using (EgressSubject.Enter("filter-nested-caller", new HighWaterMark(Secret)))
            {
                try
                {
                    RunDetached(() =>
                    {
                        using (EgressSubject.Enter("filter-nested-callback", new HighWaterMark()))
                            throw new InvalidOperationException("the nested work failed");
                    });
                }
                catch (InvalidOperationException) when (Record(seen, EgressFamilies.ModelMeai))
                {
                }
            }
        });

        seen.Should().HaveCount(2);
        string[] callers = ["filter-caller", "filter-nested-caller"];
        for (var i = 0; i < 2; i++)
        {
            var (decision, length) = seen[i];
            decision.CurrentBasis.Should().Be(
                SubjectPrefix + callers[i], "the caller's filter runs in the caller's frame, never in the frame the callback entered");
            decision.Current.Should().Be(Secret, "the caller's filter decides at the caller's mark");
            decision.Access.Reason.Should().Be(AccessDenialReason.LevelTooLow, "Secret data may not go to an Internal model");
            length.Should().Be(i == 0 ? 1 : 2, "the flow is back in exactly the caller's frame before the caller's filter runs");
        }
    }

    [Fact]
    public void A_read_in_a_callers_exception_filter_raises_the_callers_frame()
    {
        var callerMark = new HighWaterMark(Internal);

        using (EgressSubject.Enter("filter-reader", callerMark))
        {
            try
            {
                RunDetached(() => throw new InvalidOperationException("the work handed off failed"));
            }
            catch (InvalidOperationException) when (Observed(Confidential))
            {
            }

            callerMark.Current.Should().Be(Confidential, "what the caller's filter observes is the caller's read");

            try
            {
                RunDetached(() => throw new InvalidOperationException("the work handed off failed again"));
            }
            catch (InvalidOperationException) when (Read(Secret))
            {
            }

            callerMark.Current.Should().Be(Secret, "and so is what a read scope begun and ended in the caller's filter reports");
            var after = Decide(EgressFamilies.ModelMeai);
            after.CurrentBasis.Should().Be(SubjectPrefix + "filter-reader");
            after.Current.Should().Be(Secret);
            after.Access.Reason.Should().Be(AccessDenialReason.LevelTooLow, "the caller read Secret, in its own filter, before it decided");
        }
    }

    [Fact]
    public void The_callbacks_filters_and_finally_blocks_run_detached_and_only_then_the_callers_filter_in_the_callers_frame()
    {
        var order = new List<string>();
        var seen = new List<(EgressDecision Decision, int Length)>();

        using (EgressSubject.Enter("unwind-caller", new HighWaterMark(Internal)))
        {
            try
            {
                RunDetached(() =>
                {
                    try
                    {
                        throw new InvalidOperationException("the work handed off failed");
                    }
                    catch (InvalidOperationException) when (Declined(order, "callback filter", seen))
                    {
                    }
                    finally
                    {
                        Step(order, "callback finally", seen);
                    }
                });
            }
            catch (InvalidOperationException) when (Step(order, "caller filter", seen))
            {
            }
        }

        string[] expected = ["callback filter", "callback finally", "caller filter"];
        order.Should().Equal(
            expected,
            "the callback unwinds detached, and the caller's frame is back before the caller's first filter runs");
        seen[0].Decision.CurrentBasis.Should().Be(NoSubject, "a filter inside the callback runs detached");
        seen[1].Decision.CurrentBasis.Should().Be(NoSubject, "a finally block inside the callback runs detached");
        seen[1].Decision.Current.Should().Be(SecurityLabel.SystemHigh);
        seen[2].Decision.CurrentBasis.Should().Be(SubjectPrefix + "unwind-caller", "the caller's filter runs in the caller's frame");
        seen[2].Decision.Current.Should().Be(Internal);
        seen[2].Length.Should().Be(1, "exactly the caller's frame");
    }

    [Fact]
    public void A_frame_a_callers_exception_filter_enters_stays_on_the_callers_flow_until_the_flow_disposes_it()
    {
        // The caller's filter runs in the rethrow's first pass, after RunDetached has put the caller's frame back.
        // Anything RunDetached did in the second pass, such as a finally block, would run after that filter and could
        // drop a frame the filter entered, which the flow never disposed.
        var filterMark = new HighWaterMark(Secret);
        IDisposable? entered = null;

        using (EgressSubject.Enter("filter-frame-caller", new HighWaterMark(Internal)))
        {
            try
            {
                RunDetached(() => throw new InvalidOperationException("the work handed off failed"));
            }
            catch (InvalidOperationException) when (Entered("filter-frame", filterMark, ref entered))
            {
            }

            var inside = Decide(EgressFamilies.ModelMeai);
            inside.CurrentBasis.Should().Be(
                SubjectPrefix + "filter-frame", "the flow never disposed the frame its filter entered, so it is still the flow's head");
            inside.Current.Should().Be(Secret, "the flow decides at that frame's mark, joined with the caller's");
            inside.Access.Reason.Should().Be(AccessDenialReason.LevelTooLow);
            EgressSubjectNestingTests.RestorePathLength().Should().Be(2, "the filter's frame, inside the caller's");

            entered!.Dispose();
            EgressSubjectNestingTests.RestorePathLength().Should().Be(1, "disposing its head takes the flow back to the caller's frame");
            var back = Decide(EgressFamilies.ModelMeai);
            back.CurrentBasis.Should().Be(SubjectPrefix + "filter-frame-caller");
            back.Current.Should().Be(Secret, "the filter's frame observed its mark into the caller's frame when it ended");
        }
    }

    [Fact]
    public async Task A_task_or_callback_the_caller_creates_keeps_the_callers_frame_when_RunDetached_starts_or_triggers_it()
    {
        // A Task, a Timer, a cancellation registration and a continuation capture the flow where they are created, not
        // where they start or fire. So one the caller created and the callback starts or triggers runs in the caller's
        // frame, and one the callback created runs detached, wherever it is started or triggered.
        var callerMade = new Dictionary<string, Task<EgressDecision>>();
        var callbackMade = new Dictionary<string, Task<EgressDecision>>();

        using (EgressSubject.Enter("created-caller", new HighWaterMark(Secret)))
        {
            var cold = new Task<EgressDecision>(() => Decide(EgressFamilies.ModelMeai));
            var (timer, timerSeen) = IdleTimer();
            using var cancel = new CancellationTokenSource();
            var registrationSeen = Signal();
            using var registration = cancel.Token.Register(() => registrationSeen.TrySetResult(Decide(EgressFamilies.ModelMeai)));
            var antecedent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var continuation = antecedent.Task.ContinueWith(_ => Decide(EgressFamilies.ModelMeai), TaskScheduler.Default);

            Task<EgressDecision>? cold2 = null;
            Timer? timer2 = null;
            Task<EgressDecision>? timer2Seen = null;
            CancellationTokenSource? cancel2 = null;
            CancellationTokenRegistration registration2 = default;
            var registration2Seen = Signal();
            var antecedent2 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<EgressDecision>? continuation2 = null;

            RunDetached(() =>
            {
                // Created by the caller, started or triggered here.
                cold.Start();
                timer.Change(0, Timeout.Infinite);
                cancel.Cancel();
                antecedent.SetResult();

                // Created here. Task.Run starts at once; the rest are started or triggered by the caller after the
                // callback returns.
                callbackMade["Task.Run"] = Task.Run(() => Decide(EgressFamilies.ModelMeai));
                cold2 = new Task<EgressDecision>(() => Decide(EgressFamilies.ModelMeai));
                (timer2, timer2Seen) = IdleTimer();
                cancel2 = new CancellationTokenSource();
                registration2 = cancel2.Token.Register(() => registration2Seen.TrySetResult(Decide(EgressFamilies.ModelMeai)));
                continuation2 = antecedent2.Task.ContinueWith(_ => Decide(EgressFamilies.ModelMeai), TaskScheduler.Default);
            });

            Decide(UnknownFamily).CurrentBasis.Should().Be(SubjectPrefix + "created-caller", "the callback has returned");
            cold2!.Start();
            timer2!.Change(0, Timeout.Infinite);
            cancel2!.Cancel();
            antecedent2.SetResult();

            callerMade["a cold task"] = cold;
            callerMade["a timer"] = timerSeen;
            callerMade["a registration"] = registrationSeen.Task;
            callerMade["a continuation"] = continuation;
            callbackMade["a cold task"] = cold2;
            callbackMade["a timer"] = timer2Seen!;
            callbackMade["a registration"] = registration2Seen.Task;
            callbackMade["a continuation"] = continuation2!;

            // Every decision is awaited while the caller's frame is live, so a frame that ended cannot explain what it
            // shows.
            foreach (var (what, decided) in callerMade)
            {
                var decision = await decided.WaitAsync(Patience);
                decision.CurrentBasis.Should().Be(
                    SubjectPrefix + "created-caller",
                    "{0} the caller created captured the caller's flow, so it keeps the caller's frame although the callback started or triggered it",
                    what);
                decision.Current.Should().Be(Secret, "{0} the caller created decides at the caller's mark", what);
                decision.Access.Reason.Should().Be(AccessDenialReason.LevelTooLow);
            }

            foreach (var (what, decided) in callbackMade)
            {
                var decision = await decided.WaitAsync(Patience);
                decision.CurrentBasis.Should().Be(
                    NoSubject, "{0} the callback created captured the detachment, wherever it was started or triggered", what);
                decision.Current.Should().Be(SecurityLabel.SystemHigh);
                decision.Access.Reason.Should().Be(AccessDenialReason.SystemHighData);
            }

            timer.Dispose();
            timer2.Dispose();
            registration2.Dispose();
            cancel2.Dispose();
        }

        callerMade.Should().HaveCount(4);
        callbackMade.Should().HaveCount(5);
    }

    [Fact]
    public async Task A_thread_or_a_timers_timer_keeps_the_frame_of_the_flow_that_starts_it_not_of_the_one_that_creates_it()
    {
        // Unlike a Task, a System.Threading.Timer, a registration or a continuation, a Thread and a System.Timers.Timer
        // capture the flow when they are started (Thread.Start, and Timer.Start, which builds the timer underneath). So
        // one the caller created and the callback starts runs detached, and one the callback created and the caller
        // starts after it returned runs in the caller's frame: a site creates and starts the work it hands off inside
        // the callback.
        var startedInside = new Dictionary<string, Task<EgressDecision>>();
        var startedByCaller = new Dictionary<string, Task<EgressDecision>>();

        using (EgressSubject.Enter("started-caller", new HighWaterMark(Secret)))
        {
            var (thread, threadSeen) = IdleThread();
            var (timer, timerSeen) = IdleTimersTimer();
            Thread? thread2 = null;
            Task<EgressDecision>? thread2Seen = null;
            System.Timers.Timer? timer2 = null;
            Task<EgressDecision>? timer2Seen = null;

            RunDetached(() =>
            {
                // Created by the caller, started here.
                thread.Start();
                timer.Start();

                // Created here, started by the caller after the callback returns.
                (thread2, thread2Seen) = IdleThread();
                (timer2, timer2Seen) = IdleTimersTimer();
            });

            Decide(UnknownFamily).CurrentBasis.Should().Be(SubjectPrefix + "started-caller", "the callback has returned");
            thread2!.Start();
            timer2!.Start();

            startedInside["a thread"] = threadSeen;
            startedInside["a System.Timers.Timer"] = timerSeen;
            startedByCaller["a thread"] = thread2Seen!;
            startedByCaller["a System.Timers.Timer"] = timer2Seen!;

            // Every decision is awaited while the caller's frame is live, so a frame that ended cannot explain what it
            // shows.
            foreach (var (what, decided) in startedInside)
            {
                var decision = await decided.WaitAsync(Patience);
                decision.CurrentBasis.Should().Be(
                    NoSubject, "{0} the callback started captured the detachment, although the caller created it", what);
                decision.Current.Should().Be(SecurityLabel.SystemHigh);
                decision.Access.Reason.Should().Be(AccessDenialReason.SystemHighData);
            }

            foreach (var (what, decided) in startedByCaller)
            {
                var decision = await decided.WaitAsync(Patience);
                decision.CurrentBasis.Should().Be(
                    SubjectPrefix + "started-caller",
                    "{0} the caller started captured the caller's flow, although the callback created it",
                    what);
                decision.Current.Should().Be(Secret, "{0} the caller started decides at the caller's mark", what);
                decision.Access.Reason.Should().Be(AccessDenialReason.LevelTooLow);
            }

            thread.Join(Patience).Should().BeTrue();
            thread2.Join(Patience).Should().BeTrue();
            timer.Dispose();
            timer2.Dispose();
        }

        startedInside.Should().HaveCount(2);
        startedByCaller.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_task_started_inside_RunDetached_keeps_no_subject_after_it_returns_and_after_the_callers_frame_ends()
    {
        var returned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var decidedAfterReturn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callerEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callerMark = new HighWaterMark(Internal);
        Task<(EgressDecision AfterReturn, EgressDecision AfterCallerEnded, EgressDecision Late)>? task = null;

        using (EgressSubject.Enter("detach-spawner", callerMark))
        {
            RunDetached(() => task = Task.Run(async () =>
            {
                await returned.Task.WaitAsync(Patience);
                var afterReturn = Decide(EgressFamilies.ModelMeai);
                EgressSubject.Observe(Secret);
                decidedAfterReturn.SetResult();

                await callerEnded.Task.WaitAsync(Patience);
                var afterCallerEnded = Decide(EgressFamilies.ModelMeai);
                using (EgressSubject.Enter("detach-spawned-late", new HighWaterMark()))
                    return (afterReturn, afterCallerEnded, Decide(UnknownFamily));
            }));

            Decide(EgressFamilies.ModelMeai).CurrentBasis.Should().Be(SubjectPrefix + "detach-spawner", "the callback has returned");

            // The task decides after the callback returned, while the caller's frame is still live.
            returned.SetResult();
            await decidedAfterReturn.Task.WaitAsync(Patience);
        }

        callerEnded.SetResult();
        var (afterReturn, afterCallerEnded, late) = await task!.WaitAsync(Patience);

        afterReturn.CurrentBasis.Should().Be(NoSubject, "the task never falls back to the caller's frame, which is still live");
        afterReturn.Current.Should().Be(SecurityLabel.SystemHigh);
        afterCallerEnded.CurrentBasis.Should().Be(NoSubject, "nor once the caller's frame has ended: the task keeps the detachment for its life");
        afterCallerEnded.Current.Should().Be(SecurityLabel.SystemHigh);
        late.CurrentBasis.Should().Be(SubjectPrefix + "detach-spawned-late");
        late.Current.Should().Be(
            SecurityLabel.Public, "a frame the task enters starts a chain of its own, which the caller's mark never reaches");
        callerMark.Current.Should().Be(Internal, "and what the task read under the detachment never reaches the caller's frame");
    }

    [Fact]
    public async Task A_task_started_inside_RunDetached_keeps_no_subject_when_the_detachment_ends_before_its_frame()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callerMark = new HighWaterMark(Internal);
        var detachedMark = new HighWaterMark();
        Task<(EgressDecision InFrame, EgressDecision After)>? task = null;

        using (EgressSubject.Enter("detach-order-caller", callerMark))
        {
            RunDetached(() =>
            {
                var detached = EgressSubject.Enter("detach-order-detached", detachedMark);

                // Started inside a frame entered under the detachment, and handed that frame to end. The callback returns
                // while that frame is still its head, so the detachment ends first.
                task = Task.Run(async () =>
                {
                    await release.Task.WaitAsync(Patience);
                    EgressSubject.Observe(Secret);
                    var inFrame = Decide(UnknownFamily);
                    detached.Dispose();
                    return (inFrame, Decide(EgressFamilies.ModelMeai));
                });
            });

            Decide(EgressFamilies.ModelMeai).CurrentBasis.Should().Be(
                SubjectPrefix + "detach-order-caller", "the frame the callback left undisposed is dropped from this flow");
            release.SetResult();
            var (inFrame, after) = await task!.WaitAsync(Patience);

            inFrame.Current.Should().Be(Secret);
            detachedMark.Current.Should().Be(Secret);
            callerMark.Current.Should().Be(Internal, "what was read under the detachment never reaches the caller's frames");
            after.CurrentBasis.Should().Be(
                NoSubject, "a task started under a detachment goes back to it, never to the caller's frame, whichever order they end in");
            after.Current.Should().Be(SecurityLabel.SystemHigh, "the task read Secret under the detachment; no subject is the top");
            after.Access.Reason.Should().Be(AccessDenialReason.SystemHighData);
        }
    }

    [Fact]
    public void A_frame_entered_inside_RunDetached_and_left_undisposed_is_dropped_from_the_calling_flow()
    {
        var callerMark = new HighWaterMark(Internal);
        var leftMark = new HighWaterMark();
        IDisposable? left = null;

        using (EgressSubject.Enter("dropped-caller", callerMark))
        {
            RunDetached(() =>
            {
                left = EgressSubject.Enter("dropped-left", leftMark);
                EgressSubject.Observe(Secret);
            });

            leftMark.Current.Should().Be(Secret);
            EgressSubjectNestingTests.RestorePathLength().Should().Be(
                1, "the calling flow is back in exactly the caller's frame, and nothing the callback entered is on it");
            var back = Decide(EgressFamilies.ModelMeai);
            back.CurrentBasis.Should().Be(SubjectPrefix + "dropped-caller", "the frame the callback entered and did not dispose is dropped on return");
            back.Current.Should().Be(Internal, "the caller decides at its own mark: a frame entered under a detachment never reaches the caller");
            back.Access.Allowed.Should().BeTrue("Internal data may go to an Internal model: {0}", back.Access);

            // On the calling flow, after the callback returned: not this flow's head, so no flow moves.
            left!.Dispose();
            EgressSubjectNestingTests.RestorePathLength().Should().Be(1);
            callerMark.Current.Should().Be(Internal, "its mark reaches only the frames it was entered inside, which end at the detachment");
            Decide(UnknownFamily).CurrentBasis.Should().Be(SubjectPrefix + "dropped-caller");

            using (EgressSubject.Enter("dropped-late", new HighWaterMark()))
            {
                var late = Decide(UnknownFamily);
                late.CurrentBasis.Should().Be(SubjectPrefix + "dropped-late");
                late.Current.Should().Be(Internal, "a frame entered afterwards is inside the caller's frame only");
            }

            // And what the flow reads after the callback returned reaches the caller's frame, which it is back inside.
            EgressSubject.Observe(Confidential);
            using (var read = EgressSubject.BeginRead())
            {
                read.Report(Confidential);
                read.Complete();
            }

            callerMark.Current.Should().Be(Confidential, "reads after the callback returned are the caller's reads");
            Decide(EgressFamilies.ModelMeai).Access.Reason.Should().Be(AccessDenialReason.LevelTooLow);
        }
    }

    [Fact]
    public void A_callback_that_returns_with_frames_it_entered_undisposed_never_leaves_the_flow_below_the_callers_frame()
    {
        var callerLabel = new SecurityLabel(SecurityLevel.Secret, ["DETACH-PROBE-CALLER"]);
        var failures = new List<string>();
        var programs = 0;

        using (EgressSubject.Enter("detach-probe-caller", new HighWaterMark(callerLabel)))
        {
            // A callback that returns while frames it entered are still undisposed. Three frames entered inside, disposed
            // in every order (6), the first `inside` of them before the callback returns and the rest after it (4 splits):
            // 24 programs, one flow.
            foreach (var order in Permutations(3))
            {
                for (var inside = 0; inside <= 3; inside++)
                {
                    programs++;
                    var tag = $"[order {string.Join(",", order)}, {inside} disposed inside]";
                    var frames = new IDisposable[3];
                    var disposedInside = order.Take(inside).ToArray();
                    RunDetached(() =>
                    {
                        for (var i = 0; i < 3; i++)
                            frames[i] = EgressSubject.Enter("detach-probe-" + i.ToString(CultureInfo.InvariantCulture), new HighWaterMark());

                        foreach (var k in disposedInside)
                            frames[k].Dispose();
                    });

                    failures.AddRange(AtTheProbeCallersFrame(callerLabel, $"{tag} on return"));
                    foreach (var k in order.Skip(inside))
                    {
                        frames[k].Dispose();
                        failures.AddRange(AtTheProbeCallersFrame(callerLabel, $"{tag} after disposing frame {k} outside"));
                    }

                    using (EgressSubject.Enter("detach-probe-late", new HighWaterMark()))
                    {
                        var late = Decide(UnknownFamily);
                        if (!late.Current.Dominates(callerLabel))
                            failures.Add($"{tag} a frame entered afterwards decided {late.CurrentBasis} at {late.Current}");
                    }
                }
            }
        }

        programs.Should().Be(24);
        failures.Should().BeEmpty(
            "no sequence on one flow leaves it detached, or below the live caller frame it is inside; every failure: {0}",
            string.Join(" || ", failures));
    }

    [Fact]
    public void Every_program_of_frames_and_detached_callbacks_on_one_flow_leaves_only_the_head_it_disposes_and_never_writes_down()
    {
        var clean = ExecutionContext.Capture();
        clean.Should().NotBeNull("the test runs with the execution context flowing");
        var failures = new List<string>();
        var programs = 0;
        var leftOnTheFlow = 0;

        // Inside a live enclosing frame: up to two more frames and up to two callbacks run detached, nested or in turn and
        // never empty, and every frame disposed by the program's end, in any order, inside or outside any callback, the
        // enclosing frame included. Each program runs on its own copy of this test's flow, which has no frame, and the
        // model of the rule must agree with it after every step.
        foreach (var program in Programs(frames: 2, callbacks: 2))
        {
            programs++;
            ExecutionContext.Run(clean!.CreateCopy(), _ => leftOnTheFlow += new ProgramRun(program, failures).Run(), null);
        }

        programs.Should().Be(2092);
        failures.Should().BeEmpty(
            "a flow leaves a frame only by disposing its own head, a callback run detached returns to exactly the caller's frame, and a flow outside every callback never decides below a live frame it entered there; {0} failures, the first: {1}",
            failures.Count,
            string.Join(" || ", failures.Take(10)));
        leftOnTheFlow.Should().Be(
            2474, "the frames each program disposed out of order stay on its flow (a known limit, fail closed): growth is pinned, not hidden");
    }

    [Fact]
    public void RunDetached_is_called_only_at_the_listed_dispatch_points()
    {
        var root = RepoPathResolver.FindRepoRoot();
        var (sites, scanned) = Sites(root);

        scanned.Should().BeGreaterThanOrEqualTo(
            ScannedFileFloor, "the scan must reach the repository's C#; an emptied scan proves nothing");
        sites.Should().Equal(
            ExpectedSites,
            "RunDetached runs work with no subject. It belongs only where work is handed to another component (AgentBus "
            + "subscriber dispatch). A new site needs a reason recorded in the design and a row here; a missing one "
            + "means a dispatch point decides at its caller's mark again");
    }

    [Fact]
    public void The_AgentBus_dispatch_starts_every_subscriber_inside_the_detachment()
    {
        var root = RepoPathResolver.FindRepoRoot();
        var text = File.ReadAllText(Path.Combine(root, AgentBusPath));

        var call = text.IndexOf(DetachCall + "() =>", StringComparison.Ordinal);
        call.Should().BeGreaterThanOrEqualTo(0, "PublishAsync dispatches its subscribers inside the callback it runs detached");

        var open = text.IndexOf('{', call);
        open.Should().BeGreaterThan(call);
        var close = MatchingBrace(text, open);
        close.Should().BeGreaterThan(open, "the detached callback must close");

        var inside = text.Substring(open, close - open + 1);
        var outside = text.Remove(open, close - open + 1);

        inside.Should().Contain("Task.Run(", "each subscriber's task starts inside the detached callback");
        inside.Should().Contain("subscription.Handler(", "and runs the subscriber's handler");
        outside.Should().NotContain("Task.Run(", "no dispatch task starts outside it");
        outside.Should().NotContain(".Handler(", "no handler runs outside it");
    }

    /// <summary>The internal <c>EgressSubject.RunDetached</c>, reached by reflection.</summary>
    internal static void RunDetached(Action start)
    {
        var method = typeof(EgressSubject).GetMethod(
            "RunDetached", BindingFlags.NonPublic | BindingFlags.Static, binder: null, [typeof(Action)], modifiers: null);
        method.Should().NotBeNull("EgressSubject.RunDetached is the internal, raise-only way to run work with no subject (SPEC-007 PR 4.4)");
        method!.CreateDelegate<Action<Action>>()(start);
    }

    private static EgressDecision Decide(string family) =>
        Guard.Evaluate(new EgressRequest(family, "twin:detach:" + Guid.NewGuid().ToString("N"), new Uri(Remote)));

    // Exception filters: each records where it ran and returns true, so the catch it guards is taken.
    private static bool Record(List<(EgressDecision Decision, int Length)> seen, string family)
    {
        seen.Add((Decide(family), EgressSubjectNestingTests.RestorePathLength()));
        return true;
    }

    private static bool Step(List<string> order, string step, List<(EgressDecision Decision, int Length)> seen)
    {
        order.Add(step);
        return Record(seen, UnknownFamily);
    }

    // A filter that records where it ran and declines, so the exception goes on up the stack.
    private static bool Declined(List<string> order, string step, List<(EgressDecision Decision, int Length)> seen) =>
        !Step(order, step, seen);

    // A filter that enters a frame on the flow it runs on and leaves it open, so the catch it guards is taken.
    private static bool Entered(string subjectId, HighWaterMark mark, ref IDisposable? entered)
    {
        entered = EgressSubject.Enter(subjectId, mark);
        return true;
    }

    private static TaskCompletionSource<EgressDecision> Signal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    // A timer created on the calling flow, so it captures that flow, and not yet due: it decides once when it fires.
    private static (Timer Timer, Task<EgressDecision> Decided) IdleTimer()
    {
        var decided = Signal();
        var timer = new Timer(_ => decided.TrySetResult(Decide(EgressFamilies.ModelMeai)), null, Timeout.Infinite, Timeout.Infinite);
        return (timer, decided.Task);
    }

    // A thread not yet started: it captures the flow that starts it, and decides once when it runs.
    private static (Thread Thread, Task<EgressDecision> Decided) IdleThread()
    {
        var decided = Signal();
        var thread = new Thread(() => decided.TrySetResult(Decide(EgressFamilies.ModelMeai))) { IsBackground = true };
        return (thread, decided.Task);
    }

    // A System.Timers.Timer not yet started: it captures the flow that starts it, and decides once when it elapses.
    private static (System.Timers.Timer Timer, Task<EgressDecision> Decided) IdleTimersTimer()
    {
        var decided = Signal();
        var timer = new System.Timers.Timer(1) { AutoReset = false };
        timer.Elapsed += (_, _) => decided.TrySetResult(Decide(EgressFamilies.ModelMeai));
        return (timer, decided.Task);
    }

    // Whether an instruction lies in [start, end) of a method body; a null end runs to the end of the body.
    private static bool Within(
        Mono.Cecil.Cil.Instruction instruction, Mono.Cecil.Cil.Instruction start, Mono.Cecil.Cil.Instruction? end) =>
        instruction.Offset >= start.Offset && (end is null || instruction.Offset < end.Offset);

    private static bool Observed(SecurityLabel label)
    {
        EgressSubject.Observe(label);
        return true;
    }

    private static bool Read(SecurityLabel label)
    {
        using var read = EgressSubject.BeginRead();
        read.Report(label);
        read.Complete();
        return true;
    }

    // The D1 probe's check, after each step outside the callback: back in exactly the caller's frame, at its mark.
    private static List<string> AtTheProbeCallersFrame(SecurityLabel callerLabel, string step)
    {
        var decision = Decide(UnknownFamily);
        var length = EgressSubjectNestingTests.RestorePathLength();
        return decision.CurrentBasis == SubjectPrefix + "detach-probe-caller" && decision.Current.Dominates(callerLabel) && length == 1
            ? []
            : [$"{step}: {decision.CurrentBasis} at {decision.Current}, {length} frames on the flow"];
    }

    // Every program over the ops "E" (enter the next frame), "X<i>" (dispose frame i; the enclosing frame is 0), "(" (start
    // a callback run detached) and ")" (return from it), in order: a program is complete once every callback has
    // returned and every frame but the enclosing one is disposed, and no callback is empty.
    private static List<string[]> Programs(int frames, int callbacks)
    {
        var programs = new List<string[]>();
        Extend([], entered: 1, live: 1, open: 0, started: 0);
        return programs;

        void Extend(List<string> program, int entered, int live, int open, int started)
        {
            if (open == 0 && (live & ~1) == 0 && program.Count > 0)
                programs.Add([.. program]);
            if (entered <= frames)
                Extend([.. program, "E"], entered + 1, live | (1 << entered), open, started);
            for (var i = 0; i < entered; i++)
            {
                if ((live & (1 << i)) != 0)
                    Extend([.. program, "X" + i.ToString(CultureInfo.InvariantCulture)], entered, live & ~(1 << i), open, started);
            }

            if (started < callbacks)
                Extend([.. program, "("], entered, live, open + 1, started + 1);
            if (open > 0 && program[^1] != "(")
                Extend([.. program, ")"], entered, live, open - 1, started);
        }
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

    private static (List<string> Sites, int Scanned) Sites(string root)
    {
        // The whole repository, as ProcessGlobalEnvironmentConventionTests reads it: tests, samples, spikes and docs
        // included, less build output, dot directories and nested checkouts (IsPruned).
        var found = new List<string>();
        var scanned = Collect(root, root, found);
        found.Sort(StringComparer.Ordinal);
        return (found, scanned);
    }

    private static int Collect(string root, string directory, List<string> found)
    {
        var scanned = 0;
        foreach (var file in Directory.EnumerateFiles(directory, "*.cs"))
        {
            scanned++;
            var text = File.ReadAllText(file);
            var count = 0;
            for (var i = text.IndexOf(DetachCall, StringComparison.Ordinal); i >= 0; i = text.IndexOf(DetachCall, i + DetachCall.Length, StringComparison.Ordinal))
                count++;

            if (count > 0)
                found.Add($"{Path.GetRelativePath(root, file).Replace('\\', '/')} x{count}");
        }

        foreach (var child in Directory.EnumerateDirectories(directory))
        {
            if (!IsPruned(child))
                scanned += Collect(root, child, found);
        }

        return scanned;
    }

    /// <summary>Build output, tool state and the root of any nested checkout, as the other convention tests prune.</summary>
    private static bool IsPruned(string directory)
    {
        var name = Path.GetFileName(directory);
        if (name is "bin" or "obj" || name.StartsWith('.'))
            return true;

        var git = Path.Combine(directory, ".git");
        return File.Exists(git) || Directory.Exists(git);
    }

    private static int MatchingBrace(string text, int open)
    {
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            if (text[i] == '{')
                depth++;
            else if (text[i] == '}' && --depth == 0)
                return i;
        }

        return -1;
    }

    /// <summary>
    /// One program of <see cref="Programs"/>, run on this flow and in a model of the rule in step. After every step the
    /// decision and the flow's restore path must be the model's; outside every callback the flow must never decide with
    /// no subject, or below the mark of a live frame it entered there; and at the end every frame's mark must be the
    /// model's, and no frame entered outside the callbacks may hold the mark of one entered inside them.
    /// </summary>
    private sealed class ProgramRun
    {
        private readonly string[] _ops;
        private readonly List<string> _failures;
        private readonly string _tag;
        private readonly EgressSubjectNestingTests.FlowModel _model = new();
        private readonly List<IDisposable> _frames = [];
        private readonly List<EgressSubjectNestingTests.FlowModel.Node> _nodes = [];
        private readonly List<HighWaterMark> _marks = [];
        private readonly List<SecurityLabel> _labels = [];
        private readonly List<int> _depths = [];
        private int _at;

        internal ProgramRun(string[] ops, List<string> failures)
        {
            _ops = ops;
            _failures = failures;
            _tag = "[" + string.Join(" ", ops) + "]";
        }

        /// <summary>Runs the program; returns how many frames are left on the flow once the enclosing frame has ended.</summary>
        internal int Run()
        {
            Enter(depth: 0);
            Execute(depth: 0);

            Dispose(0);
            Check(depth: 0, "after the enclosing frame");
            var left = EgressSubjectNestingTests.RestorePathLength();

            using (EgressSubject.Enter("program-fresh", new HighWaterMark()))
            {
                var fresh = _model.Enter("program-fresh", SecurityLabel.Public);
                _failures.AddRange(_model.Compare(Decide(UnknownFamily), EgressSubjectNestingTests.RestorePathLength(), $"{_tag} in a frame entered afterwards"));
                _model.Dispose(fresh);
            }

            for (var i = 0; i < _frames.Count; i++)
            {
                if (!_marks[i].Current.Equals(_nodes[i].Mark))
                    _failures.Add($"{_tag} frame {i} holds {_marks[i].Current}, not {_nodes[i].Mark}");

                for (var j = 0; j < _frames.Count; j++)
                {
                    if (_depths[i] == 0 && _depths[j] > 0 && _marks[i].Current.Dominates(_labels[j]))
                        _failures.Add($"{_tag} frame {i}, entered outside the callbacks, holds the mark of frame {j}, entered inside one");
                }
            }

            return left;
        }

        // Runs ops until the ")" that returns from the callback at this depth, or the end of the program.
        private void Execute(int depth)
        {
            while (_at < _ops.Length)
            {
                var op = _ops[_at++];
                switch (op)
                {
                    case ")":
                        return;
                    case "(":
                        RunDetached(() => _model.RunDetached(() => Execute(depth + 1)));
                        break;
                    case "E":
                        Enter(depth);
                        break;
                    default:
                        Dispose(int.Parse(op.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture));
                        break;
                }

                Check(depth, $"after {op} (op {_at})");
            }
        }

        private void Enter(int depth)
        {
            var index = _frames.Count.ToString(CultureInfo.InvariantCulture);
            var label = new SecurityLabel(SecurityLevel.Internal, ["PROGRAM-" + index]);
            var mark = new HighWaterMark(label);
            _frames.Add(EgressSubject.Enter("program-" + index, mark));
            _nodes.Add(_model.Enter("program-" + index, label));
            _marks.Add(mark);
            _labels.Add(label);
            _depths.Add(depth);
        }

        private void Dispose(int frame)
        {
            _frames[frame].Dispose();
            _model.Dispose(_nodes[frame]);
        }

        private void Check(int depth, string step)
        {
            var decision = Decide(UnknownFamily);
            _failures.AddRange(_model.Compare(decision, EgressSubjectNestingTests.RestorePathLength(), $"{_tag} {step}"));
            if (depth > 0)
                return;

            // Outside every callback: the flow is inside every live frame it entered there.
            for (var i = 0; i < _frames.Count; i++)
            {
                if (_depths[i] == 0 && !_nodes[i].Disposed && (decision.CurrentBasis == NoSubject || !decision.Current.Dominates(_labels[i])))
                    _failures.Add($"{_tag} {step}: a write-down, {decision.CurrentBasis} at {decision.Current} below live frame {i}");
            }
        }
    }
}
