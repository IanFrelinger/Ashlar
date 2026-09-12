using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Ashlar.Core.Application.Bricks;
using Ashlar.Core.Application.Common.Ports;
using Ashlar.Core.Application.Execution.Models;
using Ashlar.Core.Application.Execution.Ports;
using Ashlar.Core.Domain.Agents;
using Ashlar.Core.Domain.Bricks;
using Ashlar.Core.Domain.Behaviors;
using Ashlar.Core.Domain.Execution;
using Ashlar.Core.Domain.Execution.Events;
using Ashlar.Core.Domain.Workflows;

namespace Ashlar.Infrastructure.Execution;

/// <summary>
/// Executes behaviors by running their brick steps in sequence.
/// Implements Core.Domain.Execution.IBehaviorExecutor (used by Guide and other consumers).
/// </summary>
public class BehaviorExecutor : Ashlar.Core.Domain.Execution.IBehaviorExecutor
{
    private readonly Ashlar.Core.Domain.Execution.IBrickRegistry _brickRegistry;
    private readonly IProviderFactory _providerFactory;
    private readonly IAgenticBrickEngine? _agenticBrickEngine;
    private readonly ISemanticCache _semanticCache;
    private readonly ILoopKernel _loops;
    private readonly ILogger<BehaviorExecutor> _logger;
    private readonly IStepExecutionMode? _stepExecutionMode;
    private readonly IMetricsCollector? _metricsCollector;
    
    /// <summary>
    /// Creates a new behavior executor.
    /// </summary>
    /// <param name="brickRegistry">Registry of available bricks.</param>
    /// <param name="providerFactory">Factory for LLM providers.</param>
    /// <param name="semanticCache">Semantic cache for brick outputs.</param>
    /// <param name="loops">Loop kernel for step iteration.</param>
    /// <param name="logger">Logger for diagnostics.</param>
    /// <param name="agenticBrickEngine">Optional NCR-backed model resolution for agentic execution.</param>
    /// <param name="stepExecutionMode">Optional runtime mode override for steps.</param>
    /// <param name="metricsCollector">Optional telemetry collector for execution metrics.</param>
    public BehaviorExecutor(
        Ashlar.Core.Domain.Execution.IBrickRegistry brickRegistry,
        IProviderFactory providerFactory,
        ISemanticCache semanticCache,
        ILoopKernel loops,
        ILogger<BehaviorExecutor> logger,
        IAgenticBrickEngine? agenticBrickEngine = null,
        IStepExecutionMode? stepExecutionMode = null,
        IMetricsCollector? metricsCollector = null)
    {
        _brickRegistry = brickRegistry;
        _providerFactory = providerFactory;
        _agenticBrickEngine = agenticBrickEngine;
        _semanticCache = semanticCache;
        _loops = loops;
        _logger = logger;
        _stepExecutionMode = stepExecutionMode;
        _metricsCollector = metricsCollector;
    }
    
    /// <inheritdoc />
    public async Task<Ashlar.Core.Domain.Execution.BehaviorResult> ExecuteAsync(
        AgentCard agent,
        Behavior behavior,
        BehaviorInput input,
        ExecutionOptions options,
        CancellationToken cancellationToken = default)
    {
        var outputs = new Dictionary<string, object>();
        var errors = new List<string>();
        var stopwatch = Stopwatch.StartNew();
        
        await foreach (var evt in ExecuteWithEventsAsync(agent, behavior, input, options, cancellationToken))
        {
            if (evt is BehaviorCompletedEvent completed)
            {
                outputs = new Dictionary<string, object>(completed.Outputs);
            }
            else if (evt is StepErrorEvent error)
            {
                errors.Add($"{error.StepId}: {error.Error}");
            }
        }
        
        stopwatch.Stop();
        
        return new Ashlar.Core.Domain.Execution.BehaviorResult
        {
            Success = errors.Count == 0,
            Outputs = outputs,
            Errors = errors,
            Duration = stopwatch.Elapsed
        };
    }
    
    /// <inheritdoc />
    public async IAsyncEnumerable<ExecutionEvent> ExecuteWithEventsAsync(
        AgentCard agent,
        Behavior behavior,
        BehaviorInput input,
        ExecutionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateUnbounded<ExecutionEvent>();

        _ = Task.Run(async () =>
        {
            var writer = channel.Writer;
            try
            {
                var context = new ExecutionContext
                {
                    AgentId = agent.Id,
                    BehaviorId = behavior.Id,
                    IsAirGapped = options.IsAirGapped,
                    AuditMode = options.AuditMode,
                    Provider = options.Provider,
                    Variables = new Dictionary<string, object>(input.Parameters)
                };

                await writer.WriteAsync(new BehaviorStartedEvent(behavior.Id, behavior.Name, DateTime.UtcNow), cancellationToken);

                // Tracks steps that errored, so the final BehaviorCompletedEvent.Success reflects
                // reality. It used to be hardcoded true — a behavior reported success even when
                // steps failed. Captured by the loop closure below.
                var stepFailures = 0;

                await _loops.ForEachAsync(behavior.Steps, async (step, stepIndex, ct) =>
                {
                    if (ct.IsCancellationRequested)
                    {
                        await writer.WriteAsync(new BehaviorCancelledEvent(behavior.Id), ct);
                        return LoopAction.Break;
                    }

                    // Check step condition. Distinguish a recognised-false condition (a legitimate
                    // skip) from unsupported syntax. The latter used to silently map to false and
                    // skip the step with no signal — an authoring error that vanished. Now it is
                    // surfaced as a step error and honours OnStepFailure.
                    if (step.Condition != null)
                    {
                        if (!TryEvaluateCondition(step.Condition, out var conditionMet))
                        {
                            await writer.WriteAsync(new StepErrorEvent(step.Id, $"Unsupported step condition syntax: '{step.Condition}'"), ct);
                            stepFailures++;
                            return behavior.OnStepFailure == FailurePolicy.Abort ? LoopAction.Break : LoopAction.Continue;
                        }
                        if (!conditionMet)
                        {
                            await writer.WriteAsync(new StepSkippedEvent(step.Id, step.Condition), ct);
                            return LoopAction.Continue;
                        }
                    }

                    var brick = _brickRegistry.GetBrick(step.BrickId);
                    if (brick == null)
                    {
                        await writer.WriteAsync(new StepErrorEvent(step.Id, $"DomainBrick not found: {step.BrickId}"), ct);
                        stepFailures++;
                        return behavior.OnStepFailure == FailurePolicy.Abort ? LoopAction.Break : LoopAction.Continue;
                    }

                    // Map inputs once per step (implementation swapping should use same inputs).
                    var brickInput = MapInputs(step.InputMapping, context);
                    BrickInputDefaults.Apply(brick, brickInput);

                    // Determine implementation chain (preference + fallback), filtering for availability.
                    var chain = ResolveImplementationChain(step, brick, behavior, options, context);
                    if (chain.Count == 0)
                    {
                        await writer.WriteAsync(new StepErrorEvent(step.Id, "No available implementation"), ct);
                        stepFailures++;
                        return behavior.OnStepFailure == FailurePolicy.Abort ? LoopAction.Break : LoopAction.Continue;
                    }

                    BrickOutput? output = null;
                    string? error = null;
                    var usedFallback = false;

                    await _loops.ForEachAsync(chain, async (implementation, _, ct2) =>
                    {
                        await writer.WriteAsync(new StepStartedEvent(
                            step.Id,
                            brick.Id,
                            brick.Name,
                            implementation,
                            usedFallback,
                            stepIndex,
                            behavior.Steps.Count), ct2);

                        var stopwatch = Stopwatch.StartNew();

                        var cacheKey = ComputeCacheKey(brick.Id, brickInput, implementation);
                        var cached = await _semanticCache.GetAsync(cacheKey, ct2);

                        if (cached != null)
                        {
                            output = cached;
                            await writer.WriteAsync(new CacheHitEvent(step.Id, cacheKey), ct2);
                        }
                        else
                        {
                            var executionStart = DateTimeOffset.UtcNow;
                            Ashlar.Core.Application.NodeCapabilityRuntime.Models.ModelResolution? resolution = null;
                            try
                            {
                                if (implementation == ImplementationType.Agentic && _agenticBrickEngine is not null)
                                {
                                    resolution = await _agenticBrickEngine.ResolveModelForBrickAsync(brick, context, ct2);
                                    if (resolution.Target == Ashlar.Core.Application.NodeCapabilityRuntime.Models.InferenceTarget.Escalate)
                                    {
                                        var modelId = string.IsNullOrWhiteSpace(resolution.Model.Id)
                                            ? null
                                            : resolution.Model.Id;
                                        var provider = resolution.Model.Provider ?? resolution.Model.ProviderModelId ?? context.Provider;
                                        error = $"Agentic execution escalated: {resolution.Reason}";
                                        _metricsCollector?.IncrementCounter($"ncr.execution.escalated.{resolution.Reason}");
                                        await writer.WriteAsync(
                                            new AgenticEscalatedEvent(step.Id, brick.Id, resolution.Reason.ToString(), modelId, provider),
                                            ct2);
                                        await _agenticBrickEngine.RecordExecutionOutcomeAsync(
                                            resolution,
                                            new BrickExecutionOutcome
                                            {
                                                Succeeded = false,
                                                Duration = DateTimeOffset.UtcNow - executionStart,
                                                ErrorCode = "Escalated"
                                            },
                                            ct2);
                                    }
                                    else
                                    {
                                        var provider = resolution.Model.Provider ?? resolution.Model.ProviderModelId ?? context.Provider;
                                        if (!string.IsNullOrWhiteSpace(provider))
                                        {
                                            var stepContext = new OverrideProviderExecutionContext(context, provider);
                                            output = await brick.ExecuteAsync(brickInput, implementation, stepContext, ct2);
                                        }
                                        else
                                        {
                                            output = await brick.ExecuteAsync(brickInput, implementation, context, ct2);
                                        }
                                    }
                                }
                                else
                                {
                                    output = await brick.ExecuteAsync(brickInput, implementation, context, ct2);
                                }
                                if (output is not null)
                                {
                                    await _semanticCache.SetAsync(cacheKey, output, ct2);
                                }

                                if (resolution is not null && _agenticBrickEngine is not null && output is not null)
                                {
                                    await _agenticBrickEngine.RecordExecutionOutcomeAsync(
                                        resolution,
                                        new BrickExecutionOutcome
                                        {
                                            Succeeded = true,
                                            Duration = DateTimeOffset.UtcNow - executionStart
                                        },
                                        ct2);
                                }
                            }
                            catch (Exception ex)
                            {
                                output = null;
                                error = ex.Message;
                                _logger.LogError(ex, "Error executing step {StepId} with {Implementation}", step.Id, implementation);
                                if (resolution is not null && _agenticBrickEngine is not null)
                                {
                                    await _agenticBrickEngine.RecordExecutionOutcomeAsync(
                                        resolution,
                                        new BrickExecutionOutcome
                                        {
                                            Succeeded = false,
                                            TimedOut = ex is TimeoutException,
                                            Duration = DateTimeOffset.UtcNow - executionStart,
                                            ErrorCode = ex.GetType().Name
                                        },
                                        ct2);
                                }
                            }
                        }

                        stopwatch.Stop();

                        if (output != null)
                        {
                            _metricsCollector?.RecordExecutionTime("ncr.execution.step.duration", stopwatch.Elapsed);
                            MapOutputs(step.OutputMapping, output, context);
                            await writer.WriteAsync(new StepCompletedEvent(
                                step.Id,
                                brick.Id,
                                implementation,
                                stopwatch.ElapsedMilliseconds,
                                output.Summary), ct2);

                            error = null;
                            return LoopAction.Break;
                        }

                        await writer.WriteAsync(new StepErrorEvent(step.Id, error ?? "Step failed", stopwatch.ElapsedMilliseconds), ct2);
                        _metricsCollector?.IncrementCounter("ncr.execution.step.error");

                        if (!options.SwapOnFailure)
                        {
                            return LoopAction.Break;
                        }

                        usedFallback = true;
                        return LoopAction.Continue;
                    }, new LoopOptions { Name = "behavior-step-impl-chain" }, ct);

                    // The step failed if no implementation in the chain produced output.
                    if (error != null && output == null)
                    {
                        stepFailures++;
                        if (behavior.OnStepFailure == FailurePolicy.Abort)
                        {
                            return LoopAction.Break;
                        }
                    }

                    return LoopAction.Continue;
                }, new LoopOptions { Name = "behavior-steps" }, cancellationToken);

                var success = EvaluateSuccessCriteria(behavior.SuccessCriteria, stepFailures == 0);
                await writer.WriteAsync(new BehaviorCompletedEvent(behavior.Id, success, context.Variables), cancellationToken);
            }
            catch (Exception ex)
            {
                // If something unexpected happens, emit a final failure event via StepError with synthetic step id.
                await channel.Writer.WriteAsync(new StepErrorEvent("behavior", ex.Message), CancellationToken.None);
            }
            finally
            {
                writer.TryComplete();
            }
        }, cancellationToken);

        await foreach (var evt in channel.Reader.ReadAllAsync(cancellationToken))
        {
            yield return evt;
        }
    }
    
    private IReadOnlyList<ImplementationType> ResolveImplementationChain(
        BehaviorStep step,
        DomainBrick brick,
        Behavior behavior,
        ExecutionOptions options,
        ExecutionContext context)
    {
        // Runtime hot-swap override (takes effect on next execution)
        if (_stepExecutionMode != null)
        {
            var mode = _stepExecutionMode.GetMode(step.Id);
            var impl = mode == ExecutionMode.Agentic ? ImplementationType.Agentic : ImplementationType.Deterministic;
            var chain = BuildChain(impl, options, brick, context);
            if (chain.Count > 0)
                return chain;
            if (mode == ExecutionMode.Agentic && !brick.Implementations.HasAgentic)
            {
                _logger?.LogWarning("Step {StepId} set to Agentic but no agent registered; reverting to Deterministic", step.Id);
                return BuildChain(ImplementationType.Deterministic, options, brick, context);
            }
        }

        // Always force deterministic in air-gapped.
        if (options.IsAirGapped || context.IsAirGapped)
        {
            return IsImplementationAvailable(brick, ImplementationType.Deterministic, context)
                ? new[] { ImplementationType.Deterministic }
                : Array.Empty<ImplementationType>();
        }

        // Behavior mode override
        var behaviorMode = options.BehaviorOverrides.TryGetValue(behavior.Id, out var m) ? m : options.ImplementationMode;

        // Highest priority: explicit step implementation.
        if (step.Implementation != ImplementationType.Auto)
        {
            return BuildChain(step.Implementation, options, brick, context);
        }

        // Next: per-brick override (by brick id).
        if (options.BrickOverrides.TryGetValue(brick.Id, out var forced))
        {
            return BuildChain(forced, options, brick, context);
        }

        // Next: runtime per-brick prefer/fallback.
        if (options.BrickRuntime.TryGetValue(brick.Id, out var runtimeSpec))
        {
            var preferred = PreferToImplementation(runtimeSpec.Prefer, brick, context, behaviorMode);
            return BuildChain(preferred, options, brick, context, runtimeSpec);
        }

        // Global mode-based selection.
        var selected = behaviorMode switch
        {
            ImplementationMode.DeterministicOnly => ImplementationType.Deterministic,
            ImplementationMode.AgenticPreferred => ImplementationType.Agentic,
            _ => brick.Selector != null ? brick.Selector.Select(context) : brick.DefaultImplementation
        };

        return BuildChain(selected, options, brick, context);
    }
    
    private bool IsImplementationAvailable(
        DomainBrick brick, 
        ImplementationType implementation, 
        ExecutionContext context)
    {
        return implementation switch
        {
            ImplementationType.Deterministic => brick.Implementations.HasDeterministic,
            ImplementationType.Agentic => brick.Implementations.HasAgentic && 
                                          !context.IsAirGapped &&
                                          _providerFactory.IsProviderAvailable(context.Provider),
            _ => false
        };
    }
    
    /// <summary>
    /// Delegates to the one framework-wide selection rule, supplying this
    /// executor's stricter availability filter: an agentic implementation also
    /// requires a reachable provider here, which the workflow and cluster paths
    /// deliberately leave to the call itself.
    /// </summary>
    private IReadOnlyList<ImplementationType> BuildChain(
        ImplementationType first,
        ExecutionOptions options,
        DomainBrick brick,
        ExecutionContext context,
        BrickRuntimeSpec? runtimeSpec = null) =>
        ImplementationChainResolver.Instance.Resolve(
            new ImplementationChainRequest(
                brick,
                context,
                first,
                runtimeSpec,
                (b, t, ctx) => IsImplementationAvailable(b, t, (ExecutionContext)ctx)));

    private static ImplementationType PreferToImplementation(
        string? prefer,
        DomainBrick brick,
        ExecutionContext context,
        ImplementationMode mode)
    {
        var p = (prefer ?? "auto").Trim().ToLowerInvariant();
        return p switch
        {
            "deterministic" => ImplementationType.Deterministic,
            "agentic" => ImplementationType.Agentic,
            _ => mode == ImplementationMode.DeterministicOnly
                ? ImplementationType.Deterministic
                : (brick.Selector != null ? brick.Selector.Select(context) : brick.DefaultImplementation)
        };
    }
    
    /// <summary>
    /// Evaluates a step condition. Returns whether the syntax was RECOGNISED; the boolean
    /// result is in <paramref name="result"/>. Only the literals "true"/"false" are supported.
    /// Returning false (unrecognised) lets the caller surface an authoring error instead of
    /// silently skipping the step, which is what the old <c>_ => false</c> arm did.
    /// </summary>
    private static bool TryEvaluateCondition(string condition, out bool result)
    {
        switch (condition.Trim().ToLowerInvariant())
        {
            case "true": result = true; return true;
            case "false": result = false; return true;
            default: result = false; return false;
        }
    }
    
    private static BrickInput MapInputs(
        IReadOnlyDictionary<string, string> inputMapping,
        ExecutionContext context)
    {
        var brickInput = new BrickInput();
        
        foreach (var mapping in inputMapping)
        {
            // Simple mapping: evaluate expression or use variable
            if (context.Variables.TryGetValue(mapping.Value, out var value))
            {
                brickInput.Set(mapping.Key, value);
            }
            else
            {
                // Try to parse as literal
                brickInput.Set(mapping.Key, mapping.Value);
            }
        }
        
        return brickInput;
    }
    
    private static void MapOutputs(
        IReadOnlyDictionary<string, string> outputMapping,
        BrickOutput output,
        ExecutionContext context)
    {
        foreach (var mapping in outputMapping)
        {
            // Simple mapping: get from output
            if (output.ToDictionary().TryGetValue(mapping.Value, out var value))
            {
                context.Variables[mapping.Key] = value;
            }
        }
    }
    
    /// <summary>
    /// Derives the semantic-cache key. Delegates to <see cref="SemanticCacheKey"/>, which
    /// <see cref="ClusterExecutor"/> also uses; see that type for what the private copy this
    /// replaces got wrong.
    /// </summary>
    private static string ComputeCacheKey(string brickId, BrickInput input, ImplementationType implementation)
        => SemanticCacheKey.For(brickId, input, implementation);
    
    /// <summary>
    /// Computes whether the behavior succeeded. When <see cref="SuccessCriteria.AllStepsComplete"/>
    /// is required, success means every step actually completed without error
    /// (<paramref name="allStepsSucceeded"/>). It used to return true unconditionally, so a
    /// behavior reported success even when steps errored.
    /// </summary>
    private static bool EvaluateSuccessCriteria(SuccessCriteria criteria, bool allStepsSucceeded)
    {
        if (criteria.AllStepsComplete)
        {
            return allStepsSucceeded;
        }

        // The behavior does not gate on per-step success.
        return true;
    }
}

