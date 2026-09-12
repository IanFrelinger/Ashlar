using System.Runtime.CompilerServices;
using System.Text.Json;
using FluentAssertions;
using Ashlar.Core.Application.Observation.Models;
using Ashlar.Core.Application.Observation.Ports;
using Ashlar.Infrastructure.Observation;
using Ashlar.Tests.Infrastructure.Helpers;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Observation;

/// <summary>Tests for composite event source.</summary>
public sealed class CompositeEventSourceTests
{
    [Fact]
    public async Task SubscribeAsync_WithEmptySources_YieldsNothing()
    {
        var composite = new CompositeEventSource(Array.Empty<IObservableEventSource>());
        var collected = new List<NormalizedEvent>();

        await foreach (var evt in composite.SubscribeAsync(CancellationToken.None).WithCancellation(CancellationToken.None))
        {
            collected.Add(evt);
        }

        collected.Should().BeEmpty();
    }

    [Fact]
    public async Task SubscribeAsync_WithMockSource_YieldsMergedEvents()
    {
        var evt1 = Event("e1");
        var evt2 = Event("e2");

        var mockSource = new MockEventSource("mock", [evt1, evt2]);
        var composite = new CompositeEventSource([mockSource]);
        var collected = new List<NormalizedEvent>();

        await foreach (var evt in composite.SubscribeAsync(CancellationToken.None).WithCancellation(CancellationToken.None))
        {
            collected.Add(evt);
            if (collected.Count >= 2) break;
        }

        collected.Should().HaveCount(2);
        collected[0].EventId.Should().Be("e1");
        collected[1].EventId.Should().Be("e2");
    }

    /// <summary>
    /// The consumer breaking out of the loop must stop every child before SubscribeAsync returns.
    ///
    /// <para>This is what "stopped" has to mean to <c>ObservationPipelineService</c>: its
    /// <c>StopAsync</c> completes as soon as <c>ExecuteAsync</c> returns, and <c>ExecuteAsync</c>
    /// returns as soon as this enumeration ends. Without the join the host tears down its DI
    /// container and exits while the real children — a <c>FileSystemWatcher</c> with live callback
    /// threads, and a process poller — are still running.</para>
    ///
    /// <para>Deterministic, no sleep: the child publishes a TaskCompletionSource from its own
    /// <c>finally</c>, and the assertion reads the completed state of that task at the instant the
    /// enumeration returns. On the unjoined implementation the child's token is never cancelled by
    /// the break at all, so the task is not merely late — it is still pending, forever.</para>
    /// </summary>
    [Fact]
    public async Task SubscribeAsync_WhenConsumerBreaks_StopsAndJoinsEveryChild()
    {
        var child = new LongLivedEventSource("child-a");
        var other = new LongLivedEventSource("child-b");
        var composite = new CompositeEventSource([child, other]);

        // Hang net, not a budget: the child only exits when its token trips, and on an
        // implementation that never trips it this is what stops the test leaking a live pump.
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(TestTimeouts.Quick));

        try
        {
            await foreach (var evt in composite.SubscribeAsync(cts.Token).WithCancellation(cts.Token))
            {
                evt.SourceId.Should().NotBeNullOrEmpty();
                break;
            }

            child.Stopped.Task.IsCompleted.Should().BeTrue(
                "SubscribeAsync must not return until every child pump has stopped -- StopAsync "
                + "means nothing otherwise");
            other.Stopped.Task.IsCompleted.Should().BeTrue(
                "a child that produced nothing still has to be joined");
            child.IsRunning.Should().BeFalse();
            other.IsRunning.Should().BeFalse();
        }
        finally
        {
            await cts.CancelAsync();
        }
    }

    /// <summary>
    /// Same guarantee on the cancellation path, which is how the background service actually
    /// stops: the token trips, the enumeration ends, and every child must already be finished.
    /// </summary>
    [Fact]
    public async Task SubscribeAsync_WhenTokenCancelled_StopsAndJoinsEveryChild()
    {
        var child = new LongLivedEventSource("child-a");
        var composite = new CompositeEventSource([child]);
        using var cts = new CancellationTokenSource();

        var caught = false;
        try
        {
            await foreach (var evt in composite.SubscribeAsync(cts.Token).WithCancellation(cts.Token))
            {
                evt.SourceId.Should().Be("child-a");
                await cts.CancelAsync();
            }
        }
        catch (OperationCanceledException)
        {
            caught = true;
        }

        caught.Should().BeTrue("cancelling the subscription token surfaces as OperationCanceledException");
        child.Stopped.Task.IsCompleted.Should().BeTrue(
            "the child must be joined even when the enumeration ends by throwing");
        child.IsRunning.Should().BeFalse();
    }

    /// <summary>
    /// Nothing a child emits after SubscribeAsync returns can reach anyone, because there is no
    /// child left to emit. The grace here guards a NEGATIVE assertion (the counter must not move),
    /// so it can only ever produce a false PASS, never a false FAIL.
    /// </summary>
    [Fact]
    public async Task SubscribeAsync_AfterConsumerBreaks_ChildEmitsNothingFurther()
    {
        var child = new LongLivedEventSource("child-a");
        var composite = new CompositeEventSource([child]);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(TestTimeouts.Quick));

        try
        {
            await foreach (var evt in composite.SubscribeAsync(cts.Token).WithCancellation(cts.Token))
            {
                evt.SourceId.Should().Be("child-a");
                break;
            }

            var emittedAtReturn = child.Emitted;
            await Task.Delay(200);

            child.Emitted.Should().Be(
                emittedAtReturn,
                "the child was joined before SubscribeAsync returned, so it has no way to emit again");
        }
        finally
        {
            await cts.CancelAsync();
        }
    }

    private static NormalizedEvent Event(string id, string sourceId = "mock") => new()
    {
        EventId = id,
        Timestamp = DateTimeOffset.UtcNow,
        SourceId = sourceId,
        Category = "test",
        Payload = JsonSerializer.SerializeToElement(new { id }),
    };

    /// <summary>
    /// A child shaped like the real ones: it keeps producing until its token trips, and records
    /// through a TaskCompletionSource the moment its iterator body has actually finished.
    ///
    /// <para>The teardown in the finally costs a fixed 50 ms on purpose, and on a token that is
    /// deliberately NOT the subscription's. It models what the real children do — the file source
    /// disables and disposes every <c>FileSystemWatcher</c>, the process source awaits its poll
    /// task — and it is what makes "did the composite wait for me?" a question with an answer.
    /// It is not the synchronization for any assertion here: the assertions are exact, because a
    /// composite that joins its children cannot return before this finally has run, however long
    /// it takes.</para>
    /// </summary>
    private sealed class LongLivedEventSource : IObservableEventSource
    {
        private static readonly TimeSpan TeardownCost = TimeSpan.FromMilliseconds(50);
        private int _emitted;

        public LongLivedEventSource(string sourceId) => SourceId = sourceId;

        /// <summary>Source id.</summary>
        public string SourceId { get; }

        /// <summary>Completes when the subscription body has run its finally.</summary>
        public TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>True between the first yield and the finally.</summary>
        public bool IsRunning { get; private set; }

        /// <summary>How many events this child has produced.</summary>
        public int Emitted => Volatile.Read(ref _emitted);

        public async IAsyncEnumerable<NormalizedEvent> SubscribeAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            IsRunning = true;
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    Interlocked.Increment(ref _emitted);
                    yield return Event($"{SourceId}-{Emitted}", SourceId);
                    try
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
            finally
            {
                await Task.Delay(TeardownCost, CancellationToken.None).ConfigureAwait(false);
                IsRunning = false;
                Stopped.TrySetResult();
            }
        }
    }

    /// <summary>Tests for mock event source.</summary>
    private sealed class MockEventSource : IObservableEventSource
    {
        private readonly NormalizedEvent[] _events;

        public MockEventSource(string sourceId, NormalizedEvent[] events)
        {
            SourceId = sourceId;
            _events = events;
        }

        /// <summary>Source id.</summary>
        public string SourceId { get; }

        public async IAsyncEnumerable<NormalizedEvent> SubscribeAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var evt in _events)
            {
                await Task.Yield();
                cancellationToken.ThrowIfCancellationRequested();
                yield return evt;
            }
        }
    }
}
