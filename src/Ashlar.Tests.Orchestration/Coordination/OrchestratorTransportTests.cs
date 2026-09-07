using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Ashlar.Abstractions.Execution;
using Ashlar.Abstractions.Transport;
using Ashlar.Core.Application.Common.Ports;
using Ashlar.Core.Application.Common.Services;
using Ashlar.Core.Application.Resilience.Ports;
using Ashlar.Orchestration.Agents;
using Ashlar.Orchestration.Architect;
using Ashlar.Orchestration.Architect.Models;
using Ashlar.Orchestration.Communication;
using Ashlar.Orchestration.Coordination;
using Ashlar.Orchestration.Coordination.Conflicts;
using Ashlar.Orchestration.Metrics;
using Ashlar.Orchestration.Transport;
using Xunit;

namespace Ashlar.Tests.Orchestration.Coordination;

/// <summary>Tests for orchestrator transport.</summary>
public sealed class OrchestratorTransportTests
{
    [Fact]
    public async Task OrchestrateAsync_WhenTransportReturnsTimeout_RetriesAndSucceeds()
    {
        var transportMock = new Mock<IAgentTransport>(MockBehavior.Strict);
        transportMock
            .SetupSequence(t => t.SendAsync(It.IsAny<AgentInvocationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResult(
                Success: false,
                ErrorMessage: "Timed out",
                Metadata: new Dictionary<string, string> { ["errorCode"] = "TIMEOUT" }))
            .ReturnsAsync(new AgentResult(
                Success: true,
                Output: new { ok = true }));

        var sut = CreateOrchestrator(
            CreateArchitectMock(CreateSingleAgentDecomposition()).Object,
            transportMock.Object);

        var result = await sut.OrchestrateAsync("timeout retry");

        result.Success.Should().BeTrue();
        result.Escalations.Should().BeEmpty();
        transportMock.Verify(
            t => t.SendAsync(It.IsAny<AgentInvocationRequest>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task OrchestrateAsync_WhenTransportReturnsAgentNotFound_EscalatesWithoutRetry()
    {
        var transportMock = new Mock<IAgentTransport>(MockBehavior.Strict);
        transportMock
            .Setup(t => t.SendAsync(It.IsAny<AgentInvocationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResult(
                Success: false,
                ErrorMessage: "Agent not found",
                Metadata: new Dictionary<string, string> { ["errorCode"] = "AGENT_NOT_FOUND" }));

        var sut = CreateOrchestrator(
            CreateArchitectMock(CreateSingleAgentDecomposition()).Object,
            transportMock.Object);

        var result = await sut.OrchestrateAsync("agent not found");

        result.Success.Should().BeFalse();
        result.Escalations.Should().ContainSingle(e => e.IssueType == "AgentExecution");
        transportMock.Verify(
            t => t.SendAsync(It.IsAny<AgentInvocationRequest>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task OrchestrateAsync_WhenTransportReturnsUnknownError_RecordsCircuitBreakerFailures()
    {
        var transportMock = new Mock<IAgentTransport>(MockBehavior.Strict);
        transportMock
            .Setup(t => t.SendAsync(It.IsAny<AgentInvocationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResult(
                Success: false,
                ErrorMessage: "Provider failure",
                Metadata: new Dictionary<string, string> { ["errorCode"] = "PROVIDER_FAILURE" }));

        var sut = CreateOrchestrator(
            CreateArchitectMock(CreateSingleAgentDecomposition()).Object,
            transportMock.Object);

        await sut.OrchestrateAsync("unknown error 1");
        await sut.OrchestrateAsync("unknown error 2");
        await sut.OrchestrateAsync("unknown error 3");

        transportMock.Verify(
            t => t.SendAsync(It.IsAny<AgentInvocationRequest>(), It.IsAny<CancellationToken>()),
            Times.Exactly(3));
    }

    [Fact]
    public async Task OrchestrateAsync_PropagatesCorrelationIdToTransportRequest()
    {
        AgentInvocationRequest? capturedRequest = null;
        var transportMock = new Mock<IAgentTransport>(MockBehavior.Strict);
        transportMock
            .Setup(t => t.SendAsync(It.IsAny<AgentInvocationRequest>(), It.IsAny<CancellationToken>()))
            .Callback<AgentInvocationRequest, CancellationToken>((request, _) => capturedRequest = request)
            .ReturnsAsync(new AgentResult(
                Success: true,
                Output: new { ok = true }));

        var sut = CreateOrchestrator(
            CreateArchitectMock(CreateSingleAgentDecomposition()).Object,
            transportMock.Object);

        var result = await sut.OrchestrateAsync("correlation");

        result.CorrelationId.Should().NotBeNullOrWhiteSpace();
        capturedRequest.Should().NotBeNull();
        capturedRequest!.CorrelationId.Should().Be(result.CorrelationId);
    }

    [Fact]
    public async Task OrchestrateAsync_PropagatesClusterChainGoalsAndModelMetadata()
    {
        AgentInvocationRequest? capturedRequest = null;
        var transportMock = new Mock<IAgentTransport>(MockBehavior.Strict);
        transportMock
            .Setup(t => t.SendAsync(It.IsAny<AgentInvocationRequest>(), It.IsAny<CancellationToken>()))
            .Callback<AgentInvocationRequest, CancellationToken>((request, _) => capturedRequest = request)
            .ReturnsAsync(new AgentResult(
                Success: true,
                Output: new { ok = true }));

        var decomposition = new DecompositionResult
        {
            OriginalRequest = "request",
            Agents =
            [
                new AgentSpawnSpec
                {
                    AgentId = "agent-1",
                    Domain = "General",
                    Goal = "Execute mission",
                    ClusterId = "alpha-cluster",
                    ReportsToAgentId = "commander-1",
                    CommandChain = new[] { "commander-1", "lead-2" },
                    Goals = new[] { "primary objective", "secondary objective" },
                    OllamaModel = "qwen2.5:7b"
                }
            ]
        };

        var sut = CreateOrchestrator(CreateArchitectMock(decomposition).Object, transportMock.Object);
        var result = await sut.OrchestrateAsync("metadata propagation");

        result.Success.Should().BeTrue();
        capturedRequest.Should().NotBeNull();
        capturedRequest!.Metadata.Should().NotBeNull();
        capturedRequest.Metadata!["clusterId"].Should().Be("alpha-cluster");
        capturedRequest.Metadata["reportsToAgentId"].Should().Be("commander-1");
        capturedRequest.Metadata["commandChain"].Should().Be("commander-1|lead-2");
        capturedRequest.Metadata["goals"].Should().Be("primary objective|secondary objective");
        capturedRequest.Metadata["ollamaModel"].Should().Be("qwen2.5:7b");
        capturedRequest.Metadata[AgentExecutionIsolation.MetadataKey].Should().Be("InProcess");
    }

    [Fact]
    public async Task OrchestrateAsync_PropagatesExecutionIsolationMetadata()
    {
        AgentInvocationRequest? capturedRequest = null;
        var transportMock = new Mock<IAgentTransport>(MockBehavior.Strict);
        transportMock
            .Setup(t => t.SendAsync(It.IsAny<AgentInvocationRequest>(), It.IsAny<CancellationToken>()))
            .Callback<AgentInvocationRequest, CancellationToken>((request, _) => capturedRequest = request)
            .ReturnsAsync(new AgentResult(
                Success: true,
                Output: new { ok = true }));

        var decomposition = new DecompositionResult
        {
            OriginalRequest = "request",
            Agents =
            [
                new AgentSpawnSpec
                {
                    AgentId = "agent-1",
                    Domain = "General",
                    Goal = "Execute mission",
                    ExecutionIsolation = AgentExecutionIsolationLevel.ContainerPerAgent
                }
            ]
        };

        var sut = CreateOrchestrator(CreateArchitectMock(decomposition).Object, transportMock.Object);
        var result = await sut.OrchestrateAsync("isolation metadata");

        result.Success.Should().BeTrue();
        capturedRequest.Should().NotBeNull();
        capturedRequest!.Metadata.Should().NotBeNull();
        capturedRequest.Metadata![AgentExecutionIsolation.MetadataKey].Should().Be("ContainerPerAgent");
    }

    [Fact]
    public async Task OrchestrateAsync_PropagatesOutOfProcessIsolationMetadata()
    {
        AgentInvocationRequest? capturedRequest = null;
        var transportMock = new Mock<IAgentTransport>(MockBehavior.Strict);
        transportMock
            .Setup(t => t.SendAsync(It.IsAny<AgentInvocationRequest>(), It.IsAny<CancellationToken>()))
            .Callback<AgentInvocationRequest, CancellationToken>((request, _) => capturedRequest = request)
            .ReturnsAsync(new AgentResult(
                Success: true,
                Output: new { ok = true }));

        var decomposition = new DecompositionResult
        {
            OriginalRequest = "request",
            Agents =
            [
                new AgentSpawnSpec
                {
                    AgentId = "agent-1",
                    Domain = "General",
                    Goal = "Execute mission",
                    ExecutionIsolation = AgentExecutionIsolationLevel.OutOfProcess
                }
            ]
        };

        var sut = CreateOrchestrator(CreateArchitectMock(decomposition).Object, transportMock.Object);
        var result = await sut.OrchestrateAsync("out-of-process isolation");

        result.Success.Should().BeTrue();
        capturedRequest.Should().NotBeNull();
        capturedRequest!.Metadata![AgentExecutionIsolation.MetadataKey].Should().Be("OutOfProcess");
    }

    [Fact]
    public async Task OrchestrateAsync_PropagatesContainerPooledIsolationMetadata()
    {
        AgentInvocationRequest? capturedRequest = null;
        var transportMock = new Mock<IAgentTransport>(MockBehavior.Strict);
        transportMock
            .Setup(t => t.SendAsync(It.IsAny<AgentInvocationRequest>(), It.IsAny<CancellationToken>()))
            .Callback<AgentInvocationRequest, CancellationToken>((request, _) => capturedRequest = request)
            .ReturnsAsync(new AgentResult(
                Success: true,
                Output: new { ok = true }));

        var decomposition = new DecompositionResult
        {
            OriginalRequest = "request",
            Agents =
            [
                new AgentSpawnSpec
                {
                    AgentId = "agent-1",
                    Domain = "General",
                    Goal = "Execute mission",
                    ExecutionIsolation = AgentExecutionIsolationLevel.ContainerPooled
                }
            ]
        };

        var sut = CreateOrchestrator(CreateArchitectMock(decomposition).Object, transportMock.Object);
        var result = await sut.OrchestrateAsync("container pooled isolation");

        result.Success.Should().BeTrue();
        capturedRequest.Should().NotBeNull();
        capturedRequest!.Metadata![AgentExecutionIsolation.MetadataKey].Should().Be("ContainerPooled");
    }

    private static Mock<IArchitectAgent> CreateArchitectMock(DecompositionResult decomposition)
    {
        var architectMock = new Mock<IArchitectAgent>(MockBehavior.Strict);
        architectMock
            .Setup(a => a.DecomposeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(decomposition);
        architectMock
            .Setup(a => a.DecomposeAsync(It.IsAny<string>(), It.IsAny<DecompositionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(decomposition);
        return architectMock;
    }

    private static DecompositionResult CreateSingleAgentDecomposition()
    {
        return new DecompositionResult
        {
            OriginalRequest = "request",
            Agents =
            [
                new AgentSpawnSpec
                {
                    AgentId = "agent-1",
                    Domain = "General",
                    Goal = "Test goal"
                }
            ]
        };
    }

    [Fact]
    public async Task OrchestrateAsync_InvocationHooks_BeforeSendRunsInOrder_AndEnrichesTransportRequest()
    {
        AgentInvocationRequest? capturedRequest = null;
        var transportMock = new Mock<IAgentTransport>(MockBehavior.Strict);
        transportMock
            .Setup(t => t.SendAsync(It.IsAny<AgentInvocationRequest>(), It.IsAny<CancellationToken>()))
            .Callback<AgentInvocationRequest, CancellationToken>((request, _) => capturedRequest = request)
            .ReturnsAsync(new AgentResult(Success: true, Output: new { v = 1 }));

        var hook1 = new MetadataAppendHook("hookOrder", "1");
        var hook2 = new MetadataAppendHook("hookOrder", "2", append: false);

        var sut = CreateOrchestrator(
            CreateArchitectMock(CreateSingleAgentDecomposition()).Object,
            transportMock.Object,
            new IAgentTransportInvocationHook[] { hook1, hook2 });

        var result = await sut.OrchestrateAsync("hooks");

        result.Success.Should().BeTrue();
        capturedRequest.Should().NotBeNull();
        capturedRequest!.Metadata!["hookOrder"].Should().Be("2");
    }

    [Fact]
    public async Task OrchestrateAsync_InvocationHooks_AfterSuccessTransformsOutput()
    {
        var transportMock = new Mock<IAgentTransport>(MockBehavior.Strict);
        transportMock
            .Setup(t => t.SendAsync(It.IsAny<AgentInvocationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResult(Success: true, Output: new { n = 3 }));

        var sut = CreateOrchestrator(
            CreateArchitectMock(CreateSingleAgentDecomposition()).Object,
            transportMock.Object,
            new IAgentTransportInvocationHook[] { new OutputWrapHook() });

        var result = await sut.OrchestrateAsync("wrap");

        result.Success.Should().BeTrue();
        result.IntegratedOutput.Should().NotBeNull();
        result.IntegratedOutput!.AgentOutputs["agent-1"].GetType().GetProperty("wrapped").Should().NotBeNull();
    }

    [Fact]
    public async Task OrchestrateAsync_InvocationHooks_AfterSuccessNotCalled_WhenTransportFails()
    {
        var transportMock = new Mock<IAgentTransport>(MockBehavior.Strict);
        transportMock
            .Setup(t => t.SendAsync(It.IsAny<AgentInvocationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResult(
                Success: false,
                ErrorMessage: "fail",
                Metadata: new Dictionary<string, string> { ["errorCode"] = "AGENT_NOT_FOUND" }));

        var counter = new CountingInvocationHook();
        var sut = CreateOrchestrator(
            CreateArchitectMock(CreateSingleAgentDecomposition()).Object,
            transportMock.Object,
            new IAgentTransportInvocationHook[] { counter });

        await sut.OrchestrateAsync("fail");

        counter.BeforeSendCount.Should().Be(1);
        counter.AfterSuccessCount.Should().Be(0);
    }

    private static Orchestrator CreateOrchestrator(
        IArchitectAgent architect,
        IAgentTransport transport,
        IReadOnlyList<IAgentTransportInvocationHook>? invocationHooks = null)
    {
        var provider = new ServiceCollection()
            .AddLogging()
            .BuildServiceProvider();

        var agentFactory = new AgentFactory(
            provider.GetRequiredService<ILogger<AgentFactory>>(),
            provider);
        var lifecycleManager = new LifecycleManager(
            provider.GetRequiredService<ILogger<LifecycleManager>>(),
            new HealthMonitor(provider.GetRequiredService<ILogger<HealthMonitor>>()));
        var dependencyResolver = new DependencyResolver(
            provider.GetRequiredService<ILogger<DependencyResolver>>());
        var conflictDetector = new ConflictDetector(
            provider.GetRequiredService<ILogger<ConflictDetector>>());
        var resourceAllocator = new ResourceAllocator(
            provider.GetRequiredService<ILogger<ResourceAllocator>>());
        var progressTracker = new ProgressTracker(
            provider.GetRequiredService<ILogger<ProgressTracker>>());
        var escalationManager = new EscalationManager(
            provider.GetRequiredService<ILogger<EscalationManager>>());
        var outputIntegrator = new OutputIntegrator(
            provider.GetRequiredService<ILogger<OutputIntegrator>>());
        var agentBus = new AgentBus(
            provider.GetRequiredService<ILogger<AgentBus>>());
        var loops = new SequentialLoopKernel();
        var metrics = new OrchestrationMetrics(
            provider.GetRequiredService<ILogger<OrchestrationMetrics>>());
        var resilientExecutor = new TestResilientExecutor();
        var circuitBreaker = new Ashlar.Orchestration.Resilience.CircuitBreaker(
            name: "orchestration-agent-transport",
            failureThreshold: 3,
            timeout: TimeSpan.FromSeconds(30),
            logger: provider.GetService<ILogger<Ashlar.Orchestration.Resilience.CircuitBreaker>>());

        return new Orchestrator(
            architect,
            agentFactory,
            lifecycleManager,
            dependencyResolver,
            conflictDetector,
            resourceAllocator,
            progressTracker,
            escalationManager,
            outputIntegrator,
            agentBus,
            transport,
            loops,
            provider.GetRequiredService<ILogger<Orchestrator>>(),
            metrics: metrics,
            invocationHooks: invocationHooks,
            resilientExecutor: resilientExecutor,
            circuitBreaker: circuitBreaker);
    }

    /// <summary>Metadata append hook.</summary>
    private sealed class MetadataAppendHook : IAgentTransportInvocationHook
    {
        private readonly string _key;
        private readonly string _value;
        private readonly bool _append;

        public MetadataAppendHook(string key, string value, bool append = true)
        {
            _key = key;
            _value = value;
            _append = append;
        }

        public Task<AgentInvocationRequest> BeforeSendAsync(
            AgentInvocationContext context,
            CancellationToken cancellationToken = default)
        {
            var md = context.Request.Metadata != null
                ? new Dictionary<string, string>(context.Request.Metadata, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (_append && md.TryGetValue(_key, out var existing))
            {
                md[_key] = existing + "," + _value;
            }
            else
            {
                md[_key] = _value;
            }

            return Task.FromResult(context.Request with { Metadata = md });
        }
    }

    /// <summary>Output wrap hook.</summary>
    private sealed class OutputWrapHook : IAgentTransportInvocationHook
    {
        public Task<object> AfterSuccessAsync(
            AgentInvocationContext context,
            object output,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<object>(new { wrapped = output });
    }

    /// <summary>Counting invocation hook.</summary>
    private sealed class CountingInvocationHook : IAgentTransportInvocationHook
    {
        public int BeforeSendCount;
        public int AfterSuccessCount;

        public Task<AgentInvocationRequest> BeforeSendAsync(
            AgentInvocationContext context,
            CancellationToken cancellationToken = default)
        {
            BeforeSendCount++;
            return Task.FromResult(context.Request);
        }

        public Task<object> AfterSuccessAsync(
            AgentInvocationContext context,
            object output,
            CancellationToken cancellationToken = default)
        {
            AfterSuccessCount++;
            return Task.FromResult(output);
        }
    }

    /// <summary>Test stub for IResilientExecutor.</summary>
    private sealed class TestResilientExecutor : IResilientExecutor
    {
        public Task<T> ExecuteAsync<T>(
            Func<CancellationToken, Task<T>> operation,
            RetryPolicy policy,
            CancellationToken cancellationToken = default)
        {
            return operation(cancellationToken);
        }
    }
}
