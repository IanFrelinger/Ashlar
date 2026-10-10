using Ashlar.Abstractions.Security.Egress;
using System.Runtime.ExceptionServices;
using System.Reactive.Subjects;
using System.Reactive.Linq;
using Microsoft.Extensions.Logging;
using Ashlar.Core.Application.Bricks;
using Ashlar.Core.Application.Common.Ports;
using Ashlar.Core.Domain.Agents;
using Ashlar.Core.Domain.Behaviors;
using Ashlar.Core.Domain.Bricks;
using Ashlar.Core.Domain.Clusters;
using Ashlar.Core.Domain.Execution;
using Ashlar.Core.Domain.Execution.Events;
using Ashlar.Core.Domain.Workflows;

namespace Ashlar.Core.Application.Workflows;

/// <summary>
/// Executes a user-composed workflow from the visual composer.
/// </summary>
public class WorkflowExecutor
{
    private readonly IAgentRegistry _agents;
    private readonly IBrickRegistry _bricks;
    private readonly IBehaviorRegistry _behaviors;
    private readonly IBehaviorExecutor _behaviorExecutor;
    private readonly ILoopKernel _loops;
    private readonly ITextFileSystem _fs;
    private readonly IWorkflowPdfExporter? _pdfExporter;
    private readonly IWorkflowWebhookClient? _webhookClient;
    private readonly IWorkflowDatabaseReader? _databaseReader;
    private readonly IWorkflowDatabaseWriter? _databaseWriter;
    private readonly IClusterStore? _clusterStore;
    private readonly ILogger<WorkflowExecutor> _logger;
    private readonly Subject<WorkflowExecutionEvent> _events = new();

    /// <summary>Observable stream of workflow lifecycle and node events.</summary>
    public IObservable<WorkflowExecutionEvent> Events => _events.AsObservable();

    /// <summary>Creates a workflow executor with required domain registries and optional export integrations.</summary>
    public WorkflowExecutor(
        IAgentRegistry agents,
        IBrickRegistry bricks,
        IBehaviorRegistry behaviors,
        IBehaviorExecutor behaviorExecutor,
        ILoopKernel loops,
        ITextFileSystem fs,
        ILogger<WorkflowExecutor> logger,
        IWorkflowPdfExporter? pdfExporter = null,
        IWorkflowWebhookClient? webhookClient = null,
        IWorkflowDatabaseReader? databaseReader = null,
        IWorkflowDatabaseWriter? databaseWriter = null,
        IClusterStore? clusterStore = null)
    {
        _agents = agents;
        _bricks = bricks;
        _behaviors = behaviors;
        _behaviorExecutor = behaviorExecutor;
        _loops = loops;
        _fs = fs;
        _pdfExporter = pdfExporter;
        _webhookClient = webhookClient;
        _databaseReader = databaseReader;
        _databaseWriter = databaseWriter;
        _clusterStore = clusterStore;
        _logger = logger;
    }

    /// <summary>
    /// Validates, plans, and executes a visual workflow definition.
    /// </summary>
    /// <param name="workflow">Composed workflow graph from the visual composer.</param>
    /// <param name="input">Runtime parameters for input and cluster nodes.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Aggregate workflow result with per-node outputs.</returns>
    public async Task<WorkflowResult> ExecuteAsync(
        WorkflowDefinition workflow,
        WorkflowInput input,
        CancellationToken ct = default)
    {
        var correlationId = Guid.NewGuid().ToString();
        var context = new WorkflowExecutionContext(correlationId, workflow);
        
        _events.OnNext(new WorkflowStartedEvent(correlationId, workflow.Name));
        
        try
        {
            // Validate the workflow
            var validation = ValidateWorkflow(workflow);
            if (!validation.IsValid)
            {
                throw new WorkflowValidationException(validation.Errors);
            }
            
            // Build execution plan (topological sort)
            var plan = BuildExecutionPlan(workflow);
            _events.OnNext(new ExecutionPlanCreatedEvent(correlationId, plan));
            
            // Execute nodes in order
            var nodeResults = new Dictionary<string, NodeResult>();

            await _loops.ForEachAsync(plan.ExecutionOrder, async (nodeId, _, token) =>
            {
                var node = workflow.Nodes.First(n => n.Id == nodeId);

                _events.OnNext(new NodeStartedEvent(correlationId, node));

                // Gather inputs from connected nodes
                var nodeInputs = GatherInputs(workflow, node, nodeResults);

                // Execute the node
                var result = await ExecuteNodeAsync(node, nodeInputs, context, token);

                nodeResults[nodeId] = result;

                _events.OnNext(new NodeCompletedEvent(correlationId, node, result));
                return LoopAction.Continue;
            }, new LoopOptions { Name = "workflow-execution-order" }, ct);
            
            // Gather final outputs
            var outputs = GatherOutputs(workflow, nodeResults);
            
            var workflowResult = new WorkflowResult
            {
                CorrelationId = correlationId,
                Success = true,
                Outputs = outputs,
                NodeResults = nodeResults,
                Metrics = context.Metrics
            };
            
            _events.OnNext(new WorkflowCompletedEvent(correlationId, workflowResult));
            
            return workflowResult;
        }
        catch (Exception ex)
        {
            _events.OnNext(new WorkflowFailedEvent(correlationId, ex));
            throw;
        }
    }
    
    private async Task<NodeResult> ExecuteNodeAsync(
        WorkflowNode node,
        Dictionary<string, object> inputs,
        WorkflowExecutionContext context,
        CancellationToken ct)
    {
        return node switch
        {
            InputNode inputNode => await ExecuteInputNodeAsync(inputNode, context, ct),
            AgentNode agentNode => await ExecuteAgentNodeAsync(agentNode, inputs, context, ct),
            BrickNode brickNode => await ExecuteBrickNodeAsync(brickNode, inputs, context, ct),
            ClusterNode clusterNode => await ExecuteClusterNodeAsync(clusterNode, inputs, context, ct),
            TransformNode transformNode => ExecuteTransformNode(transformNode, inputs),
            ConditionalNode conditionalNode => ExecuteConditionalNode(conditionalNode, inputs),
            OutputNode outputNode => await ExecuteOutputNodeAsync(outputNode, inputs, context, ct),
            _ => throw new InvalidOperationException($"Unknown node type: {node.GetType().Name}")
        };
    }
    
    private async Task<NodeResult> ExecuteInputNodeAsync(
        InputNode node,
        WorkflowExecutionContext context,
        CancellationToken ct)
    {
        object? data = null;
        
        switch (node.Type)
        {
            case InputType.File:
                if (node.FilePath != null)
                {
                    data = await _fs.ReadAllTextAsync(node.FilePath, ct);
                }
                break;
            case InputType.Content:
                data = node.Content;
                break;
            case InputType.Webhook:
                if (_webhookClient == null)
                    throw new InvalidOperationException("Webhook input requires IWorkflowWebhookClient to be registered");
                if (string.IsNullOrWhiteSpace(node.WebhookUrl))
                    throw new InvalidOperationException("Webhook input node requires WebhookUrl");
                data = await _webhookClient.GetAsync(node.WebhookUrl!, ct);
                break;
            case InputType.Database:
                if (_databaseReader == null)
                    throw new InvalidOperationException("Database input requires IWorkflowDatabaseReader to be registered");
                if (string.IsNullOrWhiteSpace(node.DatabaseConnectionString))
                    throw new InvalidOperationException("Database input node requires DatabaseConnectionString");
                if (string.IsNullOrWhiteSpace(node.Query))
                    throw new InvalidOperationException("Database input node requires Query");
                data = await _databaseReader.ExecuteQueryAsync(node.DatabaseConnectionString!, node.Query!, ct);
                break;
        }
        
        return new NodeResult
        {
            NodeId = node.Id,
            Success = true,
            Outputs = new Dictionary<string, object> { ["data"] = data ?? "" }
        };
    }
    
    private async Task<NodeResult> ExecuteAgentNodeAsync(
        AgentNode node,
        Dictionary<string, object> inputs,
        WorkflowExecutionContext context,
        CancellationToken ct)
    {
        var agent = _agents.GetAgent(node.AgentId);
        if (agent == null)
        {
            throw new InvalidOperationException($"Agent not found: {node.AgentId}");
        }
        
        // Get the first behavior for now (in future, allow selection)
        var behaviorId = agent.Behaviors.FirstOrDefault();
        if (behaviorId == null)
        {
            throw new InvalidOperationException($"Agent {node.AgentId} has no behaviors");
        }
        
        var behavior = _behaviors.GetBehavior(behaviorId);
        if (behavior == null)
        {
            throw new InvalidOperationException($"Behavior not found: {behaviorId}");
        }
        
        // Create execution options with implementation overrides
        var options = new ExecutionOptions
        {
            IsAirGapped = context.ExecutionContext.IsAirGapped,
            AuditMode = context.ExecutionContext.AuditMode,
            Provider = context.ExecutionContext.Provider,
            ImplementationMode = node.Mode,
            BehaviorOverrides = node.BehaviorOverrides,
            BrickOverrides = node.BrickOverrides
        };
        
        // Stream brick-level events
        var behaviorInput = new BehaviorInput(new Dictionary<string, object>(inputs));
        
        var outputs = new Dictionary<string, object>();
        await foreach (var evt in _behaviorExecutor.ExecuteWithEventsAsync(
            agent, behavior, behaviorInput, options, ct))
        {
            // Forward events to workflow events
            _events.OnNext(new NodeBrickEvent(context.CorrelationId, node.Id, evt));
            
            if (evt is BehaviorCompletedEvent completed)
            {
                outputs = ToMutableDictionary(completed.Outputs);
            }
        }
        
        return new NodeResult
        {
            NodeId = node.Id,
            Success = true,
            Outputs = outputs
        };
    }
    
    private async Task<NodeResult> ExecuteBrickNodeAsync(
        BrickNode node,
        Dictionary<string, object> inputs,
        WorkflowExecutionContext context,
        CancellationToken ct)
    {
        var brick = _bricks.GetBrick(node.BrickId);
        if (brick == null)
        {
            throw new InvalidOperationException($"DomainBrick not found: {node.BrickId}");
        }

        // Auto is passed through rather than collapsed to DefaultImplementation:
        // that collapse is exactly what used to make this executor ignore the
        // brick's Selector. The shared resolver decides.
        var chain = BuildBrickExecutionChain(brick, node.Implementation, context.ExecutionContext);

        var brickInput = new BrickInput(inputs);
        BrickInputDefaults.Apply(brick, brickInput);
        BrickOutput? result = null;
        Exception? last = null;
        EgressRefusedException? firstRefusal = null;

        await _loops.ForEachAsync(chain, async (impl, _, token) =>
        {
            _events.OnNext(new BrickImplementationSelectedEvent(
                context.CorrelationId, node.Id, brick.Name, impl));

            try
            {
                result = await brick.ExecuteAsync(brickInput, impl, context.ExecutionContext, token);
                return LoopAction.Break;
            }
            catch (Exception ex)
            {
                firstRefusal ??= EgressRefusal.Find(ex);
                last = ex;
                return LoopAction.Continue;
            }
        }, new LoopOptions { Name = "brick-fallback-chain" }, ct);

        if (result == null)
        {
            if (firstRefusal is not null) ExceptionDispatchInfo.Capture(firstRefusal).Throw();
            throw last ?? new InvalidOperationException("DomainBrick execution failed");
        }
        
        return new NodeResult
        {
            NodeId = node.Id,
            Success = true,
            Outputs = ToMutableDictionary(result.ToDictionary())
        };
    }

    /// <summary>
    /// Delegates to the one framework-wide selection rule. The availability
    /// filter stays "declared only" here — provider reachability is enforced by
    /// the brick/provider path itself, and a failure falls back through the chain.
    /// </summary>
    private static IReadOnlyList<ImplementationType> BuildBrickExecutionChain(
        DomainBrick brick,
        ImplementationType preferred,
        IExecutionContext ctx) =>
        ImplementationChainResolver.Instance.Resolve(
            new ImplementationChainRequest(brick, ctx, preferred));
    
    /// <summary>
    /// Executes a cluster node by loading the cluster definition and running its bricks in topological order.
    /// </summary>
    private async Task<NodeResult> ExecuteClusterNodeAsync(
        ClusterNode node,
        Dictionary<string, object> inputs,
        WorkflowExecutionContext context,
        CancellationToken ct)
    {
        if (_clusterStore == null)
            throw new InvalidOperationException("Cluster execution requires IClusterStore. Register IClusterStore in DI.");

        var cluster = await _clusterStore.GetByIdAsync(node.ClusterId, ct);
        if (cluster == null)
            throw new InvalidOperationException($"Cluster not found: {node.ClusterId}");

        var resolvedParams = ResolveClusterParameters(cluster, inputs);
        var plan = BuildClusterExecutionPlan(cluster);
        var brickOutputs = new Dictionary<string, BrickOutput>();

        foreach (var step in plan)
        {
            if (ct.IsCancellationRequested) break;

            var clusterBrick = cluster.Bricks.FirstOrDefault(b => b.LocalId == step.LocalId);
            if (clusterBrick == null) continue;

            var brick = _bricks.GetBrick(clusterBrick.BrickId);
            if (brick == null)
            {
                if (cluster.Interface.FailurePolicy == FailurePolicy.Abort)
                    throw new InvalidOperationException($"DomainBrick not found: {clusterBrick.BrickId}");
                continue;
            }

            // Auto passed through so the brick's Selector is honoured here too.
            var chain = BuildBrickExecutionChain(brick, clusterBrick.DefaultImplementation, context.ExecutionContext);
            var brickInput = BuildClusterBrickInput(step, cluster, resolvedParams, brickOutputs);
            BrickInputDefaults.Apply(brick, brickInput);

            BrickOutput? result = null;
            EgressRefusedException? firstRefusal = null;
            foreach (var impl in chain)
            {
                try
                {
                    result = await brick.ExecuteAsync(brickInput, impl, context.ExecutionContext, ct);
                    break;
                }
                catch (Exception ex)
                {
                    firstRefusal ??= EgressRefusal.Find(ex);
                    _logger.LogWarning(ex, "DomainBrick {BrickId} failed with {Impl}, trying fallback", clusterBrick.BrickId, impl);
                }
            }

            if (result == null)
            {
                if (firstRefusal is not null) ExceptionDispatchInfo.Capture(firstRefusal).Throw();
                if (cluster.Interface.FailurePolicy == FailurePolicy.Abort)
                    throw new InvalidOperationException($"DomainBrick execution failed: {clusterBrick.BrickId}");
                continue;
            }

            brickOutputs[step.LocalId] = result;
        }

        var outputs = BuildClusterOutputs(cluster, brickOutputs);
        return new NodeResult { NodeId = node.Id, Success = true, Outputs = outputs };
    }

    private static Dictionary<string, object> ResolveClusterParameters(Cluster cluster, Dictionary<string, object> inputs)
    {
        var resolved = new Dictionary<string, object>();
        foreach (var p in cluster.Parameters)
            if (p.Default != null) resolved[p.Name] = p.Default;
        foreach (var kv in inputs)
            resolved[kv.Key] = kv.Value;
        return resolved;
    }

    private static List<(string LocalId, string BrickId)> BuildClusterExecutionPlan(Cluster cluster)
    {
        var steps = new List<(string, string)>();
        var visited = new HashSet<string>();
        var visiting = new HashSet<string>();

        void Visit(string localId)
        {
            if (visited.Contains(localId)) return;
            if (visiting.Contains(localId)) throw new InvalidOperationException($"Circular dependency in cluster: {localId}");
            visiting.Add(localId);
            foreach (var c in cluster.Connections.Where(c => c.ToBrickId == localId))
                Visit(c.FromBrickId);
            visiting.Remove(localId);
            visited.Add(localId);
            var cb = cluster.Bricks.First(b => b.LocalId == localId);
            steps.Add((localId, cb.BrickId));
        }

        foreach (var b in cluster.Bricks) Visit(b.LocalId);
        return steps;
    }

    private static BrickInput BuildClusterBrickInput(
        (string LocalId, string BrickId) step,
        Cluster cluster,
        Dictionary<string, object> resolvedParams,
        Dictionary<string, BrickOutput> brickOutputs)
    {
        var brickInput = new BrickInput();
        var clusterBrick = cluster.Bricks.First(b => b.LocalId == step.LocalId);

        foreach (var kv in clusterBrick.StaticParameters)
            brickInput.Set(kv.Key, kv.Value);
        foreach (var kv in clusterBrick.ParameterMappings)
            if (resolvedParams.TryGetValue(kv.Value, out var v)) brickInput.Set(kv.Key, v);
        foreach (var c in cluster.Connections.Where(c => c.ToBrickId == step.LocalId))
            if (brickOutputs.TryGetValue(c.FromBrickId, out var o) && o.ToDictionary().TryGetValue(c.FromOutput, out var val))
                brickInput.Set(c.ToInput, val);

        return brickInput;
    }

    private static Dictionary<string, object> BuildClusterOutputs(Cluster cluster, Dictionary<string, BrickOutput> brickOutputs)
    {
        var output = new Dictionary<string, object>();
        foreach (var port in cluster.Interface.Outputs)
        {
            var parts = port.InternalMapping.Split('.');
            if (parts.Length == 2 && brickOutputs.TryGetValue(parts[0], out var o) && o.ToDictionary().TryGetValue(parts[1], out var v))
                output[port.Name] = v;
        }
        if (output.Count == 0) output["data"] = brickOutputs.Values.LastOrDefault()?.ToDictionary() ?? new Dictionary<string, object>();
        return output;
    }
    
    /// <summary>
    /// Executes a transform node for data manipulation.
    /// Supports Map, Filter, Reduce, Sort, GroupBy, Merge operations.
    /// </summary>
    private NodeResult ExecuteTransformNode(
        TransformNode node,
        Dictionary<string, object> inputs)
    {
        var data = inputs.Values.FirstOrDefault();
        if (data == null)
            return new NodeResult { NodeId = node.Id, Success = true, Outputs = new Dictionary<string, object> { ["data"] = new List<object>() } };

        var list = ToListOfDictionaries(data);
        object result = node.Operation switch
        {
            TransformOperation.Map => ApplyMap(list, node.Expression),
            TransformOperation.Filter => ApplyFilter(list, node.Expression),
            TransformOperation.Reduce => ApplyReduce(list, node.Expression),
            TransformOperation.Sort => ApplySort(list, node.Expression),
            TransformOperation.GroupBy => ApplyGroupBy(list, node.Expression),
            TransformOperation.Merge => ApplyMerge(inputs),
            _ => list
        };

        return new NodeResult
        {
            NodeId = node.Id,
            Success = true,
            Outputs = new Dictionary<string, object> { ["data"] = result }
        };
    }

    private static List<Dictionary<string, object>> ToListOfDictionaries(object data)
    {
        if (data is List<Dictionary<string, object>> list) return list;
        if (data is System.Collections.IEnumerable enumerable and not string)
        {
            var result = new List<Dictionary<string, object>>();
            foreach (var item in enumerable)
            {
                if (item is Dictionary<string, object> d) result.Add(d);
                else if (item != null) result.Add(new Dictionary<string, object> { ["value"] = item });
            }
            if (result.Count > 0) return result;
        }
        if (data is Dictionary<string, object> single)
            return new List<Dictionary<string, object>> { single };
        var json = System.Text.Json.JsonSerializer.Serialize(data);
        var doc = System.Text.Json.JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            var arr = new List<Dictionary<string, object>>();
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                var dict = new Dictionary<string, object>();
                if (e.ValueKind == System.Text.Json.JsonValueKind.Object)
                    foreach (var p in e.EnumerateObject())
                        dict[p.Name] = GetJsonValue(p.Value);
                else
                    dict["value"] = GetJsonValue(e);
                arr.Add(dict);
            }
            return arr;
        }
        if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            var dict = new Dictionary<string, object>();
            foreach (var p in doc.RootElement.EnumerateObject())
                dict[p.Name] = GetJsonValue(p.Value);
            return new List<Dictionary<string, object>> { dict };
        }
        return new List<Dictionary<string, object>> { new Dictionary<string, object> { ["value"] = data } };
    }

    private static object GetJsonValue(System.Text.Json.JsonElement e)
    {
        return e.ValueKind switch
        {
            System.Text.Json.JsonValueKind.String => e.GetString() ?? "",
            System.Text.Json.JsonValueKind.Number => e.TryGetInt64(out var i) ? i : e.GetDouble(),
            System.Text.Json.JsonValueKind.True => true,
            System.Text.Json.JsonValueKind.False => false,
            System.Text.Json.JsonValueKind.Null => (object?)null!,
            _ => e.GetRawText()
        };
    }

    private static object ApplyMap(List<Dictionary<string, object>> list, string expression)
    {
        var key = string.IsNullOrWhiteSpace(expression) ? "value" : expression.Trim();
        return list.Select(d => d.TryGetValue(key, out var v) ? v : (object)d).ToList();
    }

    private static object ApplyFilter(List<Dictionary<string, object>> list, string expression)
    {
        if (string.IsNullOrWhiteSpace(expression)) return list;
        var parts = expression.Split(new[] { ' ', '=', '>', '<', '!', '\'' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return list;
        var key = parts[0];
        var op = expression.Contains(">=") ? ">=" : expression.Contains("<=") ? "<=" : expression.Contains("!=") ? "!=" : expression.Contains("==") ? "==" : expression.Contains(">") ? ">" : expression.Contains("<") ? "<" : "==";
        var valStr = parts.Length > 1 ? parts[parts.Length - 1].Trim('\'') : "";
        return list.Where(d =>
        {
            if (!d.TryGetValue(key, out var v) || v == null) return false;
            var vStr = v.ToString() ?? "";
            return op switch
            {
                "==" => vStr.Equals(valStr, StringComparison.OrdinalIgnoreCase),
                "!=" => !vStr.Equals(valStr, StringComparison.OrdinalIgnoreCase),
                ">" => double.TryParse(vStr, out var vn) && double.TryParse(valStr, out var cn) && vn > cn,
                "<" => double.TryParse(vStr, out var vn) && double.TryParse(valStr, out var cn) && vn < cn,
                ">=" => double.TryParse(vStr, out var vn) && double.TryParse(valStr, out var cn) && vn >= cn,
                "<=" => double.TryParse(vStr, out var vn) && double.TryParse(valStr, out var cn) && vn <= cn,
                _ => false
            };
        }).ToList();
    }

    private static object ApplyReduce(List<Dictionary<string, object>> list, string expression)
    {
        var op = (expression ?? "").Trim().ToLowerInvariant();
        if (list.Count == 0) return new Dictionary<string, object> { ["result"] = (object?)null! };
        return op switch
        {
            "sum" or "count" => new Dictionary<string, object> { ["result"] = list.Count },
            "first" => list[0],
            "last" => list[list.Count - 1],
            _ => new Dictionary<string, object> { ["result"] = list, ["count"] = list.Count }
        };
    }

    private static object ApplySort(List<Dictionary<string, object>> list, string expression)
    {
        var key = string.IsNullOrWhiteSpace(expression) ? "value" : expression.Trim();
        return list.OrderBy(d => d.TryGetValue(key, out var v) ? v?.ToString() ?? "" : "").ToList();
    }

    private static object ApplyGroupBy(List<Dictionary<string, object>> list, string expression)
    {
        var key = string.IsNullOrWhiteSpace(expression) ? "value" : expression.Trim();
        return list.GroupBy(d => d.TryGetValue(key, out var v) ? v?.ToString() ?? "" : "")
            .ToDictionary(g => g.Key, g => (object)g.ToList());
    }

    private static object ApplyMerge(Dictionary<string, object> inputs)
    {
        var result = new List<Dictionary<string, object>>();
        foreach (var v in inputs.Values)
        {
            result.AddRange(ToListOfDictionaries(v ?? new { }));
        }
        return result;
    }
    
    /// <summary>
    /// Executes a conditional node for branching logic.
    /// Evaluates the condition against inputs and returns condition=true/false for downstream routing.
    /// </summary>
    private NodeResult ExecuteConditionalNode(
        ConditionalNode node,
        Dictionary<string, object> inputs)
    {
        var condition = (node.Condition ?? "").Trim();
        var result = true;
        if (!string.IsNullOrEmpty(condition))
            result = EvaluateCondition(condition, inputs);

        return new NodeResult
        {
            NodeId = node.Id,
            Success = true,
            Outputs = new Dictionary<string, object>
            {
                ["condition"] = result,
                ["result"] = inputs.Values.FirstOrDefault() ?? inputs
            }
        };
    }

    private static bool EvaluateCondition(string condition, Dictionary<string, object> inputs)
    {
        var data = inputs.Values.FirstOrDefault() ?? inputs;
        var parts = condition.Split(new[] { ' ', '=', '>', '<', '!', '\'' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return GetBool(data, condition);

        var path = parts[0];
        var op = condition.Contains(">=") ? ">=" : condition.Contains("<=") ? "<=" : condition.Contains("!=") ? "!=" : condition.Contains("==") ? "==" : condition.Contains(">") ? ">" : condition.Contains("<") ? "<" : "";
        var valStr = parts.Length > 1 ? parts[parts.Length - 1].Trim('\'') : "";

        var v = GetValueByPath(data, path);
        var vStr = v?.ToString() ?? "";

        if (string.IsNullOrEmpty(op)) return GetBool(v, valStr);

        return op switch
        {
            "==" => vStr.Equals(valStr, StringComparison.OrdinalIgnoreCase),
            "!=" => !vStr.Equals(valStr, StringComparison.OrdinalIgnoreCase),
            ">" => double.TryParse(vStr, out var vn) && double.TryParse(valStr, out var cn) && vn > cn,
            "<" => double.TryParse(vStr, out var vn) && double.TryParse(valStr, out var cn) && vn < cn,
            ">=" => double.TryParse(vStr, out var vn) && double.TryParse(valStr, out var cn) && vn >= cn,
            "<=" => double.TryParse(vStr, out var vn) && double.TryParse(valStr, out var cn) && vn <= cn,
            _ => GetBool(v, valStr)
        };
    }

    private static object? GetValueByPath(object? data, string path)
    {
        if (data == null || string.IsNullOrEmpty(path)) return data;
        foreach (var p in path.Split('.'))
        {
            if (data is Dictionary<string, object> d && d.TryGetValue(p, out var v)) data = v;
            else if (data is System.Collections.IDictionary id && id.Contains(p)) data = id[p];
            else return null;
        }
        return data;
    }

    private static bool GetBool(object? v, string fallback)
    {
        if (v is bool b) return b;
        if (v == null) return false;
        var s = v.ToString() ?? "";
        return s.Equals("true", StringComparison.OrdinalIgnoreCase) || s.Equals("1", StringComparison.OrdinalIgnoreCase);
    }
    
    private async Task<NodeResult> ExecuteOutputNodeAsync(
        OutputNode node,
        Dictionary<string, object> inputs,
        WorkflowExecutionContext context,
        CancellationToken ct)
    {
        var data = inputs.Values.FirstOrDefault();
        
        switch (node.Type)
        {
            case OutputType.Display:
                // Data will be returned in workflow result
                break;
            case OutputType.File:
                if (node.FilePath != null && data != null)
                {
                    if (node.Format == WorkflowOutputFormat.Pdf)
                    {
                        if (_pdfExporter == null)
                            throw new NotSupportedException("PDF export requires IWorkflowPdfExporter to be registered");
                        var markdown = SerializeOutput(data, WorkflowOutputFormat.Markdown);
                        var pdfBytes = await _pdfExporter.ExportToPdfAsync(markdown, ct);
                        await _fs.WriteAllBytesAsync(node.FilePath, pdfBytes, ct);
                    }
                    else
                    {
                        var content = SerializeOutput(data, node.Format);
                        await _fs.WriteAllTextAsync(node.FilePath, content, ct);
                    }
                }
                break;
            case OutputType.Webhook:
                if (_webhookClient == null)
                    throw new InvalidOperationException("Webhook output requires IWorkflowWebhookClient to be registered");
                if (string.IsNullOrWhiteSpace(node.WebhookUrl))
                    throw new InvalidOperationException("Webhook output node requires WebhookUrl");
                await _webhookClient.PostAsync(node.WebhookUrl!, data ?? new { }, ct);
                break;
            case OutputType.Database:
                if (_databaseWriter == null)
                    throw new InvalidOperationException("Database output requires IWorkflowDatabaseWriter to be registered");
                if (string.IsNullOrWhiteSpace(node.DatabaseConnectionString))
                    throw new InvalidOperationException("Database output node requires DatabaseConnectionString");
                if (string.IsNullOrWhiteSpace(node.TableName))
                    throw new InvalidOperationException("Database output node requires TableName");
                await _databaseWriter.WriteAsync(node.DatabaseConnectionString!, node.TableName!, data ?? new { }, ct);
                break;
        }
        
        return new NodeResult
        {
            NodeId = node.Id,
            Success = true,
            Outputs = new Dictionary<string, object> { ["output"] = data ?? "" }
        };
    }
    
    private ExecutionPlan BuildExecutionPlan(WorkflowDefinition workflow)
    {
        // Topological sort of nodes based on connections
        var graph = new Dictionary<string, List<string>>();
        var inDegree = new Dictionary<string, int>();
        
        foreach (var node in workflow.Nodes)
        {
            graph[node.Id] = new List<string>();
            inDegree[node.Id] = 0;
        }
        
        foreach (var connection in workflow.Connections)
        {
            graph[connection.FromNodeId].Add(connection.ToNodeId);
            inDegree[connection.ToNodeId]++;
        }
        
        var queue = new Queue<string>();
        foreach (var kvp in inDegree)
        {
            var nodeId = kvp.Key;
            var degree = kvp.Value;
            if (degree == 0)
                queue.Enqueue(nodeId);
        }
        
        var order = new List<string>();
        while (queue.Count > 0)
        {
            var nodeId = queue.Dequeue();
            order.Add(nodeId);
            
            foreach (var neighbor in graph[nodeId])
            {
                inDegree[neighbor]--;
                if (inDegree[neighbor] == 0)
                    queue.Enqueue(neighbor);
            }
        }
        
        if (order.Count != workflow.Nodes.Count)
            throw new WorkflowValidationException(new List<string> { "Workflow contains cycles" });
        
        return new ExecutionPlan
        {
            ExecutionOrder = order,
            ParallelGroups = IdentifyParallelGroups(order, workflow)
        };
    }
    
    private Dictionary<string, object> GatherInputs(
        WorkflowDefinition workflow,
        WorkflowNode node,
        Dictionary<string, NodeResult> nodeResults)
    {
        var inputs = new Dictionary<string, object>();
        
        // Find all connections to this node
        var incomingConnections = workflow.Connections
            .Where(c => c.ToNodeId == node.Id)
            .ToList();
        
        foreach (var connection in incomingConnections)
        {
            if (nodeResults.TryGetValue(connection.FromNodeId, out var fromResult))
            {
                // Get the output from the source node
                var port = workflow.Nodes
                    .First(n => n.Id == connection.FromNodeId)
                    .Outputs.FirstOrDefault(p => p.Id == connection.FromPortId);
                
                if (port != null && fromResult.Outputs.TryGetValue(port.Name, out var value))
                {
                    var targetPort = node.Inputs.FirstOrDefault(p => p.Id == connection.ToPortId);
                    if (targetPort != null)
                    {
                        inputs[targetPort.Name] = value;
                    }
                }
            }
        }
        
        return inputs;
    }

    private static Dictionary<string, object> ToMutableDictionary(IReadOnlyDictionary<string, object> source)
    {
        var dict = new Dictionary<string, object>(source.Count);
        foreach (var kvp in source)
        {
            dict[kvp.Key] = kvp.Value;
        }
        return dict;
    }
    
    private Dictionary<string, object> GatherOutputs(
        WorkflowDefinition workflow,
        Dictionary<string, NodeResult> nodeResults)
    {
        var outputs = new Dictionary<string, object>();
        
        // Find all output nodes
        var outputNodes = workflow.Nodes.OfType<OutputNode>().ToList();
        
        foreach (var outputNode in outputNodes)
        {
            if (nodeResults.TryGetValue(outputNode.Id, out var result))
            {
                outputs[outputNode.Name] = result.Outputs;
            }
        }
        
        return outputs;
    }
    
    private WorkflowValidationResult ValidateWorkflow(WorkflowDefinition workflow)
    {
        var errors = new List<string>();
        
        // Check for cycles (handled in BuildExecutionPlan)
        // Check for type mismatches in connections
        foreach (var connection in workflow.Connections)
        {
            var fromNode = workflow.Nodes.FirstOrDefault(n => n.Id == connection.FromNodeId);
            var toNode = workflow.Nodes.FirstOrDefault(n => n.Id == connection.ToNodeId);
            
            if (fromNode == null || toNode == null)
            {
                errors.Add($"Connection references non-existent node");
                continue;
            }
            
            var fromPort = fromNode.Outputs.FirstOrDefault(p => p.Id == connection.FromPortId);
            var toPort = toNode.Inputs.FirstOrDefault(p => p.Id == connection.ToPortId);
            
            if (fromPort == null || toPort == null)
            {
                errors.Add($"Connection references non-existent port");
                continue;
            }
            
            // Type compatibility check
            if (!IsTypeCompatible(fromPort.DataType, toPort.DataType))
            {
                errors.Add($"Type mismatch: {fromPort.DataType} -> {toPort.DataType}");
            }
        }
        
        return new WorkflowValidationResult
        {
            IsValid = errors.Count == 0,
            Errors = errors
        };
    }
    
    private bool IsTypeCompatible(string fromType, string toType)
    {
        if (fromType == toType) return true;
        if (toType == "any") return true;
        
        // Add more compatibility rules as needed
        return false;
    }
    
    private List<List<string>> IdentifyParallelGroups(
        List<string> executionOrder,
        WorkflowDefinition workflow)
    {
        // Simple implementation: nodes at the same depth can run in parallel
        // More sophisticated analysis could identify truly independent nodes
        var groups = new List<List<string>>();
        var currentGroup = new List<string> { executionOrder[0] };
        
        for (int i = 1; i < executionOrder.Count; i++)
        {
            var nodeId = executionOrder[i];
            var hasDependency = workflow.Connections.Any(c => c.ToNodeId == nodeId);
            
            if (hasDependency)
            {
                groups.Add(currentGroup);
                currentGroup = new List<string> { nodeId };
            }
            else
            {
                currentGroup.Add(nodeId);
            }
        }
        
        if (currentGroup.Count > 0)
            groups.Add(currentGroup);
        
        return groups;
    }
    
    private string SerializeOutput(object data, WorkflowOutputFormat format)
    {
        return format switch
        {
            WorkflowOutputFormat.Json => System.Text.Json.JsonSerializer.Serialize(data),
            WorkflowOutputFormat.Xml => SerializeToXml(data),
            WorkflowOutputFormat.Csv => SerializeToCsv(data),
            WorkflowOutputFormat.Markdown => SerializeToMarkdown(data),
            WorkflowOutputFormat.Html => SerializeToHtml(data),
            WorkflowOutputFormat.Pdf => throw new NotSupportedException("PDF is binary; use file output with IWorkflowPdfExporter or Json/Xml/Markdown/Html for string output"),
            _ => data.ToString() ?? ""
        };
    }

    private static string SerializeToXml(object data)
    {
        if (data == null) return "<?xml version=\"1.0\" encoding=\"utf-8\"?><root/>";
        var json = System.Text.Json.JsonSerializer.Serialize(data);
        var doc = System.Text.Json.JsonDocument.Parse(json);
        return "<?xml version=\"1.0\" encoding=\"utf-8\"?>" + JsonToXml(doc.RootElement, "root");
    }

    private static string JsonToXml(System.Text.Json.JsonElement element, string name)
    {
        var safeName = SanitizeXmlName(name);
        return element.ValueKind switch
        {
            System.Text.Json.JsonValueKind.Object => "<" + safeName + ">" +
                string.Concat(element.EnumerateObject().Select(p => JsonToXml(p.Value, p.Name))) +
                "</" + safeName + ">",
            System.Text.Json.JsonValueKind.Array => string.Concat(
                element.EnumerateArray().Select(e => JsonToXml(e, "item"))),
            System.Text.Json.JsonValueKind.String => "<" + safeName + ">" + EscapeXml(element.GetString() ?? "") + "</" + safeName + ">",
            System.Text.Json.JsonValueKind.Number => "<" + safeName + ">" + element.GetRawText() + "</" + safeName + ">",
            System.Text.Json.JsonValueKind.True => "<" + safeName + ">true</" + safeName + ">",
            System.Text.Json.JsonValueKind.False => "<" + safeName + ">false</" + safeName + ">",
            System.Text.Json.JsonValueKind.Null => "",
            _ => ""
        };
    }

    private static string SanitizeXmlName(string s)
    {
        if (string.IsNullOrEmpty(s)) return "item";
        var cleaned = new string(s.Where(c => char.IsLetterOrDigit(c) || c == '_' || c == '-').ToArray());
        return string.IsNullOrEmpty(cleaned) ? "item" : (char.IsLetter(cleaned[0]) || cleaned[0] == '_' ? cleaned : "item" + cleaned);
    }

    private static string EscapeXml(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        return s
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&apos;");
    }

    private static string SerializeToCsv(object data)
    {
        if (data == null) return "";
        var json = System.Text.Json.JsonSerializer.Serialize(data);
        var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            var rows = root.EnumerateArray().ToList();
            if (rows.Count == 0) return "";
            var first = rows[0];
            if (first.ValueKind != System.Text.Json.JsonValueKind.Object) return CsvEscape(GetValue(first));
            var keys = first.EnumerateObject().Select(p => p.Name).ToList();
            var sb = new System.Text.StringBuilder();
            sb.AppendLine(string.Join(",", keys.Select(CsvEscape)));
            foreach (var row in rows)
            {
                if (row.ValueKind == System.Text.Json.JsonValueKind.Object)
                    sb.AppendLine(string.Join(",", keys.Select(k => row.TryGetProperty(k, out var v) ? CsvEscape(GetValue(v)) : "")));
            }
            return sb.ToString();
        }
        if (root.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Key,Value");
            foreach (var p in root.EnumerateObject())
                sb.AppendLine(CsvEscape(p.Name) + "," + CsvEscape(GetValue(p.Value)));
            return sb.ToString();
        }
        return CsvEscape(GetValue(root));
    }

    private static string GetValue(System.Text.Json.JsonElement e)
    {
        return e.ValueKind switch
        {
            System.Text.Json.JsonValueKind.String => e.GetString() ?? "",
            System.Text.Json.JsonValueKind.Number => e.GetRawText(),
            System.Text.Json.JsonValueKind.True => "true",
            System.Text.Json.JsonValueKind.False => "false",
            System.Text.Json.JsonValueKind.Null => "",
            _ => e.GetRawText()
        };
    }

    private static string CsvEscape(string s)
    {
        if (s == null) return "";
        if (s.Contains(',') || s.Contains('"') || s.Contains('\n') || s.Contains('\r'))
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        return s;
    }

    private static string SerializeToMarkdown(object data)
    {
        if (data == null) return "";
        var json = System.Text.Json.JsonSerializer.Serialize(data);
        var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            var rows = root.EnumerateArray().ToList();
            if (rows.Count == 0) return "";
            var first = rows[0];
            if (first.ValueKind != System.Text.Json.JsonValueKind.Object) return "- " + GetValue(first);
            var keys = first.EnumerateObject().Select(p => p.Name).ToList();
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("| " + string.Join(" | ", keys) + " |");
            sb.AppendLine("| " + string.Join(" | ", keys.Select(_ => "---")) + " |");
            foreach (var row in rows)
            {
                if (row.ValueKind == System.Text.Json.JsonValueKind.Object)
                    sb.AppendLine("| " + string.Join(" | ", keys.Select(k => row.TryGetProperty(k, out var v) ? GetValue(v).Replace("|", "\\|") : "")) + " |");
            }
            return sb.ToString();
        }
        if (root.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var p in root.EnumerateObject())
                sb.AppendLine("- **" + p.Name + ":** " + GetValue(p.Value).Replace("\n", " "));
            return sb.ToString();
        }
        return GetValue(root);
    }

    private static string SerializeToHtml(object data)
    {
        if (data == null) return "<html><body></body></html>";
        var json = System.Text.Json.JsonSerializer.Serialize(data);
        var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            var rows = root.EnumerateArray().ToList();
            if (rows.Count == 0) return "<html><body><table></table></body></html>";
            var first = rows[0];
            if (first.ValueKind != System.Text.Json.JsonValueKind.Object)
                return "<html><body><pre>" + EscapeHtml(GetValue(first)) + "</pre></body></html>";
            var keys = first.EnumerateObject().Select(p => p.Name).ToList();
            var sb = new System.Text.StringBuilder();
            sb.Append("<html><body><table><tr>");
            foreach (var k in keys) sb.Append("<th>").Append(EscapeHtml(k)).Append("</th>");
            sb.AppendLine("</tr>");
            foreach (var row in rows)
            {
                if (row.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    sb.Append("<tr>");
                    foreach (var k in keys)
                        sb.Append("<td>").Append(EscapeHtml(row.TryGetProperty(k, out var v) ? GetValue(v) : "")).Append("</td>");
                    sb.AppendLine("</tr>");
                }
            }
            sb.Append("</table></body></html>");
            return sb.ToString();
        }
        if (root.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("<html><body><table><tr><th>Key</th><th>Value</th></tr>");
            foreach (var p in root.EnumerateObject())
            {
                sb.Append("<tr><td>").Append(EscapeHtml(p.Name)).Append("</td><td>").Append(EscapeHtml(GetValue(p.Value))).Append("</td></tr>");
            }
            sb.Append("</table></body></html>");
            return sb.ToString();
        }
        return "<html><body><pre>" + EscapeHtml(GetValue(root)) + "</pre></body></html>";
    }

    private static string EscapeHtml(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        return s
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;");
    }
}
