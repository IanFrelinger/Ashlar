using System.CommandLine;
using System.CommandLine.Invocation;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Ashlar.Abstractions.Security.Egress;
using Ashlar.Core.Application.Mesh.Models;
using Ashlar.Core.Application.Mesh.Ports;
using Ashlar.CLI.Runtime;
using Ashlar.Infrastructure.Execution;
using Ashlar.Orchestration.Models;
using Process = System.Diagnostics.Process;

namespace Ashlar.CLI.Commands;

/// <summary>
/// First-class workflow lab command for composing and stress-testing orchestrated agent workflows.
/// </summary>
public sealed partial class WorkflowCommand : Command
{
    internal delegate Task<ScenarioExecutionResult> ScenarioExecutor(
        string request,
        string runtimeSpecJson,
        string? provider,
        bool outputJson,
        bool verbose,
        CancellationToken cancellationToken);
    internal delegate Task<ScenarioExecutionResult> MeshScenarioExecutor(
        string endpoint,
        string request,
        string runtimeSpecJson,
        string? provider,
        bool outputJson,
        bool verbose,
        CancellationToken cancellationToken);

    private readonly ScenarioExecutor _scenarioExecutor;
    private readonly MeshScenarioExecutor _meshScenarioExecutor;
    private readonly Func<string, CancellationToken, Task<PreflightResult>> _providerPreflight;
    private readonly Func<IReadOnlyList<string>, CancellationToken, Task<ModelPullResult>> _ollamaModelPuller;
    private readonly Func<CancellationToken, Task<IReadOnlyList<PeerInfo>>> _meshPeerDiscovery;
    private readonly ScaffoldHandler _scaffoldHandler;
    private readonly HistoryHandler _historyHandler;
    private readonly BaselineHandler _baselineHandler;
    private readonly ReportHandler _reportHandler;
    private readonly GateHandler _gateHandler;
    private readonly StressHandler _stressHandler;
    private readonly OptimizeHandler _optimizeHandler;

    /// <summary>Creates a new WorkflowCommand instance.</summary>
    public WorkflowCommand(Func<OrchestrateCommand> orchestrateFactory)
        : this(
            async (request, runtimeSpecJson, provider, outputJson, verbose, ct) =>
            {
                var orchestrate = orchestrateFactory();
                return await ExecuteScenarioAsync(orchestrate, request, runtimeSpecJson, provider, outputJson, verbose, ct).ConfigureAwait(false);
            },
            (provider, ct) => PreflightProviderAsync(provider, ct),
            null,
            _ => Task.FromResult<IReadOnlyList<PeerInfo>>(Array.Empty<PeerInfo>()),
            ExecuteScenarioOnMeshPeerAsync)
    {
    }

    /// <summary>Creates a new WorkflowCommand instance.</summary>
    public WorkflowCommand(Func<IServiceScope> orchestrationScopeFactory)
        : this(
            async (request, runtimeSpecJson, provider, outputJson, verbose, ct) =>
            {
                using var scope = orchestrationScopeFactory();
                var orchestrate = scope.ServiceProvider.GetRequiredService<OrchestrateCommand>();
                return await ExecuteScenarioAsync(orchestrate, request, runtimeSpecJson, provider, outputJson, verbose, ct).ConfigureAwait(false);
            },
            async (provider, ct) =>
            {
                using var scope = orchestrationScopeFactory();
                return await PreflightProviderAsync(
                    provider,
                    ct,
                    scope.ServiceProvider.GetService<IProviderFactory>()).ConfigureAwait(false);
            },
            null,
            async ct =>
            {
                using var scope = orchestrationScopeFactory();
                var discovery = scope.ServiceProvider.GetService<IInstanceDiscovery>();
                if (discovery is null)
                    return Array.Empty<PeerInfo>();
                var peers = await discovery.DiscoverAsync(ct).ConfigureAwait(false);
                return peers ?? Array.Empty<PeerInfo>();
            },
            ExecuteScenarioOnMeshPeerAsync)
    {
    }

    internal WorkflowCommand(ScenarioExecutor scenarioExecutor)
        : this(
            scenarioExecutor,
            (provider, ct) => Task.FromResult(
                new PreflightResult(
                    Ok: true,
                    Provider: string.IsNullOrWhiteSpace(provider) ? "unset" : provider,
                    Detail: "Test preflight override: provider assumed available.")),
            (models, _) => Task.FromResult(
                new ModelPullResult(
                    Ok: true,
                    Summary: $"Test model pull override: assumed available for {models.Count} model(s).",
                    Models: models.ToArray(),
                    PulledModels: models.ToArray())),
            _ => Task.FromResult<IReadOnlyList<PeerInfo>>(Array.Empty<PeerInfo>()),
            ExecuteScenarioOnMeshPeerAsync)
    {
    }

    internal WorkflowCommand(
        ScenarioExecutor scenarioExecutor,
        Func<string, CancellationToken, Task<bool>>? providerPreflight)
        : this(
            scenarioExecutor,
            providerPreflight,
            null,
            _ => Task.FromResult<IReadOnlyList<PeerInfo>>(Array.Empty<PeerInfo>()),
            ExecuteScenarioOnMeshPeerAsync)
    {
    }

    internal WorkflowCommand(
        ScenarioExecutor scenarioExecutor,
        Func<string, CancellationToken, Task<bool>>? providerPreflight,
        Func<IReadOnlyList<string>, CancellationToken, Task<ModelPullResult>>? ollamaModelPuller)
        : this(
            scenarioExecutor,
            providerPreflight,
            ollamaModelPuller,
            _ => Task.FromResult<IReadOnlyList<PeerInfo>>(Array.Empty<PeerInfo>()),
            ExecuteScenarioOnMeshPeerAsync)
    {
    }

    internal WorkflowCommand(
        ScenarioExecutor scenarioExecutor,
        Func<string, CancellationToken, Task<bool>>? providerPreflight,
        Func<IReadOnlyList<string>, CancellationToken, Task<ModelPullResult>>? ollamaModelPuller,
        Func<CancellationToken, Task<IReadOnlyList<PeerInfo>>>? meshPeerDiscovery,
        MeshScenarioExecutor? meshScenarioExecutor)
        : this(
            scenarioExecutor,
            providerPreflight is null
                ? (provider, ct) => Task.FromResult(
                    new PreflightResult(
                        Ok: true,
                        Provider: string.IsNullOrWhiteSpace(provider) ? "unset" : provider,
                        Detail: "Test preflight override: provider assumed available."))
                : async (provider, ct) =>
                {
                    var ok = await providerPreflight(provider, ct).ConfigureAwait(false);
                    return new PreflightResult(
                        Ok: ok,
                        Provider: string.IsNullOrWhiteSpace(provider) ? "unset" : provider.Trim(),
                        Detail: ok ? "Provider available." : "Provider unavailable.");
                },
            ollamaModelPuller,
            meshPeerDiscovery,
            meshScenarioExecutor)
    {
    }

    private WorkflowCommand(
        ScenarioExecutor scenarioExecutor,
        Func<string, CancellationToken, Task<PreflightResult>> providerPreflight,
        Func<IReadOnlyList<string>, CancellationToken, Task<ModelPullResult>>? ollamaModelPuller = null,
        Func<CancellationToken, Task<IReadOnlyList<PeerInfo>>>? meshPeerDiscovery = null,
        MeshScenarioExecutor? meshScenarioExecutor = null)
        : base("workflow", "Scaffold and stress-test agentic workflow compositions.")
    {
        _scenarioExecutor = scenarioExecutor ?? throw new ArgumentNullException(nameof(scenarioExecutor));
        _providerPreflight = providerPreflight ?? throw new ArgumentNullException(nameof(providerPreflight));
        _ollamaModelPuller = ollamaModelPuller ?? PullOllamaModelsAsync;
        _meshPeerDiscovery = meshPeerDiscovery ?? (_ => Task.FromResult<IReadOnlyList<PeerInfo>>(Array.Empty<PeerInfo>()));
        _meshScenarioExecutor = meshScenarioExecutor ?? ExecuteScenarioOnMeshPeerAsync;
        _scaffoldHandler = new ScaffoldHandler();
        _historyHandler = new HistoryHandler();
        _baselineHandler = new BaselineHandler(WorkflowCommandUtilities.NormalizeBenchmarkSet, WorkflowCommandUtilities.LoadGatePolicy, WorkflowCommandUtilities.BuildBaselineId);
        _reportHandler = new ReportHandler(WorkflowCommandUtilities.BuildBenchmarkReport, WorkflowCommandUtilities.BuildComparison, WorkflowCommandUtilities.RenderReportContent, WorkflowCommandUtilities.RenderComparisonText);
        _gateHandler = new GateHandler(WorkflowCommandUtilities.NormalizeBenchmarkSet, WorkflowCommandUtilities.LoadGatePolicy, WorkflowCommandUtilities.BuildComparison, WorkflowCommandUtilities.RenderComparisonText);
        _stressHandler = new StressHandler(
            _providerPreflight,
            WorkflowCommandUtilities.ResolveDefaultSpecPath,
            WorkflowCommandUtilities.NormalizeRequests,
            WorkflowCommandUtilities.NormalizeCompositions,
            WorkflowCommandUtilities.NormalizeProfiles,
            WorkflowCommandUtilities.NormalizeBenchmarkSet,
            WorkflowCommandUtilities.BuildRunId,
            WorkflowCommandUtilities.ResolveGitSha,
            WorkflowCommandUtilities.ComputeSpecHash,
            WorkflowCommandUtilities.BuildProviderSnapshot,
            ResolveExecutionTargetsAsync,
            WorkflowCommandUtilities.BuildScenarioPlans,
            WorkflowCommandUtilities.ShuffleScenarioPlans,
            WorkflowCommandUtilities.BuildRuntimeSpec,
            WorkflowCommandUtilities.BuildScenarioId,
            ExecuteScenarioForTargetAsync,
            WorkflowCommandUtilities.CaptureRuntimeTelemetry,
            WorkflowCommandUtilities.ComputeScore);
        _optimizeHandler = new OptimizeHandler(
            _providerPreflight,
            _ollamaModelPuller,
            ResolveExecutionTargetsAsync,
            ExecuteScenarioForTargetAsync);
        ConfigureScaffoldCommand();
        ConfigureStressCommand();
        ConfigureHistoryCommand();
        ConfigureReportCommand();
        ConfigureGateCommand();
        ConfigureBaselineCommand();
        ConfigureOptimizeCommand();
    }

    internal Task<int> ExecuteScaffoldAsync(string outputPath, bool force, bool json) => _scaffoldHandler.ExecuteAsync(outputPath, force, json);

    internal Task<int> ExecuteHistoryAsync(string repoRoot, int limit, string? benchmarkSet, bool json) => _historyHandler.ExecuteAsync(repoRoot, limit, benchmarkSet, json);

    internal Task<int> ExecuteBaselinePromoteAsync(
        string repoRoot,
        string? benchmarkSet,
        string runId,
        string? notes,
        string? policyFile,
        bool json) => _baselineHandler.ExecutePromoteAsync(repoRoot, benchmarkSet, runId, notes, policyFile, json);

    internal Task<int> ExecuteBaselineListAsync(string repoRoot, string? benchmarkSet, bool json) => _baselineHandler.ExecuteListAsync(repoRoot, benchmarkSet, json);

    internal Task<int> ExecuteBaselineShowAsync(string repoRoot, string? benchmarkSet, string? baselineId, bool json) => _baselineHandler.ExecuteShowAsync(repoRoot, benchmarkSet, baselineId, json);

    internal Task<int> ExecuteReportAsync(
        string repoRoot,
        int limit,
        string? benchmarkSet,
        string? runId,
        string? baselineRunId,
        string? since,
        string? outputPath,
        bool json) => _reportHandler.ExecuteAsync(repoRoot, limit, benchmarkSet, runId, baselineRunId, since, outputPath, json);

    internal Task<int> ExecuteGateAsync(
        string repoRoot,
        string? benchmarkSet,
        string runId,
        string? baselineRunId,
        string? policyFile,
        double minSuccessRateDelta,
        long maxP95LatencyRegressionMs,
        long maxAverageLatencyRegressionMs,
        double minAverageScoreDelta,
        int maxRegressedScenarios,
        bool json) => _gateHandler.ExecuteAsync(repoRoot, benchmarkSet, runId, baselineRunId, policyFile, minSuccessRateDelta, maxP95LatencyRegressionMs, maxAverageLatencyRegressionMs, minAverageScoreDelta, maxRegressedScenarios, json);

    internal Task<int> ExecuteStressAsync(
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
        CancellationToken ct) => _stressHandler.ExecuteAsync(requestOverride, specPath, specJson, providerOverride, preferOverride, iterationsOverride, benchmarkSetOverride, persistHistoryOverride, warmupRunsOverride, shuffleScenariosOverride, randomSeedOverride, cooldownMsOverride, includeMeshPeers, meshCapability, json, verbose, ct);

    internal Task<int> ExecuteOptimizeAsync(
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
        CancellationToken ct) => _optimizeHandler.ExecuteAsync(requestOverride, objective, objectiveFile, specPath, specJson, providerOverride, preferOverride, iterationsOverride, benchmarkSetOverride, persistHistoryOverride, warmupRunsOverride, shuffleScenariosOverride, randomSeedOverride, cooldownMsOverride, maxCandidates, budgetRuns, searchStrategy, earlyStopMinRuns, earlyStopMinSuccessRate, includeMeshPeers, meshCapability, autoPullModels, promoteWinner, policyFile, reportOutputPath, json, verbose, ct);

    private static async Task<ModelPullResult> PullOllamaModelsAsync(
        IReadOnlyList<string> models,
        CancellationToken ct)
    {
        var requested = (models ?? Array.Empty<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (requested.Length == 0)
        {
            return new ModelPullResult(
                Ok: true,
                Summary: "No Ollama models required for this candidate.",
                Models: requested,
                PulledModels: Array.Empty<string>(),
                FailedModels: Array.Empty<string>());
        }

        var pulled = new List<string>();
        var failed = new List<string>();
        foreach (var model in requested)
        {
            ct.ThrowIfCancellationRequested();
            EgressGuard.ProcessDefault.Evaluate(new EgressRequest(EgressFamilies.Process, "EG-PROC-09", "process:ollama")).ThrowIfRefused();
            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo("ollama")
                    {
                        UseShellExecute = false,
                        RedirectStandardError = false,
                        RedirectStandardOutput = false,
                        CreateNoWindow = true
                    }
                };
                process.StartInfo.ArgumentList.Add("pull");
                process.StartInfo.ArgumentList.Add(model);
                if (!process.Start())
                {
                    failed.Add(model);
                    continue;
                }

                await process.WaitForExitAsync(ct).ConfigureAwait(false);
                if (process.ExitCode == 0)
                    pulled.Add(model);
                else
                    failed.Add(model);
            }
            catch
            {
                failed.Add(model);
            }
        }

        var ok = failed.Count == 0;
        var summary = ok
            ? $"Pulled {pulled.Count} Ollama model(s): {string.Join(", ", pulled)}."
            : $"Pulled {pulled.Count}/{requested.Length} Ollama model(s); failed: {string.Join(", ", failed)}.";
        return new ModelPullResult(
            Ok: ok,
            Summary: summary,
            Models: requested,
            PulledModels: pulled.ToArray(),
            FailedModels: failed.ToArray());
    }

    private static async Task<ScenarioExecutionResult> ExecuteScenarioAsync(
        OrchestrateCommand orchestrate,
        string request,
        string runtimeSpecJson,
        string? provider,
        bool json,
        bool verbose,
        CancellationToken ct)
    {
        var result = await orchestrate.ExecuteStructuredAsync(
            request,
            runtimeSpecPath: null,
            runtimeSpecJson,
            preferModel: null,
            provider,
            barrierLevel: null,
            preferredRegion: null,
            cancellationToken: ct).ConfigureAwait(false);

        if (result.Ok)
        {
            return new ScenarioExecutionResult(
                Ok: true,
                Summary: "Orchestration run completed successfully.",
                ConflictCount: result.Conflicts ?? 0,
                EscalationCount: result.Escalations ?? 0,
                FailureCategory: "none");
        }

        var summary = string.IsNullOrWhiteSpace(result.ErrorCode)
            ? $"Orchestration failed: {result.Error ?? "unknown error"}"
            : $"Orchestration failed with errorCode={result.ErrorCode}: {result.Error ?? "no error details"}";
        var category = WorkflowCommandUtilities.ClassifyFailureCategory(result.Error ?? string.Empty, result.ErrorCode);
        return new ScenarioExecutionResult(
            Ok: false,
            Summary: summary,
            ConflictCount: result.Conflicts ?? 0,
            EscalationCount: result.Escalations ?? 0,
            FailureCategory: category);
    }

    private async Task<ScenarioExecutionResult> ExecuteScenarioForTargetAsync(
        ExecutionTarget target,
        string request,
        string runtimeSpecJson,
        string? provider,
        bool verbose,
        CancellationToken ct)
    {
        if (target.IsLocal || string.IsNullOrWhiteSpace(target.Endpoint))
        {
            return await _scenarioExecutor(
                request,
                runtimeSpecJson,
                provider,
                true,
                verbose,
                ct).ConfigureAwait(false);
        }

        return await _meshScenarioExecutor(
            target.Endpoint,
            request,
            runtimeSpecJson,
            provider,
            true,
            verbose,
            ct).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<ExecutionTarget>> ResolveExecutionTargetsAsync(
        bool includeMeshPeers,
        string? meshCapability,
        CancellationToken ct)
    {
        var targets = new List<ExecutionTarget> { ExecutionTarget.Local };
        if (!includeMeshPeers)
            return targets;

        IReadOnlyList<PeerInfo> discovered;
        try
        {
            discovered = await _meshPeerDiscovery(ct).ConfigureAwait(false) ?? Array.Empty<PeerInfo>();
        }
        catch
        {
            return targets;
        }

        if (discovered.Count == 0)
            return targets;

        var capabilityFilter = string.IsNullOrWhiteSpace(meshCapability) ? null : meshCapability.Trim();
        var seenEndpoints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var peer in discovered)
        {
            ct.ThrowIfCancellationRequested();
            if (peer is null)
                continue;
            var endpoint = peer.Endpoint?.Trim();
            if (string.IsNullOrWhiteSpace(endpoint))
                continue;
            if (!string.IsNullOrWhiteSpace(capabilityFilter) &&
                !peer.Capabilities.Any(x => string.Equals(x?.Trim(), capabilityFilter, StringComparison.OrdinalIgnoreCase)))
                continue;
            if (!WorkflowOptimizeReportRenderer.TryNormalizeMeshEndpoint(endpoint, out var normalizedEndpoint))
                continue;
            if (!seenEndpoints.Add(normalizedEndpoint))
                continue;

            var peerId = string.IsNullOrWhiteSpace(peer.PeerId)
                ? normalizedEndpoint
                : peer.PeerId.Trim();
            targets.Add(new ExecutionTarget(peerId, normalizedEndpoint, false));
        }

        return targets;
    }


    private sealed record MeshOrchestrateRequest(
        string Request,
        string RuntimeSpecJson,
        string? Provider,
        bool Verbose,
        bool Json);

    private sealed record MeshOrchestrateResponse(
        bool Success,
        string? Summary,
        object? Output,
        int? Conflicts = null,
        int? Escalations = null,
        string? ErrorCode = null);

    private static async Task<ScenarioExecutionResult> ExecuteScenarioOnMeshPeerAsync(
        string endpoint,
        string request,
        string runtimeSpecJson,
        string? provider,
        bool outputJson,
        bool verbose,
        CancellationToken cancellationToken)
    {
        if (!WorkflowOptimizeReportRenderer.TryNormalizeMeshEndpoint(endpoint, out var normalizedEndpoint))
        {
            return new ScenarioExecutionResult(
                Ok: false,
                Summary: $"Mesh peer endpoint is invalid: {endpoint}",
                ConflictCount: 0,
                EscalationCount: 0,
                FailureCategory: "infra_unavailable");
        }

        try
        {
            using var client = EgressHttp.CreateClient(EgressFamilies.Http, "EG-HTTP-05");
            client.BaseAddress = new Uri($"{normalizedEndpoint}/");
            client.Timeout = TimeSpan.FromMinutes(3);
            var payload = new MeshOrchestrateRequest(
                Request: request,
                RuntimeSpecJson: runtimeSpecJson,
                Provider: provider,
                Verbose: verbose,
                Json: outputJson);
            using var response = await client.PostAsJsonAsync("api/orchestrate", payload, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new ScenarioExecutionResult(
                    Ok: false,
                    Summary: $"Mesh peer returned HTTP {(int)response.StatusCode} for {normalizedEndpoint}.",
                    ConflictCount: 0,
                    EscalationCount: 0,
                    FailureCategory: "infra_unavailable");
            }

            var orchestrate = await response.Content.ReadFromJsonAsync<MeshOrchestrateResponse>(cancellationToken: cancellationToken).ConfigureAwait(false);
            if (orchestrate is null)
            {
                return new ScenarioExecutionResult(
                    Ok: false,
                    Summary: $"Mesh peer response missing payload for {normalizedEndpoint}.",
                    ConflictCount: 0,
                    EscalationCount: 0,
                    FailureCategory: "orchestration_failure");
            }

            if (orchestrate.Success)
            {
                return new ScenarioExecutionResult(
                    Ok: true,
                    Summary: string.IsNullOrWhiteSpace(orchestrate.Summary)
                        ? $"Mesh peer run succeeded on {normalizedEndpoint}."
                        : orchestrate.Summary!,
                    ConflictCount: orchestrate.Conflicts ?? 0,
                    EscalationCount: orchestrate.Escalations ?? 0,
                    FailureCategory: "none");
            }

            var failureSummary = string.IsNullOrWhiteSpace(orchestrate.Summary)
                ? $"Mesh peer run failed on {normalizedEndpoint}."
                : $"Mesh peer run failed on {normalizedEndpoint}: {orchestrate.Summary}";
            return new ScenarioExecutionResult(
                Ok: false,
                Summary: failureSummary,
                ConflictCount: orchestrate.Conflicts ?? 0,
                EscalationCount: orchestrate.Escalations ?? 0,
                FailureCategory: WorkflowCommandUtilities.ClassifyFailureCategory(failureSummary, orchestrate.ErrorCode));
        }
        catch (Exception ex)
        {
            var summary = $"Mesh peer request failed for {normalizedEndpoint}: {ex.Message}";
            return new ScenarioExecutionResult(
                Ok: false,
                Summary: summary,
                ConflictCount: 0,
                EscalationCount: 0,
                FailureCategory: WorkflowCommandUtilities.ClassifyFailureCategory(summary));
        }
    }

    private static Task<PreflightResult> PreflightProviderAsync(
        string provider,
        CancellationToken ct,
        IProviderFactory? providerFactory = null)
    {
        var normalized = string.IsNullOrWhiteSpace(provider) ? "unset" : provider.Trim().ToLowerInvariant();
        if (normalized is "unset" or "offline" or "mock-json")
            return Task.FromResult(new PreflightResult(true, normalized, "Provider does not require runtime connectivity check."));

        if (providerFactory is null)
            return Task.FromResult(new PreflightResult(false, normalized, "Provider factory unavailable for preflight."));

        try
        {
            var available = providerFactory.IsProviderAvailable(normalized);
            return Task.FromResult(available
                ? new PreflightResult(true, normalized, "Provider available.")
                : new PreflightResult(false, normalized, "Provider unavailable."));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new PreflightResult(false, normalized, ex.Message));
        }
    }




    internal sealed record ModelPullResult(
        bool Ok,
        string Summary,
        IReadOnlyList<string> Models,
        IReadOnlyList<string> PulledModels,
        IReadOnlyList<string>? FailedModels = null);

    internal sealed record ScenarioExecutionResult(
        bool Ok,
        string Summary,
        int ConflictCount,
        int EscalationCount,
        bool Skipped = false,
        string FailureCategory = "none");

    internal sealed record PreflightResult(
        bool Ok,
        string Provider,
        string Detail);
}
