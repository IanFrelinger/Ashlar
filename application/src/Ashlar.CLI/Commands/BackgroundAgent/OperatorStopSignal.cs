using System.Runtime.InteropServices;

namespace Ashlar.CLI.Commands.BackgroundAgent;

/// <summary>
/// Positive evidence that a HUMAN — or an orchestrator acting for one — asked this process to stop.
///
/// <para><b>Why this exists.</b> The daemon has to tell an operator's stop apart from a crash, and
/// every signal it had for that was an inference. The cancellation token the command is handed is
/// not enough: <c>System.CommandLine</c>'s <c>CancelOnProcessTermination</c> cancels it from
/// <c>Console.CancelKeyPress</c> and <c>AppDomain.ProcessExit</c>, and neither fires on the SIGTERM
/// that `docker stop` sends while a generic host is running — <c>ConsoleLifetime</c> registers its
/// own SIGTERM handler with <c>Cancel = true</c>, which suppresses the runtime's default
/// termination and therefore suppresses <c>ProcessExit</c> too. So on the exact case that was
/// reported, the token stays uncancelled and the only thing that moves is
/// <c>ApplicationStopping</c>, which a crashing hosted service raises identically. Inferring the
/// difference from "did anything log an error" is a guess, and it guessed wrong in production.
/// This observes the signal itself, and the observation is reported (the stop report's
/// <c>signal</c> field) so the mechanism stays checkable instead of merely asserted.</para>
///
/// <para><b>What it deliberately does not do.</b> It never sets <c>PosixSignalContext.Cancel</c>
/// and never stops the host. <c>ConsoleLifetime</c> already registers for SIGTERM, SIGINT and
/// SIGQUIT and calls <c>StopApplication</c> for all three — measured: SIGINT and SIGTERM each stop
/// a bounded and an unbounded run within a second, exit 0, reason "shutdown". A second actor
/// setting <c>Cancel</c> would add nothing except a way to go wrong, and it would be actively
/// harmful outside the host's lifetime: this object also spans the park/retry loop, where there is
/// no host to stop, and suppressing the default action there would turn `docker stop` of a parked
/// node into a ten-second wait for SIGKILL. All POSIX signal registrations for a signal run, so
/// recording alongside <c>ConsoleLifetime</c> costs nothing and changes nothing.</para>
///
/// <para><b>One inherited-disposition trap, for whoever debugs this next.</b> A process started as
/// a background job by a shell with job control OFF (any non-interactive script doing
/// <c>ashlar background-agent daemon &amp;</c>) inherits SIGINT and SIGQUIT set to <c>SIG_IGN</c>,
/// per POSIX — and .NET honours an inherited ignore, so no handler here or in
/// <c>ConsoleLifetime</c> ever runs and the daemon appears to ignore Ctrl+C entirely. It is the
/// shell, not the daemon: <c>grep SigIgn /proc/&lt;pid&gt;/status</c> shows bits 2 and 3 set, SIGTERM
/// still works, and the same binary stops instantly on SIGINT under <c>set -m</c> or a real
/// terminal. `docker stop` sends SIGTERM, so containers are unaffected.</para>
/// </summary>
internal sealed class OperatorStopSignal : IDisposable
{
    private readonly List<PosixSignalRegistration> _registrations = [];
    private readonly object _gate = new();
    private string? _observed;

    private OperatorStopSignal()
    {
    }

    /// <summary>
    /// Starts listening for the signals an operator stop actually arrives on: SIGTERM
    /// (`docker stop`, systemd, Kubernetes), SIGINT (Ctrl+C) and SIGQUIT.
    ///
    /// <para>Each registration is attempted separately and a platform that refuses one is not a
    /// failure: the daemon must run on Windows, where the set of raisable POSIX signals is
    /// different, and being unable to listen for a stop is never a reason to refuse to start. When
    /// nothing can be registered this degrades to <see cref="Observed"/> staying false, which the
    /// caller already handles — it falls back to the cancellation token and to
    /// <c>ApplicationStopping</c>.</para>
    /// </summary>
    public static OperatorStopSignal Listen()
    {
        var signal = new OperatorStopSignal();
        foreach (var posix in new[] { PosixSignal.SIGTERM, PosixSignal.SIGINT, PosixSignal.SIGQUIT })
        {
            try
            {
                signal._registrations.Add(PosixSignalRegistration.Create(posix, context =>
                {
                    // No context.Cancel, no StopApplication — see the type remarks. Record and get
                    // out of the way, on a thread the runtime needs back.
                    signal.Record(context.Signal.ToString());
                }));
            }
            catch (Exception ex) when (ex is PlatformNotSupportedException or ArgumentOutOfRangeException or IOException)
            {
                // This platform does not raise that signal. The others still count.
            }
        }
        return signal;
    }

    /// <summary>True once any operator stop signal has been delivered to this process.</summary>
    public bool Observed
    {
        get { lock (_gate) { return _observed is not null; } }
    }

    /// <summary>The first signal observed (e.g. <c>SIGTERM</c>), or null if none has been.</summary>
    public string? Name
    {
        get { lock (_gate) { return _observed; } }
    }

    /// <summary>
    /// Records a stop signal. Internal rather than private so the signal-to-verdict path can be
    /// tested without raising a real signal at the test host — the wiring that was never tested is
    /// exactly how the reported defect survived.
    /// </summary>
    internal void Record(string name)
    {
        lock (_gate)
        {
            _observed ??= name;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var registration in _registrations)
        {
            registration.Dispose();
        }
        _registrations.Clear();
    }
}
