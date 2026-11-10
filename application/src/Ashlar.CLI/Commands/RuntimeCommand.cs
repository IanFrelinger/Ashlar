using System.CommandLine;
using System.CommandLine.Invocation;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Ashlar.CLI.Runtime;

namespace Ashlar.CLI.Commands;

/// <summary>
/// Adaptive runtime control-plane command.
/// Provides planning, execution, and matrix evaluation with history-informed policy tuning.
/// </summary>
public sealed partial class RuntimeCommand : Command
{
    private readonly RecommendHandler _recommendHandler;
    private readonly RuntimeHistoryHandler _historyHandler;
    private readonly PlanHandler _planHandler;
    private readonly RuntimeGateHandler _gateHandler;
    private readonly EvaluateHandler _evaluateHandler;
    private readonly ReleaseGateHandler _releaseGateHandler;
    private readonly ExecuteHandler _executeHandler;

    /// <summary>Creates a new RuntimeCommand instance.</summary>
    public RuntimeCommand() : base("runtime", "Adaptive runtime control plane commands.")
    {
        _recommendHandler = new RecommendHandler();
        _historyHandler = new RuntimeHistoryHandler();
        _planHandler = new PlanHandler(BuildPlanContext);
        _gateHandler = new RuntimeGateHandler();
        _executeHandler = new ExecuteHandler(BuildPlanContext);
        _evaluateHandler = new EvaluateHandler(_executeHandler.ExecuteCoreAsync);
        _releaseGateHandler = new ReleaseGateHandler(ExecuteEvaluateAsync, ExecuteGateAsync);
        ConfigureExecuteCommand();
        ConfigurePlanCommand();
        ConfigureEvaluateCommand();
        ConfigureHistoryCommand();
        ConfigureRecommendCommand();
        ConfigureGateCommand();
        ConfigureReleaseGateCommand();
    }

    internal Task<int> ExecuteAsync(
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
        CancellationToken ct) => _executeHandler.ExecuteAsync(goal, repoRoot, provider, allowMock, runTests, testFilter, bootstrapProfile, qaPolicy, runtimeManifestPath, runtimeManifestJson, maxIterationsOverride, bootstrapApply, bootstrapYes, bootstrapDryRun, runPreflight, useHistory, historyWindow, persistHistory, benchmarkSet, allowVisualCapabilityDegrade, autoRemediate, maxRemediationAttempts, json, ct);

    internal Task<int> ExecutePlanAsync(
        string goal,
        string repoRoot,
        string testFilter,
        string bootstrapProfile,
        string qaPolicy,
        string? runtimeManifestPath,
        string? runtimeManifestJson,
        int? maxIterationsOverride,
        bool useHistory,
        int historyWindow,
        bool json) => _planHandler.ExecuteAsync(goal, repoRoot, testFilter, bootstrapProfile, qaPolicy, runtimeManifestPath, runtimeManifestJson, maxIterationsOverride, useHistory, historyWindow, json);

    internal Task<int> ExecuteHistoryAsync(
        string repoRoot,
        int limit,
        string? goal,
        string? policy,
        string? benchmarkSet,
        bool json) => _historyHandler.ExecuteAsync(repoRoot, limit, goal, policy, benchmarkSet, json);

    internal Task<int> ExecuteRecommendAsync(
        string goal,
        string repoRoot,
        int historyWindow,
        bool json) => _recommendHandler.ExecuteAsync(goal, repoRoot, historyWindow, json);

    internal Task<int> ExecuteGateAsync(
        string repoRoot,
        int historyWindow,
        double minPassRate,
        int minTotal,
        string? goal,
        string? policy,
        string? benchmarkSet,
        string? stage,
        int minConsecutivePasses,
        bool json) => _gateHandler.ExecuteAsync(repoRoot, historyWindow, minPassRate, minTotal, goal, policy, benchmarkSet, stage, minConsecutivePasses, json);

    internal Task<int> ExecuteReleaseGateAsync(
        string mode,
        string repoRoot,
        string provider,
        bool allowMock,
        bool runTests,
        string testFilter,
        bool runPreflight,
        double coreMinPassRate,
        int coreMinTotal,
        int coreHistoryWindow,
        double visualMinPassRate,
        int visualMinTotal,
        int visualHistoryWindow,
        int visualPromotionStreak,
        string visualRequiredMode,
        int laneRepetitions,
        bool sloWarningOnly,
        bool emitSloEvidence,
        string? evidenceOutput,
        double ncrResolutionMsSlo,
        double ncrLoadMsSlo,
        double ncrOutcomeMsSlo,
        double ncrFailureRateSlo,
        bool json,
        CancellationToken ct) => _releaseGateHandler.ExecuteAsync(mode, repoRoot, provider, allowMock, runTests, testFilter, runPreflight, coreMinPassRate, coreMinTotal, coreHistoryWindow, visualMinPassRate, visualMinTotal, visualHistoryWindow, visualPromotionStreak, visualRequiredMode, laneRepetitions, sloWarningOnly, emitSloEvidence, evidenceOutput, ncrResolutionMsSlo, ncrLoadMsSlo, ncrOutcomeMsSlo, ncrFailureRateSlo, json, ct);

    internal Task<int> ExecuteEvaluateAsync(
        string? goalsJson,
        string? goalsFile,
        string policiesCsv,
        string repoRoot,
        string? provider,
        bool allowMock,
        bool runTests,
        string testFilter,
        string bootstrapProfile,
        string? runtimeManifestPath,
        string? runtimeManifestJson,
        int? maxIterationsOverride,
        bool bootstrapApply,
        bool runPreflight,
        bool useHistory,
        int historyWindow,
        bool persistHistory,
        string benchmarkSet,
        bool allowVisualCapabilityDegrade,
        bool json,
        CancellationToken ct) => _evaluateHandler.ExecuteAsync(goalsJson, goalsFile, policiesCsv, repoRoot, provider, allowMock, runTests, testFilter, bootstrapProfile, runtimeManifestPath, runtimeManifestJson, maxIterationsOverride, bootstrapApply, runPreflight, useHistory, historyWindow, persistHistory, benchmarkSet, allowVisualCapabilityDegrade, json, ct);

    private RuntimePlanContext BuildPlanContext(
        string goal,
        string repoRoot,
        string testFilter,
        string bootstrapProfile,
        string qaPolicy,
        string? runtimeManifestPath,
        string? runtimeManifestJson,
        int? maxIterationsOverride,
        bool useHistory,
        int historyWindow)
    {
        var manifest = AdaptiveRuntimeManifestLoader.Load(runtimeManifestPath, runtimeManifestJson);
        var requestedQaPolicy = RuntimeCommandUtilities.NormalizeQaPolicy(qaPolicy);
        string? adaptivePolicyReason = null;
        var effectiveQaPolicy = requestedQaPolicy;

        if (useHistory && string.Equals(requestedQaPolicy, "auto", StringComparison.OrdinalIgnoreCase))
        {
            var history = AdaptiveRuntimeExecutionHistoryStore.ReadRecent(repoRoot, Math.Max(1, historyWindow));
            var recommendation = AdaptiveRuntimePolicyAdvisor.RecommendQaPolicy(goal, history);
            if (recommendation != null)
            {
                effectiveQaPolicy = recommendation.QaPolicy;
                adaptivePolicyReason = recommendation.Reason;
            }
        }

        var plan = AdaptiveRuntimePlanResolver.Resolve(goal, manifest, bootstrapProfile, effectiveQaPolicy);
        if (maxIterationsOverride.HasValue && maxIterationsOverride.Value > 0)
            plan = plan with { MaxIterations = maxIterationsOverride.Value };
        if (!string.IsNullOrWhiteSpace(adaptivePolicyReason))
            plan = plan with { Reasons = plan.Reasons.Concat([$"adaptive-policy: {adaptivePolicyReason}"]).ToArray() };

        var workflowSpec = AdaptiveRuntimePlanResolver.BuildRuntimeSpec(plan, testFilter);
        return new RuntimePlanContext(goal, manifest, plan, workflowSpec, requestedQaPolicy, adaptivePolicyReason);
    }

    private static bool ResolveVisualRequired(
        string visualRequiredMode,
        string repoRoot,
        int historyWindow,
        int strictPromotionStreak) =>
        RuntimeGateEvaluation.ResolveVisualRequired(visualRequiredMode, repoRoot, historyWindow, strictPromotionStreak);

    private static string[] ResolveGoals(string? goalsJson, string? goalsFile)
    {
        if (!string.IsNullOrWhiteSpace(goalsJson))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<string[]>(goalsJson);
                return NormalizeGoals(parsed);
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        if (!string.IsNullOrWhiteSpace(goalsFile) && File.Exists(goalsFile))
        {
            var lines = File.ReadAllLines(goalsFile);
            return NormalizeGoals(lines);
        }

        return Array.Empty<string>();
    }

    private static string[] NormalizeGoals(IEnumerable<string>? goals)
    {
        return (goals ?? Array.Empty<string>())
            .Select(g => (g ?? string.Empty).Trim())
            .Where(g => !string.IsNullOrWhiteSpace(g))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string[] ResolvePolicies(string csv)
    {
        return (csv ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(RuntimeCommandUtilities.NormalizeQaPolicy)
            .Where(p => p is "demo" or "release" or "prod" or "research" or "auto")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

}
