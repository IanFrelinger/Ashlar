using System.Diagnostics;
using Ashlar.Abstractions.Security.Egress;

namespace Ashlar.Tools.Dev;

/// <summary>Dotnet runner.</summary>
internal static class DotnetRunner
{
    public static async Task<(int exitCode, string stdout, string stderr, bool timedOut)> RunAsync(
        string workingDirectory,
        string arguments,
        TimeSpan timeout,
        CancellationToken ct)
    {
        // SPEC-007 EG-PROC-01, report-only: a build or test that restores reaches the NuGet feeds.
        _ = EgressGuard.ProcessDefault.Evaluate(new EgressRequest(EgressFamilies.Process, "EG-PROC-01", RestoreDestination(arguments)));
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        var psi = new ProcessStartInfo("dotnet", arguments)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        using var p = Process.Start(psi)!;
        var soTask = p.StandardOutput.ReadToEndAsync(timeoutCts.Token);
        var seTask = p.StandardError.ReadToEndAsync(timeoutCts.Token);

        try
        {
            await p.WaitForExitAsync(timeoutCts.Token);
            return (p.ExitCode, await soTask, await seTask, timedOut: false);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { /* ignore */ }

            var so = "";
            var se = "";
            try { so = await soTask; } catch { /* ignore */ }
            try { se = await seTask; } catch { /* ignore */ }
            return (-1, so, se, timedOut: true);
        }
    }

    // The decision's destination. "host:dotnet" only when the verb is build, test, publish or pack AND an argument is
    // exactly --no-restore or --no-build, so nothing is restored; anything else (dotnet run, an unknown verb, an
    // implicit restore) is "nuget-feeds". Whitespace-split on purpose: a quoted or embedded flag reads as a restore.
    private static string RestoreDestination(string? arguments)
    {
        var tokens = (arguments ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var noRestore = tokens.Length > 0
            && tokens[0] is "build" or "test" or "publish" or "pack"
            && tokens.Any(t => t is "--no-restore" or "--no-build");
        return noRestore ? "host:dotnet" : "nuget-feeds";
    }
}

