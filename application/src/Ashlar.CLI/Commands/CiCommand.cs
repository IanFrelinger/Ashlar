using System.CommandLine;
using System.CommandLine.Invocation;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ashlar.Core.Application.Paths;
using Ashlar.Infrastructure.HostProcess;

namespace Ashlar.CLI.Commands;

/// <summary>
/// CI verification: build + smoke tests + architecture validation.
/// Replaces scripts/ci-verify.sh. Used by make ci-verify.
/// </summary>
public sealed class CiCommand : Command
{
    /// <summary>Creates a new CiCommand instance.</summary>
    public CiCommand() : base("ci", "CI verification: build, smoke tests, and architecture validation")
    {
        var verifyCmd = new Command("verify", "Run full CI verification (build, test, validate)");
        verifyCmd.SetHandler(async (InvocationContext ctx) =>
        {
            var exitCode = await ExecuteVerifyAsync();
            Environment.Exit(exitCode);
        });
        AddCommand(verifyCmd);

        var runtimeGateCmd = new Command("runtime-gate", "Run runtime release gate benchmark and SLO checks.");
        runtimeGateCmd.SetHandler(async (InvocationContext ctx) =>
        {
            var exitCode = await ExecuteRuntimeGateAsync();
            Environment.Exit(exitCode);
        });
        AddCommand(runtimeGateCmd);

        var runtimePromotionCmd = new Command("runtime-promotion", "Run strict runtime promotion gate profile.");
        runtimePromotionCmd.SetHandler(async (InvocationContext ctx) =>
        {
            var exitCode = await ExecuteRuntimePromotionAsync();
            Environment.Exit(exitCode);
        });
        AddCommand(runtimePromotionCmd);

        var releaseBundleCmd = new Command("release-bundle", "Run a single-shot release gate bundle and emit unified report.");
        var profileOpt = new Option<string>("--profile", () => "default", "Bundle profile: default | quick | full.");
        var outputDirOpt = new Option<string?>("--output-dir", "Optional output directory for unified report artifacts.");
        releaseBundleCmd.AddOption(profileOpt);
        releaseBundleCmd.AddOption(outputDirOpt);
        releaseBundleCmd.SetHandler(async (InvocationContext ctx) =>
        {
            var profile = ctx.ParseResult.GetValueForOption(profileOpt) ?? "default";
            var outputDir = ctx.ParseResult.GetValueForOption(outputDirOpt);
            var exitCode = await ExecuteReleaseBundleAsync(profile, outputDir);
            Environment.Exit(exitCode);
        });
        AddCommand(releaseBundleCmd);
    }

    /// <summary>
    /// Runs: dotnet build → dotnet test (ProdStyle) → dotnet test (smoke) → ashlar validate.
    /// Exits 0 only if all succeed.
    /// </summary>
    public static async Task<int> ExecuteVerifyAsync()
    {
        var repoRoot = RepoPathResolver.FindRepoRoot();
        var cliProject = ResolveCliProjectPath(repoRoot);
        var infraTestsProject = Path.Combine(repoRoot, "src", "Ashlar.Tests.Infrastructure", "Ashlar.Tests.Infrastructure.csproj");
        if (!File.Exists(cliProject) || !File.Exists(infraTestsProject))
        {
            Console.Error.WriteLine("ci verify: Required projects not found. Run from repository root.");
            return 1;
        }

        // Step 1: Build targeted projects
        Console.WriteLine("=== CI Verify: Build CLI ===");
        var buildCliExit = await RunProcessAsync("dotnet", $"build \"{cliProject}\"", repoRoot);
        if (buildCliExit != 0)
        {
            Console.Error.WriteLine($"ci verify: CLI build failed (exit {buildCliExit})");
            return buildCliExit;
        }

        Console.WriteLine("=== CI Verify: Build Infrastructure Tests ===");
        var buildInfraExit = await RunProcessAsync("dotnet", $"build \"{infraTestsProject}\"", repoRoot);
        if (buildInfraExit != 0)
        {
            Console.Error.WriteLine($"ci verify: Infrastructure test build failed (exit {buildInfraExit})");
            return buildInfraExit;
        }

        // Step 2: Production-like integration (real DI graphs / hosts; fail fast before lighter smoke)
        //
        // The blame window has to clear the WIDEST per-test timeout in what this filter selects,
        // and most ProdStyle classes use TestTimeouts.HostTouching (480s). At the 120s this used
        // to pass, a stalled ProdStyle test could never fail as a timeout: the host was killed
        // first, taking every already-recorded result with it. Kept equal to
        // ValidationServiceAdapter.ValidateBlameHangTimeoutSeconds, and frozen from both sides:
        // TimeoutConventionTests fails if the two drift, and LaneBlameWindowConventionTests
        // measures this window against the real per-test deadlines its filter selects — reading
        // the [Fact(Timeout = ...)] values themselves rather than the TestTimeouts constants, so
        // a timeout written as a bare literal cannot slip under it. The 30s on the smoke step
        // below survives that check on its own evidence: BaseFrameworkSmokeTests carry no
        // deadline anywhere near it.
        Console.WriteLine("=== CI Verify: Production-like tests (ProdStyle, FluentAssertions-safe filter) ===");
        var prodStyleExit = await RunProcessAsync(
            "dotnet",
            $"test \"{infraTestsProject}\" -f net8.0 --no-build --blame-hang-timeout 900s --blame-hang-dump-type none --filter \"Category=ProdStyle&FullyQualifiedName!~ForgeEndpointsTests&FullyQualifiedName!~FrameworkVirtualProdDemosTests\" --verbosity minimal",
            repoRoot);
        if (prodStyleExit != 0)
        {
            Console.Error.WriteLine($"ci verify: Production-like tests failed (exit {prodStyleExit})");
            return prodStyleExit;
        }

        // Step 3: Smoke tests (BaseFrameworkSmokeTests)
        Console.WriteLine("=== CI Verify: Smoke Tests ===");
        var testExit = await RunProcessAsync(
            "dotnet",
            $"test \"{infraTestsProject}\" --no-build --blame-hang-timeout 30s --blame-hang-dump-type none --filter \"FullyQualifiedName~BaseFrameworkSmokeTests\" --verbosity minimal",
            repoRoot);
        if (testExit != 0)
        {
            Console.Error.WriteLine($"ci verify: Smoke tests failed (exit {testExit})");
            return testExit;
        }

        // Step 4: Architecture validation (ashlar validate)
        Console.WriteLine("=== CI Verify: Architecture Validation ===");
        var validateExit = await RunProcessAsync(
            "dotnet",
            $"run --project \"{cliProject}\" -- validate",
            repoRoot);
        if (validateExit != 0)
        {
            Console.Error.WriteLine($"ci verify: Architecture validation failed (exit {validateExit})");
            return validateExit;
        }

        Console.WriteLine("=== CI Verify: All checks passed ===");
        return 0;
    }

    /// <summary>Creates a new ExecuteRuntimeGateAsync instance.</summary>
    public static async Task<int> ExecuteRuntimeGateAsync()
    {
        var repoRoot = RepoPathResolver.FindRepoRoot();
        var cliProject = ResolveCliProjectPath(repoRoot);
        if (!File.Exists(cliProject))
        {
            Console.Error.WriteLine($"ci runtime-gate: Missing CLI project: {cliProject}");
            return 1;
        }

        Console.WriteLine("=== CI Runtime Gate ===");
        var exit = await RunProcessAsync(
            "dotnet",
            $"run --project \"{cliProject}\" -- runtime release-gate --repo-root \"{repoRoot}\" --mode core --allow-mock --core-min-pass-rate 0.85 --core-min-total 3 --core-history-window 6 --visual-required-mode false --emit-slo-evidence --slo-warning-only --slo-evidence-path \"{Path.Combine(repoRoot, ".ashlar", "runtime", "runtime-release-gate-slo.json")}\" --ncr-resolution-ms-slo 250 --ncr-load-ms-slo 1000 --ncr-outcome-ms-slo 1500 --ncr-failure-rate-slo 0.2",
            repoRoot,
            extraEnv: new Dictionary<string, string> { ["ASHLAR_ALLOW_MOCK"] = "1" });
        if (exit != 0)
            Console.Error.WriteLine($"ci runtime-gate: Failed (exit {exit})");
        return exit;
    }

    /// <summary>Creates a new ExecuteRuntimePromotionAsync instance.</summary>
    public static async Task<int> ExecuteRuntimePromotionAsync()
    {
        var repoRoot = RepoPathResolver.FindRepoRoot();
        var cliProject = ResolveCliProjectPath(repoRoot);
        if (!File.Exists(cliProject))
        {
            Console.Error.WriteLine($"ci runtime-promotion: Missing CLI project: {cliProject}");
            return 1;
        }

        Console.WriteLine("=== CI Runtime Promotion ===");
        var exit = await RunProcessAsync(
            "dotnet",
            $"run --project \"{cliProject}\" -- runtime release-gate --repo-root \"{repoRoot}\" --mode full --lane-repetitions 3 --core-min-pass-rate 0.9 --core-min-total 9 --core-history-window 9 --visual-required-mode true --visual-min-pass-rate 0.85 --visual-min-total 9 --visual-history-window 9 --visual-promotion-streak 3 --emit-slo-evidence --slo-evidence-path \"{Path.Combine(repoRoot, ".ashlar", "runtime", "runtime-release-promotion-slo.json")}\" --ncr-resolution-ms-slo 200 --ncr-load-ms-slo 900 --ncr-outcome-ms-slo 1200 --ncr-failure-rate-slo 0.1",
            repoRoot);
        if (exit != 0)
            Console.Error.WriteLine($"ci runtime-promotion: Failed (exit {exit})");
        return exit;
    }

    /// <summary>Creates a new ExecuteReleaseBundleAsync instance.</summary>
    public static async Task<int> ExecuteReleaseBundleAsync(string profile = "default", string? outputDirectory = null)
    {
        var repoRoot = RepoPathResolver.FindRepoRoot();
        var cliProject = ResolveCliProjectPath(repoRoot);
        if (!File.Exists(cliProject))
        {
            Console.Error.WriteLine($"ci release-bundle: Missing CLI project: {cliProject}");
            return 1;
        }

        var normalizedProfile = string.IsNullOrWhiteSpace(profile) ? "default" : profile.Trim().ToLowerInvariant();
        var steps = BuildReleaseBundleSteps(normalizedProfile, cliProject, repoRoot);
        if (steps.Count == 0)
        {
            Console.Error.WriteLine($"ci release-bundle: Unknown profile '{profile}'. Supported profiles: default, quick, full.");
            return 1;
        }

        var results = new List<ReleaseBundleStepResult>();
        var sloSummaries = new List<ReleaseBundleSloEvidenceSummary>();
        foreach (var step in steps)
        {
            Console.WriteLine($"=== Release Bundle: {step.Name} ===");
            var startedUtc = DateTimeOffset.UtcNow;
            var exitCode = await RunProcessAsync(step.FileName, step.Arguments, repoRoot);
            var endedUtc = DateTimeOffset.UtcNow;
            var artifactHints = step.ArtifactHints
                .Select(hint => Path.GetFullPath(Path.Combine(repoRoot, hint)))
                .ToArray();
            results.Add(new ReleaseBundleStepResult(
                step.Id,
                step.Name,
                step.FileName,
                step.Arguments,
                exitCode,
                startedUtc,
                endedUtc,
                artifactHints));

            foreach (var relativeSloPath in step.SloEvidenceRelativePaths)
            {
                var fullSloPath = Path.GetFullPath(Path.Combine(repoRoot, relativeSloPath));
                sloSummaries.Add(TryParseSloEvidenceSummary(step.Id, step.Name, relativeSloPath, fullSloPath));
            }
        }

        var verdict = results.All(result => result.ExitCode == 0) ? "PASS" : "FAIL";
        var reportDirectory = ResolveReleaseBundleOutputDirectory(repoRoot, outputDirectory);
        Directory.CreateDirectory(reportDirectory);
        var jsonPath = Path.Combine(reportDirectory, "release-bundle-report.json");
        var markdownPath = Path.Combine(reportDirectory, "release-bundle-report.md");
        var report = new ReleaseBundleReport(
            normalizedProfile,
            DateTimeOffset.UtcNow,
            verdict,
            results,
            sloSummaries,
            jsonPath,
            markdownPath);

        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(jsonPath, json).ConfigureAwait(false);

        var markdown = BuildReleaseBundleMarkdown(report);
        await File.WriteAllTextAsync(markdownPath, markdown).ConfigureAwait(false);

        Console.WriteLine($"=== Release Bundle Verdict: {verdict} ===");
        Console.WriteLine($"JSON report: {jsonPath}");
        Console.WriteLine($"Markdown report: {markdownPath}");

        return verdict == "PASS" ? 0 : 1;
    }

    private static async Task<int> RunProcessAsync(
        string fileName,
        string arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string>? extraEnv = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
        };
        if (extraEnv is not null)
        {
            foreach (var (key, value) in extraEnv)
                psi.Environment[key] = value;
        }

        var run = await TimedProcess.RunAsync(psi, TimedProcess.OperatorCommandTimeout).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(run.StdOut))
            Console.Write(run.StdOut);
        if (!string.IsNullOrEmpty(run.StdErr))
            Console.Error.Write(run.StdErr);
        return run.ExitCode;
    }

    private static List<ReleaseBundleStep> BuildReleaseBundleSteps(string profile, string cliProject, string repoRoot)
    {
        var defaultSteps = new List<ReleaseBundleStep>
        {
            new(
                "verify",
                "CI verify",
                "dotnet",
                $"run --project \"{cliProject}\" -- ci verify",
                [],
                []),
            new(
                "runtime-gate",
                "Runtime gate",
                "dotnet",
                $"run --project \"{cliProject}\" -- ci runtime-gate",
                [
                    ".ashlar/runtime/runtime-release-gate-slo.json",
                    ".ashlar/runtime/runtime-release-gate-slo.md"
                ],
                [".ashlar/runtime/runtime-release-gate-slo.json"]),
            new(
                "doctor",
                "Doctor check",
                "dotnet",
                $"run --project \"{cliProject}\" -- doctor --json",
                [
                    "docs/OnboardingAutomation.md"
                ],
                [])
        };

        if (profile == "quick")
            return defaultSteps.Where(step => step.Id != "verify").ToList();
        if (profile == "default")
            return defaultSteps;
        if (profile == "full")
        {
            var fullSteps = new List<ReleaseBundleStep>(defaultSteps)
            {
                new(
                    "trust-gate",
                    "Trust gate",
                    "dotnet",
                    $"run --project \"{cliProject}\" -- trust boundary --format-json",
                    [],
                    []),
                new(
                    "cross-platform-smoke",
                    "Cross-platform smoke",
                    "dotnet",
                    $"run --project \"{cliProject}\" -- test portable --scope smoke",
                    [],
                    [])
            };
            return fullSteps;
        }

        return new List<ReleaseBundleStep>();
    }

    internal static IReadOnlyList<string> GetReleaseBundleStepIds(string profile)
    {
        var repoRoot = RepoPathResolver.FindRepoRoot();
        var cliProject = ResolveCliProjectPath(repoRoot);
        return BuildReleaseBundleSteps(profile.Trim().ToLowerInvariant(), cliProject, repoRoot)
            .Select(step => step.Id)
            .ToList();
    }

    private static string ResolveReleaseBundleOutputDirectory(string repoRoot, string? requestedOutputDirectory)
    {
        if (!string.IsNullOrWhiteSpace(requestedOutputDirectory))
            return Path.GetFullPath(requestedOutputDirectory, repoRoot);
        return Path.Combine(repoRoot, ".ashlar", "release-bundle", "last-run");
    }

    private static string BuildReleaseBundleMarkdown(ReleaseBundleReport report)
    {
        var lines = new List<string>
        {
            "# Release bundle report",
            string.Empty,
            $"Profile: `{report.Profile}`",
            $"Generated: `{report.GeneratedUtc:O}`",
            $"Verdict: **{report.Verdict}**",
            string.Empty,
            "| Step | Exit code | Started (UTC) | Ended (UTC) | Artifacts |",
            "|------|-----------|---------------|-------------|-----------|"
        };

        foreach (var step in report.Steps)
        {
            var artifacts = step.ArtifactHints.Length == 0
                ? "n/a"
                : string.Join("<br/>", step.ArtifactHints.Select(path => $"`{path}`"));
            lines.Add($"| {step.Name} | {step.ExitCode} | {step.StartedUtc:O} | {step.EndedUtc:O} | {artifacts} |");
        }

        lines.Add(string.Empty);
        lines.Add("## SLO evidence summary");
        lines.Add(string.Empty);
        if (report.SloEvidenceSummaries.Count == 0)
        {
            lines.Add("_No SLO evidence paths configured for this profile._");
        }
        else
        {
            lines.Add("| Step | SLO artifact | Present | Parsed | Overall SLO | Mode | ncr.failure_rate | Failures (checks) |");
            lines.Add("|------|--------------|---------|--------|-------------|------|-------------------|-------------------|");
            foreach (var slo in report.SloEvidenceSummaries)
            {
                var present = slo.FileExists ? "yes" : "no";
                var parsed = slo.Parsed ? "yes" : "no";
                var overall = slo.OverallPassed.HasValue ? (slo.OverallPassed.Value ? "PASS" : "FAIL") : "n/a";
                var mode = slo.Mode ?? "n/a";
                var fr = slo.NcrFailureRate.HasValue ? slo.NcrFailureRate.Value.ToString("0.####") : "n/a";
                var failures = slo.FailedCheckCount.HasValue ? slo.FailedCheckCount.Value.ToString() : "n/a";
                lines.Add($"| {slo.StepName} | `{slo.RelativePath}` | {present} | {parsed} | {overall} | {mode} | {fr} | {failures} |");
            }
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static ReleaseBundleSloEvidenceSummary TryParseSloEvidenceSummary(
        string stepId,
        string stepName,
        string relativePath,
        string fullPath)
    {
        if (!File.Exists(fullPath))
        {
            return new ReleaseBundleSloEvidenceSummary(
                stepId,
                stepName,
                relativePath,
                fullPath,
                FileExists: false,
                Parsed: false,
                ParseError: null,
                OverallPassed: null,
                Mode: null,
                NcrFailureRate: null,
                FailedCheckCount: null);
        }

        try
        {
            var root = JsonNode.Parse(File.ReadAllText(fullPath));
            if (root is null)
            {
                return new ReleaseBundleSloEvidenceSummary(
                    stepId,
                    stepName,
                    relativePath,
                    fullPath,
                    FileExists: true,
                    Parsed: false,
                    ParseError: "empty document",
                    OverallPassed: null,
                    Mode: null,
                    NcrFailureRate: null,
                    FailedCheckCount: null);
            }

            var passed = root["passed"]?.GetValue<bool?>();
            var mode = root["mode"]?.GetValue<string>();
            var failureRate = root["metrics"]?["ncrFailureRate"]?.GetValue<double?>();
            int? failedChecks = null;
            if (root["checks"] is JsonArray checkArr)
            {
                failedChecks = checkArr
                    .OfType<JsonObject>()
                    .Count(c => c["passed"]?.GetValue<bool?>() == false);
            }

            return new ReleaseBundleSloEvidenceSummary(
                stepId,
                stepName,
                relativePath,
                fullPath,
                FileExists: true,
                Parsed: true,
                ParseError: null,
                OverallPassed: passed,
                Mode: mode,
                NcrFailureRate: failureRate,
                FailedCheckCount: failedChecks);
        }
        catch (Exception ex)
        {
            return new ReleaseBundleSloEvidenceSummary(
                stepId,
                stepName,
                relativePath,
                fullPath,
                FileExists: true,
                Parsed: false,
                ParseError: ex.Message,
                OverallPassed: null,
                Mode: null,
                NcrFailureRate: null,
                FailedCheckCount: null);
        }
    }

    private sealed record ReleaseBundleStep(
        string Id,
        string Name,
        string FileName,
        string Arguments,
        IReadOnlyList<string> ArtifactHints,
        IReadOnlyList<string> SloEvidenceRelativePaths);

    private sealed record ReleaseBundleStepResult(
        string Id,
        string Name,
        string FileName,
        string Arguments,
        int ExitCode,
        DateTimeOffset StartedUtc,
        DateTimeOffset EndedUtc,
        string[] ArtifactHints);

    private sealed record ReleaseBundleReport(
        string Profile,
        DateTimeOffset GeneratedUtc,
        string Verdict,
        IReadOnlyList<ReleaseBundleStepResult> Steps,
        IReadOnlyList<ReleaseBundleSloEvidenceSummary> SloEvidenceSummaries,
        string JsonReportPath,
        string MarkdownReportPath);

    private sealed record ReleaseBundleSloEvidenceSummary(
        string StepId,
        string StepName,
        string RelativePath,
        string FullPath,
        bool FileExists,
        bool Parsed,
        string? ParseError,
        bool? OverallPassed,
        string? Mode,
        double? NcrFailureRate,
        int? FailedCheckCount);

    /// <summary>Application CLI lives under <c>application/src/Ashlar.CLI</c>; legacy path kept as fallback.</summary>
    internal static string ResolveCliProjectPath(string repoRoot)
    {
        var candidates = new[]
        {
            Path.Combine(repoRoot, "application", "src", "Ashlar.CLI", "Ashlar.CLI.csproj"),
            Path.Combine(repoRoot, "src", "Ashlar.CLI", "Ashlar.CLI.csproj"),
        };
        foreach (var path in candidates)
        {
            if (File.Exists(path))
                return path;
        }

        return candidates[0];
    }
}
