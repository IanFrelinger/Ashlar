using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Ashlar.CLI.Runtime;
using Ashlar.Infrastructure.HostProcess;

namespace Ashlar.CLI.Commands.Runtime;
/// <summary>Handles execute requests.</summary>
internal sealed partial class ExecuteHandler(
    Func<string, string, string, string, string, string?, string?, int?, bool, int, RuntimePlanContext> buildPlanContext)
{
    /// <summary>Executes the command handler and returns a process exit code.</summary>
    public async Task<int> ExecuteAsync(
        string goal,
        string repoRoot,
        string? provider,
        bool allowMock,
        bool runTests,
        string testFilter,
        string bootstrapProfile,
        string qaPolicy,
        string? runtimeManifestPath,
        string? runtimeManifestJson,
        int? maxIterationsOverride,
        bool bootstrapApply,
        bool bootstrapYes,
        bool bootstrapDryRun,
        bool runPreflight,
        bool useHistory,
        int historyWindow,
        bool persistHistory,
        string benchmarkSet,
        bool allowVisualCapabilityDegrade,
        bool autoRemediate,
        int maxRemediationAttempts,
        bool json,
        CancellationToken ct)
    {
        var result = await ExecuteCoreAsync(
            goal,
            repoRoot,
            provider,
            allowMock,
            runTests,
            testFilter,
            bootstrapProfile,
            qaPolicy,
            runtimeManifestPath,
            runtimeManifestJson,
            maxIterationsOverride,
            bootstrapApply,
            bootstrapYes,
            bootstrapDryRun,
            runPreflight,
            useHistory,
            historyWindow,
            persistHistory,
            benchmarkSet,
            allowVisualCapabilityDegrade,
            ct).ConfigureAwait(false);

        var remediationAttempts = new List<RuntimeRemediationAttempt>();
        var attemptsBudget = Math.Max(0, maxRemediationAttempts);
        var remediationAllowed = autoRemediate &&
            !string.Equals(result.ResolvedQaPolicy, "release", StringComparison.OrdinalIgnoreCase);
        if (autoRemediate && !remediationAllowed && !result.Ok)
        {
            result = result with
            {
                Summary = $"{result.Summary} (auto-remediation disabled for release policy)"
            };
        }
        while (remediationAllowed && !result.Ok && attemptsBudget > 0)
        {
            var remediation = ChooseRemediationPolicy(result);
            if (remediation == null)
                break;
            if (remediationAttempts.Any(a => string.Equals(a.Policy, remediation.Policy, StringComparison.OrdinalIgnoreCase)))
                break;

            var retried = await ExecuteCoreAsync(
                goal,
                repoRoot,
                provider,
                allowMock,
                runTests,
                testFilter,
                bootstrapProfile,
                remediation.Policy,
                runtimeManifestPath,
                runtimeManifestJson,
                maxIterationsOverride,
                bootstrapApply,
                bootstrapYes,
                bootstrapDryRun,
                runPreflight,
                useHistory: false,
                historyWindow,
                persistHistory,
                benchmarkSet,
                allowVisualCapabilityDegrade,
                ct).ConfigureAwait(false);

            remediationAttempts.Add(new RuntimeRemediationAttempt(
                remediation.Policy,
                remediation.Reason,
                retried.Ok,
                retried.FailureStage,
                retried.RunId,
                retried.Summary));
            attemptsBudget--;
            result = retried;

            if (retried.Ok)
            {
                result = result with
                {
                    Summary = $"{result.Summary} (auto-remediated)",
                    RemediationAttempts = remediationAttempts.ToArray()
                };
                break;
            }
        }

        if (remediationAttempts.Count > 0 && result.RemediationAttempts == null)
            result = result with { RemediationAttempts = remediationAttempts.ToArray() };

        RuntimeOutputWriter.WriteResult(result, json);
        return result.Ok ? 0 : 1;
    }


    internal static async Task<RuntimePlanContext> ApplyVisualCapabilityFallbackAsync(
        RuntimePlanContext context,
        string repoRoot,
        string testFilter,
        bool runTests,
        bool runPreflight,
        bool allowVisualCapabilityDegrade,
        CancellationToken ct)
    {
        if (!runTests || !runPreflight || !allowVisualCapabilityDegrade ||
            !context.Plan.RunVisualQa || !context.Plan.RunAestheticQa ||
            string.Equals(context.Plan.Focus, "functional", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(context.Plan.VisualQaFallbackPolicy, "strict", StringComparison.OrdinalIgnoreCase))
        {
            return context;
        }

        var report = await VisualDependencyPreflight.RunAsync(repoRoot, runVisualQa: true, required: true, ct).ConfigureAwait(false);
        return ApplyVisualCapabilityFallback(context, testFilter, report);
    }

    // The operation, not the runner, owns the existing explicit capability-degrade behavior.
    internal static RuntimePlanContext ApplyVisualCapabilityFallback(
        RuntimePlanContext context, string testFilter, Ashlar.Infrastructure.Preflight.DependencyPreflightReport report)
    {
        if (report.RequiredAvailable)
            return context;

        var reason = $"runtime capability degrade applied: {VisualDependencyPreflight.Summarize(report)}";
        var adjustedPlan = context.Plan with
        {
            VisualQaFallbackPolicy = "degrade",
            Reasons = context.Plan.Reasons.Concat([reason]).ToArray()
        };
        var adjustedWorkflow = AdaptiveRuntimePlanResolver.BuildRuntimeSpec(adjustedPlan, testFilter);
        return context with
        {
            Plan = adjustedPlan,
            WorkflowSpec = adjustedWorkflow
        };
    }


    private static string[] BuildSelfExtendPreflightArgs(
        string repoRoot,
        string? provider,
        bool allowMock,
        bool runTests,
        string testFilter,
        string runtimeSpecJson)
    {
        var args = new List<string>
        {
            "self-extend",
            "preflight",
            "--repo-root", repoRoot,
            "--runtime-spec-json", runtimeSpecJson,
            "--json"
        };
        if (!string.IsNullOrWhiteSpace(provider))
        {
            args.Add("--provider");
            args.Add(provider.Trim());
        }
        if (allowMock)
            args.Add("--allow-mock");
        if (runTests)
        {
            args.Add("--run-tests");
            args.Add("--test-filter");
            args.Add(testFilter);
        }
        return args.ToArray();
    }


    private static string[] BuildSelfExtendRunArgs(
        string goal,
        string repoRoot,
        string? provider,
        bool allowMock,
        bool runTests,
        string testFilter,
        string runtimeSpecJson)
    {
        var args = new List<string>
        {
            "self-extend",
            "run",
            "--goal", goal,
            "--repo-root", repoRoot,
            "--runtime-spec-json", runtimeSpecJson,
            "--preflight", "false",
            "--json"
        };
        if (!string.IsNullOrWhiteSpace(provider))
        {
            args.Add("--provider");
            args.Add(provider.Trim());
        }
        if (allowMock)
            args.Add("--allow-mock");
        if (runTests)
        {
            args.Add("--run-tests");
            args.Add("--test-filter");
            args.Add(testFilter);
        }
        return args.ToArray();
    }


    private static async Task<RuntimeSubprocessResult> RunCliSubcommandAsync(
        string workingDirectory,
        IReadOnlyList<string> args,
        CancellationToken ct)
    {
        var (fileName, prefixArgs) = ResolveInvoker();
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var prefix in prefixArgs)
            psi.ArgumentList.Add(prefix);
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        var run = await TimedProcess.RunAsync(psi, TimedProcess.OperatorCommandTimeout, ct)
            .ConfigureAwait(false);
        return new RuntimeSubprocessResult(run.ExitCode, run.StdOut, run.StdErr);
    }


    private static (string FileName, string[] PrefixArgs) ResolveInvoker()
    {
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath) &&
            !string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return (processPath, Array.Empty<string>());
        }

        var entry = Assembly.GetEntryAssembly()?.Location;
        if (!string.IsNullOrWhiteSpace(entry) && File.Exists(entry))
            return ("dotnet", new[] { entry });

        return ("dotnet", new[] { "run", "--project", "application/src/Ashlar.CLI", "--" });
    }


    private static JsonElement? TryExtractLastJsonObject(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var idx = text.LastIndexOf('{');
        while (idx >= 0)
        {
            var candidate = text[idx..].Trim();
            try
            {
                using var doc = JsonDocument.Parse(candidate);
                return doc.RootElement.Clone();
            }
            catch
            {
                idx = idx > 0 ? text.LastIndexOf('{', idx - 1) : -1;
            }
        }

        return null;
    }


    private static bool IsPayloadOk(JsonElement? payload)
    {
        if (!payload.HasValue || payload.Value.ValueKind != JsonValueKind.Object)
            return false;
        if (!payload.Value.TryGetProperty("ok", out var okNode))
            return false;
        return okNode.ValueKind == JsonValueKind.True;
    }


    private static RuntimeRemediationPolicy? ChooseRemediationPolicy(RuntimeExecuteResult failed)
    {
        if (failed.Ok || failed.Plan == null)
            return null;

        var stage = (failed.FailureStage ?? string.Empty).Trim().ToLowerInvariant();
        if (stage == "preflight" &&
            failed.Plan.RunVisualQa &&
            string.Equals(failed.Plan.VisualQaFallbackPolicy, "strict", StringComparison.OrdinalIgnoreCase))
        {
            return new RuntimeRemediationPolicy(
                "demo",
                "Switch to demo policy to allow visual degrade fallback after strict visual preflight failure.");
        }

        if (stage == "self-extend" &&
            !string.Equals(failed.ResolvedQaPolicy, "research", StringComparison.OrdinalIgnoreCase))
        {
            return new RuntimeRemediationPolicy(
                "research",
                "Escalate to research policy after self-extend failure to increase retry depth.");
        }

        return null;
    }


}
