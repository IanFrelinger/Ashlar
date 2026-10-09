using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using System.Reflection;
using Ashlar.Abstractions.Security;
using Ashlar.Abstractions.Security.Egress;
using Ashlar.Infrastructure.Egress;
using Ashlar.AI.Pipeline.Governance;
using FluentAssertions;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

[Trait("Category", "Certification")]
public sealed class EgressRefusalDiagnosticsTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Guard_resolution_fallback_is_counted_and_logs_Warning_only_for_a_throw(bool chat, bool throws)
    {
        var capture = new Capture();
        using var factory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(capture));
        var provider = new ResolutionProvider(factory, throws);
        var counterName = throws ? "ResolutionFaults" : "ResolutionMissing";
        var counter = typeof(EgressGuard).GetProperty(counterName, BindingFlags.Static | BindingFlags.NonPublic);
        counter.Should().NotBeNull();
        var before = (long)counter!.GetValue(null)!;
        IEgressGuard? guard;
        if (chat)
        {
            var resolve = typeof(AshlarGovernanceChatClientBuilderExtensions).GetMethod("ResolveEgressGuard",
                BindingFlags.NonPublic | BindingFlags.Static)!;
            guard = (IEgressGuard?)resolve.Invoke(null, [provider]);
        }
        else
        {
            guard = EgressServiceCollectionExtensions.ResolveGuardAndActivateLogging(new ResolutionBuilder(provider));
        }
        guard.Should().BeSameAs(EgressGuard.ProcessDefault);
        ((long)counter.GetValue(null)!).Should().BeGreaterThan(before);
        var entry = capture.Entries.Should().ContainSingle().Which;
        entry.Event.Id.Should().Be(7306);
        entry.Level.Should().Be(throws ? LogLevel.Warning : LogLevel.Debug);
        entry.Fields["Resolution"].Should().Be(throws ? "failed" : "unregistered");
        entry.Fields["Fault"].Should().Be(throws ? typeof(InvalidOperationException).FullName : "none");
    }

    private sealed class ResolutionProvider(ILoggerFactory factory, bool throws) : IServiceProvider
    {
        public object? GetService(Type serviceType)
        {
            if (serviceType == typeof(ILoggerFactory)) return factory;
            if (serviceType == typeof(IEgressGuard) && throws) throw new InvalidOperationException("private host details");
            return null;
        }
    }

    private sealed class ResolutionBuilder(IServiceProvider services) : HttpMessageHandlerBuilder
    {
        public override string? Name { get; set; }
        public override HttpMessageHandler PrimaryHandler { get; set; } = null!;
        public override IList<DelegatingHandler> AdditionalHandlers { get; } = new List<DelegatingHandler>();
        public override IServiceProvider Services => services;
        public override HttpMessageHandler Build() => throw new NotSupportedException();
    }

    [Fact]
    public void Enforced_refusals_are_visible_at_Warning_with_the_full_operator_record()
    {
        var capture = new Capture();
        using var factory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Warning).AddProvider(capture));
        var sink = new LoggerEgressDecisionSink(factory);
        using var owned = (object)sink as IDisposable;
        var decision = Refused("warning-" + Guid.NewGuid().ToString("N"));
        sink.Record(decision);

        var entry = capture.Entries.Should().ContainSingle().Which;
        entry.Level.Should().Be(LogLevel.Warning);
        entry.Event.Id.Should().Be(7301);
        entry.Event.Name.Should().Be("EgressRefused");
        entry.Fields["Ref"].Should().Be(decision.Ref);
        entry.Fields["Sequence"].Should().Be(decision.Sequence);
        entry.Fields["ModeBasis"].Should().Be(decision.ModeBasis);
        entry.Fields["Detail"].Should().Be(decision.Access.Detail);
        entry.Fields["Current"].Should().Be(decision.Current.ToString());
        entry.Fields["At"].Should().Be(decision.At);
        entry.Fields["DestinationBasis"].Should().Be(decision.DestinationBasis);
        entry.Fields["Allowed"].Should().Be(false);
        entry.Fields["Refused"].Should().Be(true);
    }

    [Fact]
    public void Repeated_refusals_have_one_warning_then_a_counted_summary_even_without_more_traffic()
    {
        var capture = new Capture();
        using var factory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(capture));
        var clock = new ManualClock();
        var sink = WithClock(factory, clock);
        using var owned = (object)sink as IDisposable;
        var decision = Refused("window");
        sink.Record(decision);
        sink.Record(decision);
        sink.Record(decision);
        capture.Entries.Should().ContainSingle();
        clock.Advance(TimeSpan.FromMinutes(5));

        var summary = capture.Entries.Should().HaveCount(2).And.Subject.Last();
        summary.Level.Should().Be(LogLevel.Warning);
        summary.Event.Id.Should().Be(7305);
        summary.Fields["SuppressedCount"].Should().Be(2L);
        summary.Fields["Site"].Should().Be("window");
        clock.ActiveTimers.Should().Be(0);
        sink.Record(decision);
        capture.Entries.Count(e => e.Event.Id == 7301).Should().Be(2, "the expired window no longer suppresses warnings");
    }

    [Fact]
    public void Windows_are_independent_per_site_and_reason_and_disposal_flushes_counts_and_releases_timers()
    {
        var capture = new Capture();
        using var factory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(capture));
        var clock = new ManualClock();
        var sink = WithClock(factory, clock);
        sink.Record(Refused("one"));
        sink.Record(Refused("two"));
        sink.Record(Refused("one"));
        capture.Entries.Count(e => e.Event.Id == 7301).Should().Be(2);
        using (EgressSubject.Enter("diagnostic-window", new HighWaterMark(SecurityLabel.Secret)))
        {
            var otherReason = Refused("one");
            otherReason.Access.Reason.Should().Be(AccessDenialReason.LevelTooLow);
            sink.Record(otherReason);
        }
        capture.Entries.Count(e => e.Event.Id == 7301).Should().Be(3,
            "a different reason at the same site starts its own window");
        var owner = (object)sink as IDisposable;
        owner.Should().NotBeNull();
        owner!.Dispose();
        owner.Dispose();
        clock.ActiveTimers.Should().Be(0);
        clock.Advance(TimeSpan.FromMinutes(10));
        capture.Entries.Count(e => e.Event.Id == 7305).Should().Be(1);
        capture.Entries.Last().Fields["SuppressedCount"].Should().Be(1L);
    }

    [Fact]
    public void A_logger_fault_on_the_timer_does_not_escape_into_the_host()
    {
        var capture = new Capture();
        using var factory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(capture));
        var clock = new ManualClock();
        var sink = WithClock(factory, clock);
        using var owned = (object)sink as IDisposable;
        sink.Record(Refused("throwing"));
        sink.Record(Refused("throwing"));
        capture.Throw = true;
        Action expire = () => clock.Advance(TimeSpan.FromMinutes(5));
        expire.Should().NotThrow();
        clock.ActiveTimers.Should().Be(0);
    }

    [Fact]
    public void Refusal_event_two_is_visible_to_Warning_only_listeners_and_carries_the_operator_record()
    {
        var site = "event-two-" + Guid.NewGuid().ToString("N");
        using var listener = new WarningListener(site);
        var decision = Refused(site);
        var entry = listener.Seen.Should().ContainSingle().Which;
        entry.EventId.Should().Be(2);
        entry.EventName.Should().Be("Refused");
        entry.Level.Should().Be(EventLevel.Warning);
        var fields = entry.PayloadNames!.Zip(entry.Payload!).ToDictionary(p => p.First, p => p.Second);
        fields["ref"].Should().Be(decision.Ref);
        fields["detail"].Should().Be(decision.Access.Detail);
        fields["modeBasis"].Should().Be(decision.ModeBasis);
    }

    private static EgressDecision Refused(string site) => new EgressGuard("full", "enforce")
        .Evaluate(new EgressRequest(EgressFamilies.Http, site, new Uri("https://remote.example")));

    private static LoggerEgressDecisionSink WithClock(ILoggerFactory factory, TimeProvider clock)
    {
        var constructor = typeof(LoggerEgressDecisionSink).GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null, [typeof(ILoggerFactory), typeof(TimeProvider)], modifiers: null);
        constructor.Should().NotBeNull("the internal clock seam makes five-minute windows deterministic");
        return (LoggerEgressDecisionSink)constructor!.Invoke([factory, clock]);
    }

    private sealed record Entry(LogLevel Level, EventId Event, Dictionary<string, object?> Fields);
    private sealed class Capture : ILoggerProvider, ILogger
    {
        public List<Entry> Entries { get; } = [];
        public bool Throw { get; set; }
        public ILogger CreateLogger(string categoryName) => this;
        public bool IsEnabled(LogLevel logLevel) => true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (Throw) throw new InvalidOperationException("logger failure");
            Entries.Add(new(logLevel, eventId, ((IEnumerable<KeyValuePair<string, object?>>)state!).ToDictionary(p => p.Key, p => p.Value)));
        }
        public void Dispose() { }
    }

    private sealed class WarningListener(string site) : EventListener
    {
        public ConcurrentQueue<EventWrittenEventArgs> Seen { get; } = new();
        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name == "Ashlar-Egress") EnableEvents(eventSource, EventLevel.Warning);
        }
        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (eventData.EventId == 2 && eventData.Payload?.Contains(site) == true) Seen.Enqueue(eventData);
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;
        private readonly List<ManualTimer> _timers = [];
        public int ActiveTimers => _timers.Count(t => t.Active);
        public override DateTimeOffset GetUtcNow() => _now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            timer.Change(dueTime, period);
            _timers.Add(timer);
            return timer;
        }
        public void Advance(TimeSpan amount)
        {
            _now += amount;
            foreach (var timer in _timers.ToArray()) timer.Fire();
        }
        private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
        {
            private DateTimeOffset _due;
            public bool Active { get; private set; }
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                Active = dueTime != Timeout.InfiniteTimeSpan;
                _due = clock._now + (Active ? dueTime : TimeSpan.Zero);
                return true;
            }
            public void Fire()
            {
                if (Active && _due <= clock._now) { Active = false; callback(state); }
            }
            public void Dispose() => Active = false;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
