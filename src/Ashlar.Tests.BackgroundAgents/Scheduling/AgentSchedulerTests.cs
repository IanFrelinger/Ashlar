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
                i.State = BackgroundAgentState.Stopped;
                stopRequested.TrySetResult();
            }
        }

        await scheduler.StartAsync(instance, ExecuteOnce);

        // The schedule really ran, and really looped -- the second signal is what the one-second
        // inter-iteration delay used to be paid for and never asserted.
        await firstCall.Task.WaitAsync(IterationTimeout);
        await stopRequested.Task.WaitAsync(IterationTimeout);

        var atStop = Volatile.Read(ref callCount);
        atStop.Should().BeGreaterThanOrEqualTo(2, "the continuous loop must iterate, not fire once");

        await Task.Delay(StoppedGrace);

        Volatile.Read(ref callCount).Should().Be(
            atStop,
            "Stop must cancel the loop: the executor would otherwise have run another iteration "
            + "one second later. This is the fact the test is named for and the only one a "
            + "regression in AgentScheduler.Stop can break.");
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
