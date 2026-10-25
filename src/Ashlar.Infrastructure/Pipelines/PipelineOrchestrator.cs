using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.ObjectModel;
using Ashlar.Core.Application.Execution;
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
        if (_options.MaxRetryAttempts <= 0 || _options.RetryDelayMs < 0 || !Enum.IsDefined(_options.CompletionPolicy))
            throw new ArgumentException("Retry attempts must be positive, delay nonnegative, and completion policy valid.", nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Run asynchronously.</summary>
    public async Task<PipelineRun> RunAsync(
        PipelineExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request == null) throw new ArgumentNullException(nameof(request));
        if (request.Inputs == null) throw new ArgumentException("Pipeline inputs are required.", nameof(request));
        request = request with
        {
            Inputs = new ReadOnlyDictionary<string, string>(request.Inputs.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal)),
            Template = request.Template == null ? null! : request.Template with
            {
                Stages = Array.AsReadOnly(request.Template.Stages.Select(stage => stage with
                {
                    FallbackChain = Array.AsReadOnly(stage.FallbackChain.ToArray())
                }).ToArray()),
                Edges = Array.AsReadOnly(request.Template.Edges.ToArray())
            }
        };

        var validation = _validator.Validate(request.Template);
        if (!validation.IsValid)
            throw new InvalidOperationException($"Pipeline template invalid: {string.Join("; ", validation.Errors)}");

        var graph = _decomposer.Decompose(request.Template);
        var runId = string.IsNullOrWhiteSpace(request.RunId) ? Guid.NewGuid().ToString("N") : request.RunId!;
        using var usage = new ProviderUsageScope(runId);
        var allowResume = !string.IsNullOrWhiteSpace(request.ResumeRunId);
        var resumeFailed = request.ResumeFailedStages || _options.ResumeFailedStages;
        var stageDefinitionsById = request.Template.Stages
            .ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
        var stageStates = graph.Nodes.ToDictionary(n => n.StageId, _ => PipelineStageRunState.Pending, StringComparer.Ordinal);
        var stageAttempts = graph.Nodes.ToDictionary(n => n.StageId, _ => 0, StringComparer.Ordinal);
        var stageRuns = graph.Nodes.ToDictionary(
            n => n.StageId,
            n => new PipelineStageRun
            {
                StageId = n.StageId,
                State = PipelineStageRunState.Pending,
                Attempt = 0,
                IdempotencyKey = Guid.NewGuid().ToString("N"),
                Effects = stageDefinitionsById[n.StageId].Effects,
                ExecutionFingerprint = PipelineOperationIdentity.Fingerprint(
                    request.Template.TemplateId, request.Template.Version, stageDefinitionsById[n.StageId], n, request.Inputs)
            },
            StringComparer.Ordinal);

        if (allowResume)
            await TryHydrateFromPriorRunAsync(request, stageStates, stageAttempts, stageRuns, resumeFailed, cancellationToken);

        var run = new PipelineRun
        {
            RunId = runId,
            TemplateId = request.Template.TemplateId,
            ResumeSourceRunId = allowResume ? request.ResumeRunId : null,
            State = PipelineRunState.Running,
            StartedAt = DateTimeOffset.UtcNow,
            StageRuns = stageRuns.Values.ToArray()
        };

        run = await _runStore.MergeAsync(
            runId,
            stored => PipelineRunPersistence.Create(stored, run),
            cancellationToken);
        _logger.LogInformation("Pipeline run {RunId} started for template {TemplateId}.", run.RunId, run.TemplateId);

        Task<PipelineRun> CheckpointAsync(PipelineRun next, CancellationToken token)
            => PersistAsync(runId, next with { ProviderUsageAttempts = usage.Snapshot() }, token);

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (stageStates.Values.All(s => s == PipelineStageRunState.Completed))
                {
                    run = await CheckpointAsync(
                        FinalizeRun(run, PipelineRunState.Completed, stageRuns),
                        cancellationToken);
                    _logger.LogInformation("Pipeline run {RunId} completed successfully.", run.RunId);
                    return run;
                }

                if (IsTerminalWithoutPending(stageStates))
                {
                    var hasAnyFailure = stageRuns.Values.Any(stageRun => stageRun.State == PipelineStageRunState.Failed);
                    var hasCriticalFailure = stageRuns.Values.Any(stageRun =>
                        stageRun.ReconciliationRequired ||
                        (stageRun.State == PipelineStageRunState.Failed &&
                         stageDefinitionsById.TryGetValue(stageRun.StageId, out var stageDefinition) &&
                         stageDefinition.Constraints.Critical));

                    var terminalState = ResolveTerminalState(hasAnyFailure, hasCriticalFailure, _options.CompletionPolicy);
                    run = await CheckpointAsync(
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
                    run = await CheckpointAsync(
                        FinalizeRun(run, PipelineRunState.Failed, stageRuns),
                        cancellationToken);
                    _logger.LogWarning("Pipeline run {RunId} stalled with no ready stages.", run.RunId);
                    return run;
                }

                foreach (var stageId in readyStageIds)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var node = graph.Nodes.Single(n => n.StageId == stageId);
                    var stage = stageDefinitionsById[stageId];

                    if (!CanStageExecute(node, stageStates))
                        continue;

                    var execution = await ExecuteWithRetryAndFallbackAsync(
                        runId,
                        stage,
                        node,
                        request.Inputs,
                        stageAttempts,
                        stageRuns[stageId].IdempotencyKey!,
                        async (workerType, attempt) =>
                        {
                            // Persist intent before dispatch. If the completion write is lost, resume
                            // sees an uncertain invocation rather than a stage that never ran.
                            stageStates[stageId] = PipelineStageRunState.Running;
                            stageRuns[stageId] = stageRuns[stageId] with
                            {
                                State = PipelineStageRunState.Running,
                                Attempt = attempt,
                                WorkerType = workerType,
                                ReconciliationRequired = !PipelineOperationIdentity.CanReplay(stage.Effects),
                                Output = null,
                                Error = null
                            };
                            run = await CheckpointAsync(UpdateRun(run, stageRuns, PipelineRunState.Running), cancellationToken);
                        },
                        cancellationToken);

                    stageRuns[stageId] = stageRuns[stageId] with
                    {
                        State = execution.Succeeded ? PipelineStageRunState.Completed : PipelineStageRunState.Failed,
                        Attempt = stageAttempts[stageId],
                        WorkerId = execution.WorkerId,
                        WorkerType = execution.WorkerType,
                        Output = execution.Output,
                        Error = execution.Error,
                        ReconciliationRequired = execution.ReconciliationRequired
                    };
                    stageStates[stageId] = stageRuns[stageId].State;
                    run = await CheckpointAsync(UpdateRun(run, stageRuns, PipelineRunState.Running), cancellationToken);

                    _logger.LogInformation(
                        "Pipeline stage {StageId} completed with state {State} on worker {WorkerType}.",
                        stageId,
                        stageRuns[stageId].State,
                        stageRuns[stageId].WorkerType);

                    if (execution.ReconciliationRequired ||
                        (stageRuns[stageId].State == PipelineStageRunState.Failed && stage.Constraints.Critical))
                    {
                        run = await CheckpointAsync(
                            FinalizeRun(run, PipelineRunState.Failed, stageRuns),
                            cancellationToken);
                        _logger.LogWarning("Stage {StageId} requires the run {RunId} to stop after failure.", stageId, run.RunId);
                        return run;
                    }
                }

                var scaleSnapshot = BuildScalingSnapshot(stageRuns.Values);
                var scaleDecision = _scalingPolicy.Evaluate(scaleSnapshot);
                _logger.LogDebug(
                    "Pipeline scaling decision: deterministic={DeterministicWorkers}, agentic={AgenticWorkers}.",
                    scaleDecision.DeterministicWorkers,
                    scaleDecision.AgenticWorkers);

                run = await CheckpointAsync(
                    UpdateRun(run, stageRuns, PipelineRunState.Running),
                    cancellationToken);
            }
        }
        catch
        {
            // Preserve the actual stored state: a failed write may have committed before its
            // acknowledgement was lost. Accounting must never roll that checkpoint back.
            try
            {
                await _runStore.MergeAsync(runId, stored =>
                {
                    _ = PipelineRunPersistence.Advance(stored, run);
                    return stored! with { ProviderUsageAttempts = usage.Snapshot() };
                }, CancellationToken.None);
            }
            catch (Exception accountingError)
            {
                _logger.LogWarning(accountingError, "Could not persist provider usage for interrupted pipeline run {RunId}.", runId);
            }
            throw;
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
    /// <param name="stageStates">Stage states to hydrate.</param>
    /// <param name="stageAttempts">Stage attempt counts to hydrate.</param>
    /// <param name="stageRuns">Stage runs to hydrate.</param>
    /// <param name="resumeFailed">Whether failed stages are carried forward too.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private async Task TryHydrateFromPriorRunAsync(
        PipelineExecutionRequest request,
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

        if (!string.Equals(prior.TemplateId, request.Template.TemplateId, StringComparison.Ordinal))
            throw new InvalidOperationException("Resume source belongs to a different pipeline template.");
        if (!Enum.IsDefined(prior.State))
            throw ReconciliationRequired("checkpoint", "The source contains an invalid run state.");
        if (prior.StageRuns == null || prior.StageRuns.Any(stage => stage == null || string.IsNullOrWhiteSpace(stage.StageId)))
            throw ReconciliationRequired("checkpoint", "The source must contain one record for every stage.");
        var priorIds = new HashSet<string>(prior.StageRuns.Select(stage => stage.StageId), StringComparer.Ordinal);
        if (priorIds.Count != prior.StageRuns.Count || !priorIds.SetEquals(stageStates.Keys))
            throw ReconciliationRequired("checkpoint", "Source stage identities must match the current graph exactly without duplicates.");

        foreach (var stage in prior.StageRuns)
        {
            var current = stageRuns[stage.StageId];
            if (!Enum.IsDefined(stage.State) || stage.Attempt < 0 ||
                (stage.Effects.HasValue && !Enum.IsDefined(stage.Effects.Value)))
                throw ReconciliationRequired(stage.StageId, "Checkpoint contains invalid execution metadata.");
            if (stage.State == PipelineStageRunState.Completed && string.IsNullOrWhiteSpace(stage.ExecutionFingerprint))
                throw ReconciliationRequired(stage.StageId, "Completed output lacks its original input and stage fingerprint.");
            if (stage.ExecutionFingerprint != null &&
                !string.Equals(stage.ExecutionFingerprint, current.ExecutionFingerprint, StringComparison.Ordinal))
                throw ReconciliationRequired(stage.StageId, "The stage, topology, or inputs changed since its operation key was created.");
            if (stage.Effects.HasValue && stage.Effects != current.Effects)
                throw ReconciliationRequired(stage.StageId, "The effect declaration changed since the checkpoint.");
            // Disabling failed-stage retries must not let a resumed workflow continue past
            // an unresolved unsafe effect while silently carrying its failure forward.
            if (stage.State != PipelineStageRunState.Completed && !PipelineOperationIdentity.CanReplay(current.Effects!.Value))
                throw ReconciliationRequired(stage.StageId, "Incomplete stages require an explicit replay-safe declaration.");

            var willReplay = stage.State != PipelineStageRunState.Completed &&
                             (stage.State != PipelineStageRunState.Failed || resumeFailed);
            if (willReplay)
            {
                // Even a pending source may still be executing in another host. Resuming a
                // snapshot is not an exclusive lease on the original external operation.
                if (current.Effects == PipelineStageEffects.Idempotent &&
                    (stage.Effects != PipelineStageEffects.Idempotent ||
                     string.IsNullOrWhiteSpace(stage.IdempotencyKey) || string.IsNullOrWhiteSpace(stage.ExecutionFingerprint)))
                    throw ReconciliationRequired(stage.StageId, "An idempotent stage requires its original persisted key and fingerprint.");
            }

            var carriedState = willReplay ? PipelineStageRunState.Pending : stage.State;
            stageStates[stage.StageId] = carriedState;
            stageAttempts[stage.StageId] = stage.Attempt;
            stageRuns[stage.StageId] = stage with
            {
                State = carriedState,
                IdempotencyKey = stage.IdempotencyKey ?? (willReplay ? current.IdempotencyKey : null),
                ExecutionFingerprint = stage.ExecutionFingerprint ?? (willReplay ? current.ExecutionFingerprint : null),
                Effects = stage.Effects ?? current.Effects,
                ReconciliationRequired = !willReplay && stage.ReconciliationRequired,
                Output = willReplay ? null : stage.Output,
                Error = willReplay ? null : stage.Error
            };
        }
    }

    private static InvalidOperationException ReconciliationRequired(string stageId, string reason)
        => new($"stage-reconciliation-required: Stage '{stageId}': {reason} Reconcile external effects before starting a new operation.");

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
        string idempotencyKey,
        Func<PipelineWorkerType, int, Task> beforeAttempt,
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
                maxAttempts: _options.MaxRetryAttempts,
                delay: TimeSpan.FromMilliseconds(_options.RetryDelayMs));
            var maxAttempts = retryPolicy.MaxAttempts;
            for (var localAttempt = 1; localAttempt <= maxAttempts; localAttempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                stageAttempts[stage.Id] = checked(stageAttempts[stage.Id] + 1);
                var globalAttempt = stageAttempts[stage.Id];
                await beforeAttempt(workerType, globalAttempt);

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
                    IdempotencyKey = idempotencyKey,
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

                if (!PipelineOperationIdentity.CanReplay(stage.Effects))
                {
                    return new ExecutionOutcome
                    {
                        Succeeded = false,
                        WorkerId = result.WorkerId,
                        WorkerType = workerType,
                        ReconciliationRequired = true,
                        Error = ReconciliationRequired(stage.Id, result.Error ?? "The invocation failed with an uncertain effect outcome.").Message
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
        public bool ReconciliationRequired { get; init; }
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
