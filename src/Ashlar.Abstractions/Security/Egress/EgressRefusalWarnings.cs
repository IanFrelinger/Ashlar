namespace Ashlar.Abstractions.Security.Egress;

/// <summary>Bounded, disposable warning windows for components that intentionally degrade on refusal.</summary>
internal sealed class EgressRefusalWarnings : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<(string Site, AccessDenialReason Reason), Window> _windows = new();
    private readonly Action<EgressRefusedException, long, bool> _write;
    private readonly Func<Action, TimeSpan, IDisposable> _schedule;
    private bool _disposed;

    internal EgressRefusalWarnings(Action<EgressRefusedException, long, bool> write,
        Func<Action, TimeSpan, IDisposable>? schedule = null)
    {
        _write = write;
        _schedule = schedule ?? Schedule;
    }

    internal void Report(EgressRefusedException refusal)
    {
        var key = (refusal.Site, refusal.Reason);
        Window window;
        lock (_gate)
        {
            if (_disposed) return;
            if (_windows.TryGetValue(key, out var existing))
            {
                existing.Suppressed++;
                return;
            }
            window = new Window(refusal);
            _windows.Add(key, window);
        }
        Write(window, 0, false);
        IDisposable timer;
#pragma warning disable CA1031 // Timer/scheduler failure is diagnostic; it must not replace the refusal or stop a hosted loop.
        try { timer = _schedule(() => Expire(key, window), TimeSpan.FromMinutes(5)); }
        catch (Exception)
        {
            EgressDecisionLog.RecordSinkFault();
            Expire(key, window);
            return;
        }
#pragma warning restore CA1031
        lock (_gate)
        {
            if (!_disposed && _windows.TryGetValue(key, out var current) && ReferenceEquals(current, window))
            {
                window.Timer = timer;
                return;
            }
        }
        timer.Dispose();
    }

    private void Expire((string Site, AccessDenialReason Reason) key, Window window)
    {
        long suppressed;
        lock (_gate)
        {
            if (!_windows.TryGetValue(key, out var current) || !ReferenceEquals(current, window)) return;
            _windows.Remove(key);
            suppressed = window.Suppressed;
        }
        window.Timer?.Dispose();
        if (suppressed != 0) Write(window, suppressed, true);
    }

    public void Dispose()
    {
        Window[] windows;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            windows = _windows.Values.ToArray();
            _windows.Clear();
        }
        foreach (var window in windows)
        {
            window.Timer?.Dispose();
            if (window.Suppressed != 0) Write(window, window.Suppressed, true);
        }
    }

    private void Write(Window window, long suppressed, bool summary)
    {
#pragma warning disable CA1031 // A faulty diagnostic sink is counted, never allowed to mask the policy outcome.
        try { _write(window.Refusal, suppressed, summary); }
        catch (Exception) { EgressDecisionLog.RecordSinkFault(); }
#pragma warning restore CA1031
    }

    private static Timer Schedule(Action callback, TimeSpan delay)
    {
        // A diagnostic timer must not retain the caller's ambient subject frame.
        if (ExecutionContext.IsFlowSuppressed())
            return new Timer(static state => ((Action)state!).Invoke(), callback, delay, Timeout.InfiniteTimeSpan);
        using (ExecutionContext.SuppressFlow())
            return new Timer(static state => ((Action)state!).Invoke(), callback, delay, Timeout.InfiniteTimeSpan);
    }

    private sealed class Window(EgressRefusedException refusal)
    {
        internal EgressRefusedException Refusal { get; } = refusal;
        internal long Suppressed;
        internal IDisposable? Timer;
    }
}
