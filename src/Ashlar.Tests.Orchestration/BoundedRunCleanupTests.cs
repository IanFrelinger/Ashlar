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
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Ashlar.Tests.Orchestration;

public sealed class BoundedRunCleanupTests
{
    // Fix-side direct probe. Public-API baseline proof is in PostRegistrationCleanupTests.cs.
    [Theory(Timeout = 10000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cleanup_bounds_cooperative_and_ignoring_shutdown_and_preserves_outer_scope(bool ignoresCancellation)
    {
        var monitor = new HealthMonitor(NullLogger<HealthMonitor>.Instance, TimeSpan.FromHours(1));
        var manager = new LifecycleManager(NullLogger<LifecycleManager>.Instance, monitor);
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var agent = new CleanupAgent(async token =>
        {
            started.TrySetResult(true);
            try
            {
                if (ignoresCancellation) await release.Task;
                else await release.Task.WaitAsync(token);
            }
            finally { finished.TrySetResult(true); }
        });
        var outer = new AgentContainer(new CleanupAgent(_ => Task.CompletedTask), NullLogger<AgentContainer>.Instance);
        try
        {
            using var outerScope = manager.BeginRunScope();
            await manager.RegisterAgentAsync(outer);
            using (manager.BeginRunScope())
            {
                await manager.RegisterAgentAsync(new AgentContainer(agent, NullLogger<AgentContainer>.Instance));
                var cleanup = manager.CleanupRunAsync(TimeSpan.FromMilliseconds(100));
                await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
                var error = await cleanup.WaitAsync(TimeSpan.FromSeconds(2));
                error.Should().NotBeNull();
                manager.GetActiveAgents().Should().BeEmpty();
                monitor.GetAllHealthStatuses().Should().ContainSingle(s => ReferenceEquals(s.Container, outer));
                // Ignoring work remains alive; deregistration is not a claim of thread termination.
                if (ignoresCancellation) finished.Task.IsCompleted.Should().BeFalse();
            }
            manager.GetAgent("shared").Should().BeSameAs(outer);
            await manager.ShutdownAllAsync();
        }
        finally
        {
            release.TrySetResult(true);
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(2));
            monitor.Dispose();
        }
    }

    private static Orchestrator Orchestrator(LifecycleManager manager, AgentContainer container, ILoopKernel loops)
    {
        var architect = new Mock<IArchitectAgent>();
        architect.Setup(a => a.DecomposeAsync(It.IsAny<string>(), It.IsAny<DecompositionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DecompositionResult { OriginalRequest = "probe", Agents = [container.Agent.Spec] });
        var factory = new Mock<IAgentRuntimeFactory>();
        factory.Setup(f => f.Spawn(It.IsAny<AgentSpawnSpec>())).Returns(new InProcessAgentHandle(container));
        return new Orchestrator(architect.Object, factory.Object, manager,
            new DependencyResolver(NullLogger<DependencyResolver>.Instance),
            new ConflictDetector(NullLogger<ConflictDetector>.Instance),
            new ResourceAllocator(NullLogger<ResourceAllocator>.Instance),
            new ProgressTracker(NullLogger<ProgressTracker>.Instance),
            new EscalationManager(NullLogger<EscalationManager>.Instance),
            new OutputIntegrator(NullLogger<OutputIntegrator>.Instance),
            new AgentBus(NullLogger<AgentBus>.Instance), Mock.Of<IAgentTransport>(), loops,
            NullLogger<Orchestrator>.Instance, resilientExecutor: new DirectExecutor(),
            circuitBreaker: new Ashlar.Orchestration.Resilience.CircuitBreaker("cleanup"));
    }

    private sealed class FailBeforeExecutionLoop(Exception error) : ILoopKernel
    {
        private readonly SequentialLoopKernel _inner = new();
        public LoopResult ForEach<T>(IEnumerable<T> items, Func<T, int, CancellationToken, LoopAction> body,
            LoopOptions? options, CancellationToken ct) => _inner.ForEach(items, body, options, ct);
        public ValueTask<LoopResult> ForEachAsync<T>(IEnumerable<T> items,
            Func<T, int, CancellationToken, ValueTask<LoopAction>> body, LoopOptions? options, CancellationToken ct)
        {
            if (options?.Name == "execute-agents") throw error;
            return _inner.ForEachAsync(items, body, options, ct);
        }
        public IReadOnlyList<TOut> SelectToList<TIn, TOut>(IEnumerable<TIn> items,
            Func<TIn, int, CancellationToken, TOut> map, LoopOptions? options, CancellationToken ct) =>
            _inner.SelectToList(items, map, options, ct);
    }

    private sealed class DirectExecutor : IResilientExecutor
    {
        public Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, RetryPolicy policy,
            CancellationToken cancellationToken = default) => operation(cancellationToken);
    }

    private sealed class CleanupAgent(Func<CancellationToken, Task> shutdown) : BaseAgent(
        new AgentSpawnSpec { AgentId = "shared", Domain = "General", Goal = "cleanup" }, NullLogger<BaseAgent>.Instance)
    {
        public int ShutdownCalls { get; private set; }
        protected override Task OnInitializeAsync(CancellationToken token) => Task.CompletedTask;
        protected override Task OnDependenciesResolvedAsync(IReadOnlyDictionary<string, object> outputs,
            CancellationToken token) => Task.CompletedTask;
        protected override Task<object> OnExecuteAsync(IReadOnlyDictionary<string, object>? outputs,
            CancellationToken token) => Task.FromResult<object>("output");
        protected override Task OnShutdownAsync(CancellationToken token)
        {
            ShutdownCalls++;
            return shutdown(token);
        }
    }
}
