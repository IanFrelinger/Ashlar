using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Ashlar.Abstractions;
using Ashlar.Tests.BackgroundAgents.Agents;
using Ashlar.Orchestration.Agents;
using Ashlar.Abstractions.Agents;
using Ashlar.Orchestration.Architect.Models;
using Xunit;

namespace Ashlar.Tests.BackgroundAgents.Agents;

/// <summary>Tests for background agent manager agent.</summary>
public class BackgroundAgentManagerAgentTests
{
    [Fact]
    public void Name_EqualsSpecAgentId()
    {
        var spec = new AgentSpawnSpec
        {
            AgentId = "meta-agent",
            Domain = "BackgroundAgentManager",
            Goal = "Manage background agents"
        };
        var logger = new Mock<ILogger<BaseAgent>>();
        var agent = new BackgroundAgentManagerAgent(spec, logger.Object);

        agent.Name.Should().Be("meta-agent");
    }

    [Fact]
    public async Task ExecuteAsync_WithoutModel_ReturnsMockOutput()
    {
        var spec = new AgentSpawnSpec
        {
            AgentId = "meta-agent",
            Domain = "BackgroundAgentManager",
            Goal = "Manage background agents"
        };
        var logger = new Mock<ILogger<BaseAgent>>();
        var agent = new BackgroundAgentManagerAgent(spec, logger.Object, model: null);
        await agent.InitializeAsync(default);

        var output = await agent.ExecuteAsync(null, default);

        output.Should().NotBeNull();
        agent.State.Should().Be(AgentState.Completed);
    }

    [Fact]
    public async Task ExecuteAsync_with_model_parses_json_output()
    {
        var spec = new AgentSpawnSpec
        {
            AgentId = "meta-json",
            Domain = "BackgroundAgentManager",
            Goal = "Manage agents",
        };
        var logger = new Mock<ILogger<BaseAgent>>();
        var model = new Mock<IModel>();
        model.Setup(m => m.CompleteAsync(It.IsAny<ModelInput>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ModelOutput("""{"managed":3}"""));

        var agent = new BackgroundAgentManagerAgent(spec, logger.Object, model.Object);
        await agent.InitializeAsync(default);

        var output = await agent.ExecuteAsync(null, default);

        output.Should().NotBeNull();
        model.Verify(m => m.CompleteAsync(It.IsAny<ModelInput>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_with_model_wraps_non_json_text()
    {
        var spec = new AgentSpawnSpec
        {
            AgentId = "meta-text",
            Domain = "BackgroundAgentManager",
            Goal = "Manage agents",
        };
        var logger = new Mock<ILogger<BaseAgent>>();
        var model = new Mock<IModel>();
        model.Setup(m => m.CompleteAsync(It.IsAny<ModelInput>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ModelOutput("plain text response"));

        var agent = new BackgroundAgentManagerAgent(spec, logger.Object, model.Object);
        await agent.InitializeAsync(default);

        var output = await agent.ExecuteAsync(null, default);

        output.Should().NotBeNull();
    }

    [Fact]
    public async Task ShutdownAsync_completes_after_initialize()
    {
        var spec = new AgentSpawnSpec
        {
            AgentId = "meta-shutdown",
            Domain = "BackgroundAgentManager",
            Goal = "Manage agents",
        };
        var logger = new Mock<ILogger<BaseAgent>>();
        var agent = new BackgroundAgentManagerAgent(spec, logger.Object);
        await agent.InitializeAsync(default);
        await agent.ShutdownAsync(default);
        agent.State.Should().Be(AgentState.Terminated);
    }
}
