using Ashlar.Orchestration.Agents;
using Ashlar.Orchestration.Architect.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Ashlar.Tests.Orchestration;

public sealed class LateRunCleanupTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(3);
    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact(Timeout = 15000)]
    public async Task Cleanup_bounds_shutdown_that_blocks_before_returning_a_task()
    {
        await Task.Yield();
        var monitor = new HealthMonitor(NullLogger<HealthMonitor>.Instance, TimeSpan.FromHours(1));
        var manager = new LifecycleManager(NullLogger<LifecycleManager>.Instance, monitor);
        using var release = new ManualResetEventSlim();
        var started = Signal();
        var agent = new ProbeAgent(_ =>
        {
            started.TrySetResult(true);
            release.Wait(); // Deliberately synchronous, ignores cancellation, always released in finally.
            return Task.CompletedTask;
        });
        Task<Exception?>? cleanup = null;
        try
        {
            using var scope = manager.BeginRunScope();
            await manager.RegisterAgentAsync(Container(agent));
            // A separate caller task keeps the test able to release the fake if Task.Run is removed
            // from production cleanup: that mutation must fail the bounded wait, not hang the suite.
            cleanup = Task.Run(() => manager.CleanupRunAsync(TimeSpan.FromMilliseconds(100)));
            await started.Task.WaitAsync(Deadline);
            (await cleanup.WaitAsync(Deadline)).Should().BeOfType<TimeoutException>();
            agent.ShutdownFinished.Task.IsCompleted.Should().BeFalse();
            manager.GetActiveAgents().Should().BeEmpty();
            monitor.GetAllHealthStatuses().Should().BeEmpty();
        }
        finally
        {
            release.Set();
            if (cleanup is not null) await cleanup.WaitAsync(Deadline);
            if (started.Task.IsCompleted) await agent.ShutdownFinished.Task.WaitAsync(Deadline);
            monitor.Dispose();
        }
    }

    [Theory(Timeout = 15000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Late_shutdown_completion_preserves_same_id_root_and_live_sibling(bool lateFault)
    {
        var monitor = new HealthMonitor(NullLogger<HealthMonitor>.Instance, TimeSpan.FromHours(1));
        var manager = new LifecycleManager(NullLogger<LifecycleManager>.Instance, monitor);
        var started = Signal();
        var release = Signal();
        var late = new ProbeAgent(async _ =>
        {
            started.TrySetResult(true);
            await release.Task;
            if (lateFault) throw new InvalidOperationException("late shutdown fault");
        });
        var root = Container(new ProbeAgent(_ => Task.CompletedTask));
        var sibling = Container(new ProbeAgent(_ => Task.CompletedTask));
        try
        {
            await manager.RegisterAgentAsync(root);
            using (manager.BeginRunScope())
            {
                await manager.RegisterAgentAsync(Container(late));
                var cleanup = manager.CleanupRunAsync(TimeSpan.FromMilliseconds(100));
                await started.Task.WaitAsync(Deadline);
                (await cleanup.WaitAsync(Deadline)).Should().BeOfType<TimeoutException>();
            }
            manager.GetAgent("shared").Should().BeSameAs(root);
            using (manager.BeginRunScope())
            {
                await manager.RegisterAgentAsync(sibling);
                try
                {
                    release.TrySetResult(true);
                    await late.ShutdownFinished.Task.WaitAsync(Deadline);
                    // The old run finishes (or faults) while this same-ID run remains registered.
                    manager.GetAgent("shared").Should().BeSameAs(sibling);
                    monitor.GetHealthStatus("shared")!.Container.Should().BeSameAs(sibling);
                    monitor.GetAllHealthStatuses().Should().HaveCount(2);
                    monitor.GetAllHealthStatuses().Should().Contain(s => ReferenceEquals(s.Container, root));
                }
                finally { await manager.ShutdownAllAsync().WaitAsync(Deadline); }
            }
            manager.GetAgent("shared").Should().BeSameAs(root);
            monitor.GetHealthStatus("shared")!.Container.Should().BeSameAs(root);
            monitor.GetAllHealthStatuses().Should().ContainSingle();
        }
        finally
        {
            release.TrySetResult(true);
            if (started.Task.IsCompleted) await late.ShutdownFinished.Task.WaitAsync(Deadline);
            await manager.ShutdownAllAsync().WaitAsync(Deadline);
            monitor.Dispose();
        }
    }

    private static AgentContainer Container(ProbeAgent agent) => new(agent, NullLogger<AgentContainer>.Instance);

    private sealed class ProbeAgent(Func<CancellationToken, Task> shutdown) : BaseAgent(
        new AgentSpawnSpec { AgentId = "shared", Domain = "General", Goal = "bounded cleanup" }, NullLogger<BaseAgent>.Instance)
    {
        public TaskCompletionSource<bool> ShutdownFinished { get; } = Signal();
        public override async Task ShutdownAsync(CancellationToken token = default)
        {
            try { await base.ShutdownAsync(token); }
            finally { ShutdownFinished.TrySetResult(true); }
        }
        protected override Task OnInitializeAsync(CancellationToken token) => Task.CompletedTask;
        protected override Task OnDependenciesResolvedAsync(IReadOnlyDictionary<string, object> outputs,
            CancellationToken token) => Task.CompletedTask;
        protected override Task<object> OnExecuteAsync(IReadOnlyDictionary<string, object>? outputs,
            CancellationToken token) => Task.FromResult<object>("output");
        protected override Task OnShutdownAsync(CancellationToken token) => shutdown(token);
    }
}
