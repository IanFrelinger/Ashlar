using System.Text.Json;
using Ashlar.CLI.Runtime;

namespace Ashlar.CLI.Commands.Runtime;
/// <summary>Runtime output writer.</summary>
internal static class RuntimeOutputWriter
{
    internal static void WriteResult(RuntimeExecuteResult result, bool json)
    {
        if (json)
        {
            var payload = new
            {
                runId = result.RunId,
                startedAtUtc = result.StartedAtUtc,
                elapsedMs = result.ElapsedMs,
                failureStage = result.FailureStage,
                ok = result.Ok,
                summary = result.Summary,
                requestedQaPolicy = result.RequestedQaPolicy,
                resolvedQaPolicy = result.ResolvedQaPolicy,
                adaptivePolicyReason = result.AdaptivePolicyReason,
                plan = result.Plan == null
                    ? null
                    : new
                    {
                        bootstrapProfile = result.Plan.BootstrapProfile,
                        qaPolicy = result.Plan.QaPolicyProfile,
                        focus = result.Plan.Focus,
                        maxIterations = result.Plan.MaxIterations,
                        stopOnFirstPass = result.Plan.StopOnFirstPass,
                        runFunctionalQa = result.Plan.RunFunctionalQa,
                        runAestheticQa = result.Plan.RunAestheticQa,
                        runVisualQa = result.Plan.RunVisualQa,
                        visualFallback = result.Plan.VisualQaFallbackPolicy,
                        reasons = result.Plan.Reasons
                    },
                bootstrap = result.BootstrapAfter == null
                    ? null
                    : new
                    {
                        applied = result.BootstrapApplied,
                        applyExit = result.BootstrapApplyExit,
                        ok = result.BootstrapOk,
                        missingRequired = result.BootstrapAfter.MissingRequired.Select(m => m.Id).ToArray()
                    },
                gates = new
                {
                    preflightRan = result.PreflightRan,
                    preflightOk = result.PreflightRan == true ? result.PreflightOk : null,
                    selfExtendRan = result.SelfExtendRan,
                    selfExtendOk = result.SelfExtendOk
                },
                remediation = result.RemediationAttempts,
                preflight = result.PreflightPayload,
                execution = result.SelfExtendPayload
            };
            Console.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
            return;
        }

        Console.WriteLine($"runtime execute: {(result.Ok ? "ok" : "failed")}");
        Console.WriteLine(result.Summary);
        if (!string.IsNullOrWhiteSpace(result.FailureStage) && !string.Equals(result.FailureStage, "none", StringComparison.OrdinalIgnoreCase))
            Console.WriteLine($"  failure-stage={result.FailureStage}");
        if (result.Plan != null)
        {
            Console.WriteLine($"  profile={result.Plan.BootstrapProfile}, policy={result.Plan.QaPolicyProfile}, focus={result.Plan.Focus}");
            Console.WriteLine($"  visual-policy={result.Plan.VisualQaFallbackPolicy}, run-visual={result.Plan.RunVisualQa}");
        }
        if (result.BootstrapAfter != null)
            Console.WriteLine($"  bootstrap missing-required={result.BootstrapAfter.MissingRequired.Count()}");
        if (result.RemediationAttempts is { Length: > 0 })
        {
            foreach (var step in result.RemediationAttempts)
                Console.WriteLine($"  remediation: policy={step.Policy}, ok={step.Ok}, reason={step.Reason}");
        }
    }


    internal static void WritePlanResult(RuntimePlanResult result, bool json)
    {
        if (json)
        {
            var payload = new
            {
                ok = result.Ok,
                summary = result.Summary,
                adaptivePolicyReason = result.AdaptivePolicyReason,
                plan = result.Plan == null
                    ? null
                    : new
                    {
                        bootstrapProfile = result.Plan.BootstrapProfile,
                        qaPolicy = result.Plan.QaPolicyProfile,
                        focus = result.Plan.Focus,
                        maxIterations = result.Plan.MaxIterations,
                        stopOnFirstPass = result.Plan.StopOnFirstPass,
                        runFunctionalQa = result.Plan.RunFunctionalQa,
                        runAestheticQa = result.Plan.RunAestheticQa,
                        runVisualQa = result.Plan.RunVisualQa,
                        visualFallback = result.Plan.VisualQaFallbackPolicy,
                        reasons = result.Plan.Reasons
                    },
                workflow = result.WorkflowSpec?.Workflow
            };
            Console.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
            return;
        }

        Console.WriteLine($"runtime plan: {(result.Ok ? "ok" : "failed")}");
        Console.WriteLine(result.Summary);
        if (result.Plan != null)
        {
            Console.WriteLine($"  profile={result.Plan.BootstrapProfile}");
            Console.WriteLine($"  qa-policy={result.Plan.QaPolicyProfile}");
            Console.WriteLine($"  focus={result.Plan.Focus}");
            Console.WriteLine($"  visual-policy={result.Plan.VisualQaFallbackPolicy}, run-visual={result.Plan.RunVisualQa}");
        }
        if (!string.IsNullOrWhiteSpace(result.AdaptivePolicyReason))
            Console.WriteLine($"  adaptive-policy={result.AdaptivePolicyReason}");
    }


    internal static void WriteEvaluateResult(RuntimeEvaluateResult result, bool json)
    {
        if (json)
        {
            var payload = new
            {
                ok = result.Ok,
                summary = result.Summary,
                scenarios = result.Scenarios,
                policies = result.PolicySummaries
            };
            Console.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
            return;
        }

        Console.WriteLine($"runtime evaluate: {(result.Ok ? "ok" : "failed")}");
        Console.WriteLine(result.Summary);
        foreach (var policy in result.PolicySummaries ?? Array.Empty<RuntimeEvaluatePolicySummary>())
            Console.WriteLine($"  {policy.Policy}: {policy.Passed}/{policy.Total} passed, avg={policy.AverageElapsedMs}ms");
    }


    internal static void WriteHistoryResult(RuntimeHistoryResult result, bool json)
    {
        if (json)
        {
            var payload = new
            {
                ok = result.Ok,
                summary = result.Summary,
                summaryStats = result.SummaryStats,
                items = result.Items
            };
            Console.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
            return;
        }

        Console.WriteLine($"runtime history: {(result.Ok ? "ok" : "failed")}");
        Console.WriteLine(result.Summary);
        if (result.SummaryStats != null)
            Console.WriteLine($"  total={result.SummaryStats.Total}, passed={result.SummaryStats.Passed}, failed={result.SummaryStats.Failed}, avg={result.SummaryStats.AverageElapsedMs}ms");
        foreach (var item in result.Items ?? Array.Empty<AdaptiveRuntimeExecutionReport>())
            Console.WriteLine($"  [{item.StartedAtUtc:O}] ok={item.Success} policy={item.ResolvedQaPolicy} benchmark={item.BenchmarkSet} stage={item.FailureStage} goal=\"{item.GoalPreview}\"");
    }


    internal static void WriteRecommendResult(RuntimeRecommendResult result, bool json)
    {
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                ok = result.Ok,
                summary = result.Summary,
                policy = result.Policy,
                reason = result.Reason
            }, new JsonSerializerOptions { WriteIndented = true }));
            return;
        }

        Console.WriteLine($"runtime recommend: {(result.Ok ? "ok" : "failed")}");
        Console.WriteLine(result.Summary);
        if (!string.IsNullOrWhiteSpace(result.Policy))
            Console.WriteLine($"  policy={result.Policy}");
        if (!string.IsNullOrWhiteSpace(result.Reason))
            Console.WriteLine($"  reason={result.Reason}");
    }


    internal static void WriteGateResult(RuntimeGateResult result, bool json)
    {
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                ok = result.Ok,
                summary = result.Summary,
                total = result.Total,
                passed = result.Passed,
                passRate = result.PassRate,
                minTotal = result.MinTotal,
                minPassRate = result.MinPassRate,
                streak = result.Streak,
                minConsecutivePasses = result.MinConsecutivePasses,
                sloEvidence = result.SloEvidence
            }, new JsonSerializerOptions { WriteIndented = true }));
            return;
        }

        Console.WriteLine($"runtime gate: {(result.Ok ? "ok" : "failed")}");
        Console.WriteLine(result.Summary);
        if (result.Total.HasValue)
            Console.WriteLine($"  total={result.Total}, passed={result.Passed}, pass-rate={result.PassRate:P1}, min={result.MinPassRate:P1} over {result.MinTotal} run(s), streak={result.Streak}/{result.MinConsecutivePasses}");
    }


    internal static void WriteRuntimeSloEvidence(string repoRoot, string? evidenceOutput, RuntimeSloEvidence evidence)
    {
        var defaultJsonPath = Path.Combine(repoRoot, ".ashlar", "runtime", "release-gate", "last-run", "evidence.json");
        var jsonPath = string.IsNullOrWhiteSpace(evidenceOutput)
            ? defaultJsonPath
            : Path.GetFullPath(evidenceOutput.Trim());
        var mdPath = Path.ChangeExtension(jsonPath, ".md");
        var jsonDir = Path.GetDirectoryName(jsonPath);
        if (!string.IsNullOrWhiteSpace(jsonDir))
            Directory.CreateDirectory(jsonDir);

        var payload = new
        {
            generatedAtUtc = evidence.GeneratedAtUtc,
            mode = evidence.Mode,
            visualLaneRequired = evidence.VisualLaneRequired,
            passed = evidence.Passed,
            totalSamples = evidence.TotalSamples,
            metrics = new
            {
                ncrResolutionP95Ms = evidence.NcrResolutionP95Ms,
                ncrLoadP95Ms = evidence.NcrLoadP95Ms,
                ncrOutcomeP95Ms = evidence.NcrOutcomeP95Ms,
                ncrFailureRate = evidence.NcrFailureRate
            },
            thresholds = evidence.Thresholds,
            checks = evidence.Checks,
            lanes = evidence.Lanes
        };
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));

        var lines = new List<string>
        {
            "# Runtime Release Gate SLO Evidence",
            string.Empty,
            $"- Generated UTC: {evidence.GeneratedAtUtc:O}",
            $"- Mode: {evidence.Mode}",
            $"- Visual lane required: {(evidence.VisualLaneRequired ? "yes" : "no")}",
            $"- Overall SLO status: {(evidence.Passed ? "PASS" : "FAIL")}",
            string.Empty,
            "## NCR Metrics",
            string.Empty,
            $"- ncr.model_resolution.p95_ms: {evidence.NcrResolutionP95Ms:0.##} (max {evidence.Thresholds.NcrResolutionP95MsMax:0.##})",
            $"- ncr.model_load.p95_ms: {evidence.NcrLoadP95Ms:0.##} (max {evidence.Thresholds.NcrLoadP95MsMax:0.##})",
            $"- ncr.outcome.p95_ms: {evidence.NcrOutcomeP95Ms:0.##} (max {evidence.Thresholds.NcrOutcomeP95MsMax:0.##})",
            $"- ncr.failure_rate: {evidence.NcrFailureRate:0.####} (max {evidence.Thresholds.NcrFailureRateMax:0.####})",
            string.Empty,
            "## Gate Checks",
            string.Empty
        };
        lines.AddRange(evidence.Checks.Select(check =>
            $"- {(check.Passed ? "PASS" : "FAIL")} `{check.Name}` actual={check.Actual:0.####} threshold={check.Threshold:0.####} ({check.Detail})"));
        lines.Add(string.Empty);
        lines.Add("## Lane Summary");
        lines.Add(string.Empty);
        lines.AddRange(evidence.Lanes.Select(lane =>
            $"- `{lane.Lane}` ({lane.BenchmarkSet}) gate={(lane.GateOk.HasValue ? (lane.GateOk.Value ? "pass" : "fail") : "n/a")} passRate={(lane.PassRate.HasValue ? lane.PassRate.Value.ToString("0.####") : "n/a")} total={(lane.Total.HasValue ? lane.Total.Value.ToString() : "n/a")}"));
        File.WriteAllLines(mdPath, lines);
    }


}
