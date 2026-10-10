using System.Reflection;
using Ashlar.Abstractions.Security.Egress;
using Ashlar.Abstractions.Security;
using FluentAssertions;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

[Trait("Category", "Certification")]
public sealed class EgressRefusalWarningTests
{
    [Fact]
    public void The_same_site_with_different_reasons_has_independent_windows()
    {
        var first = Refusal("one");
        EgressRefusedException second;
        using (EgressSubject.Enter("subject", new HighWaterMark(new SecurityLabel(SecurityLevel.Secret))))
            second = Refusal("one");
        first.Reason.Should().NotBe(second.Reason);
        var writes = new List<EgressRefusedException>();
        var timers = new List<ManualTimer>();
        using var warnings = Create((r, _, _) => writes.Add(r), timers, out var report);
        report(first);
        report(second);
        writes.Should().Equal(first, second);
        timers.Should().HaveCount(2);
    }

    [Fact]
    public void Repeats_are_summarized_on_expiry_and_a_new_window_can_start()
    {
        var writes = new List<(string Site, long Count, bool Summary)>();
        var timers = new List<ManualTimer>();
        using var warnings = Create((r, n, s) => writes.Add((r.Site, n, s)), timers, out var report);
        report(Refusal("one"));
        report(Refusal("one"));
        report(Refusal("one"));
        report(Refusal("two"));
        writes.Should().Equal(("one", 0L, false), ("two", 0L, false));
        timers.Should().HaveCount(2);
        timers[0].Fire();
        timers[0].Disposed.Should().BeTrue();
        writes.Last().Should().Be(("one", 2L, true));
        report(Refusal("one"));
        writes.Last().Should().Be(("one", 0L, false));
        timers.Should().HaveCount(3);
        timers[0].Fire();
        writes.Should().HaveCount(4, "an expired callback cannot close the replacement window");
    }

    [Fact]
    public void Dispose_flushes_suppressed_counts_cancels_timers_and_is_idempotent()
    {
        var writes = new List<(long Count, bool Summary)>();
        var timers = new List<ManualTimer>();
        var warnings = Create((_, n, s) => writes.Add((n, s)), timers, out var report);
        report(Refusal("one"));
        report(Refusal("one"));
        report(Refusal("two"));
        warnings.Dispose();
        warnings.Dispose();
        report(Refusal("three"));
        foreach (var timer in timers) timer.Fire();
        timers.Should().OnlyContain(t => t.Disposed);
        writes.Should().Equal((0L, false), (0L, false), (1L, true));
    }

    [Fact]
    public void A_faulty_sink_does_not_escape_report_or_dispose()
    {
        var counter = typeof(EgressDecisionLog).GetProperty("SinkFaults", BindingFlags.Static | BindingFlags.NonPublic)!;
        var before = (long)counter.GetValue(null)!;
        var calls = 0;
        using var warnings = Create((_, _, _) => { calls++; throw new InvalidOperationException("sink"); },
            new List<ManualTimer>(), out var report);
        report(Refusal("one"));
        report(Refusal("one"));
        warnings.Dispose();
        calls.Should().Be(2);
        ((long)counter.GetValue(null)!).Should().BeGreaterThanOrEqualTo(before + 2);
    }

    private static IDisposable Create(Action<EgressRefusedException, long, bool> write,
        List<ManualTimer> timers, out Action<EgressRefusedException> report)
    {
        var type = typeof(EgressRefusedException).Assembly.GetType(
            "Ashlar.Abstractions.Security.Egress.EgressRefusalWarnings", throwOnError: true)!;
        Func<Action, TimeSpan, IDisposable> schedule = (callback, delay) =>
        {
            delay.Should().Be(TimeSpan.FromMinutes(5));
            var timer = new ManualTimer(callback);
            timers.Add(timer);
            return timer;
        };
        var instance = Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null, args: new object[] { write, schedule }, culture: null)!;
        report = (Action<EgressRefusedException>)type.GetMethod("Report", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate(typeof(Action<EgressRefusedException>), instance);
        return (IDisposable)instance;
    }

    private static EgressRefusedException Refusal(string site) => new(new EgressGuard("full", "enforce")
        .Evaluate(new EgressRequest(EgressFamilies.Http, site, new Uri("https://remote.example"))));

    private sealed class ManualTimer(Action callback) : IDisposable
    {
        public bool Disposed { get; private set; }
        public void Fire() => callback();
        public void Dispose() => Disposed = true;
    }
}
