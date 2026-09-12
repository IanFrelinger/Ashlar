using Microsoft.Extensions.Logging;

namespace Ashlar.Commercial.Tests.Fleet;

/// <summary>
/// An <see cref="ILogger{TCategoryName}"/> that sets an event when the service under test logs a
/// message the caller is waiting for.
/// </summary>
/// <typeparam name="T">Logger category.</typeparam>
/// <remarks>
/// <para>A hosted service's <c>StartAsync</c> returns at the first <c>await</c> inside its loop, so
/// "the round has happened" is not observable from the caller. The alternatives are a fixed sleep -
/// which <c>docs/HowGatesGoQuiet.md</c> section 8 names as a defect in front of a POSITIVE
/// assertion, and every assertion this exists for is positive - or polling the database, which is
/// the same guess with extra steps. This makes the signal come from the code under test: the
/// service's own log line is the handshake.</para>
///
/// <para>Waits on it are bounded, and a bound that expires is reported as itself rather than
/// asserted through: a control that times out has measured nothing and must say so.</para>
/// </remarks>
internal sealed class SignallingLogger<T> : ILogger<T>
{
    private readonly Func<string, bool> _matches;
    private readonly ManualResetEventSlim _signal;

    /// <summary>Initializes a new signalling logger.</summary>
    /// <param name="matches">Predicate over the formatted message.</param>
    /// <param name="signal">Set when a message matches.</param>
    public SignallingLogger(Func<string, bool> matches, ManualResetEventSlim signal)
    {
        _matches = matches;
        _signal = signal;
    }

    /// <inheritdoc />
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    /// <inheritdoc />
    public bool IsEnabled(LogLevel logLevel) => true;

    /// <inheritdoc />
    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (_matches(formatter(state, exception)))
            _signal.Set();
    }
}
