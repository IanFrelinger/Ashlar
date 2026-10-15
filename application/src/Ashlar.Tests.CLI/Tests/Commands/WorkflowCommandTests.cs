using Ashlar.Abstractions.Security.Egress;
using Ashlar.CLI.Commands;
using Ashlar.CLI.Runtime;
using Ashlar.Core.Application.Testing.Abstractions;
using Ashlar.Core.Application.Testing.Models;

namespace Ashlar.Tests.CLI.Tests.Commands;

/// <summary>Tests for workflow command.</summary>
public sealed class WorkflowCommandTests : UnitTestBase
{
    public override async Task<TestResult> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await TestScaffoldWritesSpecFileAsync().ConfigureAwait(false);
            await TestHistoryReturnsExpectedSummaryAsync().ConfigureAwait(false);
            await TestStressRunsWithInjectedRequestAndPersistsHistoryAsync(cancellationToken).ConfigureAwait(false);
            await TestStressReturnsFailureWhenExecutorFailsAsync(cancellationToken).ConfigureAwait(false);
            await TestReportGeneratesMarkdownBenchmarkOutputAsync(cancellationToken).ConfigureAwait(false);
            await TestReportFiltersByRunIdAsync(cancellationToken).ConfigureAwait(false);
            await TestStressClassifiesRuntimeContextFailureFromErrorCodeAsync(cancellationToken).ConfigureAwait(false);
            await TestStressHonorsWarmupShuffleAndCooldownExecutionControlsAsync(cancellationToken).ConfigureAwait(false);
            await TestReportIncludesComparisonSectionAsync(cancellationToken).ConfigureAwait(false);
            await TestGatePassesAndFailsWithThresholdsAsync().ConfigureAwait(false);
            await TestBaselinePromoteListAndShowAsync(cancellationToken).ConfigureAwait(false);
            await TestGateUsesPolicyFileAndActiveBaselineAsync(cancellationToken).ConfigureAwait(false);
            await TestOptimizeGeneratesRecommendationReportAsync(cancellationToken).ConfigureAwait(false);
            await TestOptimizeAutoPromotesWinnerBaselineAsync(cancellationToken).ConfigureAwait(false);
            await TestOptimizeInvokesModelPullerWithResolvedModelsAsync(cancellationToken).ConfigureAwait(false);
            await TestOptimizeResolvesObjectiveFileAndReportsSearchMetadataAsync(cancellationToken).ConfigureAwait(false);
            await TestOptimizeSynthesizesObjectiveCandidatesAndReportsProvenanceAsync(cancellationToken).ConfigureAwait(false);
            await TestOptimizeAdaptiveAllocationAndConfidenceGatingAsync(cancellationToken).ConfigureAwait(false);
            await TestOptimizeHonorsBudgetAndEarlyStopAsync(cancellationToken).ConfigureAwait(false);
            await TestGateIgnoresSkippedRunsInSuccessRateAsync().ConfigureAwait(false);
            await TestStressTreatsRefusedPreflightAsFailureNotSkippedSuccessAsync(cancellationToken).ConfigureAwait(false);
            await TestOptimizeSurfacesRefusalReferenceInResultAsync(cancellationToken).ConfigureAwait(false);
            await TestStressNonRefusalPreflightSkipStillCountsAsPassedAsync(cancellationToken).ConfigureAwait(false);
            return new TestResult
            {
                Name = nameof(WorkflowCommandTests),
                Category = "CLI",
                Passed = true,
                Message = "Workflow command tests passed"
            };
        }
        catch (AssertionException ex)
        {
            return new TestResult
            {
                Name = nameof(WorkflowCommandTests),
                Category = "CLI",
                Passed = false,
                ErrorMessage = ex.Message,
                StackTrace = ex.StackTrace
            };
        }
        catch (Exception ex)
        {
            return new TestResult
            {
                Name = nameof(WorkflowCommandTests),
                Category = "CLI",
                Passed = false,
                ErrorMessage = ex.Message,
                StackTrace = ex.StackTrace
            };
        }
    }

    private static WorkflowCommand CreateCommand(
        Func<string, string, string?, bool, bool, CancellationToken, Task<WorkflowCommand.ScenarioExecutionResult>>? scenarioExecutor = null)
    {
        WorkflowCommand.ScenarioExecutor executor = scenarioExecutor is null
            ? StubScenarioExecutorAsync
            : new WorkflowCommand.ScenarioExecutor(scenarioExecutor);
        /// <summary>Workflow command.</summary>
        return new WorkflowCommand(executor);
    }

    private static WorkflowCommand CreateCommandWithPreflight(
        Func<string, string, string?, bool, bool, CancellationToken, Task<WorkflowCommand.ScenarioExecutionResult>>? scenarioExecutor,
        Func<string, CancellationToken, Task<bool>> providerPreflight,
        Func<IReadOnlyList<string>, CancellationToken, Task<WorkflowCommand.ModelPullResult>>? modelPuller = null)
    {
        WorkflowCommand.ScenarioExecutor executor = scenarioExecutor is null
            ? StubScenarioExecutorAsync
            : new WorkflowCommand.ScenarioExecutor(scenarioExecutor);
        return new WorkflowCommand(
            executor,
            providerPreflight is null
                ? null
                : (provider, ct) => providerPreflight(provider, ct),
            modelPuller);
    }

    private static WorkflowCommand CreateCommandWithTypedPreflight(
        Func<string, string, string?, bool, bool, CancellationToken, Task<WorkflowCommand.ScenarioExecutionResult>>? scenarioExecutor,
        Func<string, CancellationToken, Task<WorkflowCommand.PreflightResult>> providerPreflight,
        Func<IReadOnlyList<string>, CancellationToken, Task<WorkflowCommand.ModelPullResult>>? modelPuller = null)
    {
        WorkflowCommand.ScenarioExecutor executor = scenarioExecutor is null
            ? StubScenarioExecutorAsync
            : new WorkflowCommand.ScenarioExecutor(scenarioExecutor);
        return new WorkflowCommand(executor, providerPreflight, modelPuller);
    }

    private static EgressRefusedException CreateTestRefusal() =>
        new(new EgressGuard("secure-workstation", "enforce")
            .Evaluate(new EgressRequest(EgressFamilies.ModelLegacy, "EG-MDL-07", "http://127.0.0.1:11434")));

    private static WorkflowCommand CreateCommandWithPreflightAndPuller(
        Func<string, string, string?, bool, bool, CancellationToken, Task<WorkflowCommand.ScenarioExecutionResult>>? scenarioExecutor,
        Func<string, CancellationToken, Task<bool>> providerPreflight,
        Func<IReadOnlyList<string>, CancellationToken, Task<WorkflowCommand.ModelPullResult>> modelPuller)
    {
        WorkflowCommand.ScenarioExecutor executor = scenarioExecutor is null
            ? StubScenarioExecutorAsync
            : new WorkflowCommand.ScenarioExecutor(scenarioExecutor);
        return new WorkflowCommand(
            executor,
            providerPreflight is null
                ? null
                : (provider, ct) => providerPreflight(provider, ct),
            modelPuller);
    }

    private async Task TestScaffoldWritesSpecFileAsync()
    {
        var repoRoot = CreateTempRepoRoot();
        try
        {
            var outputPath = Path.Combine(repoRoot, ".ashlar", "workflow", "workflow_lab.runtime.json");
            var command = CreateCommand();
            var exitCode = await command.ExecuteScaffoldAsync(outputPath, force: false, json: true).ConfigureAwait(false);
            /// <summary>Assert equal.</summary>
            AssertEqual(0, exitCode);
            AssertTrue(File.Exists(outputPath), "Expected workflow scaffold file to be created.");

            var content = await File.ReadAllTextAsync(outputPath).ConfigureAwait(false);
            AssertTrue(content.Contains("\"compositions\"", StringComparison.OrdinalIgnoreCase), "Scaffold should include compositions.");
            AssertTrue(content.Contains("\"modelProfiles\"", StringComparison.OrdinalIgnoreCase), "Scaffold should include model profiles.");
        }
        finally
        {
            if (Directory.Exists(repoRoot))
                Directory.Delete(repoRoot, recursive: true);
        }
    }

    private async Task TestHistoryReturnsExpectedSummaryAsync()
    {
        var repoRoot = CreateTempRepoRoot();
        try
        {
            WorkflowLabHistoryStore.Append(repoRoot, new WorkflowLabStressHistoryRow
            {
                ScenarioId = "request-a::composition-a::profile-a::iter-1",
                RequestId = "request-a",
                CompositionId = "composition-a",
                ModelProfileId = "profile-a",
                Iteration = 1,
                Success = true,
                Score = 103.5,
                ElapsedMs = 128,
                BenchmarkSet = "workflow-lab"
            });
            WorkflowLabHistoryStore.Append(repoRoot, new WorkflowLabStressHistoryRow
            {
                ScenarioId = "request-a::composition-a::profile-b::iter-1",
                RequestId = "request-a",
                CompositionId = "composition-a",
                ModelProfileId = "profile-b",
                Iteration = 1,
                Success = false,
                Score = 62.4,
                ElapsedMs = 172,
                BenchmarkSet = "workflow-lab"
            });

            var command = CreateCommand();
            var (exitCode, output) = await CaptureConsoleAsync(
                () => command.ExecuteHistoryAsync(repoRoot, limit: 10, benchmarkSet: "workflow-lab", json: true)).ConfigureAwait(false);
            /// <summary>Assert equal.</summary>
            AssertEqual(0, exitCode);
            AssertTrue(output.Contains("\"summaryStats\"", StringComparison.OrdinalIgnoreCase), "History output should include summary stats.");
            AssertTrue(output.Contains("\"bestScenarioId\"", StringComparison.OrdinalIgnoreCase), "History output should include best scenario.");
        }
        finally
        {
            if (Directory.Exists(repoRoot))
                Directory.Delete(repoRoot, recursive: true);
        }
    }

    private async Task TestStressRunsWithInjectedRequestAndPersistsHistoryAsync(CancellationToken cancellationToken)
    {
        var repoRoot = CreateTempRepoRoot();
        var previousCurrent = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = repoRoot;
            var runtimeSpecJson = """
{
  "execution": {
    "iterations": 1,
    "persistHistory": true,
    "benchmarkSet": "workflow-lab"
  },
  "requests": [
    { "id": "incident", "prompt": "Investigate issue and deliver mitigation plan." }
  ],
  "compositions": [
    {
      "id": "triage-squad",
      "roles": [
        { "agentId": "planner-1", "role": "planner", "domain": "coordination", "goal": "Plan execution", "clusterId": "core" },
        { "agentId": "builder-1", "role": "builder", "domain": "engineering", "goal": "Implement fix", "reportsToAgentId": "planner-1", "commandChain": ["planner-1"] }
      ]
    }
  ],
  "modelProfiles": [
    {
      "id": "profile-a",
      "default": { "prefer": "agentic", "provider": "ollama", "model": "llama3.1" },
      "agents": {
        "planner-1": { "prefer": "agentic", "provider": "ollama", "model": "qwen2.5:7b" },
        "builder-1": { "prefer": "agentic", "provider": "ollama", "model": "codellama:13b" }
      }
    }
  ]
}
""";

            var command = CreateCommand();
            var (exitCode, output) = await CaptureConsoleAsync(
                () => command.ExecuteStressAsync(
                    requestOverride: "Execute this objective using mandatory hierarchy.",
                    specPath: null,
                    specJson: runtimeSpecJson,
                    providerOverride: null,
                    preferOverride: null,
                    iterationsOverride: null,
                    benchmarkSetOverride: "hardware-lab",
                    persistHistoryOverride: true,
                    warmupRunsOverride: null,
                    shuffleScenariosOverride: null,
                    randomSeedOverride: null,
                    cooldownMsOverride: null,
                    includeMeshPeers: false,
                    meshCapability: "ashlar-cli",
                    json: true,
                    verbose: false,
                    ct: cancellationToken)).ConfigureAwait(false);
            /// <summary>Assert equal.</summary>
            AssertEqual(0, exitCode);
            AssertTrue(output.Contains("\"ok\": true", StringComparison.OrdinalIgnoreCase), "Stress output should report success.");
            AssertTrue(output.Contains("\"aggregates\"", StringComparison.OrdinalIgnoreCase), "Stress output should include aggregate rankings.");

            var historyRows = WorkflowLabHistoryStore.ReadRecent(repoRoot, 10);
            /// <summary>Assert true.</summary>
            /// <param name="1">1.</param>
            /// <param name="row."">Row.".</param>
            AssertTrue(historyRows.Count >= 1, "Expected stress run to persist at least one history row.");
            /// <summary>Assert equal.</summary>
            AssertEqual("hardware-lab", historyRows[0].BenchmarkSet);
            /// <summary>Assert equal.</summary>
            AssertEqual("none", historyRows[0].FailureCategory);
        }
        finally
        {
            Environment.CurrentDirectory = previousCurrent;
            if (Directory.Exists(repoRoot))
                Directory.Delete(repoRoot, recursive: true);
        }
    }

    private static string CreateTempRepoRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ashlar-workflow-command-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static async Task<(int ExitCode, string Output)> CaptureConsoleAsync(Func<Task<int>> action)
    {
        var writer = new StringWriter();  // not disposed on purpose: a disposed writer left in Console.Out poisons later tests
        try
        {
            Console.SetOut(writer);
            Console.SetError(writer);
            var exitCode = await action().ConfigureAwait(false);
            return (exitCode, writer.ToString());
        }
        finally
        {
            Console.SetOut(ConsoleCapture.Out);
            Console.SetError(ConsoleCapture.Error);
        }
    }

    private static Task<WorkflowCommand.ScenarioExecutionResult> StubScenarioExecutorAsync(
        string request,
        string runtimeSpecJson,
        string? provider,
        bool json,
        bool verbose,
        CancellationToken cancellationToken)
    {
        var isValid = !string.IsNullOrWhiteSpace(request) &&
                      !string.IsNullOrWhiteSpace(runtimeSpecJson) &&
                      json;
        return Task.FromResult(
            new WorkflowCommand.ScenarioExecutionResult(
                isValid,
                isValid ? "stub orchestrate success" : "stub orchestrate failure",
                ConflictCount: 1,
                EscalationCount: 1));
    }

    private async Task TestStressReturnsFailureWhenExecutorFailsAsync(CancellationToken cancellationToken)
    {
        var repoRoot = CreateTempRepoRoot();
        var previousCurrent = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = repoRoot;
            const string runtimeSpecJson = """
{
  "execution": { "iterations": 1, "persistHistory": false, "benchmarkSet": "workflow-lab" },
  "requests": [ { "id": "request", "prompt": "Do thing" } ],
  "compositions": [ { "id": "comp", "roles": [ { "agentId": "a1", "role": "builder", "goal": "do thing" } ] } ],
  "modelProfiles": [ { "id": "profile", "default": { "prefer": "agentic", "provider": "ollama", "model": "llama3.1" } } ]
}
""";
            var command = CreateCommand((_, _, _, _, _, _) =>
                Task.FromResult(new WorkflowCommand.ScenarioExecutionResult(false, "forced failure", 0, 0, false, "executor_failure")));
            var (exitCode, output) = await CaptureConsoleAsync(
                () => command.ExecuteStressAsync(
                    requestOverride: null,
                    specPath: null,
                    specJson: runtimeSpecJson,
                    providerOverride: null,
                    preferOverride: null,
                    iterationsOverride: null,
                    benchmarkSetOverride: null,
                    persistHistoryOverride: false,
                    warmupRunsOverride: null,
                    shuffleScenariosOverride: null,
                    randomSeedOverride: null,
                    cooldownMsOverride: null,
                    includeMeshPeers: false,
                    meshCapability: "ashlar-cli",
                    json: true,
                    verbose: false,
                    ct: cancellationToken)).ConfigureAwait(false);
            /// <summary>Assert equal.</summary>
            AssertEqual(1, exitCode);
            AssertTrue(output.Contains("\"ok\": false", StringComparison.OrdinalIgnoreCase), "Stress should fail when executor fails.");
            AssertTrue(output.Contains("forced failure", StringComparison.OrdinalIgnoreCase), "Stress output should include executor failure summary.");
        }
        finally
        {
            Environment.CurrentDirectory = previousCurrent;
            if (Directory.Exists(repoRoot))
                Directory.Delete(repoRoot, recursive: true);
        }
    }

    private async Task TestReportGeneratesMarkdownBenchmarkOutputAsync(CancellationToken cancellationToken)
    {
        var repoRoot = CreateTempRepoRoot();
        var previousCurrent = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = repoRoot;
            WorkflowLabHistoryStore.Append(repoRoot, new WorkflowLabStressHistoryRow
            {
                ScenarioId = "request-a::composition-a::profile-a::iter-1",
                RequestId = "request-a",
                CompositionId = "composition-a",
                ModelProfileId = "profile-a",
                Iteration = 1,
                Success = true,
                Score = 91.1,
                ElapsedMs = 120,
                AgentCount = 2,
                ConflictCount = 1,
                EscalationCount = 0,
                BenchmarkSet = "workflow-lab"
            });
            WorkflowLabHistoryStore.Append(repoRoot, new WorkflowLabStressHistoryRow
            {
                ScenarioId = "request-a::composition-a::profile-b::iter-1",
                RequestId = "request-a",
                CompositionId = "composition-a",
                ModelProfileId = "profile-b",
                Iteration = 1,
                Success = false,
                Score = 40.0,
                ElapsedMs = 320,
                AgentCount = 2,
                ConflictCount = 2,
                EscalationCount = 1,
                FailureCategory = "orchestration_failure",
                BenchmarkSet = "workflow-lab"
            });

            var reportPath = Path.Combine(repoRoot, "workflow_report.md");
            var command = CreateCommand();
            var (exitCode, output) = await CaptureConsoleAsync(
                () => command.ExecuteReportAsync(
                    repoRoot,
                    limit: 20,
                    benchmarkSet: "workflow-lab",
                    runId: null,
                    baselineRunId: null,
                    since: null,
                    outputPath: reportPath,
                    json: false)).ConfigureAwait(false);

            /// <summary>Assert equal.</summary>
            AssertEqual(0, exitCode);
            AssertTrue(output.Contains("workflow report: ok", StringComparison.OrdinalIgnoreCase), "Report output should indicate success.");
            AssertTrue(File.Exists(reportPath), "Markdown report file should be written.");
            var markdown = await File.ReadAllTextAsync(reportPath, cancellationToken).ConfigureAwait(false);
            AssertTrue(markdown.Contains("# Workflow Stress Benchmark Report", StringComparison.Ordinal));
            AssertTrue(markdown.Contains("## Top Scenarios", StringComparison.OrdinalIgnoreCase));
            AssertTrue(markdown.Contains("## Failure Categories", StringComparison.OrdinalIgnoreCase));
            AssertTrue(markdown.Contains("orchestration_failure", StringComparison.OrdinalIgnoreCase));
            AssertTrue(markdown.Contains("## Recommendations", StringComparison.OrdinalIgnoreCase));
            AssertTrue(markdown.Contains("## Hardware Telemetry", StringComparison.OrdinalIgnoreCase));
            AssertTrue(markdown.Contains("Avg CPU time delta", StringComparison.OrdinalIgnoreCase));
            AssertTrue(markdown.Contains("global_baseline", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Environment.CurrentDirectory = previousCurrent;
            if (Directory.Exists(repoRoot))
                Directory.Delete(repoRoot, recursive: true);
        }
    }

    private async Task TestReportFiltersByRunIdAsync(CancellationToken cancellationToken)
    {
        var repoRoot = CreateTempRepoRoot();
        var previousCurrent = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = repoRoot;
            WorkflowLabHistoryStore.Append(repoRoot, new WorkflowLabStressHistoryRow
            {
                RunId = "run-a",
                GitSha = "abc123",
                SpecHash = "spec-a",
                ProviderSnapshot = "ollama",
                ScenarioId = "request-a::composition-a::profile-a::iter-1",
                RequestId = "request-a",
                CompositionId = "composition-a",
                ModelProfileId = "profile-a",
                Iteration = 1,
                Success = true,
                Score = 90.0,
                ElapsedMs = 100,
                BenchmarkSet = "workflow-lab"
            });
            WorkflowLabHistoryStore.Append(repoRoot, new WorkflowLabStressHistoryRow
            {
                RunId = "run-b",
                GitSha = "def456",
                SpecHash = "spec-b",
                ProviderSnapshot = "ollama",
                ScenarioId = "request-b::composition-b::profile-b::iter-1",
                RequestId = "request-b",
                CompositionId = "composition-b",
                ModelProfileId = "profile-b",
                Iteration = 1,
                Success = false,
                Score = 10.0,
                ElapsedMs = 300,
                FailureCategory = "orchestration_failure",
                BenchmarkSet = "workflow-lab"
            });

            var reportPath = Path.Combine(repoRoot, "workflow_report_run_a.md");
            var command = CreateCommand();
            var (exitCode, output) = await CaptureConsoleAsync(
                () => command.ExecuteReportAsync(
                    repoRoot,
                    limit: 20,
                    benchmarkSet: "workflow-lab",
                    runId: "run-a",
                    baselineRunId: null,
                    since: null,
                    outputPath: reportPath,
                    json: false)).ConfigureAwait(false);

            /// <summary>Assert equal.</summary>
            AssertEqual(0, exitCode);
            AssertTrue(output.Contains("workflow report: ok", StringComparison.OrdinalIgnoreCase));
            var markdown = await File.ReadAllTextAsync(reportPath, cancellationToken).ConfigureAwait(false);
            AssertTrue(markdown.Contains("Run ID: run-a", StringComparison.Ordinal));
            AssertTrue(!markdown.Contains("run-b", StringComparison.OrdinalIgnoreCase), "Report should filter out run-b data.");
        }
        finally
        {
            Environment.CurrentDirectory = previousCurrent;
            if (Directory.Exists(repoRoot))
                Directory.Delete(repoRoot, recursive: true);
        }
    }

    private async Task TestStressClassifiesRuntimeContextFailureFromErrorCodeAsync(CancellationToken cancellationToken)
    {
        var repoRoot = CreateTempRepoRoot();
        var previousCurrent = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = repoRoot;
            const string runtimeSpecJson = """
{
  "execution": { "iterations": 1, "persistHistory": true, "benchmarkSet": "workflow-lab" },
  "requests": [ { "id": "request", "prompt": "Do thing" } ],
  "compositions": [ { "id": "comp", "roles": [ { "agentId": "a1", "role": "builder", "goal": "do thing" } ] } ],
  "modelProfiles": [ { "id": "profile", "default": { "prefer": "agentic", "provider": "ollama", "model": "llama3.1" } } ]
}
""";
            var command = CreateCommand((_, _, _, _, _, _) =>
            {
                return Task.FromResult(new WorkflowCommand.ScenarioExecutionResult(false, "forced failure", 0, 0, false, "runtime_context_failure"));
            });
            var (exitCode, output) = await CaptureConsoleAsync(
                () => command.ExecuteStressAsync(
                    requestOverride: null,
                    specPath: null,
                    specJson: runtimeSpecJson,
                    providerOverride: null,
                    preferOverride: null,
                    iterationsOverride: null,
                    benchmarkSetOverride: null,
                    persistHistoryOverride: true,
                    warmupRunsOverride: null,
                    shuffleScenariosOverride: null,
                    randomSeedOverride: null,
                    cooldownMsOverride: null,
                    includeMeshPeers: false,
                    meshCapability: "ashlar-cli",
                    json: true,
                    verbose: false,
                    ct: cancellationToken)).ConfigureAwait(false);
            /// <summary>Assert equal.</summary>
            AssertEqual(1, exitCode);
            var historyRows = WorkflowLabHistoryStore.ReadRecent(repoRoot, 5);
            /// <summary>Assert true.</summary>
            /// <param name="1">1.</param>
            /// <param name="persisted."">Persisted.".</param>
            AssertTrue(historyRows.Count >= 1, "Expected failure row to be persisted.");
            /// <summary>Assert equal.</summary>
            AssertEqual("runtime_context_failure", historyRows[0].FailureCategory);
            AssertTrue(!string.IsNullOrWhiteSpace(historyRows[0].HardwareProfile), "Expected telemetry hardware profile in persisted history.");
        }
        finally
        {
            Environment.CurrentDirectory = previousCurrent;
            if (Directory.Exists(repoRoot))
                Directory.Delete(repoRoot, recursive: true);
        }
    }

    private async Task TestStressHonorsWarmupShuffleAndCooldownExecutionControlsAsync(CancellationToken cancellationToken)
    {
        var repoRoot = CreateTempRepoRoot();
        var previousCurrent = Environment.CurrentDirectory;
        var calls = new List<string>();
        var command = CreateCommand((request, _, _, _, _, _) =>
        {
            calls.Add(request);
            return Task.FromResult(new WorkflowCommand.ScenarioExecutionResult(true, "ok", 0, 0));
        });

        try
        {
            Environment.CurrentDirectory = repoRoot;
            const string runtimeSpecJson = """
{
  "execution": {
    "iterations": 1,
    "persistHistory": false,
    "benchmarkSet": "workflow-lab",
    "warmupRuns": 1,
    "cooldownMs": 2,
    "shuffleScenarioOrder": true,
    "randomSeed": 1337
  },
  "requests": [
    { "id": "r1", "prompt": "Do one" },
    { "id": "r2", "prompt": "Do two" }
  ],
  "compositions": [
    { "id": "c1", "roles": [ { "agentId": "a1", "role": "builder", "goal": "do thing" } ] },
    { "id": "c2", "roles": [ { "agentId": "a2", "role": "builder", "goal": "do thing" } ] }
  ],
  "modelProfiles": [
    { "id": "p1", "default": { "prefer": "agentic", "provider": "ollama", "model": "llama3.1" } }
  ]
}
""";

            var (exitCode, output) = await CaptureConsoleAsync(
                () => command.ExecuteStressAsync(
                    requestOverride: null,
                    specPath: null,
                    specJson: runtimeSpecJson,
                    providerOverride: null,
                    preferOverride: null,
                    iterationsOverride: null,
                    benchmarkSetOverride: null,
                    persistHistoryOverride: false,
                    warmupRunsOverride: null,
                    shuffleScenariosOverride: null,
                    randomSeedOverride: null,
                    cooldownMsOverride: null,
                    includeMeshPeers: false,
                    meshCapability: "ashlar-cli",
                    json: true,
                    verbose: false,
                    ct: cancellationToken)).ConfigureAwait(false);

            /// <summary>Assert equal.</summary>
            AssertEqual(0, exitCode);
            AssertTrue(output.Contains("\"ok\": true", StringComparison.OrdinalIgnoreCase));
            /// <summary>Assert equal.</summary>
            AssertEqual(8, calls.Count); // 4 scenarios x (1 warmup + 1 measured)
            AssertTrue(calls[0].Contains("Do one", StringComparison.OrdinalIgnoreCase) ||
                       calls[0].Contains("Do two", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Environment.CurrentDirectory = previousCurrent;
            if (Directory.Exists(repoRoot))
                Directory.Delete(repoRoot, recursive: true);
        }
    }

    private async Task TestReportIncludesComparisonSectionAsync(CancellationToken cancellationToken)
    {
        var repoRoot = CreateTempRepoRoot();
        var previousCurrent = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = repoRoot;
            WorkflowLabHistoryStore.Append(repoRoot, new WorkflowLabStressHistoryRow
            {
                RunId = "baseline",
                ScenarioId = "request-a::composition-a::profile-a::iter-1",
                RequestId = "request-a",
                CompositionId = "composition-a",
                ModelProfileId = "profile-a",
                Iteration = 1,
                Success = true,
                Score = 100,
                ElapsedMs = 100,
                BenchmarkSet = "workflow-lab"
            });
            WorkflowLabHistoryStore.Append(repoRoot, new WorkflowLabStressHistoryRow
            {
                RunId = "candidate",
                ScenarioId = "request-a::composition-a::profile-a::iter-1",
                RequestId = "request-a",
                CompositionId = "composition-a",
                ModelProfileId = "profile-a",
                Iteration = 1,
                Success = true,
                Score = 97,
                ElapsedMs = 130,
                BenchmarkSet = "workflow-lab"
            });

            var reportPath = Path.Combine(repoRoot, "workflow_compare.md");
            var command = CreateCommand();
            var (exitCode, output) = await CaptureConsoleAsync(
                () => command.ExecuteReportAsync(
                    repoRoot,
                    limit: 50,
                    benchmarkSet: "workflow-lab",
                    runId: "candidate",
                    baselineRunId: "baseline",
                    since: null,
                    outputPath: reportPath,
                    json: false)).ConfigureAwait(false);

            /// <summary>Assert equal.</summary>
            AssertEqual(0, exitCode);
            AssertTrue(output.Contains("comparison=candidate vs baseline", StringComparison.OrdinalIgnoreCase));
            var markdown = await File.ReadAllTextAsync(reportPath, cancellationToken).ConfigureAwait(false);
            AssertTrue(markdown.Contains("## Comparison", StringComparison.OrdinalIgnoreCase));
            AssertTrue(markdown.Contains("Candidate run: `candidate`", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Environment.CurrentDirectory = previousCurrent;
            if (Directory.Exists(repoRoot))
                Directory.Delete(repoRoot, recursive: true);
        }
    }

    private async Task TestGatePassesAndFailsWithThresholdsAsync()
    {
        var repoRoot = CreateTempRepoRoot();
        try
        {
            WorkflowLabHistoryStore.Append(repoRoot, new WorkflowLabStressHistoryRow
            {
                RunId = "baseline",
                ScenarioId = "req::comp::profile::iter-1",
                RequestId = "req",
                CompositionId = "comp",
                ModelProfileId = "profile",
                Iteration = 1,
                Success = true,
                Score = 100,
                ElapsedMs = 100,
                BenchmarkSet = "workflow-lab"
            });
            WorkflowLabHistoryStore.Append(repoRoot, new WorkflowLabStressHistoryRow
            {
                RunId = "candidate",
                ScenarioId = "req::comp::profile::iter-1",
                RequestId = "req",
                CompositionId = "comp",
                ModelProfileId = "profile",
                Iteration = 1,
                Success = false,
                Score = 70,
                ElapsedMs = 500,
                BenchmarkSet = "workflow-lab"
            });

            var command = CreateCommand();
            var (failingExit, failingOutput) = await CaptureConsoleAsync(
                () => command.ExecuteGateAsync(
                    repoRoot,
                    benchmarkSet: "workflow-lab",
                    runId: "candidate",
                    baselineRunId: "baseline",
                    policyFile: null,
                    minSuccessRateDelta: -0.05,
                    maxP95LatencyRegressionMs: 100,
                    maxAverageLatencyRegressionMs: 100,
                    minAverageScoreDelta: -10,
                    maxRegressedScenarios: 0,
                    json: false)).ConfigureAwait(false);
            /// <summary>Assert equal.</summary>
            AssertEqual(1, failingExit);
            AssertTrue(failingOutput.Contains("workflow gate: failed", StringComparison.OrdinalIgnoreCase));

            var (passingExit, passingOutput) = await CaptureConsoleAsync(
                () => command.ExecuteGateAsync(
                    repoRoot,
                    benchmarkSet: "workflow-lab",
                    runId: "candidate",
                    baselineRunId: "baseline",
                    policyFile: null,
                    minSuccessRateDelta: -1.0,
                    maxP95LatencyRegressionMs: 1000,
                    maxAverageLatencyRegressionMs: 1000,
                    minAverageScoreDelta: -100,
                    maxRegressedScenarios: 10,
                    json: false)).ConfigureAwait(false);
            /// <summary>Assert equal.</summary>
            AssertEqual(0, passingExit);
            AssertTrue(passingOutput.Contains("workflow gate: passed", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (Directory.Exists(repoRoot))
                Directory.Delete(repoRoot, recursive: true);
        }
    }

    private async Task TestBaselinePromoteListAndShowAsync(CancellationToken cancellationToken)
    {
        var repoRoot = CreateTempRepoRoot();
        var previousCurrent = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = repoRoot;
            WorkflowLabHistoryStore.Append(repoRoot, new WorkflowLabStressHistoryRow
            {
                RunId = "run-promote",
                ScenarioId = "request-a::composition-a::profile-a::iter-1",
                RequestId = "request-a",
                CompositionId = "composition-a",
                ModelProfileId = "profile-a",
                Iteration = 1,
                Success = true,
                Score = 95.5,
                ElapsedMs = 110,
                BenchmarkSet = "workflow-lab"
            });

            var command = CreateCommand();
            var (promoteExit, promoteOutput) = await CaptureConsoleAsync(
                () => command.ExecuteBaselinePromoteAsync(
                    repoRoot: repoRoot,
                    benchmarkSet: "workflow-lab",
                    runId: "run-promote",
                    notes: "promotion test",
                    policyFile: null,
                    json: false)).ConfigureAwait(false);
            /// <summary>Assert equal.</summary>
            AssertEqual(0, promoteExit);
            AssertTrue(promoteOutput.Contains("workflow baseline promote: ok", StringComparison.OrdinalIgnoreCase));

            var (listExit, listOutput) = await CaptureConsoleAsync(
                () => command.ExecuteBaselineListAsync(
                    repoRoot: repoRoot,
                    benchmarkSet: "workflow-lab",
                    json: false)).ConfigureAwait(false);
            /// <summary>Assert equal.</summary>
            AssertEqual(0, listExit);
            AssertTrue(listOutput.Contains("run-promote", StringComparison.OrdinalIgnoreCase));

            var (showExit, showOutput) = await CaptureConsoleAsync(
                () => command.ExecuteBaselineShowAsync(
                    repoRoot: repoRoot,
                    benchmarkSet: "workflow-lab",
                    baselineId: null,
                    json: false)).ConfigureAwait(false);
            /// <summary>Assert equal.</summary>
            AssertEqual(0, showExit);
            AssertTrue(showOutput.Contains("run-id=run-promote", StringComparison.OrdinalIgnoreCase));

            var baselinePath = WorkflowBaselineStore.GetPath(repoRoot);
            AssertTrue(File.Exists(baselinePath), "Expected baseline registry file to be created.");
            var baselineContent = await File.ReadAllTextAsync(baselinePath, cancellationToken).ConfigureAwait(false);
            AssertTrue(baselineContent.Contains("run-promote", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Environment.CurrentDirectory = previousCurrent;
            if (Directory.Exists(repoRoot))
                Directory.Delete(repoRoot, recursive: true);
        }
    }

    private async Task TestGateUsesPolicyFileAndActiveBaselineAsync(CancellationToken cancellationToken)
    {
        var repoRoot = CreateTempRepoRoot();
        var previousCurrent = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = repoRoot;
            WorkflowLabHistoryStore.Append(repoRoot, new WorkflowLabStressHistoryRow
            {
                RunId = "baseline",
                ScenarioId = "req::comp::profile::iter-1",
                RequestId = "req",
                CompositionId = "comp",
                ModelProfileId = "profile",
                Iteration = 1,
                Success = true,
                Score = 100,
                ElapsedMs = 100,
                BenchmarkSet = "workflow-lab"
            });
            WorkflowLabHistoryStore.Append(repoRoot, new WorkflowLabStressHistoryRow
            {
                RunId = "candidate",
                ScenarioId = "req::comp::profile::iter-1",
                RequestId = "req",
                CompositionId = "comp",
                ModelProfileId = "profile",
                Iteration = 1,
                Success = true,
                Score = 99,
                ElapsedMs = 101,
                BenchmarkSet = "workflow-lab"
            });

            var command = CreateCommand();
            var promoteExit = await command.ExecuteBaselinePromoteAsync(
                repoRoot: repoRoot,
                benchmarkSet: "workflow-lab",
                runId: "baseline",
                notes: null,
                policyFile: null,
                json: true).ConfigureAwait(false);
            /// <summary>Assert equal.</summary>
            AssertEqual(0, promoteExit);

            var policyPath = Path.Combine(repoRoot, ".ashlar", "workflow", "gate_policy.json");
            Directory.CreateDirectory(Path.GetDirectoryName(policyPath)!);
            await File.WriteAllTextAsync(policyPath, """
{
  "benchmarkSet": "workflow-lab",
  "minSuccessRateDelta": -1.0,
  "maxP95LatencyRegressionMs": 10,
  "maxAverageLatencyRegressionMs": 10,
  "minAverageScoreDelta": -5.0,
  "maxRegressedScenarios": 10
}
""", cancellationToken).ConfigureAwait(false);

            var (gateExit, gateOutput) = await CaptureConsoleAsync(
                () => command.ExecuteGateAsync(
                    repoRoot: repoRoot,
                    benchmarkSet: "workflow-lab",
                    runId: "candidate",
                    baselineRunId: null,
                    policyFile: policyPath,
                    minSuccessRateDelta: -1.0,
                    maxP95LatencyRegressionMs: 10,
                    maxAverageLatencyRegressionMs: 10,
                    minAverageScoreDelta: -5.0,
                    maxRegressedScenarios: 10,
                    json: false)).ConfigureAwait(false);
            if (gateExit != 0)
                /// <summary>Assertion exception.</summary>
                /// <param name="{gateOutput}"">{gate output}".</param>
                throw new AssertionException($"Expected gate exit 0 but got {gateExit}. Output: {gateOutput}");
            AssertTrue(gateOutput.Contains("workflow gate: passed", StringComparison.OrdinalIgnoreCase) ||
                       gateOutput.Contains("\"passed\": true", StringComparison.OrdinalIgnoreCase));
            AssertTrue(gateOutput.Contains("comparison=candidate vs baseline", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Environment.CurrentDirectory = previousCurrent;
            if (Directory.Exists(repoRoot))
                Directory.Delete(repoRoot, recursive: true);
        }
    }

    private async Task TestOptimizeGeneratesRecommendationReportAsync(CancellationToken cancellationToken)
    {
        var repoRoot = CreateTempRepoRoot();
        var previousCurrent = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = repoRoot;
            const string runtimeSpecJson = """
{
  "execution": {
    "iterations": 1,
    "persistHistory": true,
    "benchmarkSet": "workflow-lab"
  },
  "requests": [
    { "id": "req-a", "prompt": "Plan and implement feature A." }
  ],
  "compositions": [
    {
      "id": "comp-fast",
      "roles": [
        { "agentId": "planner-1", "role": "planner", "goal": "Plan" }
      ]
    },
    {
      "id": "comp-thorough",
      "roles": [
        { "agentId": "planner-2", "role": "planner", "goal": "Plan thoroughly" }
      ]
    }
  ],
  "modelProfiles": [
    {
      "id": "profile-a",
      "default": { "prefer": "agentic", "provider": "ollama", "model": "llama3.1" }
    }
  ]
}
""";

            var command = CreateCommandWithPreflight(
                (request, _, _, _, _, _) =>
            {
                var ok = request.Contains("feature A", StringComparison.OrdinalIgnoreCase);
                return Task.FromResult(new WorkflowCommand.ScenarioExecutionResult(ok, ok ? "ok" : "fail", 0, 0));
            },
                (_, _) => Task.FromResult(true),
                (models, _) => Task.FromResult(new WorkflowCommand.ModelPullResult(
                    Ok: true,
                    Summary: $"stub pull ok ({models.Count})",
                    Models: models,
                    PulledModels: models)));
            var reportPath = Path.Combine(repoRoot, "workflow_optimize_report.md");
            var (exitCode, output) = await CaptureConsoleAsync(
                () => command.ExecuteOptimizeAsync(
                    requestOverride: null,
                    objective: null,
                    objectiveFile: null,
                    specPath: null,
                    specJson: runtimeSpecJson,
                    providerOverride: null,
                    preferOverride: null,
                    iterationsOverride: null,
                    benchmarkSetOverride: "workflow-lab",
                    persistHistoryOverride: true,
                    warmupRunsOverride: 0,
                    shuffleScenariosOverride: false,
                    randomSeedOverride: 7,
                    cooldownMsOverride: 0,
                    maxCandidates: 8,
                    budgetRuns: null,
                    searchStrategy: "successive-halving",
                    earlyStopMinRuns: 2,
                    earlyStopMinSuccessRate: 0.35,
                    includeMeshPeers: false,
                    meshCapability: "ashlar-cli",
                    autoPullModels: false,
                    promoteWinner: false,
                    policyFile: null,
                    reportOutputPath: reportPath,
                    json: false,
                    verbose: false,
                    ct: cancellationToken)).ConfigureAwait(false);

            /// <summary>Assert equal.</summary>
            AssertEqual(0, exitCode);
            AssertTrue(output.Contains("workflow optimize: ok", StringComparison.OrdinalIgnoreCase));
            AssertTrue(output.Contains("recommendation-report=", StringComparison.OrdinalIgnoreCase));
            AssertTrue(File.Exists(reportPath), "Expected optimize recommendation report to be written.");

            var report = await File.ReadAllTextAsync(reportPath, cancellationToken).ConfigureAwait(false);
            AssertTrue(report.Contains("# Workflow Optimize Recommendation Report", StringComparison.OrdinalIgnoreCase));
            AssertTrue(report.Contains("## Winner", StringComparison.OrdinalIgnoreCase));
            AssertTrue(report.Contains("## Recommendations", StringComparison.OrdinalIgnoreCase));
            AssertTrue(report.Contains("Hardware profile", StringComparison.OrdinalIgnoreCase));
            AssertTrue(report.Contains("Avg CPU delta", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Environment.CurrentDirectory = previousCurrent;
            if (Directory.Exists(repoRoot))
                Directory.Delete(repoRoot, recursive: true);
        }
    }

    private async Task TestOptimizeAutoPromotesWinnerBaselineAsync(CancellationToken cancellationToken)
    {
        var repoRoot = CreateTempRepoRoot();
        var previousCurrent = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = repoRoot;
            const string runtimeSpecJson = """
{
  "execution": {
    "iterations": 1,
    "persistHistory": true,
    "benchmarkSet": "workflow-lab"
  },
  "requests": [
    { "id": "req-a", "prompt": "Deliver feature A." }
  ],
  "compositions": [
    {
      "id": "comp-a",
      "roles": [
        { "agentId": "builder-1", "role": "builder", "goal": "Build" }
      ]
    }
  ],
  "modelProfiles": [
    {
      "id": "profile-a",
      "default": { "prefer": "agentic", "provider": "ollama", "model": "llama3.1" }
    }
  ]
}
""";

            var command = CreateCommandWithPreflight(
                (_, _, _, _, _, _) => Task.FromResult(new WorkflowCommand.ScenarioExecutionResult(true, "ok", 0, 0)),
                (_, _) => Task.FromResult(true),
                (models, _) => Task.FromResult(new WorkflowCommand.ModelPullResult(
                    Ok: true,
                    Summary: $"stub pull ok ({models.Count})",
                    Models: models,
                    PulledModels: models)));
            var reportPath = Path.Combine(repoRoot, "workflow_optimize_report_promote.md");
            var (exitCode, output) = await CaptureConsoleAsync(
                () => command.ExecuteOptimizeAsync(
                    requestOverride: null,
                    objective: null,
                    objectiveFile: null,
                    specPath: null,
                    specJson: runtimeSpecJson,
                    providerOverride: null,
                    preferOverride: null,
                    iterationsOverride: 2,
                    benchmarkSetOverride: "workflow-lab",
                    persistHistoryOverride: true,
                    warmupRunsOverride: 0,
                    shuffleScenariosOverride: false,
                    randomSeedOverride: null,
                    cooldownMsOverride: 0,
                    maxCandidates: 4,
                    budgetRuns: null,
                    searchStrategy: "exhaustive",
                    earlyStopMinRuns: 2,
                    earlyStopMinSuccessRate: 0.35,
                    includeMeshPeers: false,
                    meshCapability: "ashlar-cli",
                    autoPullModels: false,
                    promoteWinner: true,
                    policyFile: null,
                    reportOutputPath: reportPath,
                    json: false,
                    verbose: false,
                    ct: cancellationToken)).ConfigureAwait(false);

            /// <summary>Assert equal.</summary>
            AssertEqual(0, exitCode);
            AssertTrue(output.Contains("workflow optimize: ok", StringComparison.OrdinalIgnoreCase));
            AssertTrue(output.Contains("promoted-baseline-id=", StringComparison.OrdinalIgnoreCase));

            var active = WorkflowBaselineStore.ReadActive(repoRoot, "workflow-lab");
            /// <summary>Assert true.</summary>
            /// <param name="null">Null.</param>
            /// <param name="baseline."">Baseline.".</param>
            AssertTrue(active is not null, "Expected optimize to auto-promote winner baseline.");
            AssertTrue(!string.IsNullOrWhiteSpace(active!.RunId), "Promoted baseline should have run-id.");
            AssertTrue(File.Exists(reportPath), "Expected optimize promotion report to be written.");
        }
        finally
        {
            Environment.CurrentDirectory = previousCurrent;
            if (Directory.Exists(repoRoot))
                Directory.Delete(repoRoot, recursive: true);
        }
    }

    private async Task TestOptimizeInvokesModelPullerWithResolvedModelsAsync(CancellationToken cancellationToken)
    {
        var repoRoot = CreateTempRepoRoot();
        var previousCurrent = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = repoRoot;
            const string runtimeSpecJson = """
{
  "execution": {
    "iterations": 1,
    "persistHistory": false,
    "benchmarkSet": "workflow-lab"
  },
  "requests": [
    { "id": "req-a", "prompt": "Deliver feature A." }
  ],
  "compositions": [
    {
      "id": "comp-a",
      "roles": [
        { "agentId": "planner-1", "role": "planner", "goal": "Plan", "ollamaModel": "qwen2.5:7b" }
      ]
    }
  ],
  "modelProfiles": [
    {
      "id": "profile-a",
      "default": { "prefer": "agentic", "provider": "ollama", "model": "llama3.1" },
      "agents": {
        "planner-1": { "prefer": "agentic", "provider": "ollama", "model": "qwen2.5:7b" }
      }
    }
  ]
}
""";

            IReadOnlyList<string>? pulledModels = null;
            var command = CreateCommandWithPreflightAndPuller(
                (_, _, _, _, _, _) => Task.FromResult(new WorkflowCommand.ScenarioExecutionResult(true, "ok", 0, 0)),
                (_, _) => Task.FromResult(true),
                (models, _) =>
                {
                    pulledModels = models.ToArray();
                    return Task.FromResult(new WorkflowCommand.ModelPullResult(
                        Ok: true,
                        Summary: "pulled",
                        Models: models.ToArray(),
                        PulledModels: models.ToArray(),
                        FailedModels: Array.Empty<string>()));
                });

            var (exitCode, output) = await CaptureConsoleAsync(
                () => command.ExecuteOptimizeAsync(
                    requestOverride: null,
                    objective: null,
                    objectiveFile: null,
                    specPath: null,
                    specJson: runtimeSpecJson,
                    providerOverride: null,
                    preferOverride: null,
                    iterationsOverride: null,
                    benchmarkSetOverride: "workflow-lab",
                    persistHistoryOverride: false,
                    warmupRunsOverride: 0,
                    shuffleScenariosOverride: false,
                    randomSeedOverride: null,
                    cooldownMsOverride: 0,
                    maxCandidates: 4,
                    budgetRuns: null,
                    searchStrategy: "successive-halving",
                    earlyStopMinRuns: 2,
                    earlyStopMinSuccessRate: 0.35,
                    includeMeshPeers: false,
                    meshCapability: "ashlar-cli",
                    autoPullModels: true,
                    promoteWinner: false,
                    policyFile: null,
                    reportOutputPath: null,
                    json: true,
                    verbose: false,
                    ct: cancellationToken)).ConfigureAwait(false);

            /// <summary>Assert equal.</summary>
            AssertEqual(0, exitCode);
            AssertTrue(output.Contains("\"ok\": true", StringComparison.OrdinalIgnoreCase));
            /// <summary>Assert true.</summary>
            /// <param name="null">Null.</param>
            /// <param name="puller."">Puller.".</param>
            AssertTrue(pulledModels is not null, "Expected optimize to invoke model puller.");
            var resolvedPulledModels = pulledModels ?? Array.Empty<string>();
            AssertTrue(
                resolvedPulledModels.Any(m => m.StartsWith("llama3.1", StringComparison.OrdinalIgnoreCase)),
                "Expected default Ollama model family to be pulled.");
            AssertTrue(resolvedPulledModels.Contains("qwen2.5:7b", StringComparer.OrdinalIgnoreCase), "Expected role-specific Ollama model to be pulled.");
        }
        finally
        {
            Environment.CurrentDirectory = previousCurrent;
            if (Directory.Exists(repoRoot))
                Directory.Delete(repoRoot, recursive: true);
        }
    }

    private async Task TestOptimizeResolvesObjectiveFileAndReportsSearchMetadataAsync(CancellationToken cancellationToken)
    {
        var repoRoot = CreateTempRepoRoot();
        var previousCurrent = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = repoRoot;
            const string runtimeSpecJson = """
{
  "execution": {
    "iterations": 3,
    "persistHistory": false,
    "benchmarkSet": "workflow-lab"
  },
  "requests": [
    { "id": "req-latency", "prompt": "Optimize latency for planner pipeline." }
  ],
  "compositions": [
    {
      "id": "comp-planner",
      "roles": [
        { "agentId": "planner-1", "role": "planner", "goal": "Plan quickly" }
      ]
    }
  ],
  "modelProfiles": [
    {
      "id": "profile-fast",
      "default": { "prefer": "agentic", "provider": "ollama", "model": "llama3.1" }
    }
  ]
}
""";
            var objectiveFile = Path.Combine(repoRoot, "objective.txt");
            await File.WriteAllTextAsync(objectiveFile, "optimize latency planner pipeline", cancellationToken).ConfigureAwait(false);

            var command = CreateCommandWithPreflight(
                (_, _, _, _, _, _) => Task.FromResult(new WorkflowCommand.ScenarioExecutionResult(true, "ok", 0, 0)),
                (_, _) => Task.FromResult(true));

            var reportPath = Path.Combine(repoRoot, "workflow_optimize_objective_report.json");
            var (exitCode, output) = await CaptureConsoleAsync(
                () => command.ExecuteOptimizeAsync(
                    requestOverride: null,
                    objective: null,
                    objectiveFile: objectiveFile,
                    specPath: null,
                    specJson: runtimeSpecJson,
                    providerOverride: null,
                    preferOverride: null,
                    iterationsOverride: null,
                    benchmarkSetOverride: "workflow-lab",
                    persistHistoryOverride: false,
                    warmupRunsOverride: 0,
                    shuffleScenariosOverride: false,
                    randomSeedOverride: 11,
                    cooldownMsOverride: 0,
                    maxCandidates: 4,
                    budgetRuns: 2,
                    searchStrategy: "objective-first",
                    earlyStopMinRuns: 2,
                    earlyStopMinSuccessRate: 0.0,
                    includeMeshPeers: false,
                    meshCapability: "ashlar-cli",
                    autoPullModels: false,
                    promoteWinner: false,
                    policyFile: null,
                    reportOutputPath: reportPath,
                    json: true,
                    verbose: false,
                    ct: cancellationToken)).ConfigureAwait(false);

            /// <summary>Assert equal.</summary>
            AssertEqual(0, exitCode);
            AssertTrue(output.Contains("\"searchStrategy\": \"objective-first\"", StringComparison.OrdinalIgnoreCase));
            AssertTrue(output.Contains("\"measuredRunsUsed\": 2", StringComparison.OrdinalIgnoreCase));
            AssertTrue(output.Contains("\"objectiveFile\":", StringComparison.OrdinalIgnoreCase));
            AssertTrue(File.Exists(reportPath), "Expected objective report to be generated.");

            var report = await File.ReadAllTextAsync(reportPath, cancellationToken).ConfigureAwait(false);
            AssertTrue(report.Contains("\"optimizeExecution\"", StringComparison.OrdinalIgnoreCase));
            AssertTrue(report.Contains("\"searchStrategy\": \"objective-first\"", StringComparison.OrdinalIgnoreCase));
            AssertTrue(report.Contains("\"measuredRunBudget\": 2", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Environment.CurrentDirectory = previousCurrent;
            if (Directory.Exists(repoRoot))
                Directory.Delete(repoRoot, recursive: true);
        }
    }

    private async Task TestOptimizeHonorsBudgetAndEarlyStopAsync(CancellationToken cancellationToken)
    {
        var repoRoot = CreateTempRepoRoot();
        var previousCurrent = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = repoRoot;
            const string runtimeSpecJson = """
{
  "execution": {
    "iterations": 5,
    "persistHistory": false,
    "benchmarkSet": "workflow-lab"
  },
  "requests": [
    { "id": "req-fail", "prompt": "Run failing scenario." }
  ],
  "compositions": [
    {
      "id": "comp-fail",
      "roles": [
        { "agentId": "builder-1", "role": "builder", "goal": "Build" }
      ]
    }
  ],
  "modelProfiles": [
    {
      "id": "profile-fail",
      "default": { "prefer": "agentic", "provider": "ollama", "model": "llama3.1" }
    }
  ]
}
""";

            var executions = 0;
            var command = CreateCommandWithPreflight(
                (_, _, _, _, _, _) =>
                {
                    executions++;
                    return Task.FromResult(new WorkflowCommand.ScenarioExecutionResult(false, "failed", 0, 0));
                },
                (_, _) => Task.FromResult(true));

            var (exitCode, output) = await CaptureConsoleAsync(
                () => command.ExecuteOptimizeAsync(
                    requestOverride: null,
                    objective: "reliability first",
                    objectiveFile: null,
                    specPath: null,
                    specJson: runtimeSpecJson,
                    providerOverride: null,
                    preferOverride: null,
                    iterationsOverride: null,
                    benchmarkSetOverride: "workflow-lab",
                    persistHistoryOverride: false,
                    warmupRunsOverride: 0,
                    shuffleScenariosOverride: false,
                    randomSeedOverride: null,
                    cooldownMsOverride: 0,
                    maxCandidates: 4,
                    budgetRuns: 4,
                    searchStrategy: "exhaustive",
                    earlyStopMinRuns: 2,
                    earlyStopMinSuccessRate: 0.8,
                    includeMeshPeers: false,
                    meshCapability: "ashlar-cli",
                    autoPullModels: false,
                    promoteWinner: false,
                    policyFile: null,
                    reportOutputPath: null,
                    json: true,
                    verbose: false,
                    ct: cancellationToken)).ConfigureAwait(false);

            /// <summary>Assert equal.</summary>
            AssertEqual(1, exitCode);
            /// <summary>Assert equal.</summary>
            AssertEqual(2, executions);
            AssertTrue(output.Contains("\"measuredRunsUsed\": 2", StringComparison.OrdinalIgnoreCase));
            AssertTrue(output.Contains("\"earlyStopMinRuns\": 2", StringComparison.OrdinalIgnoreCase));
            AssertTrue(output.Contains("\"earlyStopMinSuccessRate\": 0.8", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Environment.CurrentDirectory = previousCurrent;
            if (Directory.Exists(repoRoot))
                Directory.Delete(repoRoot, recursive: true);
        }
    }

    private async Task TestGateIgnoresSkippedRunsInSuccessRateAsync()
    {
        var repoRoot = CreateTempRepoRoot();
        var previousCurrent = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = repoRoot;
            WorkflowLabHistoryStore.Append(repoRoot, new WorkflowLabStressHistoryRow
            {
                RunId = "baseline",
                ScenarioId = "req::comp::profile::iter-1",
                RequestId = "req",
                CompositionId = "comp",
                ModelProfileId = "profile",
                Iteration = 1,
                Success = true,
                Skipped = false,
                Score = 100,
                ElapsedMs = 100,
                BenchmarkSet = "workflow-lab"
            });

            WorkflowLabHistoryStore.Append(repoRoot, new WorkflowLabStressHistoryRow
            {
                RunId = "candidate",
                ScenarioId = "req::comp::profile::iter-1",
                RequestId = "req",
                CompositionId = "comp",
                ModelProfileId = "profile",
                Iteration = 1,
                Success = false,
                Skipped = true,
                Score = 0,
                ElapsedMs = 50,
                BenchmarkSet = "workflow-lab",
                FailureCategory = "skipped_infra",
                Summary = "Skipped due to provider preflight failure"
            });

            var command = CreateCommand();
            var (exitCode, output) = await CaptureConsoleAsync(
                () => command.ExecuteGateAsync(
                    repoRoot,
                    benchmarkSet: "workflow-lab",
                    runId: "candidate",
                    baselineRunId: "baseline",
                    policyFile: null,
                    minSuccessRateDelta: -0.1,
                    maxP95LatencyRegressionMs: 1000,
                    maxAverageLatencyRegressionMs: 1000,
                    minAverageScoreDelta: -100,
                    maxRegressedScenarios: 10,
                    json: true)).ConfigureAwait(false);

            /// <summary>Assert equal.</summary>
            AssertEqual(1, exitCode);
            AssertTrue(
                output.Contains("\"valid\": false", StringComparison.OrdinalIgnoreCase),
                "Expected gate result JSON to report invalid comparison for skipped-only candidate data.");
        }
        finally
        {
            Environment.CurrentDirectory = previousCurrent;
            if (Directory.Exists(repoRoot))
                Directory.Delete(repoRoot, recursive: true);
        }
    }

    private async Task TestOptimizeSynthesizesObjectiveCandidatesAndReportsProvenanceAsync(CancellationToken cancellationToken)
    {
        var repoRoot = CreateTempRepoRoot();
        var previousCurrent = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = repoRoot;
            const string runtimeSpecJson = """
{
  "execution": {
    "iterations": 1,
    "persistHistory": false,
    "benchmarkSet": "workflow-lab"
  },
  "requests": [
    { "id": "req-opt", "prompt": "Optimize latency and quality for planner pipeline." }
  ],
  "compositions": [
    {
      "id": "comp-opt",
      "roles": [
        { "agentId": "planner-1", "role": "planner", "goal": "Plan quickly and correctly" },
        { "agentId": "builder-1", "role": "builder", "goal": "Implement with quality" }
      ]
    }
  ],
  "modelProfiles": [
    {
      "id": "profile-opt",
      "default": { "prefer": "agentic", "provider": "ollama", "model": "llama3.1" }
    }
  ]
}
""";
            var reportPath = Path.Combine(repoRoot, "workflow_optimize_synth_report.md");
            var command = CreateCommandWithPreflight(
                (request, _, _, _, _, _) =>
                {
                    var synthesized = request.Contains("Optimization objective focus", StringComparison.OrdinalIgnoreCase);
                    return Task.FromResult(new WorkflowCommand.ScenarioExecutionResult(
                        Ok: synthesized,
                        Summary: synthesized ? "synthesized-candidate-ok" : "baseline-candidate-failed",
                        ConflictCount: 0,
                        EscalationCount: 0));
                },
                (_, _) => Task.FromResult(true));

            var (exitCode, output) = await CaptureConsoleAsync(
                () => command.ExecuteOptimizeAsync(
                    requestOverride: null,
                    objective: "latency quality planner builder",
                    objectiveFile: null,
                    specPath: null,
                    specJson: runtimeSpecJson,
                    providerOverride: null,
                    preferOverride: null,
                    iterationsOverride: null,
                    benchmarkSetOverride: "workflow-lab",
                    persistHistoryOverride: false,
                    warmupRunsOverride: 0,
                    shuffleScenariosOverride: false,
                    randomSeedOverride: 19,
                    cooldownMsOverride: 0,
                    maxCandidates: 4,
                    budgetRuns: 2,
                    searchStrategy: "objective-first",
                    earlyStopMinRuns: 1,
                    earlyStopMinSuccessRate: 0.0,
                    includeMeshPeers: false,
                    meshCapability: "ashlar-cli",
                    autoPullModels: false,
                    promoteWinner: false,
                    policyFile: null,
                    reportOutputPath: reportPath,
                    json: true,
                    verbose: false,
                    ct: cancellationToken)).ConfigureAwait(false);

            /// <summary>Assert equal.</summary>
            AssertEqual(0, exitCode);
            AssertTrue(output.Contains("\"ok\": true", StringComparison.OrdinalIgnoreCase));
            AssertTrue(output.Contains("\"synthesizedCandidateCount\": 1", StringComparison.OrdinalIgnoreCase));
            AssertTrue(output.Contains("\"synthesized\": true", StringComparison.OrdinalIgnoreCase));
            AssertTrue(output.Contains("\"objectiveScore\":", StringComparison.OrdinalIgnoreCase));
            AssertTrue(output.Contains("\"synthesisRationale\":", StringComparison.OrdinalIgnoreCase));
            AssertTrue(File.Exists(reportPath), "Expected synthesized optimize report to be written.");

            var report = await File.ReadAllTextAsync(reportPath, cancellationToken).ConfigureAwait(false);
            AssertTrue(report.Contains("Synthesized candidates: 1", StringComparison.OrdinalIgnoreCase));
            AssertTrue(report.Contains("Synthesis rationale", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Environment.CurrentDirectory = previousCurrent;
            if (Directory.Exists(repoRoot))
                Directory.Delete(repoRoot, recursive: true);
        }
    }

    private async Task TestOptimizeAdaptiveAllocationAndConfidenceGatingAsync(CancellationToken cancellationToken)
    {
        var repoRoot = CreateTempRepoRoot();
        var previousCurrent = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = repoRoot;
            const string runtimeSpecJson = """
{
  "execution": {
    "iterations": 1,
    "persistHistory": true,
    "benchmarkSet": "workflow-lab"
  },
  "requests": [
    { "id": "req-adaptive", "prompt": "Optimize latency and throughput with high quality." }
  ],
  "compositions": [
    {
      "id": "comp-adaptive",
      "roles": [
        { "agentId": "planner-1", "role": "planner", "goal": "Plan quickly" }
      ]
    }
  ],
  "modelProfiles": [
    {
      "id": "profile-adaptive",
      "default": { "prefer": "agentic", "provider": "ollama", "model": "llama3.1" }
    }
  ]
}
""";

            var command = CreateCommandWithPreflight(
                (request, _, _, _, _, _) =>
                {
                    var synthesized = request.Contains("Optimization objective focus", StringComparison.OrdinalIgnoreCase) ||
                                      request.Contains("Adaptive follow-up", StringComparison.OrdinalIgnoreCase);
                    if (synthesized)
                    {
                        return Task.FromResult(new WorkflowCommand.ScenarioExecutionResult(
                            Ok: true,
                            Summary: "adaptive-pass",
                            ConflictCount: 0,
                            EscalationCount: 0));
                    }

                    return Task.FromResult(new WorkflowCommand.ScenarioExecutionResult(
                        Ok: false,
                        Summary: "base-fail",
                        ConflictCount: 0,
                        EscalationCount: 0,
                        FailureCategory: "executor_failure"));
                },
                (_, _) => Task.FromResult(true));

            var reportPath = Path.Combine(repoRoot, "workflow_optimize_adaptive_report.json");
            var (exitCode, output) = await CaptureConsoleAsync(
                () => command.ExecuteOptimizeAsync(
                    requestOverride: null,
                    objective: "latency throughput quality",
                    objectiveFile: null,
                    specPath: null,
                    specJson: runtimeSpecJson,
                    providerOverride: null,
                    preferOverride: null,
                    iterationsOverride: null,
                    benchmarkSetOverride: "workflow-lab",
                    persistHistoryOverride: true,
                    warmupRunsOverride: 0,
                    shuffleScenariosOverride: false,
                    randomSeedOverride: 7,
                    cooldownMsOverride: 0,
                    maxCandidates: 5,
                    budgetRuns: 3,
                    searchStrategy: "objective-first",
                    earlyStopMinRuns: 2,
                    earlyStopMinSuccessRate: 0.5,
                    includeMeshPeers: false,
                    meshCapability: "ashlar-cli",
                    autoPullModels: false,
                    promoteWinner: true,
                    policyFile: null,
                    reportOutputPath: reportPath,
                    json: true,
                    verbose: false,
                    ct: cancellationToken)).ConfigureAwait(false);

            /// <summary>Assert equal.</summary>
            AssertEqual(0, exitCode);
            AssertTrue(output.Contains("\"winnerConfidence\":", StringComparison.OrdinalIgnoreCase));
            AssertTrue(output.Contains("\"promotionConfidenceThreshold\":", StringComparison.OrdinalIgnoreCase));
            AssertTrue(output.Contains("\"allocationTrace\":", StringComparison.OrdinalIgnoreCase));
            AssertTrue(output.Contains("\"targetAllocations\":", StringComparison.OrdinalIgnoreCase));
            AssertTrue(output.Contains("\"candidateAllocations\":", StringComparison.OrdinalIgnoreCase));
            AssertTrue(output.Contains("\"adaptiveSynthesizedCandidateCount\":", StringComparison.OrdinalIgnoreCase));
            AssertTrue(output.Contains("\"kind\": \"sample-size\"", StringComparison.OrdinalIgnoreCase));
            AssertTrue(output.Contains("minimum 2 run(s) required before promotion", StringComparison.OrdinalIgnoreCase));
            AssertTrue(output.Contains("Promotion skipped: winner has 1 measured run(s), requires at least 2.", StringComparison.OrdinalIgnoreCase));
            AssertTrue(File.Exists(reportPath), "Expected adaptive optimize report to be written.");

            var report = await File.ReadAllTextAsync(reportPath, cancellationToken).ConfigureAwait(false);
            AssertTrue(report.Contains("\"winnerConfidence\":", StringComparison.OrdinalIgnoreCase));
            AssertTrue(report.Contains("\"allocation\":", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Environment.CurrentDirectory = previousCurrent;
            if (Directory.Exists(repoRoot))
                Directory.Delete(repoRoot, recursive: true);
        }
    }

    private async Task TestStressTreatsRefusedPreflightAsFailureNotSkippedSuccessAsync(CancellationToken cancellationToken)
    {
        var repoRoot = CreateTempRepoRoot();
        var previousCurrent = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = repoRoot;
            const string runtimeSpecJson = """
{
  "execution": { "iterations": 1, "persistHistory": false, "benchmarkSet": "workflow-lab" },
  "requests": [ { "id": "request", "prompt": "Do thing" } ],
  "compositions": [ { "id": "comp", "roles": [ { "agentId": "a1", "role": "builder", "goal": "do thing" } ] } ],
  "modelProfiles": [ { "id": "profile", "default": { "prefer": "agentic", "provider": "ollama", "model": "llama3.1" } } ]
}
""";
            var refusal = CreateTestRefusal();
            var executorCalls = 0;
            var command = CreateCommandWithTypedPreflight(
                (_, _, _, _, _, _) =>
                {
                    executorCalls++;
                    return Task.FromResult(new WorkflowCommand.ScenarioExecutionResult(true, "should-not-run", 0, 0));
                },
                (provider, _) => Task.FromResult(new WorkflowCommand.PreflightResult(
                    Ok: false,
                    Provider: provider,
                    Detail: refusal.Message,
                    Refusal: refusal)));

            var (exitCode, output) = await CaptureConsoleAsync(
                () => command.ExecuteStressAsync(
                    requestOverride: null,
                    specPath: null,
                    specJson: runtimeSpecJson,
                    providerOverride: null,
                    preferOverride: null,
                    iterationsOverride: null,
                    benchmarkSetOverride: null,
                    persistHistoryOverride: false,
                    warmupRunsOverride: 0,
                    shuffleScenariosOverride: false,
                    randomSeedOverride: null,
                    cooldownMsOverride: 0,
                    includeMeshPeers: false,
                    meshCapability: "ashlar-cli",
                    json: true,
                    verbose: false,
                    ct: cancellationToken)).ConfigureAwait(false);

            AssertEqual(1, exitCode);
            AssertEqual(0, executorCalls);
            AssertTrue(output.Contains("\"ok\": false", StringComparison.OrdinalIgnoreCase), output);
            AssertTrue(output.Contains("egress_refused", StringComparison.OrdinalIgnoreCase), output);
            AssertTrue(output.Contains(refusal.Ref, StringComparison.Ordinal), output);
            AssertTrue(output.Contains("Refused by egress policy", StringComparison.OrdinalIgnoreCase), output);
            AssertTrue(!output.Contains("\"skipped\": true", StringComparison.OrdinalIgnoreCase)
                || output.Contains("\"skipped\": false", StringComparison.OrdinalIgnoreCase), output);
        }
        finally
        {
            Environment.CurrentDirectory = previousCurrent;
            if (Directory.Exists(repoRoot))
                Directory.Delete(repoRoot, recursive: true);
        }
    }

    private async Task TestOptimizeSurfacesRefusalReferenceInResultAsync(CancellationToken cancellationToken)
    {
        var repoRoot = CreateTempRepoRoot();
        var previousCurrent = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = repoRoot;
            const string runtimeSpecJson = """
{
  "execution": { "iterations": 1, "persistHistory": false, "benchmarkSet": "workflow-lab" },
  "requests": [ { "id": "req-a", "prompt": "Deliver feature A." } ],
  "compositions": [ { "id": "comp-a", "roles": [ { "agentId": "builder-1", "role": "builder", "goal": "Build" } ] } ],
  "modelProfiles": [ { "id": "profile-a", "default": { "prefer": "agentic", "provider": "ollama", "model": "llama3.1" } } ]
}
""";
            var refusal = CreateTestRefusal();
            var command = CreateCommandWithTypedPreflight(
                (_, _, _, _, _, _) => Task.FromResult(new WorkflowCommand.ScenarioExecutionResult(true, "should-not-run", 0, 0)),
                (provider, _) => Task.FromResult(new WorkflowCommand.PreflightResult(
                    Ok: false,
                    Provider: provider,
                    Detail: refusal.Message,
                    Refusal: refusal)));

            var reportPath = Path.Combine(repoRoot, "workflow_optimize_refusal_report.md");
            var (exitCode, output) = await CaptureConsoleAsync(
                () => command.ExecuteOptimizeAsync(
                    requestOverride: null,
                    objective: null,
                    objectiveFile: null,
                    specPath: null,
                    specJson: runtimeSpecJson,
                    providerOverride: null,
                    preferOverride: null,
                    iterationsOverride: 1,
                    benchmarkSetOverride: "workflow-lab",
                    persistHistoryOverride: false,
                    warmupRunsOverride: 0,
                    shuffleScenariosOverride: false,
                    randomSeedOverride: null,
                    cooldownMsOverride: 0,
                    maxCandidates: 2,
                    budgetRuns: 2,
                    searchStrategy: null,
                    earlyStopMinRuns: null,
                    earlyStopMinSuccessRate: null,
                    includeMeshPeers: false,
                    meshCapability: "ashlar-cli",
                    autoPullModels: false,
                    promoteWinner: false,
                    policyFile: null,
                    reportOutputPath: reportPath,
                    json: true,
                    verbose: false,
                    ct: cancellationToken)).ConfigureAwait(false);

            AssertEqual(1, exitCode);
            AssertTrue(output.Contains("\"ok\": false", StringComparison.OrdinalIgnoreCase), output);
            AssertTrue(output.Contains(refusal.Ref, StringComparison.Ordinal), output);
            AssertTrue(output.Contains("EGRESS_REFUSED", StringComparison.Ordinal) ||
                        output.Contains("ref=", StringComparison.OrdinalIgnoreCase), output);
            AssertTrue(!output.Contains("Provider unavailable.", StringComparison.Ordinal), output);
        }
        finally
        {
            Environment.CurrentDirectory = previousCurrent;
            if (Directory.Exists(repoRoot))
                Directory.Delete(repoRoot, recursive: true);
        }
    }

    private async Task TestStressNonRefusalPreflightSkipStillCountsAsPassedAsync(CancellationToken cancellationToken)
    {
        var repoRoot = CreateTempRepoRoot();
        var previousCurrent = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = repoRoot;
            const string runtimeSpecJson = """
{
  "execution": { "iterations": 1, "persistHistory": false, "benchmarkSet": "workflow-lab" },
  "requests": [ { "id": "request", "prompt": "Do thing" } ],
  "compositions": [ { "id": "comp", "roles": [ { "agentId": "a1", "role": "builder", "goal": "do thing" } ] } ],
  "modelProfiles": [ { "id": "profile", "default": { "prefer": "agentic", "provider": "ollama", "model": "llama3.1" } } ]
}
""";
            var command = CreateCommandWithTypedPreflight(
                (_, _, _, _, _, _) => Task.FromResult(new WorkflowCommand.ScenarioExecutionResult(true, "should-not-run", 0, 0)),
                (provider, _) => Task.FromResult(new WorkflowCommand.PreflightResult(
                    Ok: false,
                    Provider: provider,
                    Detail: "Provider unavailable.")));

            var (exitCode, output) = await CaptureConsoleAsync(
                () => command.ExecuteStressAsync(
                    requestOverride: null,
                    specPath: null,
                    specJson: runtimeSpecJson,
                    providerOverride: null,
                    preferOverride: null,
                    iterationsOverride: null,
                    benchmarkSetOverride: null,
                    persistHistoryOverride: false,
                    warmupRunsOverride: 0,
                    shuffleScenariosOverride: false,
                    randomSeedOverride: null,
                    cooldownMsOverride: 0,
                    includeMeshPeers: false,
                    meshCapability: "ashlar-cli",
                    json: true,
                    verbose: false,
                    ct: cancellationToken)).ConfigureAwait(false);

            // Non-refusal preflight skips still count as passed (card #802: do not broaden).
            AssertEqual(0, exitCode);
            AssertTrue(output.Contains("\"ok\": true", StringComparison.OrdinalIgnoreCase), output);
            AssertTrue(output.Contains("skipped_infra", StringComparison.OrdinalIgnoreCase), output);
            AssertTrue(output.Contains("Provider unavailable.", StringComparison.Ordinal), output);
        }
        finally
        {
            Environment.CurrentDirectory = previousCurrent;
            if (Directory.Exists(repoRoot))
                Directory.Delete(repoRoot, recursive: true);
        }
    }

}
