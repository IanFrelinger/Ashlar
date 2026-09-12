using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ashlar.Core.Application.Pipelines.Models;
using Ashlar.Core.Application.Pipelines.Ports;
using Ashlar.Core.Application.Resilience.Ports;

namespace Ashlar.Infrastructure.Pipelines;

/// <summary>
/// Default orchestrator for template-driven pipeline execution.
/// </summary>
public sealed class PipelineOrchestrator : IPipelineOrchestrator
{
    private readonly IPipelineTemplateValidator _validator;
    private readonly IPipelineDecomposer _decomposer;
    private readonly IPipelineScheduler _scheduler;
    private readonly IPipelineRunStore _runStore;
    private readonly IPipelineScalingPolicy _scalingPolicy;
    private readonly IReadOnlyDictionary<PipelineWorkerType, IPipelineStageExecutor> _executorsByType;
    private readonly PipelineExecutionOptions _options;
    private readonly ILogger<PipelineOrchestrator> _logger;

    /// <summary>Initializes a new pipeline orchestrator.</summary>
    public PipelineOrchestrator(
        IPipelineTemplateValidator validator,
        IPipelineDecomposer decomposer,
        IPipelineScheduler scheduler,
        IPipelineRunStore runStore,
        IPipelineScalingPolicy scalingPolicy,
        IEnumerable<IPipelineStageExecutor> executors,
        IOptions<PipelineExecutionOptions> options,
        ILogger<PipelineOrchestrator> logger)
    {
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        _decomposer = decomposer ?? throw new ArgumentNullException(nameof(decomposer));
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _runStore = runStore ?? throw new ArgumentNullException(nameof(runStore));
        _scalingPolicy = scalingPolicy ?? throw new ArgumentNullException(nameof(scalingPolicy));
        _executorsByType = (executors ?? throw new ArgumentNullException(nameof(executors)))
            .GroupBy(x => x.WorkerType)
            .ToDictionary(g => g.Key, g => g.First());
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Run asynchronously.</summary>
    public async Task<PipelineRun> RunAsync(
        PipelineExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request == null) throw new ArgumentNullException(nameof(request));

        var validation = _validator.Validate(request.Template);
        if (!validation.IsValid)
            throw new InvalidOperationException($"Pipeline template invalid: {string.Join("; ", validation.Errors)}");

        var graph = _decomposer.Decompose(request.Template);
        var runId = string.IsNullOrWhiteSpace(request.RunId) ? Guid.NewGuid().ToString("N") : request.RunId!;
        var allowResume = !string.IsNullOrWhiteSpace(request.ResumeRunId);
        var resumeFailed = request.ResumeFailedStages || _options.ResumeFailedStages;
        var stageStates = graph.Nodes.ToDictionary(n => n.StageId, _ => PipelineStageRunState.Pending, StringComparer.Ordinal);
        var stageAttempts = graph.Nodes.ToDictionary(n => n.StageId, _ => 0, StringComparer.Ordinal);
        var stageRuns = graph.Nodes.ToDictionary(
            n => n.StageId,
            n => new PipelineStageRun
            {
                StageId = n.StageId,
                State = PipelineStageRunState.Pending,
                Attempt = 0
            },
            StringComparer.Ordinal);

        if (allowResume)
            await TryHydrateFromPriorRunAsync(request, graph, stageStates, stageAttempts, stageRuns, resumeFailed, cancellationToken);

        var stageDefinitionsById = request.Template.Stages
            .ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);

        var run = new PipelineRun
        {
            RunId = runId,
            TemplateId = request.Template.TemplateId,
            State = PipelineRunState.Running,
            StartedAt = DateTimeOffset.UtcNow,
            StageRuns = stageRuns.Values.ToArray()
        };

        run = await _runStore.MergeAsync(
            runId,
            stored => PipelineRunPersistence.Create(stored, run),
            cancellationToken);
        _logger.LogInformation("Pipeline run {RunId} started for template {TemplateId}.", run.RunId, run.TemplateId);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (stageStates.Values.All(s => s == PipelineStageRunState.Completed))
            {
                run = await PersistAsync(
                    runId,
                    FinalizeRun(run, PipelineRunState.Completed, stageRuns),
                    cancellationToken);
                _logger.LogInformation("Pipeline run {RunId} completed successfully.", run.RunId);
                return run;
            }

            if (IsTerminalWithoutPending(stageStates))
            {
                var hasAnyFailure = stageRuns.Values.Any(stageRun => stageRun.State == PipelineStageRunState.Failed);
                var hasCriticalFailure = stageRuns.Values.Any(stageRun =>
                    stageRun.State == PipelineStageRunState.Failed &&
                    stageDefinitionsById.TryGetValue(stageRun.StageId, out var stageDefinition) &&
                    stageDefinition.Constraints.Critical);

                var terminalState = ResolveTerminalState(hasAnyFailure, hasCriticalFailure, _options.CompletionPolicy);
                run = await PersistAsync(
                    runId,
                    FinalizeRun(run, terminalState, stageRuns),
                    cancellationToken);
                _logger.LogInformation(
                    "Pipeline run {RunId} reached terminal state {State}.",
                    run.RunId,
                    terminalState);
                return run;
            }

            var readyStageIds = _scheduler.GetReadyStages(graph, stageStates);
            if (readyStageIds.Count == 0)
            {
                run = await PersistAsync(
                    runId,
                    FinalizeRun(run, PipelineRunState.Failed, stageRuns),
                    cancellationToken);
                _logger.LogWarning("Pipeline run {RunId} stalled with no ready stages.", run.RunId);
                return run;
            }

            foreach (var stageId in readyStageIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var node = graph.Nodes.Single(n => n.StageId == stageId);
                var stage = request.Template.Stages.Single(s => s.Id.Equals(stageId, StringComparison.OrdinalIgnoreCase));

                if (!CanStageExecute(node, stageStates))
                    continue;

                stageStates[stageId] = PipelineStageRunState.Running;
                run = await PersistAsync(
                    runId,
                    UpdateRun(run, stageRuns, PipelineRunState.Running),
                    cancellationToken);

                var execution = await ExecuteWithRetryAndFallbackAsync(
                    runId,
                    stage,
                    node,
                    request.Inputs,
                    stageAttempts,
                    cancellationToken);

                stageRuns[stageId] = new PipelineStageRun
                {
                    StageId = stageId,
                    State = execution.Succeeded ? PipelineStageRunState.Completed : PipelineStageRunState.Failed,
                    Attempt = stageAttempts[stageId],
                    WorkerId = execution.WorkerId,
                    WorkerType = execution.WorkerType,
                    Output = execution.Output,
                    Error = execution.Error
                };
                stageStates[stageId] = stageRuns[stageId].State;

                _logger.LogInformation(
                    "Pipeline stage {StageId} completed with state {State} on worker {WorkerType}.",
                    stageId,
                    stageRuns[stageId].State,
                    stageRuns[stageId].WorkerType);

                if (stageRuns[stageId].State == PipelineStageRunState.Failed && stage.Constraints.Critical)
                {
                    run = await PersistAsync(
                        runId,
                        FinalizeRun(run, PipelineRunState.Failed, stageRuns),
                        cancellationToken);
                    _logger.LogWarning("Critical stage {StageId} failed; run {RunId} is failing fast.", stageId, run.RunId);
                    return run;
                }
            }

            var scaleSnapshot = BuildScalingSnapshot(stageRuns.Values);
            var scaleDecision = _scalingPolicy.Evaluate(scaleSnapshot);
            _logger.LogDebug(
                "Pipeline scaling decision: deterministic={DeterministicWorkers}, agentic={AgenticWorkers}.",
                scaleDecision.DeterministicWorkers,
                scaleDecision.AgenticWorkers);

            run = await PersistAsync(
                runId,
                UpdateRun(run, stageRuns, PipelineRunState.Running),
                cancellationToken);
        }
    }

    // Creation above grants this invocation the only execution of its destination ID. Every
    // later write stays in that execution; combining rows from different invocations could hide
    // failures or mix outputs produced for different inputs under the same stage names.
    private Task<PipelineRun> PersistAsync(
        string runId,
        PipelineRun next,
        CancellationToken cancellationToken)
        => _runStore.MergeAsync(
            runId,
            stored => PipelineRunPersistence.Advance(stored, next),
            cancellationToken);

    /// <summary>Copies resumable stages from a source run into a fresh destination.</summary>
    /// <remarks>
    /// The source is a snapshot and is never rewritten by resume. An explicit destination that
    /// already exists (including the source ID itself) is refused atomically before execution.
    /// Omitting RunId generates a fresh destination, including for interrupted source runs.
    /// </remarks>
    /// <param name="request">The execution request, carrying <c>ResumeRunId</c>.</param>
    /// <param name="graph">The execution graph.</param>
    /// <param name="stageStates">Stage states to hydrate.</param>
    /// <param name="stageAttempts">Stage attempt counts to hydrate.</param>
    /// <param name="stageRuns">Stage runs to hydrate.</param>
    /// <param name="resumeFailed">Whether failed stages are carried forward too.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private async Task TryHydrateFromPriorRunAsync(
        PipelineExecutionRequest request,
        PipelineExecutionGraph graph,
        IDictionary<string, PipelineStageRunState> stageStates,
        IDictionary<string, int> stageAttempts,
        IDictionary<string, PipelineStageRun> stageRuns,
        bool resumeFailed,
        CancellationToken cancellationToken)
    {
        var prior = await _runStore.GetAsync(request.ResumeRunId!, cancellationToken);
        if (prior == null)
        {
            if (!_options.AllowMissingResumeSource)
                throw new InvalidOperationException(
                    $"Resume requested for run '{request.ResumeRunId}', but no prior run was found.");

            _logger.LogWarning("Resume requested for run {RunId}, but no prior run was found. Starting fresh run.", request.ResumeRunId);
            return;
        }

        foreach (var stage in prior.StageRuns)
        {
            if (!stageStates.ContainsKey(stage.StageId))
                continue;

            var canCarryForward = stage.State == PipelineStageRunState.Completed ||
                                  (resumeFailed && stage.State == PipelineStageRunState.Failed);

            if (!canCarryForward)
                continue;

            var carriedState = stage.State == PipelineStageRunState.Failed
                ? PipelineStageRunState.Pending
                : stage.State;
            stageStates[stage.StageId] = carriedState;
            stageAttempts[stage.StageId] = stage.Attempt;
            stageRuns[stage.StageId] = stage with { State = carriedState };
        }

        // Ensure unknown graph stages are reset to pending.
        foreach (var node in graph.Nodes)
        {
            if (!stageRuns.ContainsKey(node.StageId))
            {
                stageRuns[node.StageId] = new PipelineStageRun
                {
                    StageId = node.StageId,
                    State = PipelineStageRunState.Pending,
                    Attempt = 0
                };
            }
        }
    }

    private bool CanStageExecute(
        PipelineExecutionNode node,
        IReadOnlyDictionary<string, PipelineStageRunState> states)
    {
        if (node.Predecessors.Count == 0)
            return true;

        var predecessorStates = new List<PipelineStageRunState>(node.Predecessors.Count);
        foreach (var predecessor in node.Predecessors)
        {
            if (!states.TryGetValue(predecessor, out var predecessorState))
                return false;
            predecessorStates.Add(predecessorState);
        }

        if (node.StageType == PipelineStageType.FanIn)
        {
            var completed = predecessorStates.Count(x => x == PipelineStageRunState.Completed);
            if (node.JoinStrategy == PipelineJoinStrategy.FirstSuccess)
                return completed >= 1;
            if (node.JoinStrategy == PipelineJoinStrategy.Quorum)
                return node.QuorumCount.GetValueOrDefault(0) > 0 && completed >= node.QuorumCount.GetValueOrDefault(0);
        }

        return predecessorStates.All(x => x == PipelineStageRunState.Completed);
    }

    private async Task<ExecutionOutcome> ExecuteWithRetryAndFallbackAsync(
        string runId,
        PipelineStageDefinition stage,
        PipelineExecutionNode node,
        IReadOnlyDictionary<string, string> inputs,
        IDictionary<string, int> stageAttempts,
        CancellationToken cancellationToken)
    {
        var chain = ResolveWorkerChain(stage);
        if (chain.Count == 0)
        {
            return new ExecutionOutcome
            {
                Succeeded = false,
                Error = $"No worker chain available for stage {stage.Id}."
            };
        }

        for (var chainIndex = 0; chainIndex < chain.Count; chainIndex++)
        {
            var workerType = chain[chainIndex];
            if (!_executorsByType.TryGetValue(workerType, out var executor))
            {
                continue;
            }

            // This loop deliberately is NOT an IResilientExecutor.ExecuteAsync call.
            // It retries on a non-exceptional RESULT flag (result.Retryable), and the
            // retry is interleaved with fallback-chain traversal and a stage-wide
            // attempt counter that the executor knows nothing about. Forcing it
            // through the executor would mean throwing exceptions to signal ordinary
            // outcomes. What it does share is the policy and its backoff.
            var retryPolicy = RetryPolicy.FixedDelay(
                maxAttempts: Math.Max(1, _options.MaxRetryAttempts),
                delay: TimeSpan.FromMilliseconds(Math.Max(0, _options.RetryDelayMs)));
            var maxAttempts = retryPolicy.MaxAttempts;
            for (var localAttempt = 1; localAttempt <= maxAttempts; localAttempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                stageAttempts[stage.Id] = stageAttempts[stage.Id] + 1;
                var globalAttempt = stageAttempts[stage.Id];

                _logger.LogDebug(
                    "Executing stage {StageId} attempt {Attempt} on {WorkerType}.",
                    stage.Id,
                    globalAttempt,
                    workerType);

                var result = await executor.ExecuteAsync(new PipelineStageExecutionRequest
                {
                    RunId = runId,
                    StageId = stage.Id,
                    Attempt = globalAttempt,
                    WorkerType = workerType,
                    Stage = stage,
                    Node = node,
                    Inputs = inputs
                }, cancellationToken);

                if (result.Succeeded)
                {
                    return new ExecutionOutcome
                    {
                        Succeeded = true,
                        WorkerId = result.WorkerId,
                        WorkerType = workerType,
                        Output = result.Output
                    };
                }

                var isLastAttempt = localAttempt >= maxAttempts;
                if (!result.Retryable || isLastAttempt)
                {
                    if (chainIndex < chain.Count - 1)
                    {
                        _logger.LogInformation(
                            "Stage {StageId} failed on {WorkerType}; falling back to {FallbackWorkerType}.",
                            stage.Id,
                            workerType,
                            chain[chainIndex + 1]);
                        break;
                    }

                    return new ExecutionOutcome
                    {
                        Succeeded = false,
                        WorkerId = result.WorkerId,
                        WorkerType = workerType,
                        Error = result.Error ?? $"Stage {stage.Id} failed."
                    };
                }

                await Task.Delay(retryPolicy.DelayForAttempt(localAttempt), cancellationToken);
            }
        }

        return new ExecutionOutcome
        {
            Succeeded = false,
            Error = $"Stage {stage.Id} exhausted retries and fallback chain."
        };
    }

    private IReadOnlyList<PipelineWorkerType> ResolveWorkerChain(PipelineStageDefinition stage)
    {
        if (stage.Mode == PipelineExecutionMode.Deterministic)
            return new[] { PipelineWorkerType.Deterministic };
        if (stage.Mode == PipelineExecutionMode.Agentic)
            return new[] { PipelineWorkerType.Agentic };

        if (stage.FallbackChain.Count > 0)
            return stage.FallbackChain;

        return new[] { PipelineWorkerType.Deterministic, PipelineWorkerType.Agentic };
    }

    private static PipelineScalingSnapshot BuildScalingSnapshot(IEnumerable<PipelineStageRun> stages)
    {
        var list = stages.ToList();
        var deterministicPending = list.Count(x => x.State == PipelineStageRunState.Pending && x.WorkerType != PipelineWorkerType.Agentic);
        var agenticPending = list.Count(x => x.State == PipelineStageRunState.Pending && x.WorkerType == PipelineWorkerType.Agentic);
        var deterministicCompleted = list.Count(x => x.WorkerType != PipelineWorkerType.Agentic && x.State == PipelineStageRunState.Completed);
        var deterministicFailed = list.Count(x => x.WorkerType != PipelineWorkerType.Agentic && x.State == PipelineStageRunState.Failed);
        var agenticCompleted = list.Count(x => x.WorkerType == PipelineWorkerType.Agentic && x.State == PipelineStageRunState.Completed);
        var agenticFailed = list.Count(x => x.WorkerType == PipelineWorkerType.Agentic && x.State == PipelineStageRunState.Failed);

        var deterministicTotal = Math.Max(1, deterministicCompleted + deterministicFailed);
        var agenticTotal = Math.Max(1, agenticCompleted + agenticFailed);

        return new PipelineScalingSnapshot
        {
            DeterministicQueueDepth = deterministicPending,
            AgenticQueueDepth = agenticPending,
            DeterministicErrorRate = (double)deterministicFailed / deterministicTotal,
            AgenticErrorRate = (double)agenticFailed / agenticTotal,
            CurrentDeterministicWorkers = Math.Max(1, deterministicCompleted + deterministicPending),
            CurrentAgenticWorkers = Math.Max(1, agenticCompleted + agenticPending)
        };
    }

    private static PipelineRun UpdateRun(
        PipelineRun current,
        IReadOnlyDictionary<string, PipelineStageRun> stageRuns,
        PipelineRunState state)
    {
        return current with
        {
            State = state,
            StageRuns = stageRuns.Values.OrderBy(x => x.StageId, StringComparer.Ordinal).ToArray()
        };
    }

    private static PipelineRun FinalizeRun(
        PipelineRun current,
        PipelineRunState state,
        IReadOnlyDictionary<string, PipelineStageRun> stageRuns)
    {
        return current with
        {
            State = state,
            CompletedAt = DateTimeOffset.UtcNow,
            StageRuns = stageRuns.Values.OrderBy(x => x.StageId, StringComparer.Ordinal).ToArray()
        };
    }

    private static bool IsTerminalWithoutPending(IReadOnlyDictionary<string, PipelineStageRunState> stageStates)
    {
        foreach (var state in stageStates.Values)
        {
            if (state == PipelineStageRunState.Pending || state == PipelineStageRunState.Running)
                return false;
        }

        return true;
    }

    private static PipelineRunState ResolveTerminalState(
        bool hasAnyFailure,
        bool hasCriticalFailure,
        PipelineCompletionPolicy completionPolicy)
    {
        if (!hasAnyFailure)
            return PipelineRunState.Completed;

        if (completionPolicy == PipelineCompletionPolicy.AllowNonCriticalStageFailures)
            return hasCriticalFailure ? PipelineRunState.Failed : PipelineRunState.Completed;

        return PipelineRunState.Failed;
    }

    private sealed record ExecutionOutcome
    {
        /// <summary>Whether the operation succeeded.</summary>
        public bool Succeeded { get; init; }
        /// <summary>Worker id.</summary>
        public string? WorkerId { get; init; }
        /// <summary>Worker type.</summary>
        public PipelineWorkerType? WorkerType { get; init; }
        /// <summary>Output.</summary>
        public string? Output { get; init; }
        /// <summary>Error.</summary>
        public string? Error { get; init; }
    }
}
