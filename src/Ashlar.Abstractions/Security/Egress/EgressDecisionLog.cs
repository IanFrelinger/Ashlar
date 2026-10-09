namespace Ashlar.Abstractions.Security.Egress;

/// <summary>
/// The process-wide hub every egress decision record is published to.
/// </summary>
/// <remarks>
/// <para>Each record goes two ways: to the <c>Ashlar-Egress</c> event source (free while nobody listens), and to
/// every subscribed <see cref="IEgressDecisionSink"/>, synchronously, on the thread that made the decision.</para>
/// <para>Publishing never throws. A sink that throws is skipped for that record and the fault is counted; the
/// other sinks still receive it. Subscribing and unsubscribing are thread-safe and never block publishing: the
/// subscriber list is copied on write.</para>
/// <para>A decision made while a record is being published on the same thread (an egress that a sink or an event
/// listener itself caused) is returned to its caller but not published again, so a sink cannot recurse into itself;
/// it is counted instead.</para>
/// </remarks>
public static class EgressDecisionLog
{
    private static Subscription[] _subscriptions = Array.Empty<Subscription>();
    private static long _sinkFaults;
    private static long _reentrantSkips;

    [ThreadStatic]
    private static bool _publishing;

    /// <summary>How many times a sink or the event source threw while recording; each throw was swallowed.</summary>
    internal static long SinkFaults => Interlocked.Read(ref _sinkFaults);

    /// <summary>Counts a sink failure outside the synchronous publish fence, such as a summary timer.</summary>
    internal static void RecordSinkFault() => Interlocked.Increment(ref _sinkFaults);

    /// <summary>How many records were not published because a publish was already running on that thread.</summary>
    internal static long ReentrantSkips => Interlocked.Read(ref _reentrantSkips);

    /// <summary>The number of live subscriptions.</summary>
    internal static int SubscriberCount => Volatile.Read(ref _subscriptions).Length;

    /// <summary>
    /// Subscribes <paramref name="sink"/> to every decision record from now on, until the returned subscription is
    /// disposed. Subscribing the same sink twice delivers each record to it twice.
    /// </summary>
    /// <param name="sink">The sink.</param>
    /// <returns>The subscription; disposing it unsubscribes. Disposing twice does nothing.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="sink"/> is <see langword="null"/>.</exception>
    public static IDisposable Subscribe(IEgressDecisionSink sink)
    {
        SecurityGuard.ThrowIfNull(sink, nameof(sink));
        var subscription = new Subscription(sink);
        Add(subscription);
        return subscription;
    }

    /// <summary>Publishes <paramref name="decision"/> to the event source and every sink. Never throws.</summary>
    internal static void Publish(EgressDecision decision)
    {
        if (_publishing)
        {
            Interlocked.Increment(ref _reentrantSkips);
            return;
        }

        _publishing = true;
        try
        {
            try
            {
                EgressEventSource.Write(decision);
            }
#pragma warning disable CA1031 // A throwing event listener must not reach the decision's caller; the fault is counted instead.
            catch (Exception)
#pragma warning restore CA1031
            {
                Interlocked.Increment(ref _sinkFaults);
            }

            foreach (var subscription in Volatile.Read(ref _subscriptions))
            {
                if (subscription.IsDisposed)
                    continue;

                try
                {
                    subscription.Sink.Record(decision);
                }
#pragma warning disable CA1031 // A throwing sink must not reach the decision's caller or starve the other sinks; the fault is counted.
                catch (Exception)
#pragma warning restore CA1031
                {
                    Interlocked.Increment(ref _sinkFaults);
                }
            }
        }
        finally
        {
            _publishing = false;
        }
    }

    private static void Add(Subscription subscription)
    {
        var seen = Volatile.Read(ref _subscriptions);
        while (true)
        {
            var next = new Subscription[seen.Length + 1];
            Array.Copy(seen, next, seen.Length);
            next[seen.Length] = subscription;

            var witnessed = Interlocked.CompareExchange(ref _subscriptions, next, seen);
            if (ReferenceEquals(witnessed, seen))
                return;

            seen = witnessed;
        }
    }

    private static void Remove(Subscription subscription)
    {
        var seen = Volatile.Read(ref _subscriptions);
        while (true)
        {
            var index = Array.IndexOf(seen, subscription);
            if (index < 0)
                return;

            var next = new Subscription[seen.Length - 1];
            Array.Copy(seen, 0, next, 0, index);
            Array.Copy(seen, index + 1, next, index, seen.Length - index - 1);

            var witnessed = Interlocked.CompareExchange(ref _subscriptions, next, seen);
            if (ReferenceEquals(witnessed, seen))
                return;

            seen = witnessed;
        }
    }

    private sealed class Subscription : IDisposable
    {
        private int _disposed;

        internal Subscription(IEgressDecisionSink sink) => Sink = sink;

        internal IEgressDecisionSink Sink { get; }

        internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                Remove(this);
        }
    }
}
