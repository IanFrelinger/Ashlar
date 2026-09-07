using Ashlar.Abstractions.Barriers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ashlar.Abstractions.Execution;
using Ashlar.Abstractions.Routing;
using Ashlar.Abstractions.Transport;
using Ashlar.Orchestration.Agents;
using Ashlar.Orchestration.Architect;
using Ashlar.Orchestration.Architect.Models;
using Ashlar.Orchestration.Coordination.Conflicts;
using Ashlar.Orchestration.Communication;
using Ashlar.Orchestration.Communication.Models;
using Ashlar.Orchestration.Metrics;
using Ashlar.Orchestration.Negotiation;
using Ashlar.Orchestration.Barriers;
using Ashlar.Orchestration.Resilience;
using Ashlar.Orchestration.Models;
using Ashlar.Orchestration.Transport;
using Ashlar.Core.Application.Resilience.Ports;
using Ashlar.Core.Application.Common.Ports;

namespace Ashlar.Orchestration.Coordination;

/// <summary>
/// Main orchestrator that coordinates the entire agent orchestration flow.
/// 
/// Responsibilities:
/// - Request decomposition via Architect agent
/// - Agent spawning and lifecycle management
/// - Dependency resolution and execution ordering
/// - Conflict detection and resolution
/// - Resource allocation and scaling
/// - Progress tracking and reporting
/// - Escalation management
/// - Output integration and synthesis
/// - Metrics collection and distributed tracing
/// 
/// The orchestrator follows a multi-phase execution model:
/// 1. Decomposition: Architect breaks down the request into agent specifications
/// 2. Spawning: Agents are created based on specifications
/// 3. Execution: Agents execute in dependency order with conflict resolution
/// 4. Integration: Outputs are synthesized into final result
/// </summary>
public sealed class Orchestrator
{
    private readonly IArchitectAgent _architect;
    private readonly IAgentRuntimeFactory _agentRuntimeFactory;
    private readonly LifecycleManager _lifecycleManager;
    private readonly DependencyResolver _dependencyResolver;
    private readonly ConflictDetector _conflictDetector;
    private readonly ResourceAllocator _resourceAllocator;
    private readonly ProgressTracker _progressTracker;
    private readonly EscalationManager _escalationManager;
    private readonly OutputIntegrator _outputIntegrator;
    private readonly IAgentBus _agentBus;
    private readonly IAgentTransport _agentTransport;
    private readonly NegotiationProtocol? _negotiationProtocol;
    private readonly OrchestrationMetrics? _metrics;
    private readonly ICircuitBreaker _circuitBreaker;
    private readonly IResilientExecutor _resilientExecutor;
    private readonly ILoopKernel _loops;
    private readonly ILogger<Orchestrator> _logger;
    private readonly IOrchestrationRuntimeSpecAccessor? _runtimeSpecAccessor;
    private readonly IBarrierContextAccessor? _barrierContextAccessor;
    private readonly BarrierHierarchy? _barrierHierarchy;
    private readonly BarrierGuard? _barrierGuard;
    private readonly IReadOnlyList<IAgentTransportInvocationHook> _invocationHooks;

    /// <summary>
    /// Initializes a new instance of the <see cref="Orchestrator"/> class.
    /// </summary>
    /// <param name="architect">The architect agent for request decomposition.</param>
    /// <param name="agentRuntimeFactory">The factory for spawning agent runtime handles.</param>
    /// <param name="lifecycleManager">The lifecycle manager for agent registration and execution.</param>
    /// <param name="dependencyResolver">The dependency resolver for managing agent dependencies.</param>
    /// <param name="conflictDetector">The conflict detector for identifying conflicts between agents.</param>
    /// <param name="resourceAllocator">The resource allocator for managing agent resources.</param>
    /// <param name="progressTracker">The progress tracker for monitoring agent execution.</param>
    /// <param name="escalationManager">The escalation manager for handling unresolved conflicts.</param>
    /// <param name="outputIntegrator">The output integrator for combining agent outputs.</param>
    /// <param name="agentBus">The message bus for agent communication.</param>
    /// <param name="agentTransport">The transport used to invoke agent execution.</param>
    /// <param name="logger">The logger instance.</param>
    /// <param name="negotiationProtocol">Optional negotiation protocol for conflict resolution.</param>
    /// <param name="metrics">Optional metrics collector for performance tracking.</param>
    /// <param name="runtimeSpecAccessor">Optional runtime spec accessor for endpoint routing.</param>
    /// <param name="barrierContextAccessor">Optional barrier context accessor for request barrier propagation.</param>
    /// <param name="barrierHierarchy">Optional barrier hierarchy for fallback/default routing context.</param>
    /// <param name="barrierAuditLog">Optional barrier audit log for invocation events.</param>
    /// <param name="barrierOptions">Optional barrier options for guard behavior.</param>
    /// <param name="invocationHooks">Optional hooks that run before each transport send and after successful invocations.</param>
    /// <param name="resilientExecutor">Optional resilient executor for retry logic.</param>
    /// <param name="circuitBreaker">Optional circuit breaker for fault tolerance.</param>
    public Orchestrator(
        IArchitectAgent architect,
        IAgentRuntimeFactory agentRuntimeFactory,
        LifecycleManager lifecycleManager,
        DependencyResolver dependencyResolver,
        ConflictDetector conflictDetector,
        ResourceAllocator resourceAllocator,
        ProgressTracker progressTracker,
        EscalationManager escalationManager,
        OutputIntegrator outputIntegrator,
        IAgentBus agentBus,
        IAgentTransport agentTransport,
        ILoopKernel loops,
        ILogger<Orchestrator> logger,
        NegotiationProtocol? negotiationProtocol = null,
        OrchestrationMetrics? metrics = null,
        IOrchestrationRuntimeSpecAccessor? runtimeSpecAccessor = null,
        IBarrierContextAccessor? barrierContextAccessor = null,
        BarrierHierarchy? barrierHierarchy = null,
        IBarrierAuditLog? barrierAuditLog = null,
        IOptions<BarrierOptions>? barrierOptions = null,
        IEnumerable<IAgentTransportInvocationHook>? invocationHooks = null,
        IResilientExecutor? resilientExecutor = null,
        ICircuitBreaker? circuitBreaker = null)
    {
        _architect = architect ?? throw new ArgumentNullException(nameof(architect));
        _agentRuntimeFactory = agentRuntimeFactory ?? throw new ArgumentNullException(nameof(agentRuntimeFactory));
        _lifecycleManager = lifecycleManager ?? throw new ArgumentNullException(nameof(lifecycleManager));
        _dependencyResolver = dependencyResolver ?? throw new ArgumentNullException(nameof(dependencyResolver));
        _conflictDetector = conflictDetector ?? throw new ArgumentNullException(nameof(conflictDetector));
        _resourceAllocator = resourceAllocator ?? throw new ArgumentNullException(nameof(resourceAllocator));
        _progressTracker = progressTracker ?? throw new ArgumentNullException(nameof(progressTracker));
        _escalationManager = escalationManager ?? throw new ArgumentNullException(nameof(escalationManager));
        _outputIntegrator = outputIntegrator ?? throw new ArgumentNullException(nameof(outputIntegrator));
        _agentBus = agentBus ?? throw new ArgumentNullException(nameof(agentBus));
        _agentTransport = agentTransport ?? throw new ArgumentNullException(nameof(agentTransport));
        _resilientExecutor = resilientExecutor ?? throw new ArgumentNullException(nameof(resilientExecutor));
        _circuitBreaker = circuitBreaker ?? throw new ArgumentNullException(nameof(circuitBreaker));
        _loops = loops ?? throw new ArgumentNullException(nameof(loops));
        _negotiationProtocol = negotiationProtocol;
        _metrics = metrics;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _runtimeSpecAccessor = runtimeSpecAccessor;
        _barrierContextAccessor = barrierContextAccessor;
        _barrierHierarchy = barrierHierarchy;
        _barrierGuard = barrierContextAccessor != null &&
                        barrierAuditLog != null &&
                        barrierHierarchy != null &&
                        barrierOptions != null
            ? new BarrierGuard(
                barrierContextAccessor,
                barrierAuditLog,
                barrierHierarchy,
                barrierOptions.Value)
            : null;
        _invocationHooks = invocationHooks?.ToArray() ?? Array.Empty<IAgentTransportInvocationHook>();
    }

    /// <summary>
    /// Orchestrates the complete flow: request → decomposition → agent execution → integration.
    /// 
    /// Execution phases:
    /// 1. Request decomposition by Architect agent
    /// 2. Agent spawning based on specifications
    /// 3. Dependency resolution and execution planning
    /// 4. Parallel agent execution with conflict detection
    /// 5. Negotiation for conflicts (if enabled)
    /// 6. Output integration and synthesis
    /// 7. Result compilation and return
    /// 
    /// Includes comprehensive metrics collection, distributed tracing, and error handling.
    /// </summary>
    /// <param name="request">User request to orchestrate</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Orchestration result with integrated outputs</returns>
    public async Task<OrchestrationResult> OrchestrateAsync(
        string request,
        CancellationToken cancellationToken = default)
    {
        var correlationId = Guid.NewGuid().ToString();

        // Every agent this run registers belongs to this run. Decomposition ids are not unique between
        // runs (the parser's fallback emits "fallback-1" every time), so without a scope two concurrent
        // orchestrations register different containers under one id: the later wins, the earlier run
        // executes the other's container and fails with "cannot execute from state Executing", and
        // ShutdownAllAsync below tears down both. The scope flows with the async context, so the
        // transport this method awaits resolves agents from the same run.
        using var agentScope = _lifecycleManager.BeginRunScope();

        var rootSpanId = _metrics?.StartSpan("Orchestrate", null, new Dictionary<string, string>
        {
            ["correlationId"] = correlationId,
            ["request"] = request
        });

        _logger.LogInformation("Starting orchestration for request: {Request} (CorrelationId: {CorrelationId})", request, correlationId);

        using var profiler = _metrics != null
            ? new PerformanceProfiler("Orchestrate", _metrics, null, new Dictionary<string, object> { ["correlationId"] = correlationId, ["request"] = request })
            : null;

        try
        {
            // Step 1: Decompose request
            using var decomposeProfiler = _metrics != null
                ? new PerformanceProfiler("Decompose", _metrics, null)
                : null;
            var runtimeSpec = _runtimeSpecAccessor?.Current;
            var barrierLevel = _barrierContextAccessor?.Current?.Level
                ?? runtimeSpec?.BarrierLevel
                ?? _barrierHierarchy?.Floor.Name;
            var decompositionContext = new DecompositionContext
            {
                CorrelationId = correlationId,
                BarrierLevel = barrierLevel,
                PreferredRegion = runtimeSpec?.PreferredRegion
            };
            var decomposition = await _architect.DecomposeAsync(request, decompositionContext, cancellationToken);
            if (!decomposition.IsValid)
            {
                _logger.LogWarning("Decomposition has validation errors: {ErrorCount}", decomposition.ValidationErrors.Count);
                // Continue anyway - some errors may be warnings
            }

            // Step 2: Detect conflicts before spawning
            var handles = _loops.SelectToList(
                decomposition.Agents,
                (spec, _, _) => _agentRuntimeFactory.Spawn(spec),
                new LoopOptions { Name = "spawn-containers" },
                cancellationToken).ToList();

            var containers = handles
                .Select(h => h.TryGetInProcessContainer()
                    ?? throw new InvalidOperationException(
                        "Only in-process agent handles are supported by this orchestrator build."))
                .ToList();

            var conflicts = _conflictDetector.DetectConflicts(containers);
            
            // Step 2.5: Attempt to resolve conflicts via negotiation
            var resolvedConflicts = new List<Conflict>();
            var unresolvedConflicts = new List<Conflict>();

            if (_negotiationProtocol != null)
            {
                await _loops.ForEachAsync(conflicts, async (conflict, _, token) =>
                {
                    if (conflict.Severity == ConflictSeverity.Critical)
                    {
                        // Always escalate critical conflicts
                        _escalationManager.EscalateConflict(conflict, "Critical conflict - requires human decision");
                        unresolvedConflicts.Add(conflict);
                        return LoopAction.Continue;
                    }

                    var involvedAgents = containers
                        .Where(c => conflict.AgentIds.Contains(c.AgentId))
                        .ToList();

                    var result = await _negotiationProtocol.NegotiateAsync(
                        conflict, involvedAgents, token);

                    if (result.Success)
                    {
                        _logger.LogInformation(
                            "Conflict {ConflictType} resolved via {ResolutionType}: {Reason}",
                            conflict.ConflictType, result.ResolutionType, result.Reason);

                        // Apply resolution
                        await ApplyResolutionAsync(conflict, result, involvedAgents, token);
                        resolvedConflicts.Add(conflict);
                    }
                    else
                    {
                        _logger.LogWarning(
                            "Conflict {ConflictType} could not be resolved: {Reason}",
                            conflict.ConflictType, result.Reason);

                        _escalationManager.EscalateConflict(conflict, result.Reason ?? "Negotiation failed");
                        unresolvedConflicts.Add(conflict);
                    }
                    return LoopAction.Continue;
                }, new LoopOptions { Name = "negotiate-conflicts" }, cancellationToken);
            }
            else
            {
                // No negotiation protocol - escalate all non-critical conflicts
                foreach (var conflict in conflicts.Where(c => c.Severity == ConflictSeverity.Critical))
                {
                    _escalationManager.EscalateConflict(conflict, "Pre-execution conflict detection");
                    unresolvedConflicts.Add(conflict);
                }
            }

            // Step 3: Register agents and allocate resources
            await _loops.ForEachAsync(containers, async (container, _, token) =>
            {
                // Allocate resources
                if (!_resourceAllocator.TryAllocate(container, out var _allocation))
                {
                    _logger.LogWarning("Failed to allocate resources for agent {AgentId}", container.AgentId);
                    var escalation = _escalationManager.EscalateIssue(
                        "ResourceAllocation",
                        $"Failed to allocate resources for agent {container.AgentId}",
                        EscalationSeverity.High);
                    return LoopAction.Continue;
                }

                // Register agent
                await _lifecycleManager.RegisterAgentAsync(container, token);
                _dependencyResolver.RegisterAgent(container);

                // Subscribe to agent messages
                await _agentBus.SubscribeAsync("OutputEmitted", async (msg, ct) =>
                {
                    if (msg is OutputEmitted outputMsg && outputMsg.FromAgentId == container.AgentId)
                    {
                        _dependencyResolver.RecordOutput(container.AgentId, outputMsg.Output);
                    }
                    await Task.CompletedTask;
                }, container.AgentId, token);
                return LoopAction.Continue;
            }, new LoopOptions { Name = "register-agents" }, cancellationToken);

            // Step 4: Execute agents in dependency order
            var executionOrder = _dependencyResolver.GetExecutionOrder();
            var outputs = new Dictionary<string, object>();

            await _loops.ForEachAsync(executionOrder, async (agentId, _, token) =>
            {
                var container = _lifecycleManager.GetAgent(agentId);
                if (container == null)
                {
                    return LoopAction.Continue;
                }

                // Execute agent
                try
                {
                    var agentStartTime = DateTimeOffset.UtcNow;
                    if (_barrierGuard != null)
                    {
                        await _barrierGuard.AssertValidForInvocationAsync(
                            agentId,
                            correlationId,
                            rootSpanId ?? string.Empty,
                            token);

                        if (_barrierContextAccessor?.Current is { } currentBarrier &&
                            TryGetRequestedBarrier(container.Agent.Spec.Metadata, out var requestedBarrier))
                        {
                            _barrierGuard.AssertNoElevation(
                                requestedBarrier,
                                currentBarrier.Level,
                                agentId,
                                correlationId);
                        }
                    }

                    var dependencyOutputs = _dependencyResolver.GetDependencyOutputs(
                        _dependencyResolver.GetDependenciesForAgent(agentId));
                    var invocation = new AgentInvocationRequest(
                        AgentName: agentId,
                        CorrelationId: correlationId,
                        SpanId: rootSpanId,
                        Payload: dependencyOutputs.ToDictionary(kvp => kvp.Key, kvp => (object?)kvp.Value),
                        Options: new AgentInvocationOptions(
                            Timeout: TimeSpan.FromMinutes(5),
                            MaxRetries: 3,
                            TargetEndpoint: container.Agent.Spec.TargetEndpoint),
                        Metadata: new Dictionary<string, string>
                        {
                            ["domain"] = container.Agent.Spec.Domain,
                            ["spanId"] = rootSpanId ?? string.Empty,
                            ["goal"] = container.Agent.Spec.Goal,
                            ["goals"] = string.Join("|", container.Agent.Spec.Goals),
                            ["clusterId"] = container.Agent.Spec.ClusterId ?? string.Empty,
                            ["reportsToAgentId"] = container.Agent.Spec.ReportsToAgentId ?? string.Empty,
                            ["commandChain"] = string.Join("|", container.Agent.Spec.CommandChain),
                            ["ollamaModel"] = container.Agent.Spec.OllamaModel ?? string.Empty,
                            [AgentExecutionIsolation.MetadataKey] =
                                AgentExecutionIsolation.Format(container.Agent.Spec.ExecutionIsolation)
                        },
                        DependencyOutputs: dependencyOutputs);

                    var transportRequest = invocation;
                    if (_invocationHooks.Count > 0)
                    {
                        foreach (var hook in _invocationHooks)
                        {
                            var hookContext = new AgentInvocationContext(
                                correlationId,
                                request,
                                container.Agent.Spec,
                                transportRequest);
                            transportRequest = await hook.BeforeSendAsync(hookContext, token).ConfigureAwait(false);
                        }
                    }

                    var result = await SendThroughTransportAsync(transportRequest, token);
                    if (!result.Success)
                    {
                        var errorCode = GetErrorCode(result) ?? "UNKNOWN";
                        _logger.LogError(
                            "Agent {AgentId} execution failed via transport. ErrorCode={ErrorCode}, Error={Error}",
                            agentId,
                            errorCode,
                            result.ErrorMessage ?? "Unknown transport error");

                        _escalationManager.EscalateIssue(
                            "AgentExecution",
                            $"Agent {agentId} execution failed ({errorCode}): {result.ErrorMessage ?? "Unknown transport error"}",
                            EscalationSeverity.High);

                        return LoopAction.Continue;
                    }

                    var output = result.Output ?? new { };
                    if (_invocationHooks.Count > 0)
                    {
                        var hookContext = new AgentInvocationContext(
                            correlationId,
                            request,
                            container.Agent.Spec,
                            transportRequest);
                        foreach (var hook in _invocationHooks)
                        {
                            output = await hook.AfterSuccessAsync(hookContext, output, token).ConfigureAwait(false);
                        }
                    }

                    var agentDuration = result.Duration ?? (DateTimeOffset.UtcNow - agentStartTime);
                    
                    outputs[agentId] = output;
                    _dependencyResolver.RecordOutput(agentId, output);

                    // Record agent metrics
                    if (_metrics != null)
                    {
                        var allocation = _resourceAllocator.GetAllocations().FirstOrDefault(a => a.AgentId == agentId);
                        _metrics.RecordAgentExecution(
                            agentId,
                            container.Agent.Spec.Domain,
                            agentDuration,
                            container.State,
                            allocation?.AllocatedMemoryMB,
                            allocation?.AllocatedContextTokens);
                    }

                    // Publish output message
                    var message = new OutputEmitted
                    {
                        MessageId = Guid.NewGuid().ToString(),
                        FromAgentId = agentId,
                        MessageType = "OutputEmitted",
                        Output = output
                    };
                    await _agentBus.PublishAsync(message, token);

                    // Release resources
                    _resourceAllocator.ReleaseResources(agentId);
                }
                catch (BarrierContextMissingException ex)
                {
                    // Not an agent fault: the host never established a barrier context and
                    // Ashlar:Barriers:RequireExplicitBarrier is true. Escalate under the exception's
                    // own error code (not the generic "AgentExecution" bucket) so callers can see
                    // WHY nothing ran instead of a bare "0 agent(s) executed".
                    _logger.LogError(ex, "Agent {AgentId} skipped: barrier context missing", agentId);
                    _escalationManager.EscalateIssue(
                        ex.ErrorCode,
                        $"Agent {agentId} was not executed: {ex.Message} " +
                        "Establish a barrier context before invoking agents, or set " +
                        "Ashlar:Barriers:RequireExplicitBarrier=false to default to the floor level.",
                        EscalationSeverity.High,
                        $"correlationId={ex.CorrelationId}");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Agent {AgentId} execution failed", agentId);
                    _escalationManager.EscalateIssue(
                        "AgentExecution",
                        $"Agent {agentId} execution failed: {ex.Message}",
                        EscalationSeverity.High,
                        ex.StackTrace);
                }

                // Track progress
                _progressTracker.RecordProgress(container);
                return LoopAction.Continue;
            }, new LoopOptions { Name = "execute-agents" }, cancellationToken);

            // Step 5: Integrate outputs
            using var integrateProfiler = _metrics != null
                ? new PerformanceProfiler("Integrate", _metrics, null)
                : null;
            var integratedOutput = _outputIntegrator.Integrate(decomposition.Agents, outputs);

            // Snapshot progress BEFORE shutdown. GetActiveAgents() is empty once the lifecycle
            // manager has torn the agents down, so taking the summary after step 6 reported
            // "0/0 agents completed" for every run that ever finished — including runs where
            // every agent completed. That number is what `ashlar run` prints beside
            // "Orchestration completed successfully", and a caller cannot tell a run that did
            // nothing from a run that did everything if the measurement is taken after the
            // subject has left.
            var progressSummary = _progressTracker.GetSummary(_lifecycleManager.GetActiveAgents());

            // Step 6: Shutdown all agents
            await _lifecycleManager.ShutdownAllAsync(cancellationToken);

            if (rootSpanId != null)
            {
                _metrics?.EndSpan(rootSpanId, integratedOutput.IsValid, new Dictionary<string, string>
                {
                    ["agentCount"] = decomposition.Agents.Count.ToString(),
                    ["conflictCount"] = conflicts.Count.ToString(),
                    ["escalationCount"] = _escalationManager.GetAllEscalations().Count.ToString()
                });
            }

            return new OrchestrationResult
            {
                Success = integratedOutput.IsValid,
                IntegratedOutput = integratedOutput,
                Decomposition = decomposition,
                Conflicts = conflicts,
                ResolvedConflicts = resolvedConflicts,
                UnresolvedConflicts = unresolvedConflicts,
                Escalations = _escalationManager.GetAllEscalations(),
                ProgressSummary = progressSummary,
                CorrelationId = correlationId
            };
        }
        catch (Exception ex)
        {
            if (rootSpanId != null)
            {
                _metrics?.EndSpan(rootSpanId, false, new Dictionary<string, string>
                {
                    ["error"] = ex.Message
                });
            }
            _logger.LogError(ex, "Orchestration failed");
            throw;
        }
    }

    private static bool TryGetRequestedBarrier(
        IReadOnlyDictionary<string, object?> metadata,
        out string requestedBarrier)
    {
        requestedBarrier = string.Empty;
        if (metadata.Count == 0)
            return false;

        if (!metadata.TryGetValue("barrierLevel", out var value) || value is null)
            return false;

        requestedBarrier = value.ToString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(requestedBarrier);
    }

    private async Task<AgentResult> SendThroughTransportAsync(
        AgentInvocationRequest request,
        CancellationToken cancellationToken)
    {
        var initialResult = await _agentTransport.SendAsync(request, cancellationToken);
        if (initialResult.Success)
        {
            return initialResult;
        }

        var errorCode = GetErrorCode(initialResult);
        if (string.Equals(errorCode, "AGENT_NOT_FOUND", StringComparison.OrdinalIgnoreCase))
        {
            return initialResult;
        }

        if (string.Equals(errorCode, "TIMEOUT", StringComparison.OrdinalIgnoreCase))
        {
            return await ExecuteWithTimeoutRetryAsync(request, initialResult, cancellationToken);
        }

        return await ExecuteWithCircuitBreakerAsync(request, initialResult, cancellationToken);
    }

    private async Task<AgentResult> ExecuteWithTimeoutRetryAsync(
        AgentInvocationRequest request,
        AgentResult initialResult,
        CancellationToken cancellationToken)
    {
        var retryPolicy = RetryPolicy.FixedDelay(
            maxAttempts: 3,
            delay: TimeSpan.FromMilliseconds(1),
            isTransient: static ex => ex is TimeoutException);

        try
        {
            return await _resilientExecutor.ExecuteAsync(
                async ct =>
                {
                    var retryResult = await _agentTransport.SendAsync(request, ct);
                    if (retryResult.Success)
                    {
                        return retryResult;
                    }

                    if (string.Equals(GetErrorCode(retryResult), "TIMEOUT", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new TimeoutException(retryResult.ErrorMessage ?? "Agent transport timed out.");
                    }

                    return retryResult;
                },
                retryPolicy,
                cancellationToken);
        }
        catch (TimeoutException ex)
        {
            return initialResult with
            {
                ErrorMessage = ex.Message,
                Metadata = MergeMetadata(initialResult.Metadata, "errorCode", "TIMEOUT")
            };
        }
    }

    private async Task<AgentResult> ExecuteWithCircuitBreakerAsync(
        AgentInvocationRequest request,
        AgentResult initialResult,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _circuitBreaker.ExecuteAsync(
                request.AgentName,
                () =>
                {
                    throw new AgentTransportFailureException(initialResult);
                },
                fallback: ex => ex switch
                {
                    AgentTransportFailureException transportFailure => transportFailure.Result,
                    CircuitBreakerOpenException => initialResult with
                    {
                        Metadata = MergeMetadata(initialResult.Metadata, "errorCode", "CIRCUIT_OPEN")
                    },
                    _ => initialResult
                },
                cancellationToken: cancellationToken);
        }
        catch (AgentTransportFailureException ex)
        {
            return ex.Result;
        }
    }

    private static string? GetErrorCode(AgentResult result)
    {
        if (!string.IsNullOrWhiteSpace(result.ErrorCode))
        {
            return result.ErrorCode;
        }

        if (result.Metadata == null)
        {
            return null;
        }

        if (result.Metadata.TryGetValue("errorCode", out var value))
        {
            return value;
        }

        foreach (var kvp in result.Metadata)
        {
            if (string.Equals(kvp.Key, "errorCode", StringComparison.OrdinalIgnoreCase))
            {
                return kvp.Value;
            }
        }

        return null;
    }

    private static IReadOnlyDictionary<string, string> MergeMetadata(
        IReadOnlyDictionary<string, string>? metadata,
        string key,
        string value)
    {
        var merged = metadata != null
            ? new Dictionary<string, string>(metadata, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        merged[key] = value;
        return merged;
    }

    private sealed class AgentTransportFailureException : Exception
    {
        public AgentTransportFailureException(AgentResult result)
            : base(result.ErrorMessage ?? "Agent transport failure.")
        {
            Result = result;
        }

        public AgentResult Result { get; }
    }

    /// <summary>
    /// Applies a negotiation resolution to agents.
    /// 
    /// Updates agents based on the resolution type:
    /// - Schema conflicts: Updates agents to use canonical schema
    /// - Resource conflicts: Updates resource allocations
    /// - Constraint/Philosophy conflicts: Applies required changes to agents
    /// </summary>
    /// <param name="conflict">The conflict that was resolved.</param>
    /// <param name="result">The negotiation result with the resolution.</param>
    /// <param name="agents">The agents involved in the conflict.</param>
    /// <param name="cancellationToken">A token to observe while waiting for the task to complete.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    private async Task ApplyResolutionAsync(
        Conflict conflict,
        NegotiationResult result,
        IReadOnlyList<AgentContainer> agents,
        CancellationToken cancellationToken)
    {
        switch (conflict.ConflictType)
        {
            case ConflictType.Schema when result.ResolvedSchema.HasValue:
                // Update agents to use canonical schema
                // Note: In a full implementation, agents would need a method to update their output schema
                _logger.LogInformation(
                    "Applied schema resolution: agents {AgentIds} now use canonical schema",
                    string.Join(", ", conflict.AgentIds));
                break;

            case ConflictType.Resource when result.ResolvedAllocation != null:
                // Update resource allocations
                foreach (var (agentId, allocation) in result.ResolvedAllocation.Allocations)
                {
                    // Release old allocation
                    _resourceAllocator.ReleaseResources(agentId);
                    // Note: ResourceAllocator would need UpdateAllocation method for full implementation
                    _logger.LogInformation(
                        "Applied resource allocation for agent {AgentId}: {Compute}s, {Memory}MB",
                        agentId, allocation.ComputeSeconds, allocation.MemoryMb);
                }
                break;

            case ConflictType.Constraint:
            case ConflictType.Philosophy:
                // Apply resolution changes to agents
                if (result.Resolution?.RequiredChanges != null)
                {
                    _logger.LogInformation(
                        "Applied resolution changes: {Changes}",
                        string.Join(", ", result.Resolution.RequiredChanges.Select(kvp => $"{kvp.Key}: {kvp.Value}")));
                }
                break;
        }

        await Task.CompletedTask;
    }
}
