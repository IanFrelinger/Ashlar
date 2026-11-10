using System.CommandLine;
using System.CommandLine.Invocation;
using System.Runtime.InteropServices;
using System.Text.Json;
using Ashlar.Infrastructure.HostProcess;

namespace Ashlar.CLI.Commands;

/// <summary>
/// Aggregated onboarding doctor command with one pass/fail report.
/// </summary>
public sealed class DoctorCommand : Command
{
    /// <summary>Creates a new DoctorCommand instance.</summary>
    public DoctorCommand() : base("doctor", "Run onboarding readiness report (dependencies + CLI/runtime checks).")
    {
        var includeOptionalOpt = new Option<bool>(
            "--include-optional",
            () => false,
            "Include optional dependencies in required checks.");
        var profileOpt = new Option<string>(
            "--profile",
            () => "demo",
            "Doctor profile: demo | self-extend-functional | self-extend-aesthetic | self-extend-visual.");
        var jsonOpt = new Option<bool>("--json", () => false, "Emit JSON output (the root's global --format-json asks for the same report).");
        var fixOpt = new Option<bool>("--fix", () => false, "Attempt safe remediation for fixable onboarding failures.");
        var dryRunOpt = new Option<bool>("--dry-run", () => false, "With --fix, list remediation actions without running them.");
        var yesOpt = new Option<bool>("--yes", () => false, "Auto-approve remediation actions when --fix is enabled.");
        var pathOpt = new Option<DirectoryInfo>(
            "--path",
            () => new DirectoryInfo(Environment.CurrentDirectory),
            "Project directory to also report readiness for (defaults to current). Ignored when not an ashlar project.");

        AddOption(includeOptionalOpt);
        AddOption(profileOpt);
        AddOption(jsonOpt);
        AddOption(fixOpt);
        AddOption(dryRunOpt);
        AddOption(yesOpt);
        AddOption(pathOpt);

        this.WithJsonOutput().SetHandler(async (InvocationContext ctx) =>
        {
            var includeOptional = ctx.ParseResult.GetValueForOption(includeOptionalOpt);
            var profile = ctx.ParseResult.GetValueForOption(profileOpt) ?? "demo";
            // doctor spells its own switch `--json`, but `--format-json` is the CLI-wide flag and is
            // global, so `doctor --format-json` parsed and was then dropped on the floor: the caller
            // got prose on the stdout it was piping into a parser. The JSON payload below already
            // exists — answer to both spellings rather than invent a second report.
            var json = ctx.ParseResult.GetValueForOption(jsonOpt)
                || CommandExecutionSupport.WantsJson(ctx.ParseResult);
            var fix = ctx.ParseResult.GetValueForOption(fixOpt);
            var dryRun = ctx.ParseResult.GetValueForOption(dryRunOpt);
            var yes = ctx.ParseResult.GetValueForOption(yesOpt);
            var path = ctx.ParseResult.GetValueForOption(pathOpt)?.FullName;
            ctx.ExitCode = await ExecuteAsync(profile, includeOptional, json, fix, dryRun, yes, ctx.GetCancellationToken(), path).ConfigureAwait(false);
        });
    }

    internal static async Task<int> ExecuteAsync(
        string profile,
        bool includeOptional,
        bool json,
        bool fix,
        bool dryRun,
        bool autoApproveFixes,
        CancellationToken ct,
        string? projectPath = null)
    {
        // Project readiness is a SEPARATE question from environment health, reported alongside it
        // but never folded into the environment exit code. It is null when the path is not an
        // ashlar project, and the block is then omitted entirely.
        var readiness = DoctorProjectReadiness.Assess(projectPath ?? Environment.CurrentDirectory);

        var dependencyAssessment = await BootstrapRuntime.AssessDemoAsync(profile, includeOptional, ct).ConfigureAwait(false);
        var hostOs = RuntimeInformation.OSDescription;
        var osSupported = OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsWindows();
        var dependencyOk = dependencyAssessment.Supported && !dependencyAssessment.MissingRequired.Any();
        var cliCommand = "dotnet run --project application/src/Ashlar.CLI -- --help";
        // Daemon liveness only. Pulling ghcr.io/ianfrelinger/nexo-cli:latest on every
        // doctor/validate/test invocation is what wedged Docker Desktop on a full local run.
        var containerCommand = "docker info";
        const string recommendedContainerRun = "docker run --rm ghcr.io/ianfrelinger/nexo-cli:latest --help";

        var cliResult = await TimedProcess.RunShellAsync(cliCommand, TimedProcess.CliSmokeTimeout, ct).ConfigureAwait(false);
        var cliSmokePassed = cliResult.ExitCode == 0 && !cliResult.TimedOut;
        var cliSmokeError = cliSmokePassed
            ? string.Empty
            : cliResult.TimedOut
                ? $"cli smoke timed out after {TimedProcess.CliSmokeTimeout.TotalSeconds:0}s"
                : cliResult.StdErr.Trim();

        var containerResult = await TimedProcess.RunShellAsync(containerCommand, TimedProcess.DaemonProbeTimeout, ct)
            .ConfigureAwait(false);
        var containerSmokePassed = containerResult.ExitCode == 0 && !containerResult.TimedOut;
        var containerSmokeError = containerSmokePassed
            ? string.Empty
            : containerResult.TimedOut
                ? $"docker daemon did not answer within {TimedProcess.DaemonProbeTimeout.TotalSeconds:0}s"
                : containerResult.StdErr.Trim();

        var overallOk = osSupported && dependencyOk && cliSmokePassed;
        var remediation = new DoctorRemediationReport();
        if (fix)
        {
            if (dryRun && !json)
                Console.WriteLine("doctor --fix --dry-run: planned remediation (no commands executed):");

            remediation = await DoctorRemediation.RunAsync(
                dependencyAssessment,
                osSupported,
                cliSmokePassed,
                profile,
                includeOptional,
                autoApproveFixes,
                json,
                dryRun,
                ct).ConfigureAwait(false);

            if (!dryRun)
            {
                var postAssessment = await BootstrapRuntime.AssessDemoAsync(profile, includeOptional, ct).ConfigureAwait(false);
                dependencyAssessment = postAssessment;
                dependencyOk = postAssessment.Supported && !postAssessment.MissingRequired.Any();

                var postCli = await TimedProcess.RunShellAsync(cliCommand, TimedProcess.CliSmokeTimeout, ct)
                    .ConfigureAwait(false);
                cliSmokePassed = postCli.ExitCode == 0 && !postCli.TimedOut;
                cliSmokeError = cliSmokePassed
                    ? string.Empty
                    : postCli.TimedOut
                        ? $"cli smoke timed out after {TimedProcess.CliSmokeTimeout.TotalSeconds:0}s"
                        : postCli.StdErr.Trim();
                overallOk = osSupported && dependencyOk && cliSmokePassed;
            }
        }

        if (json)
        {
            var payload = new
            {
                ok = overallOk,
                profile,
                hostOs,
                checks = new
                {
                    osSupported,
                    dependencyCheck = new
                    {
                        supported = dependencyAssessment.Supported,
                        missingRequired = dependencyAssessment.MissingRequired.Select(m => m.Id).ToArray(),
                        includeOptional
                    },
                    cliSmoke = new
                    {
                        passed = cliSmokePassed,
                        command = cliCommand,
                        error = cliSmokeError
                    },
                    containerSmoke = new
                    {
                        passed = containerSmokePassed,
                        command = containerCommand,
                        error = containerSmokeError
                    },
                    remediation = remediation
                },
                projectReadiness = readiness is null ? null : new
                {
                    verdict = readiness.Verdict,
                    ready = readiness.Ready,
                    verified = readiness.Verified,
                    key = readiness.KeyStatus,
                    fingerprint = readiness.Fingerprint,
                    ledger = readiness.LedgerStatus,
                    ledgerEntries = readiness.LedgerEntries,
                    nextStep = readiness.NextStep
                },
                nextSteps = new
                {
                    devContainer = "Open repo in Cursor/VS Code → Dev Containers: Reopen in Container",
                    containerRun = recommendedContainerRun,
                    doctorFix = "dotnet run --project application/src/Ashlar.CLI -- doctor --fix --yes"
                }
            };
            Console.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        }
        else
        {
            Console.WriteLine($"doctor profile: {profile}");
            Console.WriteLine($"host os: {hostOs}");
            Console.WriteLine($"os supported: {(osSupported ? "yes" : "no")}");
            Console.WriteLine($"dependency check: {(dependencyOk ? "pass" : "fail")}");
            if (!dependencyOk)
            {
                Console.WriteLine($"missing required dependencies: {string.Join(", ", dependencyAssessment.MissingRequired.Select(m => m.DisplayName))}");
            }

            Console.WriteLine($"cli smoke: {(cliSmokePassed ? "pass" : "fail")}");
            if (!cliSmokePassed && !string.IsNullOrWhiteSpace(cliSmokeError))
                Console.WriteLine($"  cli smoke error: {cliSmokeError}");

            Console.WriteLine($"container smoke: {(containerSmokePassed ? "pass" : "warn")}");
            if (!containerSmokePassed && !string.IsNullOrWhiteSpace(containerSmokeError))
                Console.WriteLine($"  container smoke error: {containerSmokeError}");

            if (fix)
            {
                Console.WriteLine(dryRun ? "remediation (dry run):" : "remediation:");
                if (remediation.Attempts.Count == 0)
                {
                    Console.WriteLine("  no fixable issues were detected.");
                }
                else
                {
                    foreach (var attempt in remediation.Attempts)
                    {
                        var outcomeLabel = dryRun
                            ? "would-run"
                            : attempt.Success ? "fixed" : "not-fixed";
                        Console.WriteLine(
                            $"  - {attempt.Id}: {outcomeLabel} ({attempt.Status})");
                        if (!string.IsNullOrWhiteSpace(attempt.Command))
                            Console.WriteLine($"      command: {attempt.Command}");
                        if (!string.IsNullOrWhiteSpace(attempt.Message))
                            Console.WriteLine($"      {attempt.Message}");
                    }
                }
            }

            Console.WriteLine($"overall: {(overallOk ? "PASS" : "FAIL")}");
            Console.WriteLine("recommended next steps:");
            Console.WriteLine("  - dev container: Reopen in Container (.devcontainer/)");
            Console.WriteLine($"  - container lane: {recommendedContainerRun}");
            Console.WriteLine("  - remediation lane: dotnet run --project application/src/Ashlar.CLI -- doctor --fix --yes");

            if (readiness is not null)
            {
                Console.WriteLine();
                Console.WriteLine($"project readiness: {readiness.Verdict}");
                Console.WriteLine($"  verify: {(readiness.Verified ? "pass" : "fail")}");
                Console.WriteLine($"  operator key: {readiness.KeyStatus}"
                    + (readiness.Fingerprint is { } fp ? $" ({fp})" : string.Empty));
                Console.WriteLine($"  ledger: {readiness.LedgerStatus}"
                    + (readiness.LedgerStatus == "intact" ? $" ({readiness.LedgerEntries} signed)" : string.Empty));
                Console.WriteLine($"  next: {readiness.NextStep}");
            }
        }

        return overallOk ? 0 : 1;
    }
}
