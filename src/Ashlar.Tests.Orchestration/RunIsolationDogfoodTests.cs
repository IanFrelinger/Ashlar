using Ashlar.Abstractions.Agents;
using Ashlar.Abstractions.Transport;
using Ashlar.Core.Application.Common.Services;
using Ashlar.Core.Application.Resilience.Ports;
using Ashlar.Orchestration.Agents;
using Ashlar.Orchestration.Architect;
using Ashlar.Orchestration.Architect.Models;
using Ashlar.Orchestration.Communication;
using Ashlar.Orchestration.Communication.Models;
using Ashlar.Orchestration.Coordination;
using Ashlar.Orchestration.Coordination.Conflicts;
using Ashlar.Orchestration.Transport;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Ashlar.Tests.Orchestration;

/// <summary>Deterministic probes for isolation between runs that reuse decomposition IDs.</summary>
public sealed class RunIsolationDogfoodTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static HealthMonitor Monitor() => new(NullLogger<HealthMonitor>.Instance, TimeSpan.FromHours(1));
    private static AgentContainer Container(string marker) => new(
        new MarkerAgent(marker), NullLogger<AgentContainer>.Instance);

    [Fact(Timeout = 30000)]
    public async Task Nested_run_shutdown_preserves_outer_live_agent_health()
    {
        var monitor = Monitor();
        var manager = new LifecycleManager(NullLogger<LifecycleManager>.Instance, monitor);
        try
        {
            using var outerScope = manager.BeginRunScope();
            var outer = Container("outer");
            await manager.RegisterAgentAsync(outer);
            try
            {
                using (manager.BeginRunScope())
                {
                    await manager.RegisterAgentAsync(Container("inner"));
                    await manager.ShutdownAllAsync();
                }
                manager.GetAgent("shared").Should().BeSameAs(outer);
                monitor.GetAllHealthStatuses().Should().ContainSingle(
                    status => ReferenceEquals(status.Container, outer),
                    "finishing a nested run must retain monitoring for the live outer container");
            }
            finally { await manager.ShutdownAllAsync(); }
        }
        finally { monitor.Dispose(); }
    }

    [Fact(Timeout = 30000)]
    public async Task Concurrent_run_scopes_flow_through_transport_and_restore_root()
    {
        var monitor = Monitor();
        var manager = new LifecycleManager(NullLogger<LifecycleManager>.Instance, monitor);
        var transport = new InProcessAgentTransport(manager, NullLogger<InProcessAgentTransport>.Instance);
        var aRegistered = Signal();
        var bRegistered = Signal();
        var aFinished = Signal();
        using var cancellation = new CancellationTokenSource(Deadline);
        var root = Container("root");
        await manager.RegisterAgentAsync(root, cancellation.Token);
        async Task LaneA()
        {
            using var scope = manager.BeginRunScope();
            try
            {
                await manager.RegisterAgentAsync(Container("A"), cancellation.Token);
                aRegistered.TrySetResult(true);
                await bRegistered.Task.WaitAsync(cancellation.Token);
                // Task.Run and a real asynchronous suspension must retain this lane's ambient map.
                var result = await Task.Run(async () =>
                {
                    await Task.Yield();
                    return await transport.SendAsync(new AgentInvocationRequest("shared", "A"), cancellation.Token);
                }, cancellation.Token);
                result.Success.Should().BeTrue();
                result.Output.Should().Be("A");
            }
            finally
            {
                await manager.ShutdownAllAsync();
                aFinished.TrySetResult(true);
            }
        }
        async Task LaneB()
        {
            await aRegistered.Task.WaitAsync(cancellation.Token);
            using var scope = manager.BeginRunScope();
            try
            {
                await manager.RegisterAgentAsync(Container("B"), cancellation.Token);
                bRegistered.TrySetResult(true);
                await aFinished.Task.WaitAsync(cancellation.Token);
                var result = await transport.SendAsync(new AgentInvocationRequest("shared", "B"), cancellation.Token);
                result.Success.Should().BeTrue();
                result.Output.Should().Be("B", "another run's cleanup must not shut down this container");
            }
            finally { await manager.ShutdownAllAsync(); }
        }
        try
        {
            await Task.WhenAll(LaneA(), LaneB()).WaitAsync(Deadline);
            manager.GetAgent("shared").Should().BeSameAs(root);
        }
        finally
        {
            cancellation.Cancel();
            await manager.ShutdownAllAsync();
            monitor.Dispose();
        }
    }

    [Fact(Timeout = 30000)]
    public async Task Concurrent_orchestrators_do_not_read_other_runs_dependency_output()
    {
        using var provider = new ServiceCollection().AddLogging().BuildServiceProvider();
        var monitor = Monitor();
        var manager = new LifecycleManager(NullLogger<LifecycleManager>.Instance, monitor);
        var resolver = new DependencyResolver(NullLogger<DependencyResolver>.Instance);
        var aAtGate = Signal();
        var releaseA = Signal();
        using var cancellation = new CancellationTokenSource(Deadline);
        object? aInput = null;
        object? bInput = null;
        IAgentTransport Transport(string run)
        {
            var mock = new Mock<IAgentTransport>(MockBehavior.Strict);
            mock.Setup(t => t.SendAsync(It.IsAny<AgentInvocationRequest>(), It.IsAny<CancellationToken>()))
                .Returns(async (AgentInvocationRequest request, CancellationToken token) =>
                {
                    if (run == "A" && request.AgentName == "gate")
                    {
                        // Producer A is already recorded; prevent A from reading consumer dependencies.
                        aAtGate.TrySetResult(true);
                        await releaseA.Task.WaitAsync(token);
                    }
                    if (request.AgentName == "consumer")
                    {
                        request.DependencyOutputs!.TryGetValue("producer", out var input);
                        if (run == "A") aInput = input;
                        else bInput = input;
                    }
                    return new AgentResult(true, Output: $"{run}:{request.AgentName}");
                });
            return mock.Object;
        }
        var a = CreateOrchestrator(provider, manager, resolver, Transport("A"), NoopBus());
        var b = CreateOrchestrator(provider, manager, resolver, Transport("B"), NoopBus());
        var aTask = a.OrchestrateAsync("A", cancellation.Token);
        try
        {
            await aAtGate.Task.WaitAsync(cancellation.Token);
            var bResult = await b.OrchestrateAsync("B", cancellation.Token).WaitAsync(Deadline);
            releaseA.TrySetResult(true);
            var aResult = await aTask.WaitAsync(Deadline);
            aResult.Success.Should().BeTrue();
            bResult.Success.Should().BeTrue();
            bInput.Should().Be("B:producer");
            aInput.Should().Be("A:producer", "run B must not replace run A's recorded dependency output");
        }
        finally
        {
            releaseA.TrySetResult(true);
            cancellation.Cancel();
            try { await aTask.WaitAsync(Deadline); }
            catch (OperationCanceledException) { }
            finally { monitor.Dispose(); }
        }
    }

    [Fact(Timeout = 30000)]
    public async Task Concurrent_orchestrators_preserve_global_capacity_and_release_owned_allocations()
    {
        using var provider = new ServiceCollection().AddLogging().BuildServiceProvider();
        var monitor = Monitor();
        var manager = new LifecycleManager(NullLogger<LifecycleManager>.Instance, monitor);
        var resolver = new DependencyResolver(NullLogger<DependencyResolver>.Instance);
        var allocator = new ResourceAllocator(NullLogger<ResourceAllocator>.Instance,
            new ResourceBudget { MaxComputeSeconds = 50 });
        var aAtGate = Signal();
        var bAtGate = Signal();
        var releaseA = Signal();
        var releaseB = Signal();
        using var cancellation = new CancellationTokenSource(Deadline);
        IAgentTransport Transport(string run)
        {
            var mock = new Mock<IAgentTransport>(MockBehavior.Strict);
            mock.Setup(t => t.SendAsync(It.IsAny<AgentInvocationRequest>(), It.IsAny<CancellationToken>()))
                .Returns(async (AgentInvocationRequest request, CancellationToken token) =>
                {
                    if (run == "A" && request.AgentName == "gate")
                    {
                        aAtGate.TrySetResult(true); // A's producer already released 10; A owns 20.
                        await releaseA.Task.WaitAsync(token);
                    }
                    if (run == "B" && request.AgentName == "producer")
                    {
                        bAtGate.TrySetResult(true); // All B's agents registered; B still owns 30.
                        await releaseB.Task.WaitAsync(token);
                    }
                    return new AgentResult(true, Output: $"{run}:{request.AgentName}");
                });
            return mock.Object;
        }
        var a = CreateOrchestrator(provider, manager, resolver, Transport("A"), NoopBus(), allocator: allocator);
        var b = CreateOrchestrator(provider, manager, resolver, Transport("B"), NoopBus(), allocator: allocator);
        var aTask = a.OrchestrateAsync("A", cancellation.Token);
        Task<OrchestrationResult>? bTask = null;
        try
        {
            await aAtGate.Task.WaitAsync(cancellation.Token);
            allocator.GetUsageReport().TotalAllocatedComputeSeconds.Should().Be(20);
            bTask = b.OrchestrateAsync("B", cancellation.Token);
            await bAtGate.Task.WaitAsync(cancellation.Token);
            allocator.GetUsageReport().TotalAllocatedComputeSeconds.Should().Be(50);
            var outsider = new AgentContainer(new GenericAgent(new AgentSpawnSpec
            {
                AgentId = "outsider", Domain = "General", Goal = "allocate", Priority = int.MinValue,
                ResourceRequirements = new ResourceRequirements { EstimatedComputeSeconds = 10 }
            }, NullLogger<GenericAgent>.Instance), NullLogger<AgentContainer>.Instance);
            allocator.TryAllocate(outsider, out _).Should().BeFalse(
                "both runs must compete against one global budget");
            releaseB.TrySetResult(true);
            (await bTask.WaitAsync(Deadline)).Success.Should().BeTrue();
            allocator.GetUsageReport().TotalAllocatedComputeSeconds.Should().Be(20,
                "finishing B must preserve A’s outstanding reservations");
            releaseA.TrySetResult(true);
            (await aTask.WaitAsync(Deadline)).Success.Should().BeTrue();
            allocator.GetUsageReport().TotalAllocatedComputeSeconds.Should().Be(0,
                "each run must release its own same-ID allocations without stranded charges");
            allocator.GetAllocations().Should().BeEmpty();
        }
        finally
        {
            releaseA.TrySetResult(true);
            releaseB.TrySetResult(true);
            cancellation.Cancel();
            try
            {
                await Task.WhenAll(bTask is null ? new Task[] { aTask } : new Task[] { aTask, bTask })
                    .WaitAsync(Deadline);
            }
            catch (OperationCanceledException) { }
            finally { monitor.Dispose(); }
        }
    }

    [Fact(Timeout = 30000)]
    public async Task Successful_result_reports_agents_before_cleanup_removes_them()
    {
        using var provider = new ServiceCollection().AddLogging().BuildServiceProvider();
        var monitor = Monitor();
        var manager = new LifecycleManager(NullLogger<LifecycleManager>.Instance, monitor);
        var resolver = new DependencyResolver(NullLogger<DependencyResolver>.Instance);
        var transport = new Mock<IAgentTransport>(MockBehavior.Strict);
        transport.Setup(t => t.SendAsync(It.IsAny<AgentInvocationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResult(true, Output: "output"));
        try
        {
            var sut = CreateOrchestrator(provider, manager, resolver, transport.Object, NoopBus());
            var result = await sut.OrchestrateAsync("three agents").WaitAsync(Deadline);
            result.Success.Should().BeTrue();
            result.ProgressSummary.Should().NotBeNull();
            result.ProgressSummary!.TotalAgents.Should().Be(3,
                "the result describes executed agents even though cleanup removed them from the active registry");
            manager.GetActiveAgents().Should().BeEmpty();
        }
        finally { monitor.Dispose(); }
    }

    [Fact(Timeout = 30000)]
    public async Task Repeated_orchestrations_leave_no_owned_subscriptions_on_shared_bus()
    {
        using var provider = new ServiceCollection().AddLogging().BuildServiceProvider();
        var monitor = Monitor();
        var manager = new LifecycleManager(NullLogger<LifecycleManager>.Instance, monitor);
        var resolver = new DependencyResolver(NullLogger<DependencyResolver>.Instance);
        var tokens = new List<TrackingSubscription>();
        var bus = new Mock<IAgentBus>(MockBehavior.Strict);
        bus.Setup(b => b.SubscribeAsync(It.IsAny<string?>(),
                It.IsAny<Func<AgentMessage, CancellationToken, Task>>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                var token = new TrackingSubscription();
                tokens.Add(token);
                return Task.FromResult<IDisposable>(token);
            });
        bus.Setup(b => b.PublishAsync(It.IsAny<AgentMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var transport = new Mock<IAgentTransport>(MockBehavior.Strict);
        transport.Setup(t => t.SendAsync(It.IsAny<AgentInvocationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResult(true, Output: "output"));
        try
        {
            for (var run = 0; run < 2; run++)
            {
                var sut = CreateOrchestrator(provider, manager, resolver, transport.Object, bus.Object);
                var result = await sut.OrchestrateAsync($"run {run}").WaitAsync(Deadline);
                result.Success.Should().BeTrue();
            }
            // Zero subscriptions also satisfies this: dependency output is recorded directly.
            tokens.Should().OnlyContain(token => token.Disposed,
                "the shared bus must not retain completed runs' handlers and captured containers");
            bus.Verify(b => b.PublishAsync(It.IsAny<AgentMessage>(), It.IsAny<CancellationToken>()), Times.Exactly(6));
        }
        finally { monitor.Dispose(); }
    }

    [Fact(Timeout = 30000)]
    public async Task Successful_run_excludes_prior_escalations_but_preserves_operator_queue()
    {
        using var provider = new ServiceCollection().AddLogging().BuildServiceProvider();
        var monitor = Monitor();
        var manager = new LifecycleManager(NullLogger<LifecycleManager>.Instance, monitor);
        var resolver = new DependencyResolver(NullLogger<DependencyResolver>.Instance);
        var escalations = new EscalationManager(NullLogger<EscalationManager>.Instance);
        var fail = true;
        var transport = new Mock<IAgentTransport>(MockBehavior.Strict);
        transport.Setup(t => t.SendAsync(It.IsAny<AgentInvocationRequest>(), It.IsAny<CancellationToken>()))
            .Returns(() => Task.FromResult(fail
                ? new AgentResult(false, ErrorMessage: "run A failure", ErrorCode: "AGENT_NOT_FOUND")
                : new AgentResult(true, Output: "run B output")));
        try
        {
            var first = CreateOrchestrator(provider, manager, resolver, transport.Object,
                escalations: escalations);
            var firstResult = await first.OrchestrateAsync("A").WaitAsync(Deadline);
            firstResult.Escalations.Should().NotBeEmpty();
            var priorIds = firstResult.Escalations.Select(e => e.Id).ToArray();
            fail = false;
            var second = CreateOrchestrator(provider, manager, resolver, transport.Object,
                escalations: escalations);
            var secondResult = await second.OrchestrateAsync("B").WaitAsync(Deadline);
            secondResult.Success.Should().BeTrue();
            secondResult.Escalations.Should().BeEmpty("this request did not produce an escalation");
            escalations.GetAllEscalations().Select(e => e.Id).Should().Contain(priorIds,
                "operators must still be able to inspect and resolve earlier requests' issues");
        }
        finally { monitor.Dispose(); }
    }

    private static IAgentBus NoopBus()
    {
        var bus = new Mock<IAgentBus>(MockBehavior.Strict);
        bus.Setup(b => b.PublishAsync(It.IsAny<AgentMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        bus.Setup(b => b.SubscribeAsync(It.IsAny<string?>(),
                It.IsAny<Func<AgentMessage, CancellationToken, Task>>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Returns(() => Task.FromResult<IDisposable>(new TrackingSubscription()));
        return bus.Object;
    }

    [Fact(Timeout = 30000)]
    public void Empty_summary_excludes_other_agents_thrashing_history()
    {
        var tracker = new ProgressTracker(NullLogger<ProgressTracker>.Instance);
        var agent = new MarkerAgent("unrelated");
        var container = new AgentContainer(agent, NullLogger<AgentContainer>.Instance);
        for (var index = 0; index < 14; index++)
        {
            agent.SetStateForProbe(index % 2 == 0 ? AgentState.Ready : AgentState.Executing);
            tracker.RecordProgress(container);
        }
        tracker.GetThrashingAgents().Should().Contain("shared");
        tracker.GetSummary(Array.Empty<AgentContainer>()).ThrashingAgents.Should().Be(0);
    }

    private sealed class TrackingSubscription : IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    private static Orchestrator CreateOrchestrator(ServiceProvider provider, LifecycleManager manager,
        DependencyResolver resolver, IAgentTransport transport, IAgentBus? bus = null,
        EscalationManager? escalations = null, ResourceAllocator? allocator = null)
    {
        var architect = new Mock<IArchitectAgent>(MockBehavior.Strict);
        architect.Setup(a => a.DecomposeAsync(It.IsAny<string>(), It.IsAny<DecompositionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DecompositionResult
            {
                OriginalRequest = "isolated run",
                Agents =
                [
                    new() { AgentId = "producer", Domain = "General", Goal = "produce", ResourceRequirements = allocator is null ? null : new() { EstimatedComputeSeconds = 10 } },
                    new() { AgentId = "gate", Domain = "General", Goal = "gate", Dependencies = ["producer"], ResourceRequirements = allocator is null ? null : new() { EstimatedComputeSeconds = 10 } },
                    new() { AgentId = "consumer", Domain = "General", Goal = "consume", Dependencies = ["producer", "gate"], ResourceRequirements = allocator is null ? null : new() { EstimatedComputeSeconds = 10 } }
                ]
            });
        return new Orchestrator(architect.Object,
            new AgentFactory(provider.GetRequiredService<ILogger<AgentFactory>>(), provider), manager, resolver,
            new ConflictDetector(NullLogger<ConflictDetector>.Instance),
            allocator ?? new ResourceAllocator(NullLogger<ResourceAllocator>.Instance),
            new ProgressTracker(NullLogger<ProgressTracker>.Instance),
            escalations ?? new EscalationManager(NullLogger<EscalationManager>.Instance),
            new OutputIntegrator(NullLogger<OutputIntegrator>.Instance),
            bus ?? new AgentBus(NullLogger<AgentBus>.Instance), transport, new SequentialLoopKernel(),
            NullLogger<Orchestrator>.Instance,
            resilientExecutor: new DirectExecutor(),
            circuitBreaker: new Ashlar.Orchestration.Resilience.CircuitBreaker("isolation", 3, TimeSpan.FromSeconds(30)));
    }

    private sealed class DirectExecutor : IResilientExecutor
    {
        public Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, RetryPolicy policy,
            CancellationToken cancellationToken = default) => operation(cancellationToken);
    }

    private sealed class MarkerAgent(string marker) : BaseAgent(
        new AgentSpawnSpec { AgentId = "shared", Domain = "General", Goal = marker }, NullLogger<BaseAgent>.Instance)
    {
        public void SetStateForProbe(AgentState state) => State = state;
        protected override Task OnInitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        protected override Task OnDependenciesResolvedAsync(IReadOnlyDictionary<string, object> outputs,
            CancellationToken cancellationToken) => Task.CompletedTask;
        protected override Task OnShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        protected override Task<object> OnExecuteAsync(IReadOnlyDictionary<string, object>? outputs,
            CancellationToken cancellationToken) => Task.FromResult<object>(marker);
    }
}
