using System.Text.Json;
using Ashlar.Infrastructure.Preflight;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

public sealed class DependencyPreflightTests
{
    [Fact]
    public async Task Only_selected_dependencies_run_and_duplicates_cannot_weaken_requirements()
    {
        var calls = new List<string>();
        var runner = new DependencyPreflight([
            new("git", _ => { calls.Add("git"); return Available(); }),
            new("docker", _ => throw new InvalidOperationException("Unrelated dependency must not run."))
        ]);
        var report = await runner.RunAsync("inspect", [new("git", false), new("git"), new("git", false)]);
        Assert.Equal(new[] { "git" }, calls);
        Assert.True(Assert.Single(report.Checks).Required);
        Assert.True(report.RequiredAvailable);
        Assert.True(report.Complete);
        Assert.Equal("inspect", report.Operation);
    }

    [Fact]
    public async Task Missing_dependency_is_data_until_caller_selects_exception_policy()
    {
        var runner = new DependencyPreflight([new("docker", _ => Task.FromResult(new DependencyObservation(DependencyAvailability.Missing, "Install Docker.")))]);
        var report = await runner.RunAsync("visual", [new("docker")]);
        Assert.False(report.RequiredAvailable);
        Assert.Equal(DependencyAvailability.Missing, Assert.Single(report.Checks).Availability);
        var error = Assert.Throws<DependencyPreflightException>(() => DependencyPreflightPolicy.RequireAvailable(report));
        Assert.Same(report, error.Report);
        Assert.Contains("docker: Missing", error.Message, StringComparison.Ordinal);
        // A different caller may choose an alternative, without relabeling missing as available.
        var selectedOperation = report.RequiredAvailable ? "visual" : "functional-only";
        Assert.Equal("functional-only", selectedOperation);
        Assert.False(report.RequiredAvailable);
    }

    [Fact]
    public async Task Optional_failure_does_not_block_required_dependencies()
    {
        var runner = new DependencyPreflight([new("git", _ => Available())]);
        var report = await runner.RunAsync("inspect", [new("git"), new("unregistered-optional", false)]);
        Assert.True(report.RequiredAvailable);
        Assert.Equal(DependencyAvailability.NotRegistered, report.Checks[1].Availability);
        DependencyPreflightPolicy.RequireAvailable(report);
    }

    [Fact]
    public async Task Unknown_required_dependency_is_not_success()
    {
        var report = await new DependencyPreflight([]).RunAsync("new-operation", [new("new-tool")]);
        Assert.False(report.RequiredAvailable);
        Assert.Equal(DependencyAvailability.NotRegistered, Assert.Single(report.Checks).Availability);
    }

    [Fact]
    public async Task Disabled_preflight_does_no_work_and_is_not_passing_evidence()
    {
        var runner = new DependencyPreflight([new("docker", _ => throw new InvalidOperationException("Probe ran."))]);
        var report = await runner.RunAsync("visual", [new("docker")], enabled: false);
        Assert.False(report.Ran);
        Assert.False(report.Complete);
        Assert.False(report.RequiredAvailable);
        Assert.Equal(DependencyAvailability.NotChecked, Assert.Single(report.Checks).Availability);
        Assert.Throws<DependencyPreflightException>(() => DependencyPreflightPolicy.RequireAvailable(report));
    }

    [Fact]
    public async Task Operation_with_no_dependencies_needs_no_probes()
    {
        var runner = new DependencyPreflight([]);
        Assert.True((await runner.RunAsync("offline", [])).RequiredAvailable);
        Assert.False((await runner.RunAsync("offline", [], enabled: false)).RequiredAvailable);
    }

    [Fact]
    public async Task Probe_outcomes_are_not_cached_across_operations()
    {
        var attempts = 0;
        var runner = new DependencyPreflight([new("service", _ => Task.FromResult(new DependencyObservation(
            attempts++ == 0 ? DependencyAvailability.Missing : DependencyAvailability.Available, "observation")))]);
        Assert.False((await runner.RunAsync("first", [new("service")])).RequiredAvailable);
        Assert.True((await runner.RunAsync("second", [new("service")])).RequiredAvailable);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task Probe_deadline_cancels_adapter_and_does_not_block_later_dependencies()
    {
        var clock = new ManualClock();
        CancellationToken probeToken = default;
        var runner = new DependencyPreflight([
            new("hung", async ct => { probeToken = ct; await Task.Delay(Timeout.InfiniteTimeSpan, ct); return new(DependencyAvailability.Available, "unreachable"); }),
            new("healthy", _ => Available())
        ], probeTimeout: TimeSpan.FromMilliseconds(50), timeProvider: clock);
        var running = runner.RunAsync("operation", [new("hung"), new("healthy")]);
        Assert.True(probeToken.CanBeCanceled);
        Assert.Equal(1, clock.TimerCount);
        clock.Advance(TimeSpan.FromMilliseconds(49));
        Assert.False(running.IsCompleted);
        Assert.False(probeToken.IsCancellationRequested);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True(probeToken.IsCancellationRequested);
        var report = await running.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(DependencyAvailability.TimedOut, report.Checks[0].Availability);
        Assert.True(probeToken.IsCancellationRequested);
        Assert.True(report.Checks[1].Available);
        Assert.False(report.RequiredAvailable);
        Assert.Equal(0, clock.TimerCount);
    }

    [Fact]
    public async Task Default_system_clock_enforces_the_probe_deadline()
    {
        CancellationToken probeToken = default;
        var runner = new DependencyPreflight([new("hung", async ct => {
            probeToken = ct;
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new(DependencyAvailability.Available, "unreachable");
        })], probeTimeout: TimeSpan.FromMilliseconds(50));

        var report = await runner.RunAsync("operation", [new("hung")]).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(DependencyAvailability.TimedOut, Assert.Single(report.Checks).Availability);
        Assert.True(probeToken.IsCancellationRequested);
        Assert.False(report.RequiredAvailable);
    }

    [Fact]
    public async Task Runner_wait_is_bounded_even_when_adapter_ignores_cancellation()
    {
        var pending = new TaskCompletionSource<DependencyObservation>(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = new ManualClock();
        CancellationToken probeToken = default;
        var runner = new DependencyPreflight([new("hung", ct => { probeToken = ct; return pending.Task; })],
            probeTimeout: TimeSpan.FromMilliseconds(50), timeProvider: clock);
        try
        {
            var running = runner.RunAsync("operation", [new("hung")]);
            clock.Advance(TimeSpan.FromMilliseconds(50));
            Assert.True(probeToken.IsCancellationRequested);
            var report = await running.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(DependencyAvailability.TimedOut, Assert.Single(report.Checks).Availability);
            Assert.True(probeToken.IsCancellationRequested);
        }
        finally { pending.TrySetResult(new(DependencyAvailability.Available, "late")); }
    }

    [Fact]
    public async Task Operation_budget_leaves_unreached_checks_explicitly_unverified()
    {
        var pending = new TaskCompletionSource<DependencyObservation>(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = new ManualClock();
        var laterRan = false;
        var runner = new DependencyPreflight([
            new("hung", _ => pending.Task),
            new("later", _ => { laterRan = true; return Available(); })
        ], probeTimeout: TimeSpan.FromSeconds(1), operationTimeout: TimeSpan.FromMilliseconds(50), timeProvider: clock);
        try
        {
            var running = runner.RunAsync("operation", [new("hung"), new("later")]);
            // A timer can fire before the elapsed-time clock reaches the exact budget boundary.
            // The fired operation deadline must still prevent a later probe from starting.
            clock.Advance(TimeSpan.FromMilliseconds(49));
            clock.FireNextTimer();
            var report = await running.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(DependencyAvailability.TimedOut, report.Checks[0].Availability);
            Assert.Equal(DependencyAvailability.NotChecked, report.Checks[1].Availability);
            Assert.False(laterRan);
            Assert.False(report.Complete);
            Assert.False(report.RequiredAvailable);
        }
        finally { pending.TrySetResult(new(DependencyAvailability.Available, "late")); }
    }

    [Fact]
    public async Task Later_probe_gets_only_the_remaining_operation_budget()
    {
        var clock = new ManualClock();
        var pending = new TaskCompletionSource<DependencyObservation>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken laterToken = default;
        var runner = new DependencyPreflight([
            new("first", _ => { clock.Advance(TimeSpan.FromMilliseconds(30)); return Available(); }),
            new("later", ct => { laterToken = ct; return pending.Task; }),
            new("unreached", _ => throw new InvalidOperationException("Operation budget was exhausted."))
        ], probeTimeout: TimeSpan.FromSeconds(1), operationTimeout: TimeSpan.FromMilliseconds(50), timeProvider: clock);
        try
        {
            var running = runner.RunAsync("operation", [new("first"), new("later"), new("unreached")]);
            Assert.True(laterToken.CanBeCanceled);
            clock.Advance(TimeSpan.FromMilliseconds(19));
            Assert.False(running.IsCompleted);
            Assert.False(laterToken.IsCancellationRequested);
            clock.Advance(TimeSpan.FromMilliseconds(1));
            var report = await running.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(report.Checks[0].Available);
            Assert.Equal(DependencyAvailability.TimedOut, report.Checks[1].Availability);
            Assert.True(laterToken.IsCancellationRequested);
            Assert.Equal(DependencyAvailability.NotChecked, report.Checks[2].Availability);
            Assert.False(report.Complete);
            Assert.False(report.RequiredAvailable);
            Assert.Equal(0, clock.TimerCount);
        }
        finally { pending.TrySetResult(new(DependencyAvailability.Available, "late")); }
    }

    [Fact]
    public async Task Adapter_reported_timeout_does_not_exhaust_the_runners_operation_budget()
    {
        var runner = new DependencyPreflight([
            new("short-io-timeout", _ => Task.FromResult(new DependencyObservation(DependencyAvailability.TimedOut, "Adapter I/O timeout."))),
            new("healthy", _ => Available())
        ], probeTimeout: TimeSpan.FromSeconds(2), operationTimeout: TimeSpan.FromSeconds(1));
        var report = await runner.RunAsync("operation", [new("short-io-timeout"), new("healthy")]);
        Assert.Equal(DependencyAvailability.TimedOut, report.Checks[0].Availability);
        Assert.True(report.Checks[1].Available);
        Assert.True(report.Complete);
    }

    [Fact]
    public async Task Caller_cancellation_propagates_instead_of_becoming_dependency_failure()
    {
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new DependencyPreflight([new("service", async ct => {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new(DependencyAvailability.Available, "unreachable");
        })]);
        var running = runner.RunAsync("operation", [new("service")], cancellationToken: cancellation.Token);
        await started.Task;
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync("operation", [], enabled: false, cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task Mandatory_authorization_failure_is_not_converted_to_missing_dependency()
    {
        var refusal = new UnauthorizedAccessException("Mandatory validation denied.");
        var runner = new DependencyPreflight([new("restricted", _ => Task.FromException<DependencyObservation>(refusal))]);
        var observed = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => runner.RunAsync("operation", [new("restricted", false)]));
        Assert.Same(refusal, observed);
    }

    [Fact]
    public async Task Programming_errors_do_not_become_fallback_eligible_results()
    {
        var runner = new DependencyPreflight([new("broken", _ => Task.FromResult(new DependencyObservation((DependencyAvailability)999, "invalid")))]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync("operation", [new("broken")]));
        Assert.Throws<ArgumentException>(() => new DependencyPreflight([new("same", _ => Available()), new("same", _ => Available())]));
        await Assert.ThrowsAsync<ArgumentException>(() => runner.RunAsync("operation", [new(" ")]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Adapter_timeout_exception_is_not_mistaken_for_runner_deadline(bool synchronous)
    {
        var error = new TimeoutException("Adapter failure, not runner deadline.");
        var runner = new DependencyPreflight([new("broken", _ => synchronous
            ? throw error : Task.FromException<DependencyObservation>(error))]);
        Assert.Same(error, await Assert.ThrowsAsync<TimeoutException>(() => runner.RunAsync("operation", [new("broken")])));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(301)]
    public void Unbounded_or_invalid_deadlines_are_rejected(int seconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DependencyPreflight([], probeTimeout: TimeSpan.FromSeconds(seconds)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DependencyPreflight([], operationTimeout: TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public async Task Receipt_serializes_named_dependency_outcomes()
    {
        var report = await new DependencyPreflight([]).RunAsync("operation", [new("unknown")]);
        var encoded = JsonSerializer.Serialize(report);
        Assert.Contains("\"Availability\":\"NotRegistered\"", encoded, StringComparison.Ordinal);
        Assert.Contains("\"RequiredAvailable\":false", encoded, StringComparison.Ordinal);
    }

    // Advance only from the test. A healthy probe never loses its 50 ms budget to host scheduling.
    // Real-time WaitAsync calls above remain watchdogs for broken cancellation, not test clocks.
    private sealed class ManualClock : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ManualTimer> _timers = [];
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() { lock (_gate) return _timestamp; }
        public int TimerCount { get { lock (_gate) return _timers.Count; } }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            lock (_gate)
            {
                timer.Change(dueTime, period);
                _timers.Add(timer);
            }
            return timer;
        }

        public void Advance(TimeSpan amount)
        {
            List<ManualTimer> due;
            lock (_gate)
            {
                _timestamp += amount.Ticks;
                due = _timers.Where(timer => timer.Active && timer.Due <= _timestamp).ToList();
                foreach (var timer in due) timer.Active = false;
            }
            foreach (var timer in due) timer.Fire();
        }

        public void FireNextTimer()
        {
            ManualTimer timer;
            lock (_gate)
            {
                timer = Assert.Single(_timers.Where(candidate => candidate.Active));
                timer.Active = false;
            }
            timer.Fire();
        }

        private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
        {
            public bool Active { get; set; }
            public long Due { get; private set; }
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                Assert.Equal(Timeout.InfiniteTimeSpan, period);
                lock (clock._gate)
                {
                    Active = dueTime != Timeout.InfiniteTimeSpan;
                    Due = clock._timestamp + (Active ? dueTime.Ticks : 0);
                }
                return true;
            }
            public void Fire() => callback(state);
            public void Dispose() { lock (clock._gate) { Active = false; clock._timers.Remove(this); } }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    private static Task<DependencyObservation> Available() => Task.FromResult(new DependencyObservation(DependencyAvailability.Available, "Ready."));
}
