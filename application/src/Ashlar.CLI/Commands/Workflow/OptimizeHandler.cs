using System.Diagnostics;
using System.Text.Json;
using Ashlar.CLI.Runtime;
namespace Ashlar.CLI.Commands.Workflow;
/// <summary>Handles optimize requests.</summary>
internal sealed partial class OptimizeHandler(
    Func<string, CancellationToken, Task<WorkflowCommand.PreflightResult>> providerPreflight,
    Func<IReadOnlyList<string>, CancellationToken, Task<WorkflowCommand.ModelPullResult>> ollamaModelPuller,
    Func<bool, string?, CancellationToken, Task<IReadOnlyList<ExecutionTarget>>> resolveExecutionTargets,
    Func<ExecutionTarget, string, string, string?, bool, CancellationToken, Task<WorkflowCommand.ScenarioExecutionResult>> executeScenarioForTarget)
{
    /// <summary>Executes the command handler and returns a process exit code.</summary>
    public async Task<int> ExecuteAsync(
        string? requestOverride,
        string? objective,
        string? objectiveFile,
        string? specPath,
        string? specJson,
        string? providerOverride,
        string? preferOverride,
        int? iterationsOverride,
        string? benchmarkSetOverride,
        bool? persistHistoryOverride,
        int? warmupRunsOverride,
        bool? shuffleScenariosOverride,
        int? randomSeedOverride,
        int? cooldownMsOverride,
        int maxCandidates,
        int? budgetRuns,
        string? searchStrategy,
        int? earlyStopMinRuns,
        double? earlyStopMinSuccessRate,
        bool includeMeshPeers,
        string? meshCapability,
        bool autoPullModels,
        bool promoteWinner,
        string? policyFile,
        string? reportOutputPath,
        bool json,
        bool verbose,
        CancellationToken ct)
    {
        var resolvedSpecPath = WorkflowCommandUtilities.ResolveDefaultSpecPath(specPath);
        WorkflowLabRuntimeSpec spec;
        try
        {
            spec = WorkflowLabRuntimeSpecLoader.Load(resolvedSpecPath, specJson);
        }
        catch (Exception ex)
        {
            WriteResult(new WorkflowOptimizeResult(false, $"Failed to load workflow lab spec: {ex.Message}"), json);
            return 1;
        }
        var repoRoot = Environment.CurrentDirectory;
        var requests = WorkflowCommandUtilities.NormalizeRequests(spec.Requests);
        var compositions = WorkflowCommandUtilities.NormalizeCompositions(spec.Compositions);
        var profiles = WorkflowCommandUtilities.NormalizeProfiles(spec.ModelProfiles, providerOverride, preferOverride);
        if (requests.Length == 0 || compositions.Length == 0 || profiles.Length == 0)
        {
            WriteResult(new WorkflowOptimizeResult(
                false,
                "Workflow optimize spec must include at least one request, composition, and model profile."), json);
            return 1;
        }
        var objectiveText = WorkflowCommandUtilities.ResolveObjectiveText(objective, objectiveFile);
        if (objectiveText is null && !string.IsNullOrWhiteSpace(objectiveFile))
        {
            WriteResult(new WorkflowOptimizeResult(
                false,
                $"Failed to load objective file: {objectiveFile}",
                Objective: objective,
                ObjectiveFile: objectiveFile), json);
            return 1;
        }
        var benchmarkSet = WorkflowCommandUtilities.NormalizeBenchmarkSet(benchmarkSetOverride, spec.Execution.BenchmarkSet);
        var persistHistory = persistHistoryOverride ?? spec.Execution.PersistHistory;
        var iterations = Math.Max(1, iterationsOverride ?? spec.Execution.Iterations);
        var warmupRuns = Math.Max(0, warmupRunsOverride ?? spec.Execution.WarmupRuns);
        var cooldownMs = Math.Max(0, cooldownMsOverride ?? spec.Execution.CooldownMs);
        var shuffleScenarios = shuffleScenariosOverride ?? spec.Execution.ShuffleScenarioOrder;
        var randomSeed = randomSeedOverride ?? spec.Execution.RandomSeed;
        var rng = randomSeed.HasValue ? new Random(randomSeed.Value) : null;
        var strategy = WorkflowCommandUtilities.NormalizeSearchStrategy(searchStrategy);
        var minRunsForEarlyStop = Math.Max(1, earlyStopMinRuns ?? 2);
        var minSuccessForEarlyStop = Math.Clamp(earlyStopMinSuccessRate ?? 0.35, 0d, 1d);
        var measuredRunBudget = budgetRuns.HasValue ? Math.Max(1, budgetRuns.Value) : int.MaxValue;
        var sharedRequest = string.IsNullOrWhiteSpace(requestOverride) ? null : requestOverride.Trim();
        var optimizeRunId = WorkflowCommandUtilities.BuildRunId();
        var specHash = WorkflowCommandUtilities.ComputeSpecHash(JsonSerializer.Serialize(spec));
        var gitSha = WorkflowCommandUtilities.ResolveGitSha();
        var providerSnapshot = WorkflowCommandUtilities.BuildProviderSnapshot(profiles);
        var scenarioPlans = WorkflowCommandUtilities.BuildScenarioPlans(requests, compositions, profiles, iterations);
        var groupedCandidates = scenarioPlans
            .GroupBy(
                x => $"{x.Request.Id}::{x.Composition.Id}::{x.Profile.Id}",
                StringComparer.OrdinalIgnoreCase)
            .Select(g => new OptimizeCandidatePlan(
                g.Key,
                g.First().Request,
                g.First().Composition,
                g.First().Profile,
                g.OrderBy(x => x.Iteration).ToArray()))
            .ToList();

        var objectiveKeywordSet = WorkflowCommandUtilities.BuildObjectiveKeywordSet(objectiveText);
        var maxCandidateCount = Math.Max(1, maxCandidates);
        var synthesizedCandidates = WorkflowCommandUtilities.BuildObjectiveSynthesizedCandidates(
            groupedCandidates,
            objectiveKeywordSet,
            maxCandidateCount);
        if (synthesizedCandidates.Count > 0)
        {
            groupedCandidates = groupedCandidates
                .Concat(synthesizedCandidates)
                .GroupBy(x => x.CandidateId, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.First())
                .ToList();
        }

        if (shuffleScenarios && groupedCandidates.Count > 1)
            WorkflowOptimizeReportRenderer.ShuffleOptimizeCandidates(groupedCandidates, rng ?? new Random());

        groupedCandidates = WorkflowCommandUtilities.SortCandidatesForSearchStrategy(groupedCandidates, strategy, objectiveKeywordSet);

        if (groupedCandidates.Count > maxCandidateCount)
            groupedCandidates = groupedCandidates.Take(maxCandidateCount).ToList();

        var preflightByProvider = new Dictionary<string, WorkflowCommand.PreflightResult>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in profiles
                     .Select(x => x.Default.Provider)
                     .Where(x => !string.IsNullOrWhiteSpace(x))
                     .Select(x => x!.Trim())
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            preflightByProvider[provider] = await providerPreflight(provider, ct).ConfigureAwait(false);
        }

        var executionTargets = await resolveExecutionTargets(
            includeMeshPeers,
            meshCapability,
            ct).ConfigureAwait(false);

        var measuredRunsUsed = 0;
        var targetStats = executionTargets.ToDictionary(
            x => x.Id,
            x => new TargetExecutionStats(),
            StringComparer.OrdinalIgnoreCase);
        var allocationTrace = new List<OptimizeAllocationTrace>();
        var adaptiveSynthesisBudget = Math.Max(1, maxCandidateCount / 3);
        var adaptiveSynthesisCount = 0;
        var adaptiveSynthesisCursor = 0;
        var persistedRows = new List<WorkflowLabStressHistoryRow>();
        var candidateStates = await BuildCandidateStatesAsync(
            groupedCandidates,
            optimizeRunId,
            strategy,
            autoPullModels,
            ollamaModelPuller,
            ct).ConfigureAwait(false);

        while (measuredRunsUsed < measuredRunBudget)
        {
            var candidateState = WorkflowCommandUtilities.SelectNextCandidateState(candidateStates, objectiveKeywordSet);
            if (candidateState is null)
                break;
            ct.ThrowIfCancellationRequested();
            var plan = candidateState.Plans[candidateState.NextPlanIndex];
            candidateState.NextPlanIndex++;
            var request = plan.Request;
            var composition = plan.Composition;
            var profile = plan.Profile;
            var iteration = plan.Iteration;
            var executionTarget = WorkflowCommandUtilities.SelectExecutionTarget(executionTargets, targetStats);
            var scenarioId = WorkflowCommandUtilities.BuildScenarioId(request.Id, composition.Id, profile.Id, iteration) +
                             $"::target-{WorkflowOptimizeReportRenderer.NormalizeScenarioTargetSegment(executionTarget.Id)}";
            var runtime = WorkflowCommandUtilities.BuildRuntimeSpec(composition, profile);
            var runtimeJson = JsonSerializer.Serialize(runtime);
            var runtimeExecutionRequest = WorkflowOptimizeReportRenderer.BuildExecutionRequest(request, composition, profile, sharedRequest);

            for (var warmup = 0; warmup < warmupRuns; warmup++)
            {
                ct.ThrowIfCancellationRequested();
                if (!candidateState.PullResult.Ok && executionTarget.IsLocal)
                    break;
                if (executionTarget.IsLocal &&
                    !string.IsNullOrWhiteSpace(candidateState.ProfileProvider) &&
                    preflightByProvider.TryGetValue(candidateState.ProfileProvider, out var warmupPreflight) &&
                    !warmupPreflight.Ok)
                {
                    break;
                }

                try
                {
                    _ = await executeScenarioForTarget(
                        executionTarget,
                        runtimeExecutionRequest,
                        runtimeJson,
                        profile.Default.Provider,
                        verbose,
                        ct).ConfigureAwait(false);
                }
                catch
                {
                    // Warmups are intentionally ignored in persisted metrics.
                }
            }

            ct.ThrowIfCancellationRequested();
            var startedAt = DateTimeOffset.UtcNow;
            var cpuStart = Process.GetCurrentProcess().TotalProcessorTime;
            var sw = Stopwatch.StartNew();
            WorkflowCommand.ScenarioExecutionResult scenario;
            if (!candidateState.PullResult.Ok && executionTarget.IsLocal)
            {
                scenario = new WorkflowCommand.ScenarioExecutionResult(
                    Ok: false,
                    Summary: $"Skipped due to model pull failure: {candidateState.PullResult.Summary}",
                    ConflictCount: 0,
                    EscalationCount: 0,
                    FailureCategory: "skipped_infra",
                    Skipped: true);
            }
            else if (executionTarget.IsLocal &&
                     !string.IsNullOrWhiteSpace(candidateState.ProfileProvider) &&
                     preflightByProvider.TryGetValue(candidateState.ProfileProvider, out var preflight) &&
                     !preflight.Ok)
            {
                // A refused probe is a failure, not a successful skip (card #802). Other preflight
                // failures remain skipped_infra.
                scenario = preflight.WasRefused
                    ? new WorkflowCommand.ScenarioExecutionResult(
                        Ok: false,
                        Summary: $"Refused by egress policy ({candidateState.ProfileProvider}): {preflight.Detail}",
                        ConflictCount: 0,
                        EscalationCount: 0,
                        FailureCategory: "egress_refused",
                        Skipped: false)
                    : new WorkflowCommand.ScenarioExecutionResult(
                        Ok: false,
                        Summary: $"Skipped due to provider preflight failure ({candidateState.ProfileProvider}): {preflight.Detail}",
                        ConflictCount: 0,
                        EscalationCount: 0,
                        FailureCategory: "skipped_infra",
                        Skipped: true);
            }
            else
            {
                try
                {
                    scenario = await executeScenarioForTarget(
                        executionTarget,
                        runtimeExecutionRequest,
                        runtimeJson,
                        profile.Default.Provider,
                        verbose,
                        ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    scenario = new WorkflowCommand.ScenarioExecutionResult(
                        Ok: false,
                        Summary: $"Scenario executor failed: {ex.Message}",
                        ConflictCount: 0,
                        EscalationCount: 0,
                        FailureCategory: "executor_failure");
                }
            }

            var elapsedMs = sw.ElapsedMilliseconds;
            var telemetry = WorkflowCommandUtilities.CaptureRuntimeTelemetry(startedAt, cpuStart);
            var score = WorkflowCommandUtilities.ComputeScore(scenario.Ok, elapsedMs, composition, profile);
            var runSummary = $"{scenario.Summary} [target={executionTarget.Id}]";
            var runRecord = new WorkflowStressRunRecord(
                candidateState.CandidateRunId,
                gitSha,
                specHash,
                providerSnapshot,
                scenarioId,
                request.Id,
                composition.Id,
                profile.Id,
                iteration,
                scenario.Ok,
                composition.Roles.Count,
                scenario.ConflictCount,
                scenario.EscalationCount,
                elapsedMs,
                score,
                runSummary,
                scenario.FailureCategory,
                scenario.Skipped,
                startedAt,
                telemetry.CpuTimeDeltaMs,
                telemetry.WorkingSetMb,
                telemetry.PrivateMemoryMb,
                telemetry.ManagedMemoryMb,
                telemetry.ThreadCount,
                telemetry.HardwareProfile,
                benchmarkSet);
            candidateState.Runs.Add(runRecord);
            measuredRunsUsed++;
            WorkflowCommandUtilities.UpdateTargetExecutionStats(targetStats, executionTarget.Id, runRecord.Success, runRecord.Skipped, runRecord.ElapsedMs);
            allocationTrace.Add(new OptimizeAllocationTrace(
                measuredRunsUsed,
                candidateState.Candidate.CandidateId,
                executionTarget.Id,
                runRecord.Success,
                runRecord.ElapsedMs,
                candidateState.Candidate.Synthesized ? "synthesized-candidate" : "base-candidate"));

            var historyRow = new WorkflowLabStressHistoryRow
            {
                RunId = runRecord.RunId,
                GitSha = runRecord.GitSha,
                SpecHash = runRecord.SpecHash,
                ProviderSnapshot = runRecord.ProviderSnapshot,
                ScenarioId = runRecord.ScenarioId,
                RequestId = runRecord.RequestId,
                CompositionId = runRecord.CompositionId,
                ModelProfileId = runRecord.ModelProfileId,
                Iteration = runRecord.Iteration,
                StartedAtUtc = runRecord.StartedAtUtc,
                ElapsedMs = runRecord.ElapsedMs,
                Success = runRecord.Success,
                AgentCount = runRecord.AgentCount,
                ConflictCount = runRecord.ConflictCount,
                EscalationCount = runRecord.EscalationCount,
                Score = runRecord.Score,
                Summary = runRecord.Summary,
                Skipped = runRecord.Skipped,
                CpuTimeDeltaMs = runRecord.CpuTimeDeltaMs,
                WorkingSetMb = runRecord.WorkingSetMb,
                PrivateMemoryMb = runRecord.PrivateMemoryMb,
                ManagedMemoryMb = runRecord.ManagedMemoryMb,
                ThreadCount = runRecord.ThreadCount,
                HardwareProfile = runRecord.HardwareProfile,
                FailureCategory = runRecord.FailureCategory,
                BenchmarkSet = runRecord.BenchmarkSet
            };
            if (persistHistory)
                WorkflowLabHistoryStore.Append(repoRoot, historyRow);
            persistedRows.Add(historyRow);

            if (cooldownMs > 0)
                await Task.Delay(TimeSpan.FromMilliseconds(cooldownMs), ct).ConfigureAwait(false);

            if (candidateState.Runs.Count >= minRunsForEarlyStop)
            {
                var successful = candidateState.Runs.Count(x => x.Success);
                var successRateNow = candidateState.Runs.Count == 0 ? 0d : (double)successful / candidateState.Runs.Count;
                if (successRateNow < minSuccessForEarlyStop)
                    candidateState.EarlyStopped = true;
            }

            if (adaptiveSynthesisCount < adaptiveSynthesisBudget &&
                measuredRunsUsed < measuredRunBudget &&
                candidateStates.Count < maxCandidateCount &&
                candidateState.Runs.Count == 1 &&
                candidateState.Runs[0].Success &&
                !candidateState.Candidate.Synthesized &&
                objectiveKeywordSet.Count > 0)
            {
                var adaptiveCandidate = WorkflowCommandUtilities.BuildAdaptiveFollowUpCandidate(
                    candidateState.Candidate,
                    objectiveKeywordSet,
                    ++adaptiveSynthesisCursor);
                if (adaptiveCandidate is not null &&
                    !candidateStates.Any(x => string.Equals(x.Candidate.CandidateId, adaptiveCandidate.CandidateId, StringComparison.OrdinalIgnoreCase)))
                {
                    var adaptiveProfileProvider = adaptiveCandidate.Profile.Default.Provider?.Trim();
                    var adaptiveModels = WorkflowOptimizeReportRenderer.ResolveOllamaModelsForCandidate(adaptiveCandidate.Composition, adaptiveCandidate.Profile);
                    var adaptivePullResult = autoPullModels
                        ? await ollamaModelPuller(adaptiveModels, ct).ConfigureAwait(false)
                        : new WorkflowCommand.ModelPullResult(
                            Ok: true,
                            Summary: "Model auto-pull disabled.",
                            Models: adaptiveModels,
                            PulledModels: Array.Empty<string>());
                    var adaptivePlans = strategy == "successive-halving"
                        ? adaptiveCandidate.Plans.Take(Math.Max(1, (adaptiveCandidate.Plans.Count + 1) / 2)).ToArray()
                        : adaptiveCandidate.Plans.ToArray();
                    candidateStates.Add(new OptimizeCandidateRuntimeState(
                        adaptiveCandidate,
                        $"{optimizeRunId}-c{candidateStates.Count + 1:D2}",
                        adaptiveProfileProvider,
                        adaptiveModels,
                        adaptivePullResult,
                        adaptivePlans));
                    adaptiveSynthesisCount++;
                }
            }
        }

        var candidates = BuildCandidateResults(candidateStates, objectiveKeywordSet);

        if (candidates.Count == 0)
        {
            WriteResult(new WorkflowOptimizeResult(false, "No optimize candidates were generated."), json);
            return 1;
        }

        var ranked = candidates
            .OrderByDescending(x => x.SuccessRate)
            .ThenByDescending(x => x.AverageScore)
            .ThenByDescending(x => x.ObjectiveScore)
            .ThenBy(x => x.P95LatencyMs)
            .ThenBy(x => x.AverageLatencyMs)
            .ToArray();
        var winner = ranked[0];
        var winnerConfidence = WorkflowOptimizeReportRenderer.ComputePromotionConfidence(winner, minRunsForEarlyStop);
        const double promotionConfidenceThreshold = 0.6;
        var minimumPromotionSamples = Math.Max(2, minRunsForEarlyStop);
        var winnerHasMinimumSamples = winner.TotalRuns >= minimumPromotionSamples;
        var recommendations = BuildRecommendations(
            ranked,
            winner,
            winnerHasMinimumSamples,
            minimumPromotionSamples,
            winnerConfidence,
            promotionConfidenceThreshold);
        var synthesizedCandidateCount = ranked.Count(x => x.Synthesized);
        var targetAllocations = WorkflowOptimizeReportRenderer.BuildTargetAllocations(allocationTrace);
        var candidateAllocations = WorkflowOptimizeReportRenderer.BuildCandidateAllocations(allocationTrace, ranked);

        string? promotionSummary = null;
        string? promotedBaselineId = null;
        if (promoteWinner && persistHistory)
        {
            if (!winnerHasMinimumSamples)
            {
                promotionSummary =
                    $"Promotion skipped: winner has {winner.TotalRuns} measured run(s), requires at least {minimumPromotionSamples}.";
            }
            else if (winnerConfidence < promotionConfidenceThreshold)
            {
                promotionSummary =
                    $"Promotion skipped: winner confidence {winnerConfidence:F2} below threshold {promotionConfidenceThreshold:F2}.";
            }

            var policyLoad = WorkflowCommandUtilities.LoadGatePolicy(policyFile);
            if (!string.IsNullOrWhiteSpace(promotionSummary))
            {
                // confidence guardrail already decided promotion outcome
            }
            else if (!policyLoad.Ok)
            {
                promotionSummary = $"Promotion skipped: {policyLoad.Error}";
            }
            else
            {
                var effectiveMinSuccessRateDelta = policyLoad.Policy?.MinSuccessRateDelta ?? -0.05;
                var effectiveMaxP95LatencyRegressionMs = policyLoad.Policy?.MaxP95LatencyRegressionMs ?? 250;
                var effectiveMaxAverageLatencyRegressionMs = policyLoad.Policy?.MaxAverageLatencyRegressionMs ?? 150;
                var effectiveMinAverageScoreDelta = policyLoad.Policy?.MinAverageScoreDelta ?? -5.0;
                var effectiveMaxRegressedScenarios = policyLoad.Policy?.MaxRegressedScenarios ?? 2;
                var activeBaseline = WorkflowBaselineStore.ReadActive(repoRoot, benchmarkSet);
                if (activeBaseline is not null)
                {
                    var history = WorkflowLabHistoryStore.ReadAll(repoRoot)
                        .Where(x => string.Equals(x.BenchmarkSet, benchmarkSet, StringComparison.OrdinalIgnoreCase))
                        .ToArray();
                    var comparison = WorkflowCommandUtilities.BuildComparison(history, winner.RunId, activeBaseline.RunId);
                    if (!comparison.Valid)
                    {
                        promotionSummary = $"Promotion skipped: failed to compare against active baseline {activeBaseline.RunId}.";
                    }
                    else
                    {
                        var failures = new List<string>();
                        if (comparison.SuccessRateDelta < effectiveMinSuccessRateDelta)
                            failures.Add("success-rate");
                        if (comparison.P95LatencyDeltaMs > effectiveMaxP95LatencyRegressionMs)
                            failures.Add("p95-latency");
                        if (comparison.AverageLatencyDeltaMs > effectiveMaxAverageLatencyRegressionMs)
                            failures.Add("avg-latency");
                        if (comparison.AverageScoreDelta < effectiveMinAverageScoreDelta)
                            failures.Add("avg-score");
                        if (comparison.RegressedScenarios > effectiveMaxRegressedScenarios)
                            failures.Add("regressed-scenarios");

                        if (failures.Count > 0)
                        {
                            promotionSummary =
                                $"Promotion skipped: winner failed policy gates versus baseline {activeBaseline.RunId} ({string.Join(", ", failures)}).";
                        }
                    }
                }

                if (string.IsNullOrWhiteSpace(promotionSummary))
                {
                    var winnerRows = persistedRows
                        .Where(x => string.Equals(x.RunId, winner.RunId, StringComparison.OrdinalIgnoreCase))
                        .ToArray();
                    if (winnerRows.Length == 0)
                    {
                        promotionSummary = "Promotion skipped: winner rows missing from history.";
                    }
                    else
                    {
                        var latest = winnerRows
                            .OrderByDescending(x => x.StartedAtUtc)
                            .First();
                        var promoted = new WorkflowBaselineRecord
                        {
                            BaselineId = WorkflowCommandUtilities.BuildBaselineId(benchmarkSet, winner.RunId),
                            BenchmarkSet = benchmarkSet,
                            RunId = winner.RunId,
                            GitSha = latest.GitSha ?? "unknown",
                            SpecHash = latest.SpecHash ?? "unknown",
                            ProviderSnapshot = latest.ProviderSnapshot ?? "unknown",
                            PromotedAtUtc = DateTimeOffset.UtcNow,
                            Active = true,
                            Notes = $"auto-promoted by workflow optimize ({optimizeRunId})",
                            Policy = policyLoad.Policy is null
                                ? null
                                : new WorkflowGatePolicySpec
                                {
                                    BenchmarkSet = policyLoad.Policy.BenchmarkSet,
                                    MinSuccessRateDelta = policyLoad.Policy.MinSuccessRateDelta,
                                    MaxP95LatencyRegressionMs = policyLoad.Policy.MaxP95LatencyRegressionMs,
                                    MaxAverageLatencyRegressionMs = policyLoad.Policy.MaxAverageLatencyRegressionMs,
                                    MinAverageScoreDelta = policyLoad.Policy.MinAverageScoreDelta,
                                    MaxRegressedScenarios = policyLoad.Policy.MaxRegressedScenarios
                                }
                        };
                        WorkflowBaselineStore.Promote(repoRoot, promoted);
                        promotedBaselineId = promoted.BaselineId;
                        promotionSummary = $"Promoted winner run-id {winner.RunId} as active baseline {promoted.BaselineId}.";
                    }
                }
            }
        }
        else if (promoteWinner && !persistHistory)
        {
            promotionSummary = "Promotion skipped: persist-history disabled.";
        }

        var reportPath = WorkflowOptimizeReportRenderer.ResolveOptimizeReportPath(reportOutputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        var reportContent = WorkflowOptimizeReportRenderer.RenderOptimizationRecommendationContent(
            reportPath,
            optimizeRunId,
            benchmarkSet,
            ranked,
            winner,
            recommendations,
            promotionSummary,
            objectiveText,
            objectiveFile,
            strategy,
            measuredRunsUsed,
            measuredRunBudget,
            minRunsForEarlyStop,
            minSuccessForEarlyStop,
            synthesizedCandidateCount,
            winnerConfidence,
            promotionConfidenceThreshold,
            allocationTrace,
            targetAllocations,
            candidateAllocations);
        File.WriteAllText(reportPath, reportContent);

        WorkflowOptimizeLastStore.Write(
            repoRoot,
            new WorkflowOptimizeLastPayload
            {
                WrittenAtUtc = DateTimeOffset.UtcNow,
                OptimizeRunId = optimizeRunId,
                Ok = ranked.Any(x => x.Successes > 0),
                WinnerCandidateId = winner.CandidateId,
                WinnerRunId = winner.RunId,
                ModelProfileId = winner.ModelProfileId,
                CompositionId = winner.CompositionId,
                RequestId = winner.RequestId,
                OllamaModels = winner.Models.ToArray()
            });

        var hasSuccess = ranked.Any(x => x.Successes > 0);
        var refusedPreflights = preflightByProvider.Values
            .Where(x => x.WasRefused)
            .Select(x => x.Detail)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var failureSummary = refusedPreflights.Length > 0
            ? $"Workflow optimize completed with no successful candidates across {ranked.Length} evaluated candidate(s); refused: {string.Join("; ", refusedPreflights)}."
            : $"Workflow optimize completed with no successful candidates across {ranked.Length} evaluated candidate(s).";
        var result = new WorkflowOptimizeResult(
            Ok: hasSuccess,
            Summary: hasSuccess
                ? $"Workflow optimize completed: evaluated {ranked.Length} candidate(s); winner={winner.CandidateId}."
                : failureSummary,
            SessionRunId: optimizeRunId,
            BenchmarkSet: benchmarkSet,
            RecommendationReportPath: reportPath,
            Candidates: ranked,
            Winner: winner,
            Recommendations: recommendations,
            PromotionSummary: promotionSummary,
            PromotedBaselineId: promotedBaselineId,
            Objective: objectiveText,
            ObjectiveFile: objectiveFile,
            SearchStrategy: strategy,
            BudgetRuns: measuredRunBudget == int.MaxValue ? null : measuredRunBudget,
            MeasuredRunsUsed: measuredRunsUsed,
            EarlyStopMinRuns: minRunsForEarlyStop,
            EarlyStopMinSuccessRate: minSuccessForEarlyStop,
            SynthesizedCandidateCount: synthesizedCandidateCount,
            AdaptiveSynthesizedCandidateCount: adaptiveSynthesisCount,
            WinnerConfidence: winnerConfidence,
            PromotionConfidenceThreshold: promotionConfidenceThreshold,
            AllocationTrace: allocationTrace,
            TargetAllocations: targetAllocations,
            CandidateAllocations: candidateAllocations);
        /// <summary>Write result.</summary>
        WriteResult(result, json);
        return result.Ok ? 0 : 1;
    }

}
