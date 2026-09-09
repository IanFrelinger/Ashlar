using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Moq;
using Ashlar.BackgroundAgents.Configuration;
using Ashlar.BackgroundAgents.DataSensitivity;
using Ashlar.BackgroundAgents.Registry;
using Ashlar.BackgroundAgents.Tools;
using Ashlar.Orchestration.Agents;
using Xunit;

namespace Ashlar.Tests.BackgroundAgents.Tools;

/// <summary>Tests for agent management toolbox.</summary>
public class AgentManagementToolboxTests
{
    [Fact]
    public void Schemas_ReturnsFourTools()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        var sensitivityRegistry = new DataSensitivityRegistry();
        var configLoader = new BackgroundAgentConfigLoader(config, sensitivityRegistry, null);
        var registry = new Mock<IBackgroundAgentRegistry>().Object;
        var specBuilder = new BackgroundAgentSpecBuilder(sensitivityRegistry, null);
        var agentFactory = new AgentFactory(Mock.Of<Microsoft.Extensions.Logging.ILogger<AgentFactory>>(), Mock.Of<IServiceProvider>());
        var agentCreator = new Ashlar.Orchestration.Adapters.AgentCreatorAdapter(agentFactory, Mock.Of<Microsoft.Extensions.Logging.ILogger<Ashlar.Orchestration.Adapters.AgentCreatorAdapter>>());
        var toolbox = new AgentManagementToolbox(registry, configLoader, specBuilder, agentCreator);

        var schemas = toolbox.Schemas().ToList();

        schemas.Should().HaveCount(4);
        schemas.Select(s => s.Id).Should().Contain("enable_agent");
        schemas.Select(s => s.Id).Should().Contain("disable_agent");
        schemas.Select(s => s.Id).Should().Contain("restart_agent");
        schemas.Select(s => s.Id).Should().Contain("update_agent_config");
    }
}
