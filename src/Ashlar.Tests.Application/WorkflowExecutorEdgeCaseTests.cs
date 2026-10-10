using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Ashlar.Core.Application.Common.Ports;
using Ashlar.Core.Application.Common.Services;
using Ashlar.Core.Application.Workflows;
using Ashlar.Core.Domain.Agents;
using Ashlar.Core.Domain.Behaviors;
using Ashlar.Core.Domain.Bricks;
using Ashlar.Core.Domain.Clusters;
using Ashlar.Core.Domain.Execution;
using Ashlar.Core.Domain.Execution.Events;
using Ashlar.Core.Domain.Workflows;
using Xunit;
using Ashlar.Abstractions.Security.Egress;

namespace Ashlar.Tests.Application;

/// <summary>Tests for a workflow executor edge case.</summary>
public sealed class WorkflowExecutorEdgeCaseTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Fallback_preserves_first_refusal_unless_an_alternative_succeeds(bool clusterNode, bool succeeds)
    {
        var refusal = new EgressRefusedException(new EgressGuard("full", "enforce").Evaluate(
            new EgressRequest(EgressFamilies.Http, "workflow", new Uri("https://remote.example"))));
        var brick = new FallbackStubBrick { FirstFailure = new IOException("wrapped", refusal), FailAlternative = !succeeds };
        var bricks = new Mock<IBrickRegistry>();
        bricks.Setup(x => x.GetBrick("fallback-brick")).Returns(brick);
        var store = new Mock<IClusterStore>();
        store.Setup(x => x.GetByIdAsync("cluster", It.IsAny<CancellationToken>())).ReturnsAsync(new Cluster
        {
            Id = "cluster", Name = "cluster", Description = "test",
            Bricks = [new ClusterBrick { LocalId = "b", BrickId = "fallback-brick" }],
            Interface = new ClusterInterface()
        });
        var executor = new WorkflowExecutor(Mock.Of<IAgentRegistry>(), bricks.Object, Mock.Of<IBehaviorRegistry>(),
            Mock.Of<IBehaviorExecutor>(), new SequentialLoopKernel(), Mock.Of<ITextFileSystem>(),
            NullLogger<WorkflowExecutor>.Instance, clusterStore: store.Object);
        var definition = new WorkflowDefinition
        {
            Id = "refusal", Name = "refusal",
            Nodes = [clusterNode ? new ClusterNode { Id = "node", ClusterId = "cluster" }
                : new BrickNode { Id = "node", BrickId = "fallback-brick" }]
        };
        if (succeeds)
            (await executor.ExecuteAsync(definition, new WorkflowInput())).Success.Should().BeTrue();
        else
            (await Record.ExceptionAsync(() => executor.ExecuteAsync(definition, new WorkflowInput())))
                .Should().BeSameAs(refusal);
        brick.Calls.Should().Be(2);
    }

    [Fact]
    public async Task ExecuteAsync_runs_agent_node_and_forwards_brick_events()
    {
        var agent = new AgentCard { Id = "agent-1", Name = "Agent", Behaviors = new List<string> { "behavior-1" } };
        var behavior = new Behavior { Id = "behavior-1", Name = "Behavior", Steps = new List<BehaviorStep>() };

        var agents = new Mock<IAgentRegistry>();
        agents.Setup(a => a.GetAgent("agent-1")).Returns(agent);
        var behaviors = new Mock<IBehaviorRegistry>();
        behaviors.Setup(b => b.GetBehavior("behavior-1")).Returns(behavior);

        var executor = new Mock<IBehaviorExecutor>();
        executor.Setup(e => e.ExecuteWithEventsAsync(
                agent,
                behavior,
                It.IsAny<BehaviorInput>(),
                It.IsAny<ExecutionOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns(ToAsyncEnumerable(new ExecutionEvent[]
            {
                new BehaviorCompletedEvent("behavior-1", true, new Dictionary<string, object> { ["result"] = "ok" }),
            }));

        var workflow = new WorkflowExecutor(
            agents.Object,
            Mock.Of<IBrickRegistry>(),
            behaviors.Object,
            executor.Object,
            new SequentialLoopKernel(),
            Mock.Of<ITextFileSystem>(),
            NullLogger<WorkflowExecutor>.Instance);

        var definition = new WorkflowDefinition
        {
            Id = "wf-agent",
            Name = "Agent workflow",
            Nodes = new List<WorkflowNode>
            {
                new AgentNode
                {
                    Id = "agent-node",
                    AgentId = "agent-1",
                    Inputs = new List<NodePort>(),
                    Outputs = new List<NodePort> { new NodePort { Id = "out", Name = "result", Direction = PortDirection.Output, DataType = "string" } },
                },
            },
        };

        var result = await workflow.ExecuteAsync(definition, new WorkflowInput());
        result.Success.Should().BeTrue();
        result.NodeResults["agent-node"].Outputs["result"].Should().Be("ok");
    }

    [Fact]
    public async Task ExecuteAsync_reads_file_input_and_webhook_input()
    {
        var fs = new Mock<ITextFileSystem>();
        fs.Setup(f => f.ReadAllTextAsync("/tmp/input.txt", It.IsAny<CancellationToken>()))
            .ReturnsAsync("file-body");

        var webhook = new Mock<IWorkflowWebhookClient>();
        webhook.Setup(w => w.GetAsync("https://example.test/hook", It.IsAny<CancellationToken>()))
            .ReturnsAsync("webhook-body");

        var workflow = new WorkflowExecutor(
            Mock.Of<IAgentRegistry>(),
            Mock.Of<IBrickRegistry>(),
            Mock.Of<IBehaviorRegistry>(),
            Mock.Of<IBehaviorExecutor>(),
            new SequentialLoopKernel(),
            fs.Object,
            NullLogger<WorkflowExecutor>.Instance,
            webhookClient: webhook.Object);

        var fileWorkflow = new WorkflowDefinition
        {
            Id = "wf-file",
            Name = "File input",
            Nodes = new List<WorkflowNode>
            {
                new InputNode
                {
                    Id = "input",
                    Type = InputType.File,
                    FilePath = "/tmp/input.txt",
                    Outputs = new List<NodePort> { new NodePort { Id = "out", Name = "data", Direction = PortDirection.Output, DataType = "string" } },
                },
            },
        };

        (await workflow.ExecuteAsync(fileWorkflow, new WorkflowInput()))
            .NodeResults["input"].Outputs["data"].Should().Be("file-body");

        var webhookWorkflow = new WorkflowDefinition
        {
            Id = "wf-webhook",
            Name = "Webhook input",
            Nodes = new List<WorkflowNode>
            {
                new InputNode
                {
                    Id = "hook",
                    Type = InputType.Webhook,
                    WebhookUrl = "https://example.test/hook",
                    Outputs = new List<NodePort> { new NodePort { Id = "out", Name = "data", Direction = PortDirection.Output, DataType = "string" } },
                },
            },
        };

        (await workflow.ExecuteAsync(webhookWorkflow, new WorkflowInput()))
            .NodeResults["hook"].Outputs["data"].Should().Be("webhook-body");
    }

    [Fact]
    public async Task ExecuteAsync_throws_on_type_mismatch_and_missing_cluster_store()
    {
        var workflow = new WorkflowExecutor(
            Mock.Of<IAgentRegistry>(),
            Mock.Of<IBrickRegistry>(),
            Mock.Of<IBehaviorRegistry>(),
            Mock.Of<IBehaviorExecutor>(),
            new SequentialLoopKernel(),
            Mock.Of<ITextFileSystem>(),
            NullLogger<WorkflowExecutor>.Instance);

        var invalid = new WorkflowDefinition
        {
            Id = "wf-invalid",
            Name = "Invalid",
            Nodes = new List<WorkflowNode>
            {
                new InputNode
                {
                    Id = "input",
                    Type = InputType.Content,
                    Content = "text",
                    Outputs = new List<NodePort> { new NodePort { Id = "out", Name = "data", Direction = PortDirection.Output, DataType = "string" } },
                },
                new OutputNode
                {
                    Id = "output",
                    Type = OutputType.Display,
                    Format = WorkflowOutputFormat.Json,
                    Inputs = new List<NodePort> { new NodePort { Id = "in", Name = "input", Direction = PortDirection.Input, DataType = "number" } },
                },
            },
            Connections = new List<VisualWorkflowConnection>
            {
                new VisualWorkflowConnection { FromNodeId = "input", FromPortId = "out", ToNodeId = "output", ToPortId = "in" },
            },
        };

        var act = () => workflow.ExecuteAsync(invalid, new WorkflowInput());
        await act.Should().ThrowAsync<WorkflowValidationException>();

        var clusterWorkflow = new WorkflowDefinition
        {
            Id = "wf-cluster",
            Name = "Cluster",
            Nodes = new List<WorkflowNode>
            {
                new ClusterNode
                {
                    Id = "cluster",
                    ClusterId = "c1",
                    Inputs = new List<NodePort>(),
                    Outputs = new List<NodePort>(),
                },
            },
        };

        var actCluster = () => workflow.ExecuteAsync(clusterWorkflow, new WorkflowInput());
        await actCluster.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*IClusterStore*");
    }

    [Fact]
    public async Task ExecuteAsync_runs_brick_node_with_implementation_fallback()
    {
        var brick = new FallbackStubBrick();
        var bricks = new Mock<IBrickRegistry>();
        bricks.Setup(b => b.GetBrick("fallback-brick")).Returns(brick);

        var workflow = new WorkflowExecutor(
            Mock.Of<IAgentRegistry>(),
            bricks.Object,
            Mock.Of<IBehaviorRegistry>(),
            Mock.Of<IBehaviorExecutor>(),
            new SequentialLoopKernel(),
            Mock.Of<ITextFileSystem>(),
            NullLogger<WorkflowExecutor>.Instance);

        var definition = new WorkflowDefinition
        {
            Id = "wf-brick",
            Name = "DomainBrick workflow",
            Nodes = new List<WorkflowNode>
            {
                new BrickNode
                {
                    Id = "brick-node",
                    BrickId = "fallback-brick",
                    Outputs = new List<NodePort>
                    {
                        new NodePort { Id = "out", Name = "result", Direction = PortDirection.Output, DataType = "string" },
                    },
                },
            },
        };

        var result = await workflow.ExecuteAsync(definition, new WorkflowInput());
        result.NodeResults["brick-node"].Outputs["result"].Should().Be("agentic-ok");
    }

    [Fact]
    public async Task ExecuteAsync_executes_cluster_node_and_maps_outputs()
    {
        var brick = new SuccessStubBrick("cluster-brick", "cluster-out");
        var bricks = new Mock<IBrickRegistry>();
        bricks.Setup(b => b.GetBrick("cluster-brick")).Returns(brick);

        var cluster = new Cluster
        {
            Id = "c1",
            Name = "Test cluster",
            Description = "cluster",
            Bricks = new[]
            {
                new ClusterBrick { LocalId = "b1", BrickId = "cluster-brick" },
            },
            Interface = new ClusterInterface
            {
                Outputs = new[]
                {
                    new ClusterPort
                    {
                        Name = "out",
                        Type = "string",
                        InternalMapping = "b1.result",
                    },
                },
            },
        };

        var clusterStore = new Mock<IClusterStore>();
        clusterStore.Setup(c => c.GetByIdAsync("c1", It.IsAny<CancellationToken>())).ReturnsAsync(cluster);

        var workflow = new WorkflowExecutor(
            Mock.Of<IAgentRegistry>(),
            bricks.Object,
            Mock.Of<IBehaviorRegistry>(),
            Mock.Of<IBehaviorExecutor>(),
            new SequentialLoopKernel(),
            Mock.Of<ITextFileSystem>(),
            NullLogger<WorkflowExecutor>.Instance,
            clusterStore: clusterStore.Object);

        var definition = new WorkflowDefinition
        {
            Id = "wf-cluster-run",
            Name = "Cluster run",
            Nodes = new List<WorkflowNode>
            {
                new ClusterNode
                {
                    Id = "cluster-node",
                    ClusterId = "c1",
                    Outputs = new List<NodePort>
                    {
                        new NodePort { Id = "out", Name = "out", Direction = PortDirection.Output, DataType = "string" },
                    },
                },
            },
        };

        var result = await workflow.ExecuteAsync(definition, new WorkflowInput());
        result.NodeResults["cluster-node"].Outputs["out"].Should().Be("cluster-out");
    }

    [Fact]
    public async Task ExecuteAsync_reads_database_input_and_writes_file_and_webhook_outputs()
    {
        var fs = new Mock<ITextFileSystem>();
        fs.Setup(f => f.WriteAllTextAsync("/tmp/out.json", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var dbReader = new Mock<IWorkflowDatabaseReader>();
        dbReader.Setup(r => r.ExecuteQueryAsync("conn", "select 1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new { id = 1 });

        var dbWriter = new Mock<IWorkflowDatabaseWriter>();
        dbWriter.Setup(w => w.WriteAsync("conn", "results", It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var webhook = new Mock<IWorkflowWebhookClient>();
        webhook.Setup(w => w.PostAsync("https://example.test/out", It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var workflow = new WorkflowExecutor(
            Mock.Of<IAgentRegistry>(),
            Mock.Of<IBrickRegistry>(),
            Mock.Of<IBehaviorRegistry>(),
            Mock.Of<IBehaviorExecutor>(),
            new SequentialLoopKernel(),
            fs.Object,
            NullLogger<WorkflowExecutor>.Instance,
            webhookClient: webhook.Object,
            databaseReader: dbReader.Object,
            databaseWriter: dbWriter.Object);

        var dbInputWorkflow = new WorkflowDefinition
        {
            Id = "wf-db-in",
            Name = "Db input",
            Nodes = new List<WorkflowNode>
            {
                new InputNode
                {
                    Id = "db-in",
                    Type = InputType.Database,
                    DatabaseConnectionString = "conn",
                    Query = "select 1",
                    Outputs = new List<NodePort> { new NodePort { Id = "out", Name = "data", Direction = PortDirection.Output, DataType = "any" } },
                },
            },
        };

        (await workflow.ExecuteAsync(dbInputWorkflow, new WorkflowInput()))
            .NodeResults["db-in"].Outputs["data"].Should().NotBeNull();

        var outputWorkflow = new WorkflowDefinition
        {
            Id = "wf-outputs",
            Name = "Outputs",
            Nodes = new List<WorkflowNode>
            {
                new InputNode
                {
                    Id = "input",
                    Type = InputType.Content,
                    Content = """{"name":"ashlar"}""",
                    Outputs = new List<NodePort> { new NodePort { Id = "out", Name = "data", Direction = PortDirection.Output, DataType = "any" } },
                },
                new OutputNode
                {
                    Id = "file-out",
                    Type = OutputType.File,
                    Format = WorkflowOutputFormat.Json,
                    FilePath = "/tmp/out.json",
                    Inputs = new List<NodePort> { new NodePort { Id = "in", Name = "data", Direction = PortDirection.Input, DataType = "any" } },
                },
                new OutputNode
                {
                    Id = "hook-out",
                    Type = OutputType.Webhook,
                    WebhookUrl = "https://example.test/out",
                    Inputs = new List<NodePort> { new NodePort { Id = "in", Name = "data", Direction = PortDirection.Input, DataType = "any" } },
                },
                new OutputNode
                {
                    Id = "db-out",
                    Type = OutputType.Database,
                    DatabaseConnectionString = "conn",
                    TableName = "results",
                    Inputs = new List<NodePort> { new NodePort { Id = "in", Name = "data", Direction = PortDirection.Input, DataType = "any" } },
                },
            },
            Connections = new List<VisualWorkflowConnection>
            {
                new() { FromNodeId = "input", FromPortId = "out", ToNodeId = "file-out", ToPortId = "in" },
                new() { FromNodeId = "input", FromPortId = "out", ToNodeId = "hook-out", ToPortId = "in" },
                new() { FromNodeId = "input", FromPortId = "out", ToNodeId = "db-out", ToPortId = "in" },
            },
        };

        await workflow.ExecuteAsync(outputWorkflow, new WorkflowInput());

        fs.Verify(f => f.WriteAllTextAsync("/tmp/out.json", It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        webhook.Verify(w => w.PostAsync("https://example.test/out", It.IsAny<object>(), It.IsAny<CancellationToken>()), Times.Once);
        dbWriter.Verify(w => w.WriteAsync("conn", "results", It.IsAny<object>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_cluster_continue_policy_skips_missing_brick()
    {
        var brick = new SuccessStubBrick("only-brick", "ok");
        var bricks = new Mock<IBrickRegistry>();
        bricks.Setup(b => b.GetBrick("only-brick")).Returns(brick);

        var cluster = new Cluster
        {
            Id = "c-continue",
            Name = "Continue cluster",
            Description = "continue on missing",
            Bricks = new[]
            {
                new ClusterBrick { LocalId = "b1", BrickId = "only-brick" },
                new ClusterBrick { LocalId = "b2", BrickId = "missing-brick" },
            },
            Connections = Array.Empty<ClusterConnection>(),
            Interface = new ClusterInterface
            {
                FailurePolicy = FailurePolicy.Continue,
                Outputs = new[] { new ClusterPort { Name = "out", Type = "string", InternalMapping = "b1.result" } },
            },
        };

        var clusterStore = new Mock<IClusterStore>();
        clusterStore.Setup(c => c.GetByIdAsync("c-continue", It.IsAny<CancellationToken>())).ReturnsAsync(cluster);

        var workflow = new WorkflowExecutor(
            Mock.Of<IAgentRegistry>(),
            bricks.Object,
            Mock.Of<IBehaviorRegistry>(),
            Mock.Of<IBehaviorExecutor>(),
            new SequentialLoopKernel(),
            Mock.Of<ITextFileSystem>(),
            NullLogger<WorkflowExecutor>.Instance,
            clusterStore: clusterStore.Object);

        var result = await workflow.ExecuteAsync(new WorkflowDefinition
        {
            Id = "wf-cluster-continue",
            Name = "Cluster continue",
            Nodes = new List<WorkflowNode>
            {
                new ClusterNode
                {
                    Id = "cluster",
                    ClusterId = "c-continue",
                    Outputs = new List<NodePort> { new NodePort { Id = "out", Name = "out", Direction = PortDirection.Output, DataType = "string" } },
                },
            },
        }, new WorkflowInput());

        result.NodeResults["cluster"].Outputs["out"].Should().Be("ok");
    }

    [Fact]
    public async Task ExecuteAsync_emits_failure_event_and_rethrows()
    {
        var events = new List<WorkflowExecutionEvent>();
        var workflow = new WorkflowExecutor(
            Mock.Of<IAgentRegistry>(),
            Mock.Of<IBrickRegistry>(),
            Mock.Of<IBehaviorRegistry>(),
            Mock.Of<IBehaviorExecutor>(),
            new SequentialLoopKernel(),
            Mock.Of<ITextFileSystem>(),
            NullLogger<WorkflowExecutor>.Instance);

        using var sub = workflow.Events.Subscribe(events.Add);

        var act = () => workflow.ExecuteAsync(new WorkflowDefinition
        {
            Id = "wf-fail",
            Name = "Fail",
            Nodes = new List<WorkflowNode>
            {
                new AgentNode { Id = "missing-agent", AgentId = "nope", Inputs = new List<NodePort>(), Outputs = new List<NodePort>() },
            },
        }, new WorkflowInput());

        await act.Should().ThrowAsync<InvalidOperationException>();
        events.Should().Contain(e => e is WorkflowFailedEvent);
    }

    [Fact]
    public async Task ExecuteAsync_validates_missing_nodes_and_ports()
    {
        var workflow = new WorkflowExecutor(
            Mock.Of<IAgentRegistry>(),
            Mock.Of<IBrickRegistry>(),
            Mock.Of<IBehaviorRegistry>(),
            Mock.Of<IBehaviorExecutor>(),
            new SequentialLoopKernel(),
            Mock.Of<ITextFileSystem>(),
            NullLogger<WorkflowExecutor>.Instance);

        var invalid = new WorkflowDefinition
        {
            Id = "wf-bad-conn",
            Name = "Bad connection",
            Nodes = new List<WorkflowNode>
            {
                new InputNode
                {
                    Id = "input",
                    Type = InputType.Content,
                    Content = "x",
                    Outputs = new List<NodePort> { new NodePort { Id = "out", Name = "data", Direction = PortDirection.Output, DataType = "string" } },
                },
                new OutputNode
                {
                    Id = "output",
                    Type = OutputType.Display,
                    Format = WorkflowOutputFormat.Json,
                    Inputs = new List<NodePort> { new NodePort { Id = "in", Name = "input", Direction = PortDirection.Input, DataType = "string" } },
                },
            },
            Connections = new List<VisualWorkflowConnection>
            {
                new() { FromNodeId = "missing", FromPortId = "out", ToNodeId = "output", ToPortId = "in" },
            },
        };

        var act = () => workflow.ExecuteAsync(invalid, new WorkflowInput());
        await act.Should().ThrowAsync<WorkflowValidationException>()
            .Where(ex => ex.Errors.Any(e => e.Contains("non-existent", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task ExecuteAsync_throws_when_agent_or_brick_missing()
    {
        var agents = new Mock<IAgentRegistry>();
        agents.Setup(a => a.GetAgent("ghost")).Returns((AgentCard?)null);

        var agentWorkflow = new WorkflowExecutor(
            agents.Object,
            Mock.Of<IBrickRegistry>(),
            Mock.Of<IBehaviorRegistry>(),
            Mock.Of<IBehaviorExecutor>(),
            new SequentialLoopKernel(),
            Mock.Of<ITextFileSystem>(),
            NullLogger<WorkflowExecutor>.Instance);

        var actAgent = () => agentWorkflow.ExecuteAsync(new WorkflowDefinition
        {
            Id = "wf-agent-miss",
            Name = "Missing agent",
            Nodes = new List<WorkflowNode> { new AgentNode { Id = "a", AgentId = "ghost", Inputs = new List<NodePort>(), Outputs = new List<NodePort>() } },
        }, new WorkflowInput());
        await actAgent.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Agent not found*");

        var brickWorkflow = new WorkflowExecutor(
            Mock.Of<IAgentRegistry>(),
            Mock.Of<IBrickRegistry>(),
            Mock.Of<IBehaviorRegistry>(),
            Mock.Of<IBehaviorExecutor>(),
            new SequentialLoopKernel(),
            Mock.Of<ITextFileSystem>(),
            NullLogger<WorkflowExecutor>.Instance);

        var actBrick = () => brickWorkflow.ExecuteAsync(new WorkflowDefinition
        {
            Id = "wf-brick-miss",
            Name = "Missing brick",
            Nodes = new List<WorkflowNode> { new BrickNode { Id = "b", BrickId = "missing", Outputs = new List<NodePort>() } },
        }, new WorkflowInput());
        await actBrick.Should().ThrowAsync<InvalidOperationException>().WithMessage("*DomainBrick not found*");
    }

    [Fact]
    public async Task ExecuteAsync_rejects_cyclic_workflow()
    {
        var workflow = new WorkflowExecutor(
            Mock.Of<IAgentRegistry>(),
            Mock.Of<IBrickRegistry>(),
            Mock.Of<IBehaviorRegistry>(),
            Mock.Of<IBehaviorExecutor>(),
            new SequentialLoopKernel(),
            Mock.Of<ITextFileSystem>(),
            NullLogger<WorkflowExecutor>.Instance);

        var cyclic = new WorkflowDefinition
        {
            Id = "wf-cycle",
            Name = "Cycle",
            Nodes = new List<WorkflowNode>
            {
                new TransformNode
                {
                    Id = "a",
                    Operation = TransformOperation.Map,
                    Expression = "value",
                    Inputs = new List<NodePort> { new NodePort { Id = "in", Name = "data", Direction = PortDirection.Input, DataType = "any" } },
                    Outputs = new List<NodePort> { new NodePort { Id = "out", Name = "data", Direction = PortDirection.Output, DataType = "any" } },
                },
                new TransformNode
                {
                    Id = "b",
                    Operation = TransformOperation.Map,
                    Expression = "value",
                    Inputs = new List<NodePort> { new NodePort { Id = "in", Name = "data", Direction = PortDirection.Input, DataType = "any" } },
                    Outputs = new List<NodePort> { new NodePort { Id = "out", Name = "data", Direction = PortDirection.Output, DataType = "any" } },
                },
            },
            Connections = new List<VisualWorkflowConnection>
            {
                new() { FromNodeId = "a", FromPortId = "out", ToNodeId = "b", ToPortId = "in" },
                new() { FromNodeId = "b", FromPortId = "out", ToNodeId = "a", ToPortId = "in" },
            },
        };

        var act = () => workflow.ExecuteAsync(cyclic, new WorkflowInput());
        await act.Should().ThrowAsync<WorkflowValidationException>()
            .Where(ex => ex.Errors.Any(e => e.Contains("cycles", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task ExecuteAsync_exercises_filter_reduce_conditional_display_and_io_guards()
    {
        var brick = new ListStubBrick();
        var emptyList = new EmptyListStubBrick();
        var bricks = new Mock<IBrickRegistry>();
        bricks.Setup(b => b.GetBrick("list-brick")).Returns(brick);
        bricks.Setup(b => b.GetBrick("empty-list-brick")).Returns(emptyList);
        bricks.Setup(b => b.GetBrick("score-dict-brick")).Returns(new ScoreDictStubBrick());

        var workflow = new WorkflowExecutor(
            Mock.Of<IAgentRegistry>(),
            bricks.Object,
            Mock.Of<IBehaviorRegistry>(),
            Mock.Of<IBehaviorExecutor>(),
            new SequentialLoopKernel(),
            Mock.Of<ITextFileSystem>(),
            NullLogger<WorkflowExecutor>.Instance);

        async Task<int> FilterCount(string expression)
        {
            var result = await workflow.ExecuteAsync(new WorkflowDefinition
            {
                Id = "wf-filter-" + expression.GetHashCode(),
                Name = "Filter",
                Nodes = new List<WorkflowNode>
                {
                    new BrickNode { Id = "source", BrickId = "list-brick", Outputs = new List<NodePort> { new NodePort { Id = "out", Name = "data", Direction = PortDirection.Output, DataType = "any" } } },
                    new TransformNode
                    {
                        Id = "filter",
                        Operation = TransformOperation.Filter,
                        Expression = expression,
                        Inputs = new List<NodePort> { new NodePort { Id = "in", Name = "data", Direction = PortDirection.Input, DataType = "any" } },
                        Outputs = new List<NodePort> { new NodePort { Id = "out", Name = "data", Direction = PortDirection.Output, DataType = "any" } },
                    },
                },
                Connections = new List<VisualWorkflowConnection> { new() { FromNodeId = "source", FromPortId = "out", ToNodeId = "filter", ToPortId = "in" } },
            }, new WorkflowInput());
            return ((List<Dictionary<string, object>>)result.NodeResults["filter"].Outputs["data"]).Count;
        }

        (await FilterCount("score != '3'")).Should().Be(1);
        (await FilterCount("score >= '5'")).Should().Be(1);
        (await FilterCount("score <= '10'")).Should().Be(2);
        (await FilterCount("score < '20'")).Should().Be(2);
        (await FilterCount("score > '5'")).Should().Be(1);

        var reduceFirst = await workflow.ExecuteAsync(new WorkflowDefinition
        {
            Id = "wf-reduce-first",
            Name = "Reduce first",
            Nodes = new List<WorkflowNode>
            {
                new BrickNode { Id = "source", BrickId = "list-brick", Outputs = new List<NodePort> { new NodePort { Id = "out", Name = "data", Direction = PortDirection.Output, DataType = "any" } } },
                new TransformNode
                {
                    Id = "reduce",
                    Operation = TransformOperation.Reduce,
                    Expression = "first",
                    Inputs = new List<NodePort> { new NodePort { Id = "in", Name = "data", Direction = PortDirection.Input, DataType = "any" } },
                    Outputs = new List<NodePort> { new NodePort { Id = "out", Name = "data", Direction = PortDirection.Output, DataType = "any" } },
                },
            },
            Connections = new List<VisualWorkflowConnection> { new() { FromNodeId = "source", FromPortId = "out", ToNodeId = "reduce", ToPortId = "in" } },
        }, new WorkflowInput());
        reduceFirst.NodeResults["reduce"].Outputs["data"].Should().BeAssignableTo<Dictionary<string, object>>();

        var emptyReduce = await workflow.ExecuteAsync(new WorkflowDefinition
        {
            Id = "wf-reduce-empty",
            Name = "Reduce empty",
            Nodes = new List<WorkflowNode>
            {
                new BrickNode { Id = "source", BrickId = "empty-list-brick", Outputs = new List<NodePort> { new NodePort { Id = "out", Name = "data", Direction = PortDirection.Output, DataType = "any" } } },
                new TransformNode
                {
                    Id = "reduce",
                    Operation = TransformOperation.Reduce,
                    Expression = "sum",
                    Inputs = new List<NodePort> { new NodePort { Id = "in", Name = "data", Direction = PortDirection.Input, DataType = "any" } },
                    Outputs = new List<NodePort> { new NodePort { Id = "out", Name = "data", Direction = PortDirection.Output, DataType = "any" } },
                },
            },
            Connections = new List<VisualWorkflowConnection> { new() { FromNodeId = "source", FromPortId = "out", ToNodeId = "reduce", ToPortId = "in" } },
        }, new WorkflowInput());
        ((Dictionary<string, object>)emptyReduce.NodeResults["reduce"].Outputs["data"])["result"].Should().BeNull();

        var reduceLast = await workflow.ExecuteAsync(new WorkflowDefinition
        {
            Id = "wf-reduce-last",
            Name = "Reduce last",
            Nodes = new List<WorkflowNode>
            {
                new BrickNode { Id = "source", BrickId = "list-brick", Outputs = new List<NodePort> { new NodePort { Id = "out", Name = "data", Direction = PortDirection.Output, DataType = "any" } } },
                new TransformNode
                {
                    Id = "reduce",
                    Operation = TransformOperation.Reduce,
                    Expression = "last",
                    Inputs = new List<NodePort> { new NodePort { Id = "in", Name = "data", Direction = PortDirection.Input, DataType = "any" } },
                    Outputs = new List<NodePort> { new NodePort { Id = "out", Name = "data", Direction = PortDirection.Output, DataType = "any" } },
                },
            },
            Connections = new List<VisualWorkflowConnection> { new() { FromNodeId = "source", FromPortId = "out", ToNodeId = "reduce", ToPortId = "in" } },
        }, new WorkflowInput());
        reduceLast.NodeResults["reduce"].Outputs["data"].Should().BeAssignableTo<Dictionary<string, object>>();

        var conditional = await workflow.ExecuteAsync(new WorkflowDefinition
        {
            Id = "wf-cond-ops",
            Name = "Conditional ops",
            Nodes = new List<WorkflowNode>
            {
                new BrickNode { Id = "source", BrickId = "score-dict-brick", Outputs = new List<NodePort> { new NodePort { Id = "out", Name = "data", Direction = PortDirection.Output, DataType = "any" } } },
                new ConditionalNode
                {
                    Id = "cond",
                    Condition = "score >= '5'",
                    Inputs = new List<NodePort> { new NodePort { Id = "in", Name = "data", Direction = PortDirection.Input, DataType = "any" } },
                    Outputs = new List<NodePort> { new NodePort { Id = "out", Name = "condition", Direction = PortDirection.Output, DataType = "boolean" } },
                },
            },
            Connections = new List<VisualWorkflowConnection> { new() { FromNodeId = "source", FromPortId = "out", ToNodeId = "cond", ToPortId = "in" } },
        }, new WorkflowInput());
        conditional.NodeResults["cond"].Outputs["condition"].Should().Be(true);

        var display = await workflow.ExecuteAsync(new WorkflowDefinition
        {
            Id = "wf-display",
            Name = "Display",
            Nodes = new List<WorkflowNode>
            {
                new InputNode { Id = "input", Type = InputType.Content, Content = "shown", Outputs = new List<NodePort> { new NodePort { Id = "out", Name = "data", Direction = PortDirection.Output, DataType = "any" } } },
                new OutputNode
                {
                    Id = "display",
                    Type = OutputType.Display,
                    Format = WorkflowOutputFormat.Json,
                    Inputs = new List<NodePort> { new NodePort { Id = "in", Name = "data", Direction = PortDirection.Input, DataType = "any" } },
                },
            },
            Connections = new List<VisualWorkflowConnection> { new() { FromNodeId = "input", FromPortId = "out", ToNodeId = "display", ToPortId = "in" } },
        }, new WorkflowInput());
        display.NodeResults["display"].Outputs["output"].Should().Be("shown");

        (await FilterCount("score == '10'")).Should().Be(1);

        var fs = new Mock<ITextFileSystem>();
        fs.Setup(f => f.WriteAllTextAsync("/tmp/scalar.html", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var scalarHtml = await new WorkflowExecutor(
            Mock.Of<IAgentRegistry>(),
            bricks.Object,
            Mock.Of<IBehaviorRegistry>(),
            Mock.Of<IBehaviorExecutor>(),
            new SequentialLoopKernel(),
            fs.Object,
            NullLogger<WorkflowExecutor>.Instance).ExecuteAsync(new WorkflowDefinition
        {
            Id = "wf-scalar-html",
            Name = "Scalar html",
            Nodes = new List<WorkflowNode>
            {
                new InputNode
                {
                    Id = "input",
                    Type = InputType.Content,
                    Content = "plain",
                    Outputs = new List<NodePort> { new NodePort { Id = "out", Name = "data", Direction = PortDirection.Output, DataType = "any" } },
                },
                new OutputNode
                {
                    Id = "file-out",
                    Type = OutputType.File,
                    Format = WorkflowOutputFormat.Html,
                    FilePath = "/tmp/scalar.html",
                    Inputs = new List<NodePort> { new NodePort { Id = "in", Name = "data", Direction = PortDirection.Input, DataType = "any" } },
                },
            },
            Connections = new List<VisualWorkflowConnection>
            {
                new() { FromNodeId = "input", FromPortId = "out", ToNodeId = "file-out", ToPortId = "in" },
            },
        }, new WorkflowInput());
        scalarHtml.Success.Should().BeTrue();

        var actWebhookIn = () => workflow.ExecuteAsync(new WorkflowDefinition
        {
            Id = "wf-wh-in",
            Name = "Webhook in",
            Nodes = new List<WorkflowNode> { new InputNode { Id = "hook", Type = InputType.Webhook, WebhookUrl = "https://x.test", Outputs = new List<NodePort>() } },
        }, new WorkflowInput());
        await actWebhookIn.Should().ThrowAsync<InvalidOperationException>().WithMessage("*IWorkflowWebhookClient*");

        var actPdf = () => workflow.ExecuteAsync(new WorkflowDefinition
        {
            Id = "wf-no-pdf",
            Name = "No pdf",
            Nodes = new List<WorkflowNode>
            {
                new InputNode { Id = "input", Type = InputType.Content, Content = "x", Outputs = new List<NodePort> { new NodePort { Id = "out", Name = "data", Direction = PortDirection.Output, DataType = "any" } } },
                new OutputNode { Id = "out", Type = OutputType.File, Format = WorkflowOutputFormat.Pdf, FilePath = "/tmp/x.pdf", Inputs = new List<NodePort> { new NodePort { Id = "in", Name = "data", Direction = PortDirection.Input, DataType = "any" } } },
            },
            Connections = new List<VisualWorkflowConnection> { new() { FromNodeId = "input", FromPortId = "out", ToNodeId = "out", ToPortId = "in" } },
        }, new WorkflowInput());
        await actPdf.Should().ThrowAsync<NotSupportedException>().WithMessage("*IWorkflowPdfExporter*");
    }

    [Fact]
    public async Task ExecuteAsync_parallel_inputs_and_merge_transform_combine_sources()
    {
        var brickA = new SuccessStubBrick("brick-a", "a");
        var brickB = new SuccessStubBrick("brick-b", "b");
        var bricks = new Mock<IBrickRegistry>();
        bricks.Setup(b => b.GetBrick("brick-a")).Returns(brickA);
        bricks.Setup(b => b.GetBrick("brick-b")).Returns(brickB);

        var workflow = new WorkflowExecutor(
            Mock.Of<IAgentRegistry>(),
            bricks.Object,
            Mock.Of<IBehaviorRegistry>(),
            Mock.Of<IBehaviorExecutor>(),
            new SequentialLoopKernel(),
            Mock.Of<ITextFileSystem>(),
            NullLogger<WorkflowExecutor>.Instance);

        ExecutionPlan? plan = null;
        using (workflow.Events.Subscribe(e =>
        {
            if (e is ExecutionPlanCreatedEvent created)
            {
                plan = created.Plan;
            }
        }))
        {
            await workflow.ExecuteAsync(new WorkflowDefinition
            {
                Id = "wf-parallel",
                Name = "Parallel",
                Nodes = new List<WorkflowNode>
                {
                    new InputNode { Id = "in-a", Type = InputType.Content, Content = "alpha", Outputs = new List<NodePort> { new NodePort { Id = "out", Name = "data", Direction = PortDirection.Output, DataType = "any" } } },
                    new InputNode { Id = "in-b", Type = InputType.Content, Content = "beta", Outputs = new List<NodePort> { new NodePort { Id = "out", Name = "data", Direction = PortDirection.Output, DataType = "any" } } },
                },
            }, new WorkflowInput());
        }

        plan.Should().NotBeNull();
        plan!.ParallelGroups.Should().Contain(g => g.Count >= 2);

        var merge = await workflow.ExecuteAsync(new WorkflowDefinition
        {
            Id = "wf-merge",
            Name = "Merge",
            Nodes = new List<WorkflowNode>
            {
                new BrickNode { Id = "a", BrickId = "brick-a", Outputs = new List<NodePort> { new NodePort { Id = "out", Name = "result", Direction = PortDirection.Output, DataType = "any" } } },
                new BrickNode { Id = "b", BrickId = "brick-b", Outputs = new List<NodePort> { new NodePort { Id = "out", Name = "result", Direction = PortDirection.Output, DataType = "any" } } },
                new TransformNode
                {
                    Id = "merge",
                    Operation = TransformOperation.Merge,
                    Inputs = new List<NodePort>
                    {
                        new NodePort { Id = "in-a", Name = "left", Direction = PortDirection.Input, DataType = "any" },
                        new NodePort { Id = "in-b", Name = "right", Direction = PortDirection.Input, DataType = "any" },
                    },
                    Outputs = new List<NodePort> { new NodePort { Id = "out", Name = "data", Direction = PortDirection.Output, DataType = "any" } },
                },
            },
            Connections = new List<VisualWorkflowConnection>
            {
                new() { FromNodeId = "a", FromPortId = "out", ToNodeId = "merge", ToPortId = "in-a" },
                new() { FromNodeId = "b", FromPortId = "out", ToNodeId = "merge", ToPortId = "in-b" },
            },
        }, new WorkflowInput());

        ((System.Collections.IList)merge.NodeResults["merge"].Outputs["data"]).Count.Should().Be(2);
    }

    [Fact]
    public async Task ExecuteAsync_cluster_abort_throws_and_agent_without_behaviors_fails()
    {
        var agents = new Mock<IAgentRegistry>();
        agents.Setup(a => a.GetAgent("empty-agent")).Returns(new AgentCard { Id = "empty-agent", Name = "Empty", Behaviors = new List<string>() });

        var noBehaviors = new WorkflowExecutor(
            agents.Object,
            Mock.Of<IBrickRegistry>(),
            Mock.Of<IBehaviorRegistry>(),
            Mock.Of<IBehaviorExecutor>(),
            new SequentialLoopKernel(),
            Mock.Of<ITextFileSystem>(),
            NullLogger<WorkflowExecutor>.Instance);

        var actAgent = () => noBehaviors.ExecuteAsync(new WorkflowDefinition
        {
            Id = "wf-no-behaviors",
            Name = "No behaviors",
            Nodes = new List<WorkflowNode> { new AgentNode { Id = "a", AgentId = "empty-agent", Inputs = new List<NodePort>(), Outputs = new List<NodePort>() } },
        }, new WorkflowInput());
        await actAgent.Should().ThrowAsync<InvalidOperationException>().WithMessage("*no behaviors*");

        var cluster = new Cluster
        {
            Id = "c-abort",
            Name = "Abort cluster",
            Description = "abort on missing",
            Bricks = new[] { new ClusterBrick { LocalId = "b1", BrickId = "missing-brick" } },
            Connections = Array.Empty<ClusterConnection>(),
            Interface = new ClusterInterface { FailurePolicy = FailurePolicy.Abort },
        };
        var clusterStore = new Mock<IClusterStore>();
        clusterStore.Setup(c => c.GetByIdAsync("c-abort", It.IsAny<CancellationToken>())).ReturnsAsync(cluster);

        var clusterWorkflow = new WorkflowExecutor(
            Mock.Of<IAgentRegistry>(),
            Mock.Of<IBrickRegistry>(),
            Mock.Of<IBehaviorRegistry>(),
            Mock.Of<IBehaviorExecutor>(),
            new SequentialLoopKernel(),
            Mock.Of<ITextFileSystem>(),
            NullLogger<WorkflowExecutor>.Instance,
            clusterStore: clusterStore.Object);

        var actCluster = () => clusterWorkflow.ExecuteAsync(new WorkflowDefinition
        {
            Id = "wf-cluster-abort",
            Name = "Cluster abort",
            Nodes = new List<WorkflowNode> { new ClusterNode { Id = "cluster", ClusterId = "c-abort", Outputs = new List<NodePort>() } },
        }, new WorkflowInput());
        await actCluster.Should().ThrowAsync<InvalidOperationException>().WithMessage("*DomainBrick not found*");
    }

    [Fact]
    public async Task ExecuteAsync_serializes_empty_arrays_and_csv_escape()
    {
        var fs = new Mock<ITextFileSystem>();
        fs.Setup(f => f.WriteAllTextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var workflow = new WorkflowExecutor(
            Mock.Of<IAgentRegistry>(),
            Mock.Of<IBrickRegistry>(),
            Mock.Of<IBehaviorRegistry>(),
            Mock.Of<IBehaviorExecutor>(),
            new SequentialLoopKernel(),
            fs.Object,
            NullLogger<WorkflowExecutor>.Instance);

        await workflow.ExecuteAsync(new WorkflowDefinition
        {
            Id = "wf-empty-csv",
            Name = "Empty csv",
            Nodes = new List<WorkflowNode>
            {
                new InputNode { Id = "input", Type = InputType.Content, Content = "[]", Outputs = new List<NodePort> { new NodePort { Id = "out", Name = "data", Direction = PortDirection.Output, DataType = "any" } } },
                new OutputNode { Id = "out", Type = OutputType.File, Format = WorkflowOutputFormat.Csv, FilePath = "/tmp/empty.csv", Inputs = new List<NodePort> { new NodePort { Id = "in", Name = "data", Direction = PortDirection.Input, DataType = "any" } } },
            },
            Connections = new List<VisualWorkflowConnection> { new() { FromNodeId = "input", FromPortId = "out", ToNodeId = "out", ToPortId = "in" } },
        }, new WorkflowInput());

        await workflow.ExecuteAsync(new WorkflowDefinition
        {
            Id = "wf-empty-md",
            Name = "Empty md",
            Nodes = new List<WorkflowNode>
            {
                new InputNode { Id = "input", Type = InputType.Content, Content = "[]", Outputs = new List<NodePort> { new NodePort { Id = "out", Name = "data", Direction = PortDirection.Output, DataType = "any" } } },
                new OutputNode { Id = "out", Type = OutputType.File, Format = WorkflowOutputFormat.Markdown, FilePath = "/tmp/empty.md", Inputs = new List<NodePort> { new NodePort { Id = "in", Name = "data", Direction = PortDirection.Input, DataType = "any" } } },
            },
            Connections = new List<VisualWorkflowConnection> { new() { FromNodeId = "input", FromPortId = "out", ToNodeId = "out", ToPortId = "in" } },
        }, new WorkflowInput());

        await workflow.ExecuteAsync(new WorkflowDefinition
        {
            Id = "wf-csv-escape",
            Name = "Csv escape",
            Nodes = new List<WorkflowNode>
            {
                new InputNode { Id = "input", Type = InputType.Content, Content = """[{"name":"a,b","note":"line\nbreak"}]""", Outputs = new List<NodePort> { new NodePort { Id = "out", Name = "data", Direction = PortDirection.Output, DataType = "any" } } },
                new OutputNode { Id = "out", Type = OutputType.File, Format = WorkflowOutputFormat.Csv, FilePath = "/tmp/escaped.csv", Inputs = new List<NodePort> { new NodePort { Id = "in", Name = "data", Direction = PortDirection.Input, DataType = "any" } } },
            },
            Connections = new List<VisualWorkflowConnection> { new() { FromNodeId = "input", FromPortId = "out", ToNodeId = "out", ToPortId = "in" } },
        }, new WorkflowInput());

        fs.Verify(f => f.WriteAllTextAsync("/tmp/escaped.csv", It.Is<string>(s => s.Contains("\"a,b\"")), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_validates_type_mismatch_and_missing_ports()
    {
        var workflow = new WorkflowExecutor(
            Mock.Of<IAgentRegistry>(),
            Mock.Of<IBrickRegistry>(),
            Mock.Of<IBehaviorRegistry>(),
            Mock.Of<IBehaviorExecutor>(),
            new SequentialLoopKernel(),
            Mock.Of<ITextFileSystem>(),
            NullLogger<WorkflowExecutor>.Instance);

        var typeMismatch = new WorkflowDefinition
        {
            Id = "wf-type",
            Name = "Type mismatch",
            Nodes = new List<WorkflowNode>
            {
                new InputNode { Id = "input", Type = InputType.Content, Content = "x", Outputs = new List<NodePort> { new NodePort { Id = "out", Name = "data", Direction = PortDirection.Output, DataType = "string" } } },
                new OutputNode { Id = "output", Type = OutputType.Display, Format = WorkflowOutputFormat.Json, Inputs = new List<NodePort> { new NodePort { Id = "in", Name = "data", Direction = PortDirection.Input, DataType = "number" } } },
            },
            Connections = new List<VisualWorkflowConnection> { new() { FromNodeId = "input", FromPortId = "out", ToNodeId = "output", ToPortId = "in" } },
        };
        var actType = () => workflow.ExecuteAsync(typeMismatch, new WorkflowInput());
        await actType.Should().ThrowAsync<WorkflowValidationException>()
            .Where(ex => ex.Errors.Any(e => e.Contains("Type mismatch", StringComparison.OrdinalIgnoreCase)));

        var missingPort = new WorkflowDefinition
        {
            Id = "wf-port",
            Name = "Missing port",
            Nodes = new List<WorkflowNode>
            {
                new InputNode { Id = "input", Type = InputType.Content, Content = "x", Outputs = new List<NodePort> { new NodePort { Id = "out", Name = "data", Direction = PortDirection.Output, DataType = "any" } } },
                new OutputNode { Id = "output", Type = OutputType.Display, Format = WorkflowOutputFormat.Json, Inputs = new List<NodePort> { new NodePort { Id = "in", Name = "data", Direction = PortDirection.Input, DataType = "any" } } },
            },
            Connections = new List<VisualWorkflowConnection> { new() { FromNodeId = "input", FromPortId = "missing-out", ToNodeId = "output", ToPortId = "in" } },
        };
        var actPort = () => workflow.ExecuteAsync(missingPort, new WorkflowInput());
        await actPort.Should().ThrowAsync<WorkflowValidationException>()
            .Where(ex => ex.Errors.Any(e => e.Contains("non-existent port", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task ExecuteAsync_reports_missing_behavior_and_serializes_object_forms()
    {
        var agents = new Mock<IAgentRegistry>();
        agents.Setup(a => a.GetAgent("partial")).Returns(new AgentCard
        {
            Id = "partial",
            Name = "Partial",
            Behaviors = new List<string> { "ghost-behavior" },
        });
        var behaviors = new Mock<IBehaviorRegistry>();
        behaviors.Setup(b => b.GetBehavior("ghost-behavior")).Returns((Behavior?)null);

        var agentWorkflow = new WorkflowExecutor(
            agents.Object,
            Mock.Of<IBrickRegistry>(),
            behaviors.Object,
            Mock.Of<IBehaviorExecutor>(),
            new SequentialLoopKernel(),
            Mock.Of<ITextFileSystem>(),
            NullLogger<WorkflowExecutor>.Instance);

        var actBehavior = () => agentWorkflow.ExecuteAsync(new WorkflowDefinition
        {
            Id = "wf-behavior-miss",
            Name = "Missing behavior",
            Nodes = new List<WorkflowNode> { new AgentNode { Id = "a", AgentId = "partial", Inputs = new List<NodePort>(), Outputs = new List<NodePort>() } },
        }, new WorkflowInput());
        await actBehavior.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Behavior not found*");

        var fs = new Mock<ITextFileSystem>();
        fs.Setup(f => f.WriteAllTextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var workflow = new WorkflowExecutor(
            Mock.Of<IAgentRegistry>(),
            Mock.Of<IBrickRegistry>(),
            Mock.Of<IBehaviorRegistry>(),
            Mock.Of<IBehaviorExecutor>(),
            new SequentialLoopKernel(),
            fs.Object,
            NullLogger<WorkflowExecutor>.Instance);

        await workflow.ExecuteAsync(new WorkflowDefinition
        {
            Id = "wf-object-serialize",
            Name = "Object serialize",
            Nodes = new List<WorkflowNode>
            {
                new InputNode
                {
                    Id = "input",
                    Type = InputType.Content,
                    Content = """{"title":"ashlar","count":2}""",
                    Outputs = new List<NodePort> { new NodePort { Id = "out", Name = "data", Direction = PortDirection.Output, DataType = "any" } },
                },
                new OutputNode
                {
                    Id = "xml-out",
                    Name = "xmlResult",
                    Type = OutputType.File,
                    Format = WorkflowOutputFormat.Xml,
                    FilePath = "/tmp/object.xml",
                    Inputs = new List<NodePort> { new NodePort { Id = "in", Name = "data", Direction = PortDirection.Input, DataType = "any" } },
                },
                new OutputNode
                {
                    Id = "md-out",
                    Name = "mdResult",
                    Type = OutputType.File,
                    Format = WorkflowOutputFormat.Markdown,
                    FilePath = "/tmp/object.md",
                    Inputs = new List<NodePort> { new NodePort { Id = "in", Name = "data", Direction = PortDirection.Input, DataType = "any" } },
                },
            },
            Connections = new List<VisualWorkflowConnection>
            {
                new() { FromNodeId = "input", FromPortId = "out", ToNodeId = "xml-out", ToPortId = "in" },
                new() { FromNodeId = "input", FromPortId = "out", ToNodeId = "md-out", ToPortId = "in" },
            },
        }, new WorkflowInput());

        fs.Verify(f => f.WriteAllTextAsync("/tmp/object.xml", It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        fs.Verify(f => f.WriteAllTextAsync("/tmp/object.md", It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_validates_input_node_requirements_and_serializes_xml_scalars()
    {
        var workflow = new WorkflowExecutor(
            Mock.Of<IAgentRegistry>(),
            Mock.Of<IBrickRegistry>(),
            Mock.Of<IBehaviorRegistry>(),
            Mock.Of<IBehaviorExecutor>(),
            new SequentialLoopKernel(),
            Mock.Of<ITextFileSystem>(),
            NullLogger<WorkflowExecutor>.Instance,
            webhookClient: Mock.Of<IWorkflowWebhookClient>(),
            databaseReader: Mock.Of<IWorkflowDatabaseReader>());

        (await workflow.ExecuteAsync(new WorkflowDefinition
        {
            Id = "wf-file-empty",
            Name = "File empty path",
            Nodes = new List<WorkflowNode> { new InputNode { Id = "in", Type = InputType.File, Outputs = new List<NodePort> { new NodePort { Id = "out", Name = "data", Direction = PortDirection.Output, DataType = "any" } } } },
        }, new WorkflowInput())).NodeResults["in"].Outputs["data"].Should().Be("");

        var actHookUrl = () => workflow.ExecuteAsync(new WorkflowDefinition
        {
            Id = "wf-hook-url",
            Name = "Hook url",
            Nodes = new List<WorkflowNode> { new InputNode { Id = "in", Type = InputType.Webhook, WebhookUrl = "  ", Outputs = new List<NodePort>() } },
        }, new WorkflowInput());
        await actHookUrl.Should().ThrowAsync<InvalidOperationException>().WithMessage("*WebhookUrl*");

        var actDbConn = () => workflow.ExecuteAsync(new WorkflowDefinition
        {
            Id = "wf-db-conn",
            Name = "Db conn",
            Nodes = new List<WorkflowNode> { new InputNode { Id = "in", Type = InputType.Database, Query = "select 1", Outputs = new List<NodePort>() } },
        }, new WorkflowInput());
        await actDbConn.Should().ThrowAsync<InvalidOperationException>().WithMessage("*DatabaseConnectionString*");

        var actDbQuery = () => workflow.ExecuteAsync(new WorkflowDefinition
        {
            Id = "wf-db-query",
            Name = "Db query",
            Nodes = new List<WorkflowNode> { new InputNode { Id = "in", Type = InputType.Database, DatabaseConnectionString = "conn", Outputs = new List<NodePort>() } },
        }, new WorkflowInput());
        await actDbQuery.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Query*");

        var fs = new Mock<ITextFileSystem>();
        fs.Setup(f => f.WriteAllTextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var outWorkflow = new WorkflowExecutor(
            Mock.Of<IAgentRegistry>(),
            Mock.Of<IBrickRegistry>(),
            Mock.Of<IBehaviorRegistry>(),
            Mock.Of<IBehaviorExecutor>(),
            new SequentialLoopKernel(),
            fs.Object,
            NullLogger<WorkflowExecutor>.Instance);

        await outWorkflow.ExecuteAsync(new WorkflowDefinition
        {
            Id = "wf-xml-scalars",
            Name = "Xml scalars",
            Nodes = new List<WorkflowNode>
            {
                new InputNode
                {
                    Id = "input",
                    Type = InputType.Content,
                    Content = """{"enabled":true,"note":null}""",
                    Outputs = new List<NodePort> { new NodePort { Id = "out", Name = "data", Direction = PortDirection.Output, DataType = "any" } },
                },
                new OutputNode
                {
                    Id = "xml-out",
                    Type = OutputType.File,
                    Format = WorkflowOutputFormat.Xml,
                    FilePath = "/tmp/flag.xml",
                    Inputs = new List<NodePort> { new NodePort { Id = "in", Name = "data", Direction = PortDirection.Input, DataType = "any" } },
                },
            },
            Connections = new List<VisualWorkflowConnection> { new() { FromNodeId = "input", FromPortId = "out", ToNodeId = "xml-out", ToPortId = "in" } },
        }, new WorkflowInput());

        fs.Verify(f => f.WriteAllTextAsync("/tmp/flag.xml", It.Is<string>(s => s.Contains("enabled") && s.Contains("true")), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_runs_connected_cluster_with_parameter_mappings()
    {
        var source = new SuccessStubBrick("source-brick", "upstream-value");
        var sink = new PassthroughStubBrick("sink-brick");
        var bricks = new Mock<IBrickRegistry>();
        bricks.Setup(b => b.GetBrick("source-brick")).Returns(source);
        bricks.Setup(b => b.GetBrick("sink-brick")).Returns(sink);

        var cluster = new Cluster
        {
            Id = "c-chain",
            Name = "Chain",
            Description = "connected cluster",
            Parameters = new[] { new ClusterParameter { Name = "seed", Default = "default-seed" } },
            Bricks = new[]
            {
                new ClusterBrick { LocalId = "b1", BrickId = "source-brick", StaticParameters = new Dictionary<string, object> { ["tag"] = "first" } },
                new ClusterBrick
                {
                    LocalId = "b2",
                    BrickId = "sink-brick",
                    ParameterMappings = new Dictionary<string, string> { ["seed"] = "seed" },
                },
            },
            Connections = new[]
            {
                new ClusterConnection { FromBrickId = "b1", FromOutput = "result", ToBrickId = "b2", ToInput = "upstream" },
            },
            Interface = new ClusterInterface
            {
                Outputs = new[] { new ClusterPort { Name = "out", Type = "string", InternalMapping = "b2.result" } },
            },
        };

        var clusterStore = new Mock<IClusterStore>();
        clusterStore.Setup(c => c.GetByIdAsync("c-chain", It.IsAny<CancellationToken>())).ReturnsAsync(cluster);

        var workflow = new WorkflowExecutor(
            Mock.Of<IAgentRegistry>(),
            bricks.Object,
            Mock.Of<IBehaviorRegistry>(),
            Mock.Of<IBehaviorExecutor>(),
            new SequentialLoopKernel(),
            Mock.Of<ITextFileSystem>(),
            NullLogger<WorkflowExecutor>.Instance,
            clusterStore: clusterStore.Object);

        var result = await workflow.ExecuteAsync(new WorkflowDefinition
        {
            Id = "wf-cluster-chain",
            Name = "Cluster chain",
            Nodes = new List<WorkflowNode>
            {
                new ClusterNode
                {
                    Id = "cluster",
                    ClusterId = "c-chain",
                    Outputs = new List<NodePort> { new NodePort { Id = "out", Name = "out", Direction = PortDirection.Output, DataType = "string" } },
                },
            },
        }, new WorkflowInput { Parameters = new Dictionary<string, object> { ["seed"] = "mapped-seed" } });

        result.NodeResults["cluster"].Outputs["out"].Should().Be("upstream-value");
    }

    [Fact]
    public async Task ExecuteAsync_validates_output_nodes_and_serializes_scalar_arrays()
    {
        var webhook = new Mock<IWorkflowWebhookClient>();
        var db = new Mock<IWorkflowDatabaseWriter>();
        var workflow = new WorkflowExecutor(
            Mock.Of<IAgentRegistry>(),
            Mock.Of<IBrickRegistry>(),
            Mock.Of<IBehaviorRegistry>(),
            Mock.Of<IBehaviorExecutor>(),
            new SequentialLoopKernel(),
            Mock.Of<ITextFileSystem>(),
            NullLogger<WorkflowExecutor>.Instance,
            webhookClient: webhook.Object,
            databaseWriter: db.Object);

        var actWebhook = () => workflow.ExecuteAsync(new WorkflowDefinition
        {
            Id = "wf-out-hook",
            Name = "Out hook",
            Nodes = new List<WorkflowNode>
            {
                new InputNode { Id = "in", Type = InputType.Content, Content = "x", Outputs = new List<NodePort> { new NodePort { Id = "out", Name = "data", Direction = PortDirection.Output, DataType = "any" } } },
                new OutputNode { Id = "out", Type = OutputType.Webhook, WebhookUrl = " ", Inputs = new List<NodePort> { new NodePort { Id = "in", Name = "data", Direction = PortDirection.Input, DataType = "any" } } },
            },
            Connections = new List<VisualWorkflowConnection> { new() { FromNodeId = "in", FromPortId = "out", ToNodeId = "out", ToPortId = "in" } },
        }, new WorkflowInput());
        await actWebhook.Should().ThrowAsync<InvalidOperationException>().WithMessage("*WebhookUrl*");

        var actDb = () => workflow.ExecuteAsync(new WorkflowDefinition
        {
            Id = "wf-out-db",
            Name = "Out db",
            Nodes = new List<WorkflowNode>
            {
                new InputNode { Id = "in", Type = InputType.Content, Content = "x", Outputs = new List<NodePort> { new NodePort { Id = "out", Name = "data", Direction = PortDirection.Output, DataType = "any" } } },
                new OutputNode { Id = "out", Type = OutputType.Database, DatabaseConnectionString = "conn", Inputs = new List<NodePort> { new NodePort { Id = "in", Name = "data", Direction = PortDirection.Input, DataType = "any" } } },
            },
            Connections = new List<VisualWorkflowConnection> { new() { FromNodeId = "in", FromPortId = "out", ToNodeId = "out", ToPortId = "in" } },
        }, new WorkflowInput());
        await actDb.Should().ThrowAsync<InvalidOperationException>().WithMessage("*TableName*");

        var fs = new Mock<ITextFileSystem>();
        fs.Setup(f => f.WriteAllTextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var outWorkflow = new WorkflowExecutor(
            Mock.Of<IAgentRegistry>(),
            Mock.Of<IBrickRegistry>(),
            Mock.Of<IBehaviorRegistry>(),
            Mock.Of<IBehaviorExecutor>(),
            new SequentialLoopKernel(),
            fs.Object,
            NullLogger<WorkflowExecutor>.Instance);

        await outWorkflow.ExecuteAsync(new WorkflowDefinition
        {
            Id = "wf-scalar-array",
            Name = "Scalar array",
            Nodes = new List<WorkflowNode>
            {
                new InputNode { Id = "input", Type = InputType.Content, Content = "[1,2,3]", Outputs = new List<NodePort> { new NodePort { Id = "out", Name = "data", Direction = PortDirection.Output, DataType = "any" } } },
                new OutputNode { Id = "html-out", Type = OutputType.File, Format = WorkflowOutputFormat.Html, FilePath = "/tmp/scalar-array.html", Inputs = new List<NodePort> { new NodePort { Id = "in", Name = "data", Direction = PortDirection.Input, DataType = "any" } } },
            },
            Connections = new List<VisualWorkflowConnection> { new() { FromNodeId = "input", FromPortId = "out", ToNodeId = "html-out", ToPortId = "in" } },
        }, new WorkflowInput());

        fs.Verify(f => f.WriteAllTextAsync("/tmp/scalar-array.html", It.Is<string>(s => s.Contains("<pre>") && s.Contains("1")), It.IsAny<CancellationToken>()), Times.Once);

        var emptyTransform = new WorkflowExecutor(
            Mock.Of<IAgentRegistry>(),
            Mock.Of<IBrickRegistry>(),
            Mock.Of<IBehaviorRegistry>(),
            Mock.Of<IBehaviorExecutor>(),
            new SequentialLoopKernel(),
            Mock.Of<ITextFileSystem>(),
            NullLogger<WorkflowExecutor>.Instance);

        var transformResult = await emptyTransform.ExecuteAsync(new WorkflowDefinition
        {
            Id = "wf-empty-transform",
            Name = "Empty transform",
            Nodes = new List<WorkflowNode>
            {
                new TransformNode
                {
                    Id = "transform",
                    Operation = TransformOperation.Filter,
                    Expression = "x == '1'",
                    Inputs = new List<NodePort> { new NodePort { Id = "in", Name = "data", Direction = PortDirection.Input, DataType = "any" } },
                    Outputs = new List<NodePort> { new NodePort { Id = "out", Name = "data", Direction = PortDirection.Output, DataType = "any" } },
                },
            },
        }, new WorkflowInput());
        transformResult.NodeResults["transform"].Outputs["data"].Should().BeAssignableTo<List<object>>();
    }

    [Fact]
    public async Task ExecuteAsync_serializes_csv_scalar_and_xml_with_sanitized_element_names()
    {
        var fs = new Mock<ITextFileSystem>();
        fs.Setup(f => f.WriteAllTextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var workflow = new WorkflowExecutor(
            Mock.Of<IAgentRegistry>(),
            Mock.Of<IBrickRegistry>(),
            Mock.Of<IBehaviorRegistry>(),
            Mock.Of<IBehaviorExecutor>(),
            new SequentialLoopKernel(),
            fs.Object,
            NullLogger<WorkflowExecutor>.Instance);

        await workflow.ExecuteAsync(new WorkflowDefinition
        {
            Id = "wf-csv-scalar",
            Name = "Csv scalar",
            Nodes = new List<WorkflowNode>
            {
                new InputNode { Id = "input", Type = InputType.Content, Content = "42", Outputs = new List<NodePort> { new NodePort { Id = "out", Name = "data", Direction = PortDirection.Output, DataType = "any" } } },
                new OutputNode { Id = "csv-out", Type = OutputType.File, Format = WorkflowOutputFormat.Csv, FilePath = "/tmp/scalar.csv", Inputs = new List<NodePort> { new NodePort { Id = "in", Name = "data", Direction = PortDirection.Input, DataType = "any" } } },
            },
            Connections = new List<VisualWorkflowConnection> { new() { FromNodeId = "input", FromPortId = "out", ToNodeId = "csv-out", ToPortId = "in" } },
        }, new WorkflowInput());

        fs.Verify(f => f.WriteAllTextAsync("/tmp/scalar.csv", "42", It.IsAny<CancellationToken>()), Times.Once);

        var brick = new InvalidXmlKeysStubBrick();
        var bricks = new Mock<IBrickRegistry>();
        bricks.Setup(b => b.GetBrick("invalid-xml-keys")).Returns(brick);

        var xmlWorkflow = new WorkflowExecutor(
            Mock.Of<IAgentRegistry>(),
            bricks.Object,
            Mock.Of<IBehaviorRegistry>(),
            Mock.Of<IBehaviorExecutor>(),
            new SequentialLoopKernel(),
            fs.Object,
            NullLogger<WorkflowExecutor>.Instance);

        await xmlWorkflow.ExecuteAsync(new WorkflowDefinition
        {
            Id = "wf-xml-sanitize",
            Name = "Xml sanitize",
            Nodes = new List<WorkflowNode>
            {
                new BrickNode { Id = "source", BrickId = "invalid-xml-keys", Outputs = new List<NodePort> { new NodePort { Id = "out", Name = "data", Direction = PortDirection.Output, DataType = "any" } } },
                new OutputNode { Id = "xml-out", Type = OutputType.File, Format = WorkflowOutputFormat.Xml, FilePath = "/tmp/sanitize.xml", Inputs = new List<NodePort> { new NodePort { Id = "in", Name = "data", Direction = PortDirection.Input, DataType = "any" } } },
            },
            Connections = new List<VisualWorkflowConnection> { new() { FromNodeId = "source", FromPortId = "out", ToNodeId = "xml-out", ToPortId = "in" } },
        }, new WorkflowInput());

        fs.Verify(
            f => f.WriteAllTextAsync("/tmp/sanitize.xml", It.Is<string>(s => s.Contains("<bad>") && s.Contains("item123key")), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_cluster_rejects_circular_brick_dependencies()
    {
        var brick = new SuccessStubBrick("loop-brick", "x");
        var bricks = new Mock<IBrickRegistry>();
        bricks.Setup(b => b.GetBrick("loop-brick")).Returns(brick);

        var cluster = new Cluster
        {
            Id = "c-loop",
            Name = "Loop",
            Description = "circular",
            Bricks = new[]
            {
                new ClusterBrick { LocalId = "b1", BrickId = "loop-brick" },
                new ClusterBrick { LocalId = "b2", BrickId = "loop-brick" },
            },
            Connections = new[]
            {
                new ClusterConnection { FromBrickId = "b1", FromOutput = "result", ToBrickId = "b2", ToInput = "in" },
                new ClusterConnection { FromBrickId = "b2", FromOutput = "result", ToBrickId = "b1", ToInput = "in" },
            },
            Interface = new ClusterInterface(),
        };

        var clusterStore = new Mock<IClusterStore>();
        clusterStore.Setup(c => c.GetByIdAsync("c-loop", It.IsAny<CancellationToken>())).ReturnsAsync(cluster);

        var workflow = new WorkflowExecutor(
            Mock.Of<IAgentRegistry>(),
            bricks.Object,
            Mock.Of<IBehaviorRegistry>(),
            Mock.Of<IBehaviorExecutor>(),
            new SequentialLoopKernel(),
            Mock.Of<ITextFileSystem>(),
            NullLogger<WorkflowExecutor>.Instance,
            clusterStore: clusterStore.Object);

        var act = () => workflow.ExecuteAsync(new WorkflowDefinition
        {
            Id = "wf-cluster-loop",
            Name = "Cluster loop",
            Nodes = new List<WorkflowNode> { new ClusterNode { Id = "cluster", ClusterId = "c-loop", Outputs = new List<NodePort>() } },
        }, new WorkflowInput());

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Circular dependency*");
    }

    [Fact]
    public async Task ExecuteAsync_falls_back_from_agentic_to_deterministic_brick_implementation()
    {
        var brick = new FallbackAgenticBrick();
        var bricks = new Mock<IBrickRegistry>();
        bricks.Setup(b => b.GetBrick("fallback-brick")).Returns(brick);

        var workflow = new WorkflowExecutor(
            Mock.Of<IAgentRegistry>(),
            bricks.Object,
            Mock.Of<IBehaviorRegistry>(),
            Mock.Of<IBehaviorExecutor>(),
            new SequentialLoopKernel(),
            Mock.Of<ITextFileSystem>(),
            NullLogger<WorkflowExecutor>.Instance);

        var result = await workflow.ExecuteAsync(new WorkflowDefinition
        {
            Id = "wf-brick-fallback",
            Name = "DomainBrick fallback",
            Nodes = new List<WorkflowNode>
            {
                new BrickNode
                {
                    Id = "brick",
                    BrickId = "fallback-brick",
                    Implementation = ImplementationType.Agentic,
                    Outputs = new List<NodePort> { new NodePort { Id = "out", Name = "result", Direction = PortDirection.Output, DataType = "any" } },
                },
            },
        }, new WorkflowInput());

        result.NodeResults["brick"].Outputs["result"].Should().Be("deterministic-ok");
    }

    [Fact]
    public async Task ExecuteAsync_emits_workflow_lifecycle_events()
    {
        var events = new List<WorkflowExecutionEvent>();
        var workflow = new WorkflowExecutor(
            Mock.Of<IAgentRegistry>(),
            Mock.Of<IBrickRegistry>(),
            Mock.Of<IBehaviorRegistry>(),
            Mock.Of<IBehaviorExecutor>(),
            new SequentialLoopKernel(),
            Mock.Of<ITextFileSystem>(),
            NullLogger<WorkflowExecutor>.Instance);

        using var sub = workflow.Events.Subscribe(events.Add);

        var definition = new WorkflowDefinition
        {
            Id = "wf-events",
            Name = "Events",
            Nodes = new List<WorkflowNode>
            {
                new InputNode
                {
                    Id = "input",
                    Type = InputType.Content,
                    Content = "hello",
                    Outputs = new List<NodePort> { new NodePort { Id = "out", Name = "data", Direction = PortDirection.Output, DataType = "string" } },
                },
            },
        };

        await workflow.ExecuteAsync(definition, new WorkflowInput());

        events.Should().Contain(e => e is WorkflowStartedEvent);
        events.Should().Contain(e => e is ExecutionPlanCreatedEvent);
        events.Should().Contain(e => e is NodeStartedEvent);
        events.Should().Contain(e => e is NodeCompletedEvent);
        events.Should().Contain(e => e is WorkflowCompletedEvent);
    }

    /// <summary>Fallback stub brick.</summary>
    private sealed class FallbackStubBrick : DomainBrick
    {
        public Exception? FirstFailure { get; init; }
        public bool FailAlternative { get; init; }
        public int Calls { get; private set; }
        public FallbackStubBrick()
        {
            Id = "fallback-brick";
            Name = "Fallback";
            Description = "test";
            Category = BrickCategory.Transform;
            Implementations = new BrickImplementations
            {
                Deterministic = new DeterministicImplementation { Id = "d", Name = "d", Description = "d", Executor = "x" },
                Agentic = new AgenticImplementation { Id = "a", Name = "a", Description = "a" },
            };
        }

        public override Task<BrickOutput> ExecuteAsync(
            BrickInput input,
            ImplementationType implementation,
            IExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            if (implementation == ImplementationType.Deterministic)
            {
                throw FirstFailure ?? new InvalidOperationException("deterministic failed");
            }

            if (FailAlternative) throw new InvalidOperationException("later ordinary failure");

            var output = new BrickOutput();
            output.Set("result", "agentic-ok");
            return Task.FromResult(output);
        }
    }

    /// <summary>Invalid xml keys stub brick.</summary>
    private sealed class InvalidXmlKeysStubBrick : DomainBrick
    {
        public InvalidXmlKeysStubBrick()
        {
            Id = "invalid-xml-keys";
            Name = "invalid-xml-keys";
            Description = "test";
            Category = BrickCategory.Transform;
            Implementations = new BrickImplementations
            {
                Deterministic = new DeterministicImplementation { Id = "d", Name = "d", Description = "d", Executor = "x" },
            };
        }

        public override Task<BrickOutput> ExecuteAsync(
            BrickInput input,
            ImplementationType implementation,
            IExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            var output = new BrickOutput();
            output.Set("data", new Dictionary<string, object>
            {
                ["@bad"] = "value",
                ["123key"] = "num",
            });
            return Task.FromResult(output);
        }
    }

    /// <summary>Fallback agentic brick.</summary>
    private sealed class FallbackAgenticBrick : DomainBrick
    {
        public FallbackAgenticBrick()
        {
            Id = "fallback-brick";
            Name = "fallback-brick";
            Description = "test";
            Category = BrickCategory.Transform;
            DefaultImplementation = ImplementationType.Agentic;
            FallbackChain = [ImplementationType.Deterministic];
            Implementations = new BrickImplementations
            {
                Deterministic = new DeterministicImplementation { Id = "d", Name = "d", Description = "d", Executor = "x" },
                Agentic = new AgenticImplementation { Id = "a", Name = "a", Description = "a", Executor = "LLMChainExecutor", LLMConfig = new LLMConfig { SystemPrompt = "test" } },
            };
        }

        public override Task<BrickOutput> ExecuteAsync(
            BrickInput input,
            ImplementationType implementation,
            IExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            if (implementation == ImplementationType.Agentic)
            {
                throw new InvalidOperationException("agentic unavailable");
            }

            var output = new BrickOutput();
            output.Set("result", "deterministic-ok");
            return Task.FromResult(output);
        }
    }

    /// <summary>Success stub brick.</summary>
    private sealed class SuccessStubBrick : DomainBrick
    {
        private readonly string _value;

        public SuccessStubBrick(string id, string value)
        {
            Id = id;
            Name = id;
            Description = "test";
            Category = BrickCategory.Transform;
            _value = value;
            Implementations = new BrickImplementations
            {
                Deterministic = new DeterministicImplementation { Id = "d", Name = "d", Description = "d", Executor = "x" },
            };
        }

        public override Task<BrickOutput> ExecuteAsync(
            BrickInput input,
            ImplementationType implementation,
            IExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            var output = new BrickOutput();
            output.Set("result", _value);
            return Task.FromResult(output);
        }
    }

    /// <summary>Passthrough stub brick.</summary>
    private sealed class PassthroughStubBrick : DomainBrick
    {
        public PassthroughStubBrick(string id)
        {
            Id = id;
            Name = id;
            Description = "test";
            Category = BrickCategory.Transform;
            Implementations = new BrickImplementations
            {
                Deterministic = new DeterministicImplementation { Id = "d", Name = "d", Description = "d", Executor = "x" },
            };
        }

        public override Task<BrickOutput> ExecuteAsync(
            BrickInput input,
            ImplementationType implementation,
            IExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            var value = input.ToDictionary().TryGetValue("upstream", out var v) ? v : "missing";
            var output = new BrickOutput();
            output.Set("result", value);
            return Task.FromResult(output);
        }
    }

    /// <summary>Score dict stub brick.</summary>
    private sealed class ScoreDictStubBrick : DomainBrick
    {
        public ScoreDictStubBrick()
        {
            Id = "score-dict-brick";
            Name = "ScoreDict";
            Description = "test";
            Category = BrickCategory.Transform;
            Implementations = new BrickImplementations
            {
                Deterministic = new DeterministicImplementation { Id = "d", Name = "d", Description = "d", Executor = "x" },
            };
        }

        public override Task<BrickOutput> ExecuteAsync(
            BrickInput input,
            ImplementationType implementation,
            IExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            var output = new BrickOutput();
            output.Set("data", new Dictionary<string, object> { ["score"] = "10" });
            return Task.FromResult(output);
        }
    }

    /// <summary>Empty list stub brick.</summary>
    private sealed class EmptyListStubBrick : DomainBrick
    {
        public EmptyListStubBrick()
        {
            Id = "empty-list-brick";
            Name = "EmptyList";
            Description = "test";
            Category = BrickCategory.Transform;
            Implementations = new BrickImplementations
            {
                Deterministic = new DeterministicImplementation { Id = "d", Name = "d", Description = "d", Executor = "x" },
            };
        }

        public override Task<BrickOutput> ExecuteAsync(
            BrickInput input,
            ImplementationType implementation,
            IExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            var output = new BrickOutput
            {
                Summary = null
            };
            output.Set("data", new List<Dictionary<string, object>>
            {
                Capacity = 0
            });
            return Task.FromResult(output);
        }
    }

    /// <summary>List stub brick.</summary>
    private sealed class ListStubBrick : DomainBrick
    {
        public ListStubBrick()
        {
            Id = "list-brick";
            Name = "List";
            Description = "test";
            Category = BrickCategory.Transform;
            Implementations = new BrickImplementations
            {
                Deterministic = new DeterministicImplementation { Id = "d", Name = "d", Description = "d", Executor = "x" },
            };
        }

        public override Task<BrickOutput> ExecuteAsync(
            BrickInput input,
            ImplementationType implementation,
            IExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            var rows = new List<Dictionary<string, object>>
            {
                new() { ["score"] = "10" }, new() { ["score"] = "3" },
            };
            var output = new BrickOutput();
            output.Set("data", rows);
            return Task.FromResult(output);
        }
    }

    private static async IAsyncEnumerable<ExecutionEvent> ToAsyncEnumerable(IEnumerable<ExecutionEvent> events)
    {
        foreach (ExecutionEvent evt in events)
        {
            yield return evt;
            await Task.Yield();
        }
    }
}
