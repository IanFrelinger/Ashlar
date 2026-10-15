using System.Diagnostics;
using System.Text.Json;
using Ashlar.CLI.Runtime;
using Ashlar.Orchestration.Models;

namespace Ashlar.CLI.Commands.Workflow;
/// <summary>Handles stress requests.</summary>
internal sealed class StressHandler(
    Func<string, CancellationToken, Task<WorkflowCommand.PreflightResult>> providerPreflight,
    Func<string?, string> resolveDefaultSpecPath,
    Func<IReadOnlyList<WorkflowLabRequestSpec>, WorkflowLabRequestSpec[]> normalizeRequests,
    Func<IReadOnlyList<WorkflowLabCompositionSpec>, WorkflowLabCompositionSpec[]> normalizeCompositions,
    Func<IReadOnlyList<WorkflowLabModelProfileSpec>, string?, string?, WorkflowLabModelProfileSpec[]> normalizeProfiles,
    Func<string?, string, string> normalizeBenchmarkSet,
    Func<string> buildRunId,
    Func<string> resolveGitSha,
    Func<string, string> computeSpecHash,
    Func<IReadOnlyList<WorkflowLabModelProfileSpec>, string> buildProviderSnapshot,
    Func<bool, string?, CancellationToken, Task<IReadOnlyList<ExecutionTarget>>> resolveExecutionTargets,
    Func<IReadOnlyList<WorkflowLabRequestSpec>, IReadOnlyList<WorkflowLabCompositionSpec>, IReadOnlyList<WorkflowLabModelProfileSpec>, int, IReadOnlyList<ScenarioPlan>> buildScenarioPlans,
    Action<IReadOnlyList<ScenarioPlan>, Random> shuffleScenarioPlans,
    Func<WorkflowLabCompositionSpec, WorkflowLabModelProfileSpec, OrchestrationRuntimeSpec> buildRuntimeSpec,
    Func<string, string, string, int, string> buildScenarioId,
    Func<ExecutionTarget, string, string, string?, bool, CancellationToken, Task<WorkflowCommand.ScenarioExecutionResult>> executeScenarioForTarget,
    Func<DateTimeOffset, TimeSpan, RuntimeTelemetry> captureRuntimeTelemetry,
    Func<bool, long, WorkflowLabCompositionSpec, WorkflowLabModelProfileSpec, double> computeScore)
{
    /// <summary>Executes the command handler and returns a process exit code.</summary>
    public async Task<int> ExecuteAsync(
        string? requestOverride,
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
        bool includeMeshPeers,
        string? meshCapability,
        bool json,
        bool verbose,
        CancellationToken ct)
    {
        var resolvedSpecPath = resolveDefaultSpecPath(specPath);
        WorkflowLabRuntimeSpec spec;
        try
        {
            spec = WorkflowLabRuntimeSpecLoader.Load(resolvedSpecPath, specJson);
        }
        catch (Exception ex)
        {
            WriteResult(new WorkflowStressResult(false, $"Failed to load workflow lab spec: {ex.Message}"), json);
            return 1;
        }

        var repoRoot = Environment.CurrentDirectory;
        var requests = normalizeRequests(spec.Requests);
        var compositions = normalizeCompositions(spec.Compositions);
        var profiles = normalizeProfiles(spec.ModelProfiles, providerOverride, preferOverride);
        if (requests.Length == 0 || compositions.Length == 0 || profiles.Length == 0)
        {
            WriteResult(new WorkflowStressResult(
                false,
                "Workflow stress spec must include at least one request, composition, and model profile."), json);
            return 1;
        }

        var benchmarkSet = normalizeBenchmarkSet(benchmarkSetOverride, spec.Execution.BenchmarkSet);
        var persistHistory = persistHistoryOverride ?? spec.Execution.PersistHistory;
        var iterations = Math.Max(1, iterationsOverride ?? spec.Execution.Iterations);
        var sharedRequest = string.IsNullOrWhiteSpace(requestOverride) ? null : requestOverride.Trim();
        var runId = buildRunId();
        var specHash = computeSpecHash(JsonSerializer.Serialize(spec));
        var gitSha = resolveGitSha();
        var providerSnapshot = buildProviderSnapshot(profiles);
        var warmupRuns = Math.Max(0, warmupRunsOverride ?? spec.Execution.WarmupRuns);
        var cooldownMs = Math.Max(0, cooldownMsOverride ?? spec.Execution.CooldownMs);
        var shuffleScenarios = shuffleScenariosOverride ?? spec.Execution.ShuffleScenarioOrder;
        var randomSeed = randomSeedOverride ?? spec.Execution.RandomSeed;
        var rng = randomSeed.HasValue ? new Random(randomSeed.Value) : null;

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

        var scenarioPlans = buildScenarioPlans(requests, compositions, profiles, iterations);
        if (shuffleScenarios && scenarioPlans.Count > 1)
            shuffleScenarioPlans(scenarioPlans, rng ?? new Random());

        var runs = new List<WorkflowStressRunRecord>();
        var executionTargetCursor = 0;
        foreach (var plan in scenarioPlans)
        {
            var request = plan.Request;
            var composition = plan.Composition;
            var profile = plan.Profile;
            var i = plan.Iteration;
            var executionTarget = executionTargets[executionTargetCursor % executionTargets.Count];
            executionTargetCursor++;
            var scenarioId = buildScenarioId(request.Id, composition.Id, profile.Id, i) +
                             $"::target-{WorkflowOptimizeReportRenderer.NormalizeScenarioTargetSegment(executionTarget.Id)}";
            var runtime = buildRuntimeSpec(composition, profile);
            var runtimeJson = JsonSerializer.Serialize(runtime);
            var runtimeExecutionRequest = WorkflowOptimizeReportRenderer.BuildExecutionRequest(request, composition, profile, sharedRequest);
            var profileProvider = profile.Default.Provider?.Trim();

            for (var warmup = 0; warmup < warmupRuns; warmup++)
            {
                ct.ThrowIfCancellationRequested();
                if (!string.IsNullOrWhiteSpace(profileProvider) &&
                    preflightByProvider.TryGetValue(profileProvider, out var warmupPreflight) &&
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
            if (!string.IsNullOrWhiteSpace(profileProvider) &&
                preflightByProvider.TryGetValue(profileProvider, out var preflight) &&
                !preflight.Ok)
            {
                // A refused probe is a failure, not a successful skip (card #802). Other preflight
                // failures remain skipped_infra and still count as passed in allPassed below.
                scenario = preflight.WasRefused
                    ? new WorkflowCommand.ScenarioExecutionResult(
                        Ok: false,
                        Summary: $"Refused by egress policy ({profileProvider}): {preflight.Detail}",
                        ConflictCount: 0,
                        EscalationCount: 0,
                        FailureCategory: "egress_refused",
                        Skipped: false)
                    : new WorkflowCommand.ScenarioExecutionResult(
                        Ok: false,
                        Summary: $"Skipped due to provider preflight failure ({profileProvider}): {preflight.Detail}",
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
            var telemetry = captureRuntimeTelemetry(startedAt, cpuStart);
            var score = computeScore(scenario.Ok, elapsedMs, composition, profile);
            var runSummary = $"{scenario.Summary} [target={executionTarget.Id}]";
            runs.Add(new WorkflowStressRunRecord(
                runId,
                gitSha,
                specHash,
                providerSnapshot,
                scenarioId,
                request.Id,
                composition.Id,
                profile.Id,
                i,
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
                benchmarkSet));

            if (cooldownMs > 0)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(cooldownMs), ct).ConfigureAwait(false);
            }
        }

        var aggregates = runs
            .GroupBy(x => new { x.RequestId, x.CompositionId, x.ModelProfileId })
            .Select(group =>
            {
                var list = group.ToArray();
                var successCount = list.Count(x => x.Success);
                var avgElapsed = (long)Math.Round(list.Select(x => (double)x.ElapsedMs).DefaultIfEmpty(0d).Average());
                var avgScore = Math.Round(list.Select(x => x.Score).DefaultIfEmpty(0d).Average(), 3);
                return new WorkflowStressAggregate(
                    ScenarioGroupId: $"{group.Key.RequestId}::{group.Key.CompositionId}::{group.Key.ModelProfileId}",
                    group.Key.RequestId,
                    group.Key.CompositionId,
                    group.Key.ModelProfileId,
                    list.Length,
                    successCount,
                    list.Length - successCount,
                    avgElapsed,
                    avgScore);
            })
            .OrderByDescending(x => x.AverageScore)
            .ThenByDescending(x => x.Successes)
            .ThenBy(x => x.AverageElapsedMs)
            .ToArray();

        if (persistHistory)
        {
            foreach (var run in runs)
            {
                WorkflowLabHistoryStore.Append(
                    repoRoot,
                    new WorkflowLabStressHistoryRow
                    {
                        RunId = run.RunId,
                        GitSha = run.GitSha,
                        SpecHash = run.SpecHash,
                        ProviderSnapshot = run.ProviderSnapshot,
                        ScenarioId = run.ScenarioId,
                        RequestId = run.RequestId,
                        CompositionId = run.CompositionId,
                        ModelProfileId = run.ModelProfileId,
                        Iteration = run.Iteration,
                        StartedAtUtc = run.StartedAtUtc,
                        ElapsedMs = run.ElapsedMs,
                        Success = run.Success,
                        AgentCount = run.AgentCount,
                        ConflictCount = run.ConflictCount,
                        EscalationCount = run.EscalationCount,
                        Score = run.Score,
                        Summary = run.Summary,
                        Skipped = run.Skipped,
                        CpuTimeDeltaMs = run.CpuTimeDeltaMs,
                        WorkingSetMb = run.WorkingSetMb,
                        PrivateMemoryMb = run.PrivateMemoryMb,
                        ManagedMemoryMb = run.ManagedMemoryMb,
                        ThreadCount = run.ThreadCount,
                        HardwareProfile = run.HardwareProfile,
                        FailureCategory = run.FailureCategory,
                        BenchmarkSet = run.BenchmarkSet
                    });
            }
        }

        var allPassed = runs.All(run => run.Success || run.Skipped);
        var failureCount = runs.Count(run => !run.Success && !run.Skipped);
        var skippedCount = runs.Count(run => run.Skipped);
        var best = aggregates.FirstOrDefault(x => x.Successes > 0);
        var result = new WorkflowStressResult(
            allPassed,
            allPassed
                ? $"Workflow stress completed: {runs.Count} run(s) across {aggregates.Length} scenario groups (run-id={runId})."
                : $"Workflow stress completed with {failureCount} failing run(s), {skippedCount} skipped run(s), out of {runs.Count} (run-id={runId}).",
            runs,
            aggregates,
            best,
            runId,
            benchmarkSet,
            persistHistory);
        /// <summary>Write result.</summary>
        WriteResult(result, json);
        return result.Ok ? 0 : 1;
    }

    private static void WriteResult(WorkflowStressResult result, bool json)
    {
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                ok = result.Ok,
                summary = result.Summary,
                runId = result.RunId,
                benchmarkSet = result.BenchmarkSet,
                persistHistory = result.PersistHistory,
                runs = result.Runs,
                aggregates = result.Aggregates,
                best = result.Best
            }, new JsonSerializerOptions { WriteIndented = true }));
            return;
        }

        Console.WriteLine($"workflow stress: {(result.Ok ? "ok" : "failed")}");
        Console.WriteLine(result.Summary);
        if (!string.IsNullOrWhiteSpace(result.RunId))
            Console.WriteLine($"  run-id={result.RunId}");
        if (!string.IsNullOrWhiteSpace(result.BenchmarkSet))
            Console.WriteLine($"  benchmark-set={result.BenchmarkSet}");
        if (result.PersistHistory.HasValue)
            Console.WriteLine($"  persist-history={result.PersistHistory.Value}");
        foreach (var aggregate in result.Aggregates ?? Array.Empty<WorkflowStressAggregate>())
        {
            Console.WriteLine(
                $"  {aggregate.ScenarioGroupId}: runs={aggregate.Runs}, success={aggregate.Successes}, failed={aggregate.Failures}, avg-ms={aggregate.AverageElapsedMs}, avg-score={aggregate.AverageScore:F2}");
        }
        if (result.Best != null)
        {
            Console.WriteLine($"  best={result.Best.ScenarioGroupId} score={result.Best.AverageScore:F2}");
        }
    }
}
