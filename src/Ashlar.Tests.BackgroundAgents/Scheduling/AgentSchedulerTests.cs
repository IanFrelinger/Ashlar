using FluentAssertions;
using Ashlar.BackgroundAgents.Configuration;
using Ashlar.BackgroundAgents.Registry;
using Ashlar.BackgroundAgents.Scheduling;
using Xunit;

namespace Ashlar.Tests.BackgroundAgents.Scheduling;

/// <summary>Tests for agent scheduler.</summary>
public class AgentSchedulerTests
{
    /// <summary>
    /// Upper bound on waiting for a continuous schedule to run an iteration. A hang net: a broken
    /// scheduler fails fast with a TimeoutException instead of hanging into the blame timeout.
    /// </summary>
    private static readonly TimeSpan IterationTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// <see cref="ScheduleExecutor"/> waits one second between continuous iterations, so a third
    /// call would arrive about a second after Stop if Stop did nothing. This grace clears that
    /// with margin and guards a NEGATIVE assertion, which means it can only ever produce a false
    /// PASS (a broken Stop plus a stalled thread pool), never a false FAIL.
    /// </summary>
    private static readonly TimeSpan StoppedGrace = TimeSpan.FromMilliseconds(1500);

    [Fact]
    public async Task StartAsync_RunsScheduleAndStop_StopsLoop()
    {
        var executor = new ScheduleExecutor();
        var scheduler = new AgentScheduler(executor);

        // Interlocked/Volatile because iterations after the first resume on a thread-pool thread
        // (Task.Yield with xUnit's sync context nulled), while the assertions read from the test
        // thread. A plain ++ and a plain read have no ordering guarantee, and CI runs arm64 macOS.
        var callCount = 0;
        var tokenCancelledByStop = 0;
        var firstCall = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var instance = new BackgroundAgentInstance
        {
            Config = new BackgroundAgentConfig
            {
                Id = "agent-1",
                Schedule = new BackgroundAgentSchedule
                {
                    Type = ScheduleType.Continuous,
                    InitialDelay = TimeSpan.Zero
                }
            },
            State = BackgroundAgentState.Running
        };

        async Task ExecuteOnce(BackgroundAgentInstance i, CancellationToken ct)
        {
            var seen = Interlocked.Increment(ref callCount);
            if (seen == 1)
                firstCall.TrySetResult();

            await Task.Yield();

            if (seen >= 2)
            {
                scheduler.Stop("agent-1");

                // CancellationTokenSource.Cancel is synchronous, so by the time Stop returns this
                // is a settled fact rather than a race. Recorded here and asserted on the test
                // thread because an assertion thrown on the executor's task would be swallowed:
                // Stop has already removed that task from the scheduler and nobody awaits it.
                Volatile.Write(ref tokenCancelledByStop, ct.IsCancellationRequested ? 1 : 0);
                stopRequested.TrySetResult();
            }

            // Deliberately NOT followed by `i.State = BackgroundAgentState.Stopped`.
            // ScheduleExecutor.RunContinuousAsync loops on
            // `!cancellationToken.IsCancellationRequested && instance.State == Running`, so that
            // flip ended the loop all by itself -- which is why the assertions below used to hold
            // with AgentScheduler.Stop neutered to a no-op. Cancellation is now the only way out
            // of the loop, which is what makes them assertions about Stop.
        }

        await scheduler.StartAsync(instance, ExecuteOnce);

        // The schedule really ran, and really looped -- the second signal is what the one-second
        // inter-iteration delay used to be paid for and never asserted.
        await firstCall.Task.WaitAsync(IterationTimeout);
        await stopRequested.Task.WaitAsync(IterationTimeout);

        var atStop = Volatile.Read(ref callCount);
        atStop.Should().BeGreaterThanOrEqualTo(2, "the continuous loop must iterate, not fire once");

        Volatile.Read(ref tokenCancelledByStop).Should().Be(
            1,
            "cancelling the token the executor loops on is the whole of Stop's contract, and it is "
            + "observable synchronously the moment Stop returns");

        await Task.Delay(StoppedGrace);

        Volatile.Read(ref callCount).Should().Be(
            atStop,
            "Stop must end the loop: with nothing else able to end it, an executor whose token was "
            + "not cancelled would have run another iteration one second later");
    }

    [Fact]
    public void Stop_WhenNotStarted_DoesNotThrow()
    {
        var executor = new ScheduleExecutor();
        var scheduler = new AgentScheduler(executor);
        scheduler.Stop("nonexistent");
    }

    [Fact]
    public async Task StartAsync_SameAgentTwice_LogsWarningAndReturns()
    {
        var executor = new ScheduleExecutor();
        var scheduler = new AgentScheduler(executor);
        var instance = new BackgroundAgentInstance
        {
            Config = new BackgroundAgentConfig
            {
                Id = "agent-1",
                Schedule = new BackgroundAgentSchedule
                {
                    Type = ScheduleType.Interval,
                    Interval = TimeSpan.FromSeconds(10),
                    InitialDelay = TimeSpan.Zero
                }
            },
            State = BackgroundAgentState.Running
        };

        /// <summary>Execute once.</summary>
        /// <param name="i">I.</param>
        /// <param name="ct">Cancellation token.</param>
        static Task ExecuteOnce(BackgroundAgentInstance i, CancellationToken ct) => Task.CompletedTask;

        await scheduler.StartAsync(instance, ExecuteOnce);
        await scheduler.StartAsync(instance, ExecuteOnce);
        scheduler.Stop("agent-1");
    }
}
