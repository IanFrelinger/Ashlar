using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Ashlar.Core.Application.Bricks;
using Ashlar.Core.Domain.Behaviors;
using Ashlar.Core.Domain.Bricks;
using Ashlar.Core.Domain.Clusters;
using Ashlar.Core.Domain.Execution;
using Ashlar.Core.Domain.Execution.Events;

namespace Ashlar.Infrastructure.Execution;

/// <summary>
/// Executes cluster instances by running their bricks in topological order.
/// </summary>
public class ClusterExecutor : IClusterExecutor
{
    private readonly IClusterRegistry _clusterRegistry;
    private readonly IBrickRegistry _brickRegistry;
    private readonly ISemanticCache _semanticCache;
    private readonly ILogger<ClusterExecutor> _logger;
    
    /// <summary>Initializes a new cluster executor.</summary>
    public ClusterExecutor(
        IClusterRegistry clusterRegistry,
        IBrickRegistry brickRegistry,
        ISemanticCache semanticCache,
        ILogger<ClusterExecutor> logger)
    {
        _clusterRegistry = clusterRegistry;
        _brickRegistry = brickRegistry;
        _semanticCache = semanticCache;
        _logger = logger;
    }
    
    /// <summary>
    /// Executes a single cluster instance by running its bricks in topological order.
    /// Streams execution events in real-time for monitoring and visualization.
    /// </summary>
    /// <param name="instance">The configured cluster instance to execute.</param>
    /// <param name="input">Input parameters for the cluster execution.</param>
    /// <param name="context">Execution context containing agent, behavior, and environment information.</param>
    /// <param name="ct">Cancellation token to cancel the execution.</param>
    /// <returns>Async enumerable of execution events streamed in real-time.</returns>
    public async IAsyncEnumerable<ExecutionEvent> ExecuteAsync(
        ClusterInstance instance,
        ClusterInput input,
        IExecutionContext context,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var cluster = _clusterRegistry.Get(instance.ClusterId);
        if (cluster is null)
        {
            yield return new StepErrorEvent(
                instance.InstanceId,
                $"Cluster not found: {instance.ClusterId}");
            yield break;
        }
        
        yield return new BehaviorStartedEvent(
            instance.InstanceId,
            cluster.Name,
            DateTime.UtcNow);
        
        // Build execution plan from cluster topology
        var plan = BuildExecutionPlan(cluster);
        
        // Resolve parameters
        var resolvedParams = ResolveParameters(cluster, instance, input);
        
        // Execute bricks in topological order
        var brickOutputs = new Dictionary<string, BrickOutput>();
        
        foreach (var step in plan.Steps)
        {
            if (ct.IsCancellationRequested)
            {
                yield return new BehaviorCancelledEvent(instance.InstanceId);
                yield break;
            }
            
            var clusterBrick = cluster.Bricks.FirstOrDefault(b => b.LocalId == step.LocalId);
            if (clusterBrick is null)
            {
                yield return new StepErrorEvent(step.LocalId, $"DomainBrick not found in cluster: {step.LocalId}");
                continue;
            }
            
            var brick = _brickRegistry.GetBrick(clusterBrick.BrickId);
            if (brick is null)
            {
                yield return new StepErrorEvent(step.LocalId, $"DomainBrick not found: {clusterBrick.BrickId}");
                if (cluster.Interface.FailurePolicy == FailurePolicy.Abort)
                    yield break;
                continue;
            }
            
            // Determine implementation chain (swap on failure)
            var preferred = DetermineImplementation(brick, instance, step.LocalId, clusterBrick, context);
            var chain = BuildChain(brick, preferred, context);
            if (chain.Count == 0)
            {
                yield return new StepErrorEvent(step.LocalId, "No available implementation");
                if (cluster.Interface.FailurePolicy == FailurePolicy.Abort)
                    yield break;
                continue;
            }
            
            // Build brick input from connections and parameters
            var brickInput = BuildBrickInput(step, cluster, resolvedParams, brickOutputs);
            BrickInputDefaults.Apply(brick, brickInput);
            
            var stepIndex = 0;
            for (int i = 0; i < plan.Steps.Count; i++)
            {
                if (plan.Steps[i].LocalId == step.LocalId)
                {
                    stepIndex = i;
                    break;
                }
            }
            
            BrickOutput? output = null;
            string? error = null;
            var usedFallback = false;

            foreach (var impl in chain)
            {
                var sw = Stopwatch.StartNew();

                yield return new StepStartedEvent(
                    step.LocalId,
                    brick.Id,
                    brick.Name,
                    impl,
                    usedFallback,
                    stepIndex,
                    plan.Steps.Count);

                // Check cache outside try-catch
                var cacheKey = ComputeCacheKey(brick.Id, brickInput, impl);
                var cached = await _semanticCache.GetAsync(cacheKey, ct);

                if (cached != null)
                {
                    output = cached;
                    yield return new CacheHitEvent(step.LocalId, cacheKey);
                }
                else
                {
                    try
                    {
                        output = await brick.ExecuteAsync(brickInput, impl, context, ct);
                        await _semanticCache.SetAsync(cacheKey, output, ct);
                    }
                    catch (Exception ex)
                    {
                        output = null;
                        error = ex.Message;
                        _logger.LogError(ex, "Error executing brick {BrickId} in cluster {ClusterId} with {Impl}",
                            clusterBrick.BrickId, instance.ClusterId, impl);
                    }
                }

                sw.Stop();

                if (output != null)
                {
                    brickOutputs[step.LocalId] = output;
                    yield return new StepCompletedEvent(step.LocalId, brick.Id, impl, sw.ElapsedMilliseconds, output.Summary);
                    error = null;
                    break;
                }

                yield return new StepErrorEvent(step.LocalId, error ?? "DomainBrick failed", sw.ElapsedMilliseconds);
                usedFallback = true;
            }

            if (error != null && output == null)
            {
                if (cluster.Interface.FailurePolicy == FailurePolicy.Abort)
                {
                    yield return new BehaviorCancelledEvent(instance.InstanceId);
                    yield break;
                }
            }
        }
        
        // Build cluster output from brick outputs
        var clusterOutput = BuildClusterOutput(cluster, brickOutputs);
        
        yield return new BehaviorCompletedEvent(
            instance.InstanceId,
            true,
            clusterOutput);
    }

    /// <summary>
    /// Delegates to the one framework-wide selection rule. Availability stays
    /// "declared only" here, matching this executor's prior behaviour.
    /// </summary>
    private static IReadOnlyList<ImplementationType> BuildChain(DomainBrick brick, ImplementationType preferred, IExecutionContext ctx) =>
        ImplementationChainResolver.Instance.Resolve(
            new ImplementationChainRequest(brick, ctx, preferred));
    
    /// <summary>
    /// Executes multiple instances of a cluster in parallel with controlled concurrency.
    /// Useful for scaling workflows across multiple inputs or configurations.
    /// </summary>
    /// <param name="clusterId">The ID of the cluster being executed.</param>
    /// <param name="instances">List of cluster instances to execute in parallel.</param>
    /// <param name="sharedInput">Shared input parameters applied to all instances.</param>
    /// <param name="context">Execution context for all instances.</param>
    /// <param name="ct">Cancellation token to cancel all executions.</param>
    /// <returns>Async enumerable of execution events from all instances, interleaved as they complete.</returns>
    public async IAsyncEnumerable<ExecutionEvent> ExecuteScaledAsync(
        string clusterId,
        IReadOnlyList<ClusterInstance> instances,
        ClusterInput sharedInput,
        IExecutionContext context,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        yield return new BehaviorStartedEvent(
            clusterId,
            $"Scaled execution ({instances.Count} instances)",
            DateTime.UtcNow);
        
        // Execute instances in parallel with controlled concurrency
        var semaphore = new SemaphoreSlim(Environment.ProcessorCount);
        var tasks = new List<Task<List<ExecutionEvent>>>();
        
        foreach (var instance in instances)
        {
            await semaphore.WaitAsync(ct);
            
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    var events = new List<ExecutionEvent>();
                    await foreach (var evt in ExecuteAsync(instance, sharedInput, context, ct))
                    {
                        events.Add(evt);
                    }
                    return events;
                }
                finally
                {
                    semaphore.Release();
                }
            }, ct));
        }
        
        // Yield events as they complete
        foreach (var task in tasks)
        {
            var events = await task;
            foreach (var evt in events)
            {
                yield return evt;
            }
        }
        
        yield return new BehaviorCompletedEvent(
            clusterId,
            true,
            new Dictionary<string, object>());
    }
    
    /// <summary>
    /// Builds an execution plan by performing topological sort of bricks based on their connections.
    /// Ensures bricks are executed in the correct order respecting data dependencies.
    /// </summary>
    /// <param name="cluster">The cluster to build an execution plan for.</param>
    /// <returns>Execution plan with bricks ordered by dependencies.</returns>
    /// <exception cref="InvalidOperationException">Thrown when circular dependencies are detected.</exception>
    private ExecutionPlan BuildExecutionPlan(Cluster cluster)
    {
        // Topological sort of bricks based on connections
        var steps = new List<ExecutionStep>();
        var visited = new HashSet<string>();
        var visiting = new HashSet<string>();
        
        void Visit(string localId)
        {
            if (visited.Contains(localId)) return;
            if (visiting.Contains(localId))
                throw new InvalidOperationException($"Circular dependency detected at {localId}");
            
            visiting.Add(localId);
            
            // Visit dependencies first
            var dependencies = cluster.Connections
                .Where(c => c.ToBrickId == localId)
                .Select(c => c.FromBrickId)
                .Distinct();
            
            foreach (var dep in dependencies)
            {
                Visit(dep);
            }
            
            visiting.Remove(localId);
            visited.Add(localId);
            
            var clusterBrick = cluster.Bricks.First(b => b.LocalId == localId);
            steps.Add(new ExecutionStep(localId, clusterBrick.BrickId));
        }
        
        foreach (var brick in cluster.Bricks)
        {
            Visit(brick.LocalId);
        }
        
        return new ExecutionPlan(steps);
    }
    
    /// <summary>
    /// Determines which implementation (deterministic or agentic) to use for a brick execution.
    /// Priority: instance override > cluster brick default > brick selector > brick default.
    /// </summary>
    /// <param name="brick">The brick to determine implementation for.</param>
    /// <param name="instance">The cluster instance with potential overrides.</param>
    /// <param name="localId">Local ID of the brick within the cluster.</param>
    /// <param name="clusterBrick">Cluster-level configuration for this brick.</param>
    /// <param name="context">Execution context for selector evaluation.</param>
    /// <returns>The implementation type to use for execution.</returns>
    private ImplementationType DetermineImplementation(
        DomainBrick brick,
        ClusterInstance instance,
        string localId,
        ClusterBrick clusterBrick,
        IExecutionContext context)
    {
        // Check instance override first
        if (instance.ImplementationOverrides.TryGetValue(localId, out var overrideImpl))
        {
            return overrideImpl;
        }
        
        // Use cluster brick default
        if (clusterBrick.DefaultImplementation != ImplementationType.Auto)
        {
            return clusterBrick.DefaultImplementation;
        }
        
        // Use brick's selector if available
        if (brick.Selector is not null)
        {
            return brick.Selector.Select(context);
        }
        
        // Fall back to brick default
        return brick.DefaultImplementation;
    }
    
    /// <summary>
    /// Resolves cluster parameters by merging defaults, instance overrides, and input values.
    /// Priority: input parameters > instance parameters > cluster defaults.
    /// </summary>
    /// <param name="cluster">The cluster definition with parameter defaults.</param>
    /// <param name="instance">The cluster instance with parameter overrides.</param>
    /// <param name="input">Runtime input parameters.</param>
    /// <returns>Resolved parameter dictionary with all values merged.</returns>
    private static Dictionary<string, object> ResolveParameters(
        Cluster cluster,
        ClusterInstance instance,
        ClusterInput input)
    {
        var resolved = new Dictionary<string, object>();
        
        // Start with cluster parameter defaults
        foreach (var param in cluster.Parameters)
        {
            if (param.Default != null)
            {
                resolved[param.Name] = param.Default;
            }
        }
        
        // Override with instance parameters
        foreach (var (key, value) in instance.Parameters)
        {
            resolved[key] = value;
        }
        
        // Override with input parameters
        foreach (var (key, value) in input.Parameters)
        {
            resolved[key] = value;
        }
        
        return resolved;
    }
    
    /// <summary>
    /// Builds the input for a brick execution by combining static parameters, parameter mappings,
    /// and outputs from connected bricks via cluster connections.
    /// </summary>
    /// <param name="step">The execution step for the brick.</param>
    /// <param name="cluster">The cluster definition containing connections and mappings.</param>
    /// <param name="resolvedParams">Resolved cluster parameters.</param>
    /// <param name="brickOutputs">Outputs from previously executed bricks in the cluster.</param>
    /// <returns>DomainBrick input with all parameters and connections applied.</returns>
    private static BrickInput BuildBrickInput(
        ExecutionStep step,
        Cluster cluster,
        Dictionary<string, object> resolvedParams,
        Dictionary<string, BrickOutput> brickOutputs)
    {
        var brickInput = new BrickInput();
        var clusterBrick = cluster.Bricks.First(b => b.LocalId == step.LocalId);
        
        // Add static parameters
        foreach (var (key, value) in clusterBrick.StaticParameters)
        {
            brickInput.Set(key, value);
        }
        
        // Add mapped parameters
        foreach (var (brickInputName, mapping) in clusterBrick.ParameterMappings)
        {
            if (resolvedParams.TryGetValue(mapping, out var paramValue))
            {
                brickInput.Set(brickInputName, paramValue);
            }
        }
        
        // Add connections from other bricks
        var connections = cluster.Connections
            .Where(c => c.ToBrickId == step.LocalId);
        
        foreach (var connection in connections)
        {
            if (brickOutputs.TryGetValue(connection.FromBrickId, out var sourceOutput))
            {
                if (sourceOutput.ToDictionary().TryGetValue(connection.FromOutput, out var value))
                {
                    brickInput.Set(connection.ToInput, value);
                }
            }
        }
        
        return brickInput;
    }
    
    /// <summary>
    /// Builds the cluster output by mapping internal brick outputs to cluster interface outputs.
    /// Uses the internal mapping format "brickLocalId.outputName" to locate values.
    /// </summary>
    /// <param name="cluster">The cluster definition with output interface mappings.</param>
    /// <param name="brickOutputs">Outputs from all executed bricks in the cluster.</param>
    /// <returns>Dictionary of cluster output values mapped from brick outputs.</returns>
    private static Dictionary<string, object> BuildClusterOutput(
        Cluster cluster,
        Dictionary<string, BrickOutput> brickOutputs)
    {
        var output = new Dictionary<string, object>();
        
        // Map cluster outputs from brick outputs
        foreach (var port in cluster.Interface.Outputs)
        {
            var parts = port.InternalMapping.Split('.');
            if (parts.Length == 2)
            {
                var brickLocalId = parts[0];
                var outputName = parts[1];
                
                if (brickOutputs.TryGetValue(brickLocalId, out var brickOutput))
                {
                    if (brickOutput.ToDictionary().TryGetValue(outputName, out var value))
                    {
                        output[port.Name] = value;
                    }
                }
            }
        }
        
        return output;
    }
    
    /// <summary>
    /// Computes a cache key for semantic caching of brick execution results.
    /// Key format: "{brickId}:{implementation}:{inputHash}".
    /// </summary>
    /// <param name="brickId">ID of the brick being executed.</param>
    /// <param name="input">Input parameters for the brick.</param>
    /// <param name="implementation">Implementation type being used.</param>
    /// <returns>Cache key string for the execution.</returns>
    private static string ComputeCacheKey(string brickId, BrickInput input, ImplementationType implementation)
        => SemanticCacheKey.For(brickId, input, implementation);
}
