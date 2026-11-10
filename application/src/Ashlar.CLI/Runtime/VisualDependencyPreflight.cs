using System.ComponentModel;
using Ashlar.Infrastructure.HostProcess;
using Ashlar.Infrastructure.Preflight;

namespace Ashlar.CLI.Runtime;

/// <summary>Read-only adapters for the visual operation's dependencies. No fallback or install policy.</summary>
internal static class VisualDependencyPreflight
{
    internal const string DockerCli = "visual:docker-cli";
    internal const string DockerDaemon = "visual:docker-daemon";
    internal const string OllamaCli = "visual:ollama-cli";

    internal static Task<DependencyPreflightReport> RunAsync(
        string repoRoot,
        bool runVisualQa,
        bool required,
        CancellationToken ct,
        bool enabled = true,
        Func<string, TimeSpan, CancellationToken, Task<TimedProcessResult>>? runProbe = null)
    {
        // Reuse the enforcing, cancellable process funnel. Commands are fixed trusted probes,
        // never interpolated user input, installation, model downloads, or container execution.
        runProbe ??= (command, timeout, token) => TimedProcess.RunShellAsync(command, timeout, token, repoRoot);
        var runner = new DependencyPreflight(
        [
            Probe(DockerCli, CommandExists("docker"), DependencyAvailability.Missing, runProbe),
            Probe(DockerDaemon, OperatingSystem.IsWindows() ? "docker info *> $null; exit $LASTEXITCODE" : "docker info >/dev/null 2>&1",
                DependencyAvailability.Unavailable, runProbe),
            Probe(OllamaCli, CommandExists("ollama"), DependencyAvailability.Missing, runProbe)
        ], probeTimeout: TimedProcess.DaemonProbeTimeout);
        DependencyRequirement[] dependencies = runVisualQa
            ? [new(DockerCli, required), new(DockerDaemon, required), new(OllamaCli, required)]
            : [];
        return runner.RunAsync("visual-qa", dependencies, enabled, ct);
    }

    internal static string Summarize(DependencyPreflightReport report) => report.Checks.Count == 0
        ? "No visual dependencies selected."
        : report.RequiredAvailable
        && report.Checks.All(check => check.Available)
            ? "Visual QA infra ready (docker + daemon + ollama)."
            : "Visual QA dependencies not verified: " + string.Join("; ", report.Checks.Where(check => !check.Available)
                .Select(check => $"{check.Id}: {check.Availability}"));

    private static string CommandExists(string command) => OperatingSystem.IsWindows()
        ? $"if (Get-Command {command} -CommandType Application -ErrorAction SilentlyContinue) {{ exit 0 }} else {{ exit 1 }}"
        : $"command -v {command} >/dev/null 2>&1";

    private static DependencyProbe Probe(
        string id,
        string command,
        DependencyAvailability failure,
        Func<string, TimeSpan, CancellationToken, Task<TimedProcessResult>> runProbe) => new(id, async ct =>
    {
        try
        {
            var result = await runProbe(command, TimedProcess.DaemonProbeTimeout, ct).ConfigureAwait(false);
            return result.TimedOut
                ? new(DependencyAvailability.TimedOut, "Visual dependency probe timed out.")
                : new(result.ExitCode == 0 ? DependencyAvailability.Available : failure,
                    result.ExitCode == 0 ? "Dependency probe succeeded." : "Dependency probe did not succeed.");
        }
        catch (Win32Exception)
        {
            // Missing/inaccessible shell is not proof the target dependency is absent. Do not catch
            // egress/security refusals, cancellation, or arbitrary exceptions and permit fallback.
            return new(DependencyAvailability.Unavailable, "Could not start the dependency probe shell.");
        }
    });
}
