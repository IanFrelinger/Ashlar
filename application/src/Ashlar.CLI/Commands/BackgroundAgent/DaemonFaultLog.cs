using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ashlar.CLI.Commands.BackgroundAgent;

/// <summary>
/// Remembers the FIRST error a component logged with an exception attached, so the daemon can say
/// why it stopped instead of guessing.
///
/// <para>The defect this exists for: when a hosted service faults, .NET's default
/// <c>BackgroundServiceExceptionBehavior.StopHost</c> logs the exception and tears the host down.
/// <c>host.StartAsync</c> has already returned by then, so the command sat in its duration delay,
/// woke up, and reported <c>ok:true / reason:"duration_elapsed"</c> over a host that had been dead
/// for thirteen of those fifteen seconds. The stop was observable; nothing observed it. This is the
/// observation, and it is deliberately narrow — one exception, the one that started it.</para>
///
/// <para>Only the first is kept. A fault cascades (the service throws, the registry stops, the
/// transport drops), and the last message in that cascade is the least informative one.</para>
/// </summary>
internal sealed class DaemonFaultLog
{
    private readonly object _gate = new();
    private string? _service;
    private string? _reason;

    /// <summary>True once any component has logged an error carrying an exception.</summary>
    public bool HasFault
    {
        get { lock (_gate) { return _service is not null; } }
    }

    /// <summary>The component the fault came from — a logger category or, failing that, the type in the stack.</summary>
    public string Service
    {
        get { lock (_gate) { return _service ?? "unknown (no component logged an exception)"; } }
    }

    /// <summary>What it said, with the exception type and message folded in when the log line omitted them.</summary>
    public string Reason
    {
        get { lock (_gate) { return _reason ?? "no error was logged before the host stopped"; } }
    }

    /// <summary>Records a fault, if this is the first one.</summary>
    public void Capture(string category, string message, Exception? exception)
    {
        if (exception is null)
        {
            return;
        }
        lock (_gate)
        {
            if (_service is not null)
            {
                return;
            }
            _service = ServiceNameFor(category, exception);
            var text = string.IsNullOrWhiteSpace(message) ? exception.Message : message;
            if (!string.IsNullOrEmpty(exception.Message)
                && !text.Contains(exception.Message, StringComparison.Ordinal))
            {
                text = $"{text} ({exception.GetType().Name}: {exception.Message})";
            }
            _reason = text;
        }
    }

    /// <summary>
    /// Records a fault whose component is already known exactly, without the stack-walking guess
    /// <see cref="Capture"/> needs when a log category names the generic host instead of the
    /// component.
    /// </summary>
    public void CaptureService(string service, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        lock (_gate)
        {
            if (_service is not null)
            {
                return;
            }
            _service = service;
            _reason = $"{exception.GetType().Name}: {exception.Message}";
        }
    }

    /// <summary>
    /// Structural evidence that a hosted service died, read from the services themselves rather
    /// than from what anybody logged — and the answer to "did a service fault?" that the daemon's
    /// stop report actually needs.
    ///
    /// <para><b>Why the log is not enough, in both directions.</b> Too loud: <see cref="HasFault"/>
    /// is set by ANY error line carrying an exception, so one unreachable mesh peer over three
    /// weeks was enough to make the next ordinary `docker stop` report <c>status:faulted</c> — the
    /// reported defect, re-armed. Too quiet: a hosted service that stops the host without logging
    /// through this pipeline leaves no trace at all. <c>BackgroundService.ExecuteTask</c> has
    /// neither problem. It is the task <c>ExecuteAsync</c> returned, and
    /// <see cref="Task.IsFaulted"/> on it means that service ended in an exception — nothing else
    /// sets it. A service cancelled by an ordinary shutdown ends <em>Canceled</em>, not
    /// <em>Faulted</em>, so a clean stop cannot be mistaken for a crash here.</para>
    ///
    /// <para><b>Read this BEFORE <c>StopAsync</c>.</b> Afterwards every service has been asked to
    /// stop, which is a different question than the one being asked.</para>
    /// </summary>
    /// <param name="services">The running host's service provider.</param>
    /// <returns>True when at least one hosted service's execute task ended in an exception.</returns>
    public bool CaptureHostedServiceFaults(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var faulted = false;
        try
        {
            foreach (var hosted in services.GetServices<IHostedService>())
            {
                if (hosted is not BackgroundService background)
                {
                    continue;
                }
                if (background.ExecuteTask is not { IsFaulted: true } task)
                {
                    continue;
                }
                faulted = true;
                if (task.Exception?.GetBaseException() is { } error)
                {
                    CaptureService(hosted.GetType().FullName ?? hosted.GetType().Name, error);
                }
            }
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
            // Racing a disposal. "I could not look" must not be reported as "nothing was wrong",
            // but it must not invent a fault either — the caller still has the log and the
            // lifetime signals.
        }
        return faulted;
    }

    /// <summary>
    /// The most specific name available. The logger category is right when the faulting component
    /// logged its own failure; when the generic host logs it instead (category
    /// <c>Microsoft.Extensions.Hosting.Internal.Host</c>), the exception's stack still names the
    /// Ashlar type that threw, which is the answer an operator needs.
    /// </summary>
    private static string ServiceNameFor(string category, Exception exception)
    {
        if (category.StartsWith("Ashlar.", StringComparison.Ordinal))
        {
            return category;
        }
        try
        {
            var frames = new System.Diagnostics.StackTrace(exception, fNeedFileInfo: false).GetFrames();
            foreach (var frame in frames)
            {
                var declaring = frame?.GetMethod()?.DeclaringType?.FullName;
                if (declaring is not null && declaring.StartsWith("Ashlar.", StringComparison.Ordinal))
                {
                    return declaring;
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Reading a stack trace must never be the thing that breaks the error report.
        }
        return category;
    }
}

/// <summary>
/// Feeds <see cref="DaemonFaultLog"/> from the logging pipeline. Registered as an ordinary
/// <see cref="ILoggerProvider"/> so it sees what every component logs, and filtered to
/// <see cref="LogLevel.Error"/> and above so it costs nothing on the normal path.
/// </summary>
internal sealed class DaemonFaultLoggerProvider(DaemonFaultLog log) : ILoggerProvider
{
    private readonly DaemonFaultLog _log = log;

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new Sink(_log, categoryName);

    /// <inheritdoc />
    public void Dispose()
    {
        // Nothing owned: the log outlives the host on purpose, because the report is written after
        // the host is gone.
    }

    private sealed class Sink(DaemonFaultLog log, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel < LogLevel.Error || exception is null)
            {
                return;
            }
            var message = string.Empty;
            try
            {
                message = formatter?.Invoke(state, exception) ?? string.Empty;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A formatter that throws is not a reason to lose the fault itself.
            }
            log.Capture(category, message, exception);
        }
    }
}
