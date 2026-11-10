using System.CommandLine;
using System.CommandLine.Invocation;
using System.Diagnostics;
using System.Text.Json;
using Ashlar.BackgroundAgents.HostRunners;
using Ashlar.CLI.Runtime;
using Ashlar.Infrastructure.HostProcess;
using Ashlar.Infrastructure.Preflight;

namespace Ashlar.CLI.Commands;

/// <summary>
/// Runs a single self-extend cycle with an explicit objective.
/// Reuses the existing repo.fs toolbox and policy guardrails.
/// </summary>
public sealed class SelfExtendCommand : Command
{
    private readonly Func<SelfExtendRunnerAdapter> _runnerFactory;

    /// <summary>Creates a new SelfExtendCommand instance.</summary>
    public SelfExtendCommand(Func<SelfExtendRunnerAdapter> runnerFactory)
        : base("self-extend", "Run one self-extend cycle with file-write tools under policy.")
    {
        _runnerFactory = runnerFactory ?? throw new ArgumentNullException(nameof(runnerFactory));

        var runCmd = new Command("run", "Run one objective-driven self-extend cycle.");
        var goalOpt = new Option<string>("--goal", "Objective for the self-extend cycle.") { IsRequired = true };
        var repoRootOpt = new Option<string>("--repo-root", () => Environment.CurrentDirectory, "Repository root path.");
        var providerOpt = new Option<string?>("--provider", () => "mock-json", "Model provider override (default: mock-json).");
        var allowMockOpt = new Option<bool>("--allow-mock", () => true, "Enable mock/offline providers for this run.");
        var jsonOpt = new Option<bool>("--json", () => false, "Emit JSON output.");
        var runTestsOpt = new Option<bool>("--run-tests", () => false, "After scaffolding, run the internal test suite filter for generated extensions.");
        var testFilterOpt = new Option<string>("--test-filter", () => "SelfExtendGenerated", "Filter passed to `ashlar test local` when --run-tests is enabled.");
        var runtimeSpecOpt = new Option<string?>("--runtime-spec", () => null, "Path to self-extend workflow runtime spec JSON.");
        var runtimeSpecJsonOpt = new Option<string?>("--runtime-spec-json", () => null, "Inline self-extend workflow runtime spec JSON.");
        var focusOpt = new Option<string?>("--focus", () => null, "Workflow focus override: balanced | functional | aesthetic.");
        var maxIterationsOpt = new Option<int?>("--max-iterations", () => null, "Workflow iteration cap override.");
        var preflightOpt = new Option<bool>("--preflight", () => true, "Run infra preflight before iterative self-extend execution.");

        runCmd.AddOption(goalOpt);
        runCmd.AddOption(repoRootOpt);
        runCmd.AddOption(providerOpt);
        runCmd.AddOption(allowMockOpt);
        runCmd.AddOption(jsonOpt);
        runCmd.AddOption(runTestsOpt);
        runCmd.AddOption(testFilterOpt);
        runCmd.AddOption(runtimeSpecOpt);
        runCmd.AddOption(runtimeSpecJsonOpt);
        runCmd.AddOption(focusOpt);
        runCmd.AddOption(maxIterationsOpt);
        runCmd.AddOption(preflightOpt);
        runCmd.SetHandler(async (InvocationContext ctx) =>
        {
            var goal = ctx.ParseResult.GetValueForOption(goalOpt) ?? string.Empty;
            var repoRoot = ctx.ParseResult.GetValueForOption(repoRootOpt) ?? Environment.CurrentDirectory;
            var provider = ctx.ParseResult.GetValueForOption(providerOpt);
            var allowMock = ctx.ParseResult.GetValueForOption(allowMockOpt);
            var json = ctx.ParseResult.GetValueForOption(jsonOpt);
            var runTests = ctx.ParseResult.GetValueForOption(runTestsOpt);
            var testFilter = ctx.ParseResult.GetValueForOption(testFilterOpt) ?? "SelfExtendGenerated";
            var runtimeSpecPath = ctx.ParseResult.GetValueForOption(runtimeSpecOpt);
            var runtimeSpecJson = ctx.ParseResult.GetValueForOption(runtimeSpecJsonOpt);
            var focus = ctx.ParseResult.GetValueForOption(focusOpt);
            var maxIterations = ctx.ParseResult.GetValueForOption(maxIterationsOpt);
            var runPreflight = ctx.ParseResult.GetValueForOption(preflightOpt);
            ctx.ExitCode = await ExecuteAsync(
                goal,
                repoRoot,
                provider,
                allowMock,
                json,
                runTests,
                testFilter,
                runtimeSpecPath,
                runtimeSpecJson,
                focus,
                maxIterations,
                runPreflight,
                ctx.GetCancellationToken()).ConfigureAwait(false);
        });
        AddCommand(runCmd);

        var preflightCmd = new Command("preflight", "Validate self-extend runtime readiness for functional/aesthetic/visual QA gates.");
        preflightCmd.AddOption(repoRootOpt);
        preflightCmd.AddOption(providerOpt);
        preflightCmd.AddOption(allowMockOpt);
        preflightCmd.AddOption(runTestsOpt);
        preflightCmd.AddOption(testFilterOpt);
        preflightCmd.AddOption(runtimeSpecOpt);
        preflightCmd.AddOption(runtimeSpecJsonOpt);
        preflightCmd.AddOption(focusOpt);
        preflightCmd.AddOption(maxIterationsOpt);
        preflightCmd.AddOption(jsonOpt);
        preflightCmd.SetHandler(async (InvocationContext ctx) =>
        {
            var repoRoot = ctx.ParseResult.GetValueForOption(repoRootOpt) ?? Environment.CurrentDirectory;
            var provider = ctx.ParseResult.GetValueForOption(providerOpt);
            var allowMock = ctx.ParseResult.GetValueForOption(allowMockOpt);
            var runTests = ctx.ParseResult.GetValueForOption(runTestsOpt);
            var testFilter = ctx.ParseResult.GetValueForOption(testFilterOpt) ?? "SelfExtendGenerated";
            var runtimeSpecPath = ctx.ParseResult.GetValueForOption(runtimeSpecOpt);
            var runtimeSpecJson = ctx.ParseResult.GetValueForOption(runtimeSpecJsonOpt);
            var focus = ctx.ParseResult.GetValueForOption(focusOpt);
            var maxIterations = ctx.ParseResult.GetValueForOption(maxIterationsOpt);
            var json = ctx.ParseResult.GetValueForOption(jsonOpt);
            ctx.ExitCode = await ExecutePreflightAsync(
                repoRoot,
                provider,
                allowMock,
                runTests,
                testFilter,
                runtimeSpecPath,
                runtimeSpecJson,
                focus,
                maxIterations,
                json,
                ctx.GetCancellationToken()).ConfigureAwait(false);
        });
        AddCommand(preflightCmd);
    }

    internal async Task<int> ExecuteAsync(
        string goal,
        string repoRoot,
        string? provider,
        bool allowMock,
        bool json,
        bool runTests,
        string testFilter,
        string? runtimeSpecPath,
        string? runtimeSpecJson,
        string? focusOverride,
        int? maxIterationsOverride,
        bool runPreflight,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(goal))
        {
            WriteResult(false, "Goal is required.", repoRoot, provider, executed: 0, denied: 0, json, testsRun: false, testsPassed: null, testFilter: null, testSummary: null, focus: "balanced", maxIterations: 0, iterations: Array.Empty<object>());
            return 1;
        }

        var fullRepoRoot = Path.GetFullPath(string.IsNullOrWhiteSpace(repoRoot) ? Environment.CurrentDirectory : repoRoot);
        if (!Directory.Exists(fullRepoRoot))
        {
            WriteResult(false, $"Repo root not found: {fullRepoRoot}", fullRepoRoot, provider, executed: 0, denied: 0, json, testsRun: false, testsPassed: null, testFilter: null, testSummary: null, focus: "balanced", maxIterations: 0, iterations: Array.Empty<object>());
            return 1;
        }

        var previousAllowMock = Environment.GetEnvironmentVariable("ASHLAR_ALLOW_MOCK");
        var previousProvider = Environment.GetEnvironmentVariable("ASHLAR_MODEL_PROVIDER");
        try
        {
            // Authoritative, both directions: --allow-mock false sets "0" so the model wrappers fail
            // hard on an unreachable/unconfigured provider instead of silently echoing success.
            Environment.SetEnvironmentVariable("ASHLAR_ALLOW_MOCK", allowMock ? "1" : "0");
            if (!string.IsNullOrWhiteSpace(provider))
                Environment.SetEnvironmentVariable("ASHLAR_MODEL_PROVIDER", provider.Trim());

            var runtimeSpec = SelfExtendWorkflowRuntimeSpecLoader.Load(runtimeSpecPath, runtimeSpecJson);
            var workflow = ResolveWorkflow(runtimeSpec.Workflow, focusOverride, maxIterationsOverride);
            var focus = NormalizeFocus(workflow.Focus);
            var phaseList = ResolvePhases(workflow, runTests);
            var maxIterations = Math.Max(1, workflow.MaxIterations);
            if (!runTests && !maxIterationsOverride.HasValue)
                maxIterations = 1;

            if (runPreflight && workflow.RequirePreflight)
            {
                var preflight = await RunPreflightChecksAsync(
                    fullRepoRoot,
                    provider,
                    allowMock,
                    runTests,
                    workflow,
                    testFilter,
                    focus,
                    ct).ConfigureAwait(false);
                if (!preflight.Passed)
                {
                    WriteResult(
                        false,
                        $"Preflight failed: {preflight.Summary}",
                        fullRepoRoot,
                        provider,
                        executed: 0,
                        denied: 0,
                        json,
                        testsRun: false,
                        testsPassed: null,
                        testFilter: runTests ? testFilter : null,
                        testSummary: preflight.Summary,
                        focus,
                        maxIterations,
                        iterations: new object[]
                        {
                            new
                            {
                                phase = "preflight",
                                ok = false,
                                checks = preflight.Checks
                            }
                        });
                    return 1;
                }
            }

            var runner = _runnerFactory();
            var testsRun = false;
            bool? testsPassed = null;
            string? testSummary = null;
            var executed = 0;
            var denied = 0;
            var succeeded = true;
            var summary = string.Empty;
            var iterationReports = new List<object>();
            string? carryForwardFeedback = null;

            for (var iteration = 1; iteration <= maxIterations; iteration++)
            {
                var phaseResults = new List<object>();
                foreach (var phase in phaseList)
                {
                    var phaseObjective = BuildPhaseObjective(goal, phase, focus, iteration, maxIterations, carryForwardFeedback);
                    var agentName = workflow.EnableMultiAgentPhases ? $"self-extend-{phase}" : "self-extend";
                    var phaseResult = await runner.RunAsync(fullRepoRoot, phaseObjective, agentName, ct).ConfigureAwait(false);
                    executed += phaseResult.ToolCallsExecuted;
                    denied += phaseResult.ToolCallsDenied;
                    phaseResults.Add(new
                    {
                        phase,
                        ok = phaseResult.Success,
                        executed = phaseResult.ToolCallsExecuted,
                        denied = phaseResult.ToolCallsDenied,
                        summary = phaseResult.Summary
                    });
                    if (!phaseResult.Success)
                    {
                        succeeded = false;
                        summary = $"Phase '{phase}' failed on iteration {iteration}: {phaseResult.Summary}";
                        break;
                    }
                }

                if (!succeeded)
                {
                    iterationReports.Add(new
                    {
                        iteration,
                        ok = false,
                        phases = phaseResults
                    });
                    break;
                }

                var qa = await RunQaGatesAsync(
                    fullRepoRoot,
                    runTests,
                    workflow,
                    testFilter,
                    focus,
                    allowMock,
                    ct).ConfigureAwait(false);
                testsRun = testsRun || qa.Ran;
                testsPassed = qa.Passed;
                testSummary = qa.Summary;
                carryForwardFeedback = qa.Passed
                    ? null
                    : $"QA failed: {qa.Summary}. Address failures and improve generated output.";

                iterationReports.Add(new
                {
                    iteration,
                    ok = qa.Passed,
                    phases = phaseResults,
                    qa = new
                    {
                        ran = qa.Ran,
                        passed = qa.Passed,
                        summary = qa.Summary
                    }
                });

                if (qa.Passed && workflow.StopOnFirstPass)
                {
                    summary = $"Iteration {iteration} passed QA gates (focus={focus}).";
                    break;
                }

                if (iteration == maxIterations)
                {
                    succeeded = qa.Passed;
                    summary = qa.Passed
                        ? $"Iteration {iteration} passed QA gates."
                        : $"Reached max iterations ({maxIterations}) with QA failures.";
                }
            }

            WriteResult(
                succeeded && (testsRun ? testsPassed != false : true),
                string.IsNullOrWhiteSpace(summary)
                    ? $"Completed {maxIterations} iteration(s) with focus={focus}."
                    : summary,
                fullRepoRoot,
                provider,
                executed,
                denied,
                json,
                testsRun,
                testsPassed,
                runTests ? testFilter : null,
                testSummary,
                focus,
                maxIterations,
                iterationReports);
            if (!succeeded)
                return 1;
            if (testsRun && testsPassed == false)
                return 1;
            return 0;
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASHLAR_ALLOW_MOCK", previousAllowMock);
            Environment.SetEnvironmentVariable("ASHLAR_MODEL_PROVIDER", previousProvider);
        }
    }

    internal async Task<int> ExecutePreflightAsync(
        string repoRoot,
        string? provider,
        bool allowMock,
        bool runTests,
        string testFilter,
        string? runtimeSpecPath,
        string? runtimeSpecJson,
        string? focusOverride,
        int? maxIterationsOverride,
        bool json,
        CancellationToken ct)
    {
        var fullRepoRoot = Path.GetFullPath(string.IsNullOrWhiteSpace(repoRoot) ? Environment.CurrentDirectory : repoRoot);
        if (!Directory.Exists(fullRepoRoot))
        {
            WritePreflightResult(new PreflightResult(false, $"Repo root not found: {fullRepoRoot}", new[]
            {
                new PreflightCheck("repo-root", false, true, $"Directory does not exist: {fullRepoRoot}")
            }), json);
            return 1;
        }

        var previousAllowMock = Environment.GetEnvironmentVariable("ASHLAR_ALLOW_MOCK");
        var previousProvider = Environment.GetEnvironmentVariable("ASHLAR_MODEL_PROVIDER");
        try
        {
            // Authoritative, both directions: --allow-mock false sets "0" so the model wrappers fail
            // hard on an unreachable/unconfigured provider instead of silently echoing success.
            Environment.SetEnvironmentVariable("ASHLAR_ALLOW_MOCK", allowMock ? "1" : "0");
            if (!string.IsNullOrWhiteSpace(provider))
                Environment.SetEnvironmentVariable("ASHLAR_MODEL_PROVIDER", provider.Trim());

            var runtimeSpec = SelfExtendWorkflowRuntimeSpecLoader.Load(runtimeSpecPath, runtimeSpecJson);
            var workflow = ResolveWorkflow(runtimeSpec.Workflow, focusOverride, maxIterationsOverride);
            var focus = NormalizeFocus(workflow.Focus);

            var preflight = await RunPreflightChecksAsync(
                fullRepoRoot,
                provider,
                allowMock,
                runTests,
                workflow,
                testFilter,
                focus,
                ct).ConfigureAwait(false);

            WritePreflightResult(preflight, json);
            return preflight.Passed ? 0 : 1;
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASHLAR_ALLOW_MOCK", previousAllowMock);
            Environment.SetEnvironmentVariable("ASHLAR_MODEL_PROVIDER", previousProvider);
        }
    }

    private static SelfExtendWorkflowSpec ResolveWorkflow(
        SelfExtendWorkflowSpec source,
        string? focusOverride,
        int? maxIterationsOverride)
    {
        var iterations = maxIterationsOverride ?? source.MaxIterations;
        if (iterations <= 0)
            iterations = 1;

        var focus = string.IsNullOrWhiteSpace(focusOverride)
            ? source.Focus
            : focusOverride.Trim();

        return source with
        {
            MaxIterations = iterations,
            Focus = NormalizeFocus(focus),
            VisualQaFallbackPolicy = NormalizeVisualQaFallbackPolicy(source.VisualQaFallbackPolicy)
        };
    }

    private static string NormalizeFocus(string? focus)
    {
        var normalized = (focus ?? "balanced").Trim().ToLowerInvariant();
        return normalized switch
        {
            "functional" => "functional",
            "aesthetic" => "aesthetic",
            _ => "balanced"
        };
    }

    private static string NormalizeVisualQaFallbackPolicy(string? policy)
    {
        var normalized = (policy ?? "strict").Trim().ToLowerInvariant();
        return normalized switch
        {
            "degrade" => "degrade",
            _ => "strict"
        };
    }

    private static string[] ResolvePhases(SelfExtendWorkflowSpec workflow, bool runTests)
    {
        if (!workflow.EnableMultiAgentPhases)
            return ["builder"];

        var phases = workflow.AgentPhases
            .Select(p => (p ?? string.Empty).Trim().ToLowerInvariant())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Where(p => runTests || !p.StartsWith("qa-", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return phases.Length == 0 ? ["builder"] : phases;
    }

    private static string BuildPhaseObjective(
        string goal,
        string phase,
        string focus,
        int iteration,
        int maxIterations,
        string? carryForwardFeedback)
    {
        var phaseDirective = phase switch
        {
            "planner" => "Plan an incremental change set with explicit extension boundaries and QA intent.",
            "builder" => "Implement or refine extension files incrementally and preserve prior working behaviors.",
            "qa-functional" => "Prioritize functional correctness and testability of generated extensions.",
            "qa-aesthetic" => "Prioritize UX clarity, visual coherence, and UI interaction quality where applicable.",
            _ => $"Execute phase '{phase}' with disciplined, incremental improvements."
        };

        var feedback = string.IsNullOrWhiteSpace(carryForwardFeedback)
            ? string.Empty
            : $"\nPrevious iteration feedback:\n{carryForwardFeedback.Trim()}\n";

        return
            $"Iteration {iteration}/{maxIterations}.\n" +
            $"Workflow focus: {focus}.\n" +
            $"Role: {phase}.\n" +
            $"{phaseDirective}\n\n" +
            $"Primary objective:\n{goal.Trim()}\n" +
            feedback +
            "\nApply minimal, high-signal changes and keep outputs composable.";
    }

    private static async Task<PreflightResult> RunPreflightChecksAsync(
        string repoRoot,
        string? provider,
        bool allowMock,
        bool runTests,
        SelfExtendWorkflowSpec workflow,
        string functionalFilterOverride,
        string focus,
        CancellationToken ct)
    {
        var checks = new List<PreflightCheck>();

        checks.Add(new PreflightCheck("repo-root", true, true, $"Using repo root: {repoRoot}"));

        var providerCheck = ValidateProviderMode(provider, allowMock);
        checks.Add(providerCheck);

        if (!runTests)
        {
            return BuildPreflightResult(checks);
        }

        var runFunctional = workflow.RunFunctionalQa && !string.Equals(focus, "aesthetic", StringComparison.OrdinalIgnoreCase);
        var runAesthetic = workflow.RunAestheticQa && !string.Equals(focus, "functional", StringComparison.OrdinalIgnoreCase);

        if (runFunctional)
        {
            var functionalFilter = string.IsNullOrWhiteSpace(functionalFilterOverride)
                ? workflow.FunctionalTestFilter
                : functionalFilterOverride;
            checks.Add(await CheckTestFilterDiscoverabilityAsync(repoRoot, functionalFilter, "functional-filter", required: true, allowMock, ct).ConfigureAwait(false));
        }

        if (runAesthetic)
        {
            var aestheticFilter = string.IsNullOrWhiteSpace(workflow.AestheticTestFilter)
                ? "UiDomainKnowledgeRetentionTests"
                : workflow.AestheticTestFilter;
            checks.Add(await CheckTestFilterDiscoverabilityAsync(repoRoot, aestheticFilter, "aesthetic-filter", required: true, allowMock, ct).ConfigureAwait(false));

            var smokeCheck = CheckUiSmokeProject(workflow.UiSmokeProjectPath, repoRoot, allowMock);
            checks.Add(smokeCheck);
        }

        if (runAesthetic && workflow.RunVisualQa)
        {
            var visualInfra = await AssessVisualQaReadinessAsync(repoRoot, ct).ConfigureAwait(false);
            var strictVisual = !string.Equals(workflow.VisualQaFallbackPolicy, "degrade", StringComparison.OrdinalIgnoreCase);
            var infraOk = visualInfra.Ready || !strictVisual || allowMock;
            var message = visualInfra.Ready
                ? visualInfra.Summary
                : allowMock && strictVisual
                    ? $"{visualInfra.Summary} (allow-mock: visual infra treated as satisfied)"
                    : strictVisual
                        ? visualInfra.Summary
                        : $"{visualInfra.Summary} (fallback policy=degrade)";
            checks.Add(new PreflightCheck(
                "visual-infra",
                infraOk,
                strictVisual,
                message,
                visualInfra.Dependencies));
        }

        return BuildPreflightResult(checks);
    }

    private static PreflightResult BuildPreflightResult(IReadOnlyList<PreflightCheck> checks)
    {
        var blocking = checks.Where(c => c.Required && !c.Ok).ToList();
        var summary = blocking.Count == 0
            ? "Preflight passed."
            : $"Preflight failed: {string.Join(" | ", blocking.Select(b => $"{b.Id}: {b.Message}"))}";
        return new PreflightResult(blocking.Count == 0, summary, checks.ToArray());
    }

    private static PreflightCheck ValidateProviderMode(string? provider, bool allowMock)
    {
        if (string.IsNullOrWhiteSpace(provider))
            return new PreflightCheck("provider", true, true, "Provider uses default resolution.");

        var p = provider.Trim().ToLowerInvariant();
        if (p is "mock" or "offline" or "mock-json" or "echo")
        {
            if (!allowMock)
                return new PreflightCheck("provider", false, true, $"Provider '{p}' requires --allow-mock.");
            return new PreflightCheck("provider", true, true, $"Provider '{p}' allowed via --allow-mock.");
        }

        return new PreflightCheck("provider", true, true, $"Provider '{p}' selected.");
    }

    private static async Task<PreflightCheck> CheckTestFilterDiscoverabilityAsync(
        string repoRoot,
        string filter,
        string id,
        bool required,
        bool allowMock,
        CancellationToken ct)
    {
        var run = await RunProcessAsync(
            "dotnet",
            repoRoot,
            new[]
            {
                "run",
                "--project",
                "application/src/Ashlar.CLI",
                "--",
                "test",
                "local",
                "--filter",
                filter,
                "--format-json"
            },
            ct).ConfigureAwait(false);

        var totalTests = TryReadTotalTests(run.StdOut);
        if (run.ExitCode != 0)
            return new PreflightCheck(id, false, required, $"Test discovery command failed for filter '{filter}' (exit={run.ExitCode}).");
        if (totalTests <= 0)
        {
            if (allowMock)
                return new PreflightCheck(id, true, required, $"No tests discovered for filter '{filter}' (allow-mock: skipping strict discoverability).");
            return new PreflightCheck(id, false, required, $"No tests discovered for filter '{filter}'.");
        }
        return new PreflightCheck(id, true, required, $"Discovered {totalTests} test(s) for filter '{filter}'.");
    }

    private static PreflightCheck CheckUiSmokeProject(string? projectPath, string repoRoot, bool allowMock)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
            return new PreflightCheck("ui-smoke-project", false, true, "UI smoke project path is not configured.");

        var normalized = projectPath.Trim().Replace('\\', '/');
        var fullPath = Path.GetFullPath(Path.Combine(repoRoot, normalized));
        if (!File.Exists(fullPath))
        {
            if (allowMock)
                return new PreflightCheck("ui-smoke-project", true, true, $"UI smoke project not found: {normalized} (allow-mock: skipping strict check).");
            return new PreflightCheck("ui-smoke-project", false, true, $"UI smoke project not found: {normalized}");
        }
        return new PreflightCheck("ui-smoke-project", true, true, $"UI smoke project found: {normalized}");
    }

    private static async Task<VisualInfraReadiness> AssessVisualQaReadinessAsync(string repoRoot, CancellationToken ct)
    {
        var report = await VisualDependencyPreflight.RunAsync(repoRoot, runVisualQa: true, required: true, ct).ConfigureAwait(false);
        return new VisualInfraReadiness(report.RequiredAvailable, VisualDependencyPreflight.Summarize(report), report);
    }

    private static int TryReadTotalTests(string stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout))
            return 0;
        var match = System.Text.RegularExpressions.Regex.Match(stdout, "\"TotalTests\"\\s*:\\s*(\\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!match.Success)
            return 0;
        return int.TryParse(match.Groups[1].Value, out var total) ? total : 0;
    }

    private static async Task<QaGateResult> RunQaGatesAsync(
        string repoRoot,
        bool runTests,
        SelfExtendWorkflowSpec workflow,
        string functionalFilterOverride,
        string focus,
        bool allowMock,
        CancellationToken ct)
    {
        if (!runTests)
            return new QaGateResult(false, true, "QA gates skipped (--run-tests disabled).");

        var notes = new List<string>();
        var ran = false;
        var passed = true;

        var runFunctional = workflow.RunFunctionalQa && !string.Equals(focus, "aesthetic", StringComparison.OrdinalIgnoreCase);
        var runAesthetic = workflow.RunAestheticQa && !string.Equals(focus, "functional", StringComparison.OrdinalIgnoreCase);

        if (runFunctional)
        {
            ran = true;
            var functionalFilter = string.IsNullOrWhiteSpace(functionalFilterOverride)
                ? workflow.FunctionalTestFilter
                : functionalFilterOverride;
            var functional = await RunGeneratedTestSuiteAsync(repoRoot, functionalFilter, allowMock, ct).ConfigureAwait(false);
            var functionalPass = functional.ExitCode == 0;
            passed &= functionalPass;
            notes.Add(functionalPass
                ? $"Functional QA passed (filter={functionalFilter})."
                : $"Functional QA failed (filter={functionalFilter}, exit={functional.ExitCode}).");
        }

        if (runAesthetic)
        {
            var uiRoot = Path.Combine(repoRoot, "docs", "UiDomainDemoGenerated");
            if (!Directory.Exists(uiRoot))
            {
                notes.Add("Aesthetic QA skipped (UI artifacts not found).");
            }
            else
            {
                ran = true;
                var aestheticFilter = string.IsNullOrWhiteSpace(workflow.AestheticTestFilter)
                    ? "UiDomainKnowledgeRetentionTests"
                    : workflow.AestheticTestFilter;
                var aesthetic = await RunGeneratedTestSuiteAsync(repoRoot, aestheticFilter, allowMock, ct).ConfigureAwait(false);
                var aestheticPass = aesthetic.ExitCode == 0;
                passed &= aestheticPass;
                notes.Add(aestheticPass
                    ? $"Aesthetic QA tests passed (filter={aestheticFilter})."
                    : $"Aesthetic QA tests failed (filter={aestheticFilter}, exit={aesthetic.ExitCode}).");

                var smoke = await RunUiSmokeAsync(repoRoot, workflow.UiSmokeProjectPath, ct).ConfigureAwait(false);
                if (smoke.Ran)
                {
                    passed &= smoke.Passed;
                    notes.Add(smoke.Summary);
                }

                if (workflow.RunVisualQa)
                {
                    var visualReady = await AssessVisualQaReadinessAsync(repoRoot, ct).ConfigureAwait(false);
                    var degrade = string.Equals(workflow.VisualQaFallbackPolicy, "degrade", StringComparison.OrdinalIgnoreCase);
                    if (!visualReady.Ready)
                    {
                        if (degrade || allowMock)
                        {
                            notes.Add(allowMock && !degrade
                                ? $"Visual QA skipped: {visualReady.Summary} (allow-mock bypass)."
                                : $"Visual QA skipped: {visualReady.Summary} (fallback=degrade).");
                        }
                        else
                        {
                            passed = false;
                            notes.Add($"Visual QA required but infra not ready: {visualReady.Summary}");
                        }
                    }
                    else
                    {
                        var visual = await RunVisualQaAsync(repoRoot, aestheticFilter, ct).ConfigureAwait(false);
                        passed &= visual.Passed;
                        notes.Add(visual.Summary);
                    }
                }
            }
        }

        if (!ran)
            return new QaGateResult(false, true, "QA gates skipped by workflow focus/spec.");

        return new QaGateResult(true, passed, string.Join(" ", notes));
    }

    private static async Task<QaCheckResult> RunUiSmokeAsync(string repoRoot, string? smokeProjectPath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(smokeProjectPath))
            return new QaCheckResult(false, true, "UI smoke skipped (project path not configured).");

        var normalized = smokeProjectPath.Trim().Replace('\\', '/');
        var fullPath = Path.GetFullPath(Path.Combine(repoRoot, normalized));
        if (!File.Exists(fullPath))
            return new QaCheckResult(false, true, "UI smoke skipped (project file missing).");

        var run = await RunProcessAsync(
            "dotnet",
            repoRoot,
            new[] { "run", "--project", normalized, "--", "--smoke" },
            ct).ConfigureAwait(false);

        return run.ExitCode == 0
            ? new QaCheckResult(true, true, $"UI smoke passed ({normalized}).")
            : new QaCheckResult(true, false, $"UI smoke failed ({normalized}, exit={run.ExitCode}).");
    }

    private static async Task<QaCheckResult> RunVisualQaAsync(string repoRoot, string filter, CancellationToken ct)
    {
        var run = await RunProcessAsync(
            "dotnet",
            repoRoot,
            new[]
            {
                "run",
                "--project",
                "application/src/Ashlar.CLI",
                "--",
                "test",
                "--visual",
                "--filter",
                filter,
                "--format-json"
            },
            ct).ConfigureAwait(false);

        return run.ExitCode == 0
            ? new QaCheckResult(true, true, $"Visual QA passed (filter={filter}).")
            : new QaCheckResult(true, false, $"Visual QA failed (filter={filter}, exit={run.ExitCode}).");
    }

    private static void WriteResult(
        bool success,
        string summary,
        string repoRoot,
        string? provider,
        int executed,
        int denied,
        bool json,
        bool testsRun,
        bool? testsPassed,
        string? testFilter,
        string? testSummary,
        string focus,
        int maxIterations,
        IReadOnlyList<object> iterations)
    {
        if (json)
        {
            var payload = new
            {
                ok = success,
                repoRoot,
                provider = provider ?? "(default)",
                executed,
                denied,
                summary,
                workflow = new
                {
                    focus,
                    maxIterations,
                    iterations
                },
                tests = new
                {
                    run = testsRun,
                    passed = testsPassed,
                    filter = testFilter,
                    summary = testSummary
                }
            };
            Console.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
            return;
        }

        Console.WriteLine($"self-extend: {(success ? "ok" : "failed")}");
        Console.WriteLine($"repo-root: {repoRoot}");
        Console.WriteLine($"provider: {provider ?? "(default)"}");
        Console.WriteLine($"executed: {executed}, denied: {denied}");
        Console.WriteLine($"workflow-focus: {focus}");
        Console.WriteLine($"workflow-max-iterations: {maxIterations}");
        Console.WriteLine(summary);
        if (testsRun)
        {
            Console.WriteLine($"tests-filter: {testFilter}");
            Console.WriteLine(testSummary ?? $"tests-passed={testsPassed}");
        }
    }

    private static void WritePreflightResult(PreflightResult result, bool json)
    {
        if (json)
        {
            var payload = new
            {
                ok = result.Passed,
                summary = result.Summary,
                checks = result.Checks.Select(c => new
                {
                    id = c.Id,
                    ok = c.Ok,
                    required = c.Required,
                    message = c.Message,
                    dependencies = c.Dependencies
                }).ToArray()
            };
            Console.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
            return;
        }

        Console.WriteLine($"self-extend preflight: {(result.Passed ? "ok" : "failed")}");
        Console.WriteLine(result.Summary);
        foreach (var check in result.Checks)
        {
            var status = check.Ok ? "OK" : (check.Required ? "FAIL" : "WARN");
            Console.WriteLine($"  - [{status}] {check.Id}: {check.Message}");
        }
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunGeneratedTestSuiteAsync(
        string repoRoot,
        string testFilter,
        bool allowMock,
        CancellationToken ct)
    {
        var build = await RunProcessAsync(
            "dotnet",
            repoRoot,
            new[] { "build", "application/src/Ashlar.Tests.CLI/Ashlar.Tests.CLI.csproj", "-m:1" },
            ct).ConfigureAwait(false);
        if (build.ExitCode != 0)
            return build;

        var run = await RunProcessAsync(
            "dotnet",
            repoRoot,
            new[]
            {
                "run",
                "--project",
                "application/src/Ashlar.CLI",
                "--",
                "test",
                "local",
                "--filter",
                testFilter,
                "--format-json"
            },
            ct).ConfigureAwait(false);

        // Treat "0 tests discovered" as failure for generated-extension validation (strict unless mock/offline lane).
        if (run.ExitCode == 0 && run.StdOut.Contains("\"TotalTests\":0", StringComparison.Ordinal))
        {
            if (allowMock)
                return run;
            return (1, run.StdOut, run.StdErr + Environment.NewLine + $"No tests discovered for filter '{testFilter}'.");
        }

        return run;
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunProcessAsync(
        string fileName,
        string workingDirectory,
        IEnumerable<string> args,
        CancellationToken ct,
        TimeSpan? timeout = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        var result = await TimedProcess.RunAsync(psi, timeout ?? Timeout.InfiniteTimeSpan, ct)
            .ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(result.StdErr))
            Console.Error.WriteLine(result.StdErr.Trim());
        if (result.TimedOut)
            return (TimedProcess.TimeoutExitCode, result.StdOut, result.StdErr);
        return (result.ExitCode, result.StdOut, result.StdErr);
    }

    private sealed record QaGateResult(bool Ran, bool Passed, string Summary);

    private sealed record QaCheckResult(bool Ran, bool Passed, string Summary);

    private sealed record PreflightResult(bool Passed, string Summary, IReadOnlyList<PreflightCheck> Checks);

    private sealed record PreflightCheck(string Id, bool Ok, bool Required, string Message, DependencyPreflightReport? Dependencies = null);

    private sealed record VisualInfraReadiness(bool Ready, string Summary, DependencyPreflightReport Dependencies);
}
