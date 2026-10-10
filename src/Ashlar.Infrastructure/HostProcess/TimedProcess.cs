using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Ashlar.Abstractions.Security.Egress;

namespace Ashlar.Infrastructure.HostProcess;

/// <summary>
/// Runs a child process with a wall-clock ceiling. Timeout or caller cancel
/// always kills the entire process tree so a wedged Docker CLI cannot freeze the host.
/// </summary>
public static class TimedProcess
{
    /// <summary>GNU timeout's conventional exit status for a killed-on-timeout child.</summary>
    public const int TimeoutExitCode = 124;

    /// <summary>Ceiling for daemon liveness probes such as <c>docker info</c>.</summary>
    public static readonly TimeSpan DaemonProbeTimeout = TimeSpan.FromSeconds(8);

    /// <summary>Ceiling for the doctor's <c>dotnet run … --help</c> CLI smoke.</summary>
    public static readonly TimeSpan CliSmokeTimeout = TimeSpan.FromSeconds(90);

    /// <summary>Ceiling for operator-initiated install/remediation shells.</summary>
    public static readonly TimeSpan RemediationTimeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Ceiling for long operator commands (<c>ci verify</c>, <c>release preflight</c>,
    /// multi-env docker test). Bounded so a wedged child cannot freeze the host forever.
    /// </summary>
    public static readonly TimeSpan OperatorCommandTimeout = TimeSpan.FromHours(2);

    /// <summary>Ceiling for Docker API <c>WaitContainer</c> when the caller passes no token.</summary>
    public static readonly TimeSpan DockerWaitTimeout = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Starts <paramref name="startInfo"/>, captures stdout/stderr, and waits until exit,
    /// <paramref name="timeout"/>, or <paramref name="cancellationToken"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="Process.WaitForExitAsync(CancellationToken)"/> does not kill the child.
    /// A cancelled wait against <c>docker info</c> or <c>docker run</c> would leave the CLI
    /// attached to a wedged daemon and freeze later Docker use on the host.
    /// </remarks>
    public static async Task<TimedProcessResult> RunAsync(
        ProcessStartInfo startInfo,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        var decision = EgressGuard.ProcessDefault.Evaluate(new EgressRequest(EgressFamilies.Process,
            "EG-PROC-08", "process:" + Path.GetFileNameWithoutExtension(startInfo.FileName)));
        if (decision.Refuses) throw new EgressRefusedException(decision);
        return await RunEvaluatedAsync(startInfo, timeout, decision, cancellationToken).ConfigureAwait(false);
    }

    // Infrastructure's classified process runner already made this decision, including its narrow Docker
    // sandbox exception. Carry it explicitly rather than publishing a second decision or using ambient state.
    internal static async Task<TimedProcessResult> RunEvaluatedAsync(
        ProcessStartInfo startInfo, TimeSpan timeout, EgressDecision decision, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentNullException.ThrowIfNull(decision);
        decision.ThrowIfRefused();
        if (timeout <= TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(timeout), "Timeout must be positive or infinite.");

        startInfo.UseShellExecute = false;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.CreateNoWindow = true;

        Process? process = null;
        try
        {
            process = Process.Start(startInfo);
            if (process is null)
                return new TimedProcessResult(1, string.Empty, "Failed to start process.", TimedOut: false);

            // Drain without the wait token. Cancelling ReadToEndAsync leaves the child
            // blocked on a full pipe; kill-then-drain lets the OS close the handles.
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (timeout != Timeout.InfiniteTimeSpan)
                linked.CancelAfter(timeout);

            try
            {
                await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                await KillTreeAsync(process).ConfigureAwait(false);
                var (stdout, stderr) = await DrainAsync(stdoutTask, stderrTask).ConfigureAwait(false);
                return new TimedProcessResult(TimeoutExitCode, stdout, stderr, TimedOut: true);
            }
            catch (OperationCanceledException)
            {
                await KillTreeAsync(process).ConfigureAwait(false);
                await DrainAsync(stdoutTask, stderrTask).ConfigureAwait(false);
                throw;
            }

            var (outText, errText) = await DrainAsync(stdoutTask, stderrTask).ConfigureAwait(false);
            return new TimedProcessResult(process.ExitCode, outText, errText, TimedOut: false);
        }
        finally
        {
            process?.Dispose();
        }
    }

    /// <summary>
    /// Runs a bash (<c>-lc</c>) or PowerShell (<c>-NoProfile -Command</c>) one-liner under
    /// the same kill-on-timeout rules as <see cref="RunAsync"/>.
    /// </summary>
    public static Task<TimedProcessResult> RunShellAsync(
        string command,
        TimeSpan timeout,
        CancellationToken cancellationToken = default,
        string? workingDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var psi = new ProcessStartInfo
        {
            FileName = isWindows ? "powershell.exe" : "bash",
        };
        if (!string.IsNullOrWhiteSpace(workingDirectory))
            psi.WorkingDirectory = workingDirectory;

        if (isWindows)
        {
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add(command);
        }
        else
        {
            psi.ArgumentList.Add("-lc");
            psi.ArgumentList.Add(command);
        }

        return RunAsync(psi, timeout, cancellationToken);
    }

    private static async Task KillTreeAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // already gone
        }
        catch (Win32Exception)
        {
            // access denied / already exiting
        }

        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }
    }

    private static async Task<(string StdOut, string StdErr)> DrainAsync(Task<string> stdout, Task<string> stderr)
    {
        try
        {
            await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            return (await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
        }
        catch (TimeoutException)
        {
            return (
                stdout.IsCompletedSuccessfully ? stdout.Result : string.Empty,
                stderr.IsCompletedSuccessfully ? stderr.Result : string.Empty);
        }
    }
}

/// <summary>Captured result of a <see cref="TimedProcess"/> run.</summary>
/// <param name="ExitCode">Child exit code, or <see cref="TimedProcess.TimeoutExitCode"/> when timed out.</param>
/// <param name="StdOut">Captured standard output.</param>
/// <param name="StdErr">Captured standard error.</param>
/// <param name="TimedOut">True when the wall-clock ceiling elapsed and the tree was killed.</param>
public sealed record TimedProcessResult(int ExitCode, string StdOut, string StdErr, bool TimedOut);
