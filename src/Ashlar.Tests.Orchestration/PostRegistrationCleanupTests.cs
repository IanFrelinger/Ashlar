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

public sealed class PostRegistrationCleanupTests
{
    // These two baseline regressions use existing public APIs only. They can run red before
    // introducing CleanupRunAsync; keep the direct internal-method tests separate for baseline.
    [Theory(Timeout = 10000)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public async Task Post_registration_failure_cleans_health_and_preserves_primary_error(int shutdownMode, bool cancellation)
    {
        var monitor = new HealthMonitor(NullLogger<HealthMonitor>.Instance, TimeSpan.FromHours(1));
        var manager = new LifecycleManager(NullLogger<LifecycleManager>.Instance, monitor);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var agent = new CleanupAgent(_ => shutdownMode switch
        {
            1 => Task.FromException(new InvalidOperationException("secondary shutdown")),
            2 => release.Task, // Deliberately ignores cancellation; orchestration must stop waiting.
            _ => Task.CompletedTask
        });
        var container = new AgentContainer(agent, NullLogger<AgentContainer>.Instance);
        using var requestCancellation = new CancellationTokenSource();
        Exception primary = cancellation
            ? new OperationCanceledException("primary cancellation", requestCancellation.Token)
            : new InvalidOperationException("primary after registration");
        try
        {
            var sut = Orchestrator(manager, container, new FailBeforeExecutionLoop(primary,
                () => { if (cancellation) requestCancellation.Cancel(); }));
            var error = await Record.ExceptionAsync(() => sut.OrchestrateAsync("probe", requestCancellation.Token));
            error.Should().BeSameAs(primary);
            monitor.GetAllHealthStatuses().Should().BeEmpty("failed runs no longer own live registrations");
            agent.ShutdownCalls.Should().Be(1);
        }
        finally
        {
            release.TrySetResult(true);
            if (agent.ShutdownCalls > 0) await agent.ShutdownFinished.Task.WaitAsync(TimeSpan.FromSeconds(2));
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

    private sealed class FailBeforeExecutionLoop(Exception error, Action beforeFailure) : ILoopKernel
    {
        private readonly SequentialLoopKernel _inner = new();
        public LoopResult ForEach<T>(IEnumerable<T> items, Func<T, int, CancellationToken, LoopAction> body,
            LoopOptions? options, CancellationToken ct) => _inner.ForEach(items, body, options, ct);
        public ValueTask<LoopResult> ForEachAsync<T>(IEnumerable<T> items,
            Func<T, int, CancellationToken, ValueTask<LoopAction>> body, LoopOptions? options, CancellationToken ct)
        {
            if (options?.Name == "execute-agents")
            {
                beforeFailure();
                throw error;
            }
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
        public TaskCompletionSource<bool> ShutdownFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override Task OnInitializeAsync(CancellationToken token) => Task.CompletedTask;
        protected override Task OnDependenciesResolvedAsync(IReadOnlyDictionary<string, object> outputs,
            CancellationToken token) => Task.CompletedTask;
        protected override Task<object> OnExecuteAsync(IReadOnlyDictionary<string, object>? outputs,
            CancellationToken token) => Task.FromResult<object>("output");
        protected override async Task OnShutdownAsync(CancellationToken token)
        {
            ShutdownCalls++;
            try { await shutdown(token); }
            finally { ShutdownFinished.TrySetResult(true); }
        }
    }
}
