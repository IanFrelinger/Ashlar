using System.Diagnostics;
using System.Text.Json;
using Ashlar.CLI.Runtime;

namespace Ashlar.CLI.Commands.Runtime;
/// <summary>Handles execute requests.</summary>
internal sealed partial class ExecuteHandler
{
    internal async Task<RuntimeExecuteResult> ExecuteCoreAsync(
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
        CancellationToken ct)
    {
        var runId = Guid.NewGuid().ToString("N");
        var startedAt = DateTimeOffset.UtcNow;
        var timer = Stopwatch.StartNew();
        var goalFingerprint = AdaptiveRuntimeExecutionReport.ComputeGoalFingerprint(goal);
        var goalPreview = AdaptiveRuntimeExecutionReport.BuildGoalPreview(goal);

        RuntimeExecuteResult Finalize(RuntimeExecuteResult input)
        {
            var elapsed = timer.ElapsedMilliseconds;
            var output = input with
            {
                RunId = runId,
                StartedAtUtc = startedAt,
                ElapsedMs = elapsed,
                GoalFingerprint = goalFingerprint,
                GoalPreview = goalPreview
            };
            if (persistHistory)
            {
                try
                {
                    AdaptiveRuntimeExecutionHistoryStore.Append(
                        output.RepoRoot ?? repoRoot,
                        new AdaptiveRuntimeExecutionReport
                        {
                            RunId = output.RunId ?? runId,
                            StartedAtUtc = output.StartedAtUtc ?? startedAt,
                            ElapsedMs = output.ElapsedMs ?? elapsed,
                            GoalFingerprint = output.GoalFingerprint ?? goalFingerprint,
                            GoalPreview = output.GoalPreview ?? goalPreview,
                            BenchmarkSet = RuntimeCommandUtilities.NormalizeBenchmarkSet(benchmarkSet),
                            RequestedQaPolicy = output.RequestedQaPolicy ?? "auto",
                            ResolvedQaPolicy = output.ResolvedQaPolicy ?? "demo",
                            BootstrapProfile = output.Plan?.BootstrapProfile ?? "self-extend-functional",
                            RunVisualQa = output.Plan?.RunVisualQa ?? false,
                            VisualQaFallbackPolicy = output.Plan?.VisualQaFallbackPolicy ?? "degrade",
                            Success = output.Ok,
                            FailureStage = output.FailureStage ?? "none",
                            BootstrapOk = output.BootstrapOk ?? false,
                            PreflightRan = output.PreflightRan ?? false,
                            PreflightOk = output.PreflightOk ?? false,
                            SelfExtendRan = output.SelfExtendRan ?? false,
                            SelfExtendOk = output.SelfExtendOk ?? false,
                            PlanReasons = output.Plan?.Reasons ?? Array.Empty<string>(),
                            AdaptivePolicyReason = output.AdaptivePolicyReason
                        });
                }
                catch
                {
                    // Persistence must never block runtime execution flow.
                }
            }

            return output;
        }

        if (string.IsNullOrWhiteSpace(goal))
            return Finalize(new RuntimeExecuteResult(false, "Goal is required.", FailureStage: "input"));

        var fullRepoRoot = Path.GetFullPath(string.IsNullOrWhiteSpace(repoRoot) ? Environment.CurrentDirectory : repoRoot);
        if (!Directory.Exists(fullRepoRoot))
            return Finalize(new RuntimeExecuteResult(false, $"Repo root not found: {fullRepoRoot}", RepoRoot: fullRepoRoot, FailureStage: "input"));

        RuntimePlanContext context;
        try
        {
            context = buildPlanContext(
                goal,
                fullRepoRoot,
                testFilter,
                bootstrapProfile,
                qaPolicy,
                runtimeManifestPath,
                runtimeManifestJson,
                maxIterationsOverride,
                useHistory,
                historyWindow);
        }
        catch (Exception ex)
        {
            return Finalize(new RuntimeExecuteResult(
                false,
                $"Failed to resolve runtime plan: {ex.Message}",
                RepoRoot: fullRepoRoot,
                FailureStage: "plan"));
        }

        context = await ApplyVisualCapabilityFallbackAsync(
            context,
            fullRepoRoot,
            testFilter,
            runTests,
            runPreflight,
            allowVisualCapabilityDegrade,
            ct).ConfigureAwait(false);

        var enrichedGoal = AdaptiveRuntimePlanResolver.EnrichGoal(goal, context.Manifest, context.Plan);

        var bootstrapBefore = await BootstrapRuntime.AssessDemoAsync(
            context.Plan.BootstrapProfile,
            includeOptional: false,
            ct,
            relaxStrictVisualHostDeps: allowMock).ConfigureAwait(false);
        BootstrapAssessment bootstrapAfter = bootstrapBefore;
        int? bootstrapApplyExit = null;
        var bootstrapApplied = false;
        if (bootstrapBefore.Supported && bootstrapBefore.MissingRequired.Any() && bootstrapApply)
        {
            bootstrapApplied = true;
            bootstrapApplyExit = await BootstrapCommand.RunApplyAsync(
                context.Plan.BootstrapProfile,
                includeOptional: false,
                yes: bootstrapYes,
                dryRun: bootstrapDryRun,
                json: false,
                ct).ConfigureAwait(false);
            bootstrapAfter = await BootstrapRuntime.AssessDemoAsync(
                context.Plan.BootstrapProfile,
                includeOptional: false,
                ct,
                relaxStrictVisualHostDeps: allowMock).ConfigureAwait(false);
        }

        var bootstrapOk =
            bootstrapAfter.Supported &&
            !bootstrapAfter.MissingRequired.Any() &&
            (!bootstrapApplyExit.HasValue || bootstrapApplyExit.Value == 0);
        if (!bootstrapOk)
        {
            var summary = !bootstrapAfter.Supported
                ? bootstrapAfter.Reason ?? "Bootstrap profile unsupported."
                : bootstrapAfter.MissingRequired.Any()
                    ? $"Missing required bootstrap dependencies: {string.Join(", ", bootstrapAfter.MissingRequired.Select(d => d.Id))}"
                    : $"Bootstrap apply failed (exit={bootstrapApplyExit}).";

            return Finalize(new RuntimeExecuteResult(
                false,
                $"runtime execute failed during bootstrap: {summary}",
                context.Plan,
                bootstrapBefore,
                bootstrapAfter,
                bootstrapApplied,
                bootstrapApplyExit,
                RequestedQaPolicy: context.RequestedQaPolicy,
                ResolvedQaPolicy: context.Plan.QaPolicyProfile,
                AdaptivePolicyReason: context.AdaptivePolicyReason,
                RepoRoot: fullRepoRoot,
                FailureStage: "bootstrap",
                BootstrapOk: false,
                PreflightRan: false,
                PreflightOk: false,
                SelfExtendRan: false,
                SelfExtendOk: false));
        }

        JsonElement? preflightPayload = null;
        RuntimeSubprocessResult? preflightRun = null;
        var preflightOkResult = false;
        if (runPreflight)
        {
            var preflightArgs = BuildSelfExtendPreflightArgs(
                fullRepoRoot,
                provider,
                allowMock,
                runTests,
                testFilter,
                JsonSerializer.Serialize(context.WorkflowSpec));
            preflightRun = await RunCliSubcommandAsync(fullRepoRoot, preflightArgs, ct).ConfigureAwait(false);
            preflightPayload = TryExtractLastJsonObject(preflightRun.StdOut);
            preflightOkResult = preflightRun.ExitCode == 0 && IsPayloadOk(preflightPayload);
            if (!preflightOkResult)
            {
                return Finalize(new RuntimeExecuteResult(
                    false,
                    "runtime execute failed during preflight.",
                    context.Plan,
                    bootstrapBefore,
                    bootstrapAfter,
                    bootstrapApplied,
                    bootstrapApplyExit,
                    preflightPayload,
                    preflightRun,
                    RequestedQaPolicy: context.RequestedQaPolicy,
                    ResolvedQaPolicy: context.Plan.QaPolicyProfile,
                    AdaptivePolicyReason: context.AdaptivePolicyReason,
                    RepoRoot: fullRepoRoot,
                    FailureStage: "preflight",
                    BootstrapOk: true,
                    PreflightRan: true,
                    PreflightOk: false,
                    SelfExtendRan: false,
                    SelfExtendOk: false));
            }
        }

        var runSpec = context.WorkflowSpec with
        {
            Workflow = context.WorkflowSpec.Workflow with { RequirePreflight = false }
        };
        var runArgs = BuildSelfExtendRunArgs(
            enrichedGoal,
            fullRepoRoot,
            provider,
            allowMock,
            runTests,
            testFilter,
            JsonSerializer.Serialize(runSpec));
        var selfExtendRun = await RunCliSubcommandAsync(fullRepoRoot, runArgs, ct).ConfigureAwait(false);
        var selfExtendPayload = TryExtractLastJsonObject(selfExtendRun.StdOut);
        var selfExtendOk = selfExtendRun.ExitCode == 0 && IsPayloadOk(selfExtendPayload);

        return Finalize(new RuntimeExecuteResult(
            selfExtendOk,
            selfExtendOk ? "runtime execute completed successfully." : "runtime execute failed during self-extend run.",
            context.Plan,
            bootstrapBefore,
            bootstrapAfter,
            bootstrapApplied,
            bootstrapApplyExit,
            preflightPayload,
            preflightRun,
            selfExtendPayload,
            selfExtendRun,
            context.RequestedQaPolicy,
            context.Plan.QaPolicyProfile,
            context.AdaptivePolicyReason,
            RepoRoot: fullRepoRoot,
            FailureStage: selfExtendOk ? "none" : "self-extend",
            BootstrapOk: true,
            PreflightRan: runPreflight,
            PreflightOk: runPreflight ? preflightOkResult : null,
            SelfExtendRan: true,
            SelfExtendOk: selfExtendOk));
    }


}
