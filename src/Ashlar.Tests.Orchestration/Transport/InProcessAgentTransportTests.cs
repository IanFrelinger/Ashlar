using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Ashlar.Abstractions.Transport;
using Ashlar.Orchestration.Agents;
using Ashlar.Orchestration.Architect.Models;
using Ashlar.Orchestration.Transport;
using Xunit;
using Ashlar.Abstractions.Security.Egress;

namespace Ashlar.Tests.Orchestration.Transport;

/// <summary>Tests for in process agent transport.</summary>
public sealed class InProcessAgentTransportTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SendAsync_maps_refusal_to_structured_failure_without_wrapper_details(bool wrapped)
    {
        var refusal = new EgressRefusedException(new EgressGuard("full", "enforce").Evaluate(
            new EgressRequest(EgressFamilies.Http, "agent", new Uri("https://remote.example"))));
        var manager = CreateLifecycleManager();
        var agent = new RefusingAgent(wrapped ? new IOException("private-canary", refusal) : refusal);
        await manager.RegisterAgentAsync(new AgentContainer(agent, NullLogger<AgentContainer>.Instance));
        var transport = new InProcessAgentTransport(manager, NullLogger<InProcessAgentTransport>.Instance);
        var result = await transport.SendAsync(new AgentInvocationRequest("refusing", "correlation"));
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("EGRESS_REFUSED");
        result.Metadata!["errorCode"].Should().Be("EGRESS_REFUSED");
        result.Metadata["egressRef"].Should().Be(refusal.Ref);
        result.ErrorMessage.Should().Be(refusal.Message);
        result.Output.Should().BeNull();
        agent.Calls.Should().Be(1);
    }

    private sealed class RefusingAgent(Exception exception) : BaseAgent(
        new AgentSpawnSpec { AgentId = "refusing", Domain = "General", Goal = "test" }, NullLogger<RefusingAgent>.Instance)
    {
        public int Calls { get; private set; }
        protected override Task OnInitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        protected override Task OnDependenciesResolvedAsync(IReadOnlyDictionary<string, object> outputs,
            CancellationToken cancellationToken) => Task.CompletedTask;
        protected override Task OnShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        protected override Task<object> OnExecuteAsync(IReadOnlyDictionary<string, object>? outputs,
            CancellationToken cancellationToken)
        {
            Calls++;
            throw exception;
        }
    }

    [Fact]
    public async Task SendAsync_WithRegisteredAgent_ReturnsSuccessfulResult()
    {
        // Arrange
        var lifecycleManager = CreateLifecycleManager();
        var agent = new GenericAgent(
            new AgentSpawnSpec
            {
                AgentId = "agent-1",
                Domain = "General",
                Goal = "Generate output"
            },
            NullLogger<GenericAgent>.Instance);

        var container = new AgentContainer(agent, NullLogger<AgentContainer>.Instance);
        await lifecycleManager.RegisterAgentAsync(container);

        var transport = new InProcessAgentTransport(
            lifecycleManager,
            NullLogger<InProcessAgentTransport>.Instance);

        // Act
        var result = await transport.SendAsync(
            new AgentInvocationRequest("agent-1", "corr-1"));

        // Assert
        result.Success.Should().BeTrue();
        result.Output.Should().NotBeNull();
        result.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task SendAsync_WithUnknownAgent_ReturnsFailureResult()
    {
        // Arrange
        var lifecycleManager = CreateLifecycleManager();
        var transport = new InProcessAgentTransport(
            lifecycleManager,
            NullLogger<InProcessAgentTransport>.Instance);

        // Act
        var result = await transport.SendAsync(
            new AgentInvocationRequest("missing-agent", "corr-2"));

        // Assert
        result.Success.Should().BeFalse();
        result.Output.Should().BeNull();
        result.ErrorMessage.Should().Contain("not registered");
    }

    [Fact]
    public async Task CheckHealthAsync_ReturnsHealthyChannel()
    {
        // Arrange
        var lifecycleManager = CreateLifecycleManager();
        var transport = new InProcessAgentTransport(
            lifecycleManager,
            NullLogger<InProcessAgentTransport>.Instance);

        // Act
        var health = await transport.CheckHealthAsync();

        // Assert
        health.IsHealthy.Should().BeTrue();
        health.TransportName.Should().Be(nameof(InProcessAgentTransport));
    }

    private static LifecycleManager CreateLifecycleManager()
    {
        return new LifecycleManager(
            NullLogger<LifecycleManager>.Instance,
            new HealthMonitor(NullLogger<HealthMonitor>.Instance));
    }
}
