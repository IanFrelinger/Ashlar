using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Ashlar.Abstractions;
using Ashlar.BackgroundAgents.Configuration;
using Ashlar.BackgroundAgents.DataSensitivity;
using Ashlar.BackgroundAgents.Registry;
using Ashlar.BackgroundAgents.Tools;
using Ashlar.Orchestration.Agents;
using Xunit;

namespace Ashlar.Tests.BackgroundAgents.Tools;

/// <summary>Tests for update agent config tool.</summary>
public sealed class UpdateAgentConfigToolTests
{
    [Fact]
    public void Id_and_schema_expose_update_agent_config()
    {
        var tool = CreateTool(Mock.Of<IBackgroundAgentRegistry>(), CreateConfigLoader());
        tool.Id.Should().Be(UpdateAgentConfigTool.DefaultId);
        tool.Schema.Id.Should().Be(UpdateAgentConfigTool.DefaultId);
        tool.Schema.Description.Should().Contain("re-register");
    }

    [Fact]
    public async Task InvokeAsync_missing_agentId_returns_error()
    {
        var tool = CreateTool(Mock.Of<IBackgroundAgentRegistry>(), CreateConfigLoader());
        var result = await tool.InvokeAsync(
            new ToolCall(UpdateAgentConfigTool.DefaultId, JsonSerializer.SerializeToElement(new { })),
            new WorldSnapshot(0, new Dictionary<string, object?>()),
            CancellationToken.None);

        result.Delta.Log.Should().Contain(l => l.Contains("agentId is required"));
    }

    [Fact]
    public async Task InvokeAsync_malformed_arguments_treated_as_missing_agentId()
    {
        var tool = CreateTool(Mock.Of<IBackgroundAgentRegistry>(), CreateConfigLoader());
        var result = await tool.InvokeAsync(
            new ToolCall(UpdateAgentConfigTool.DefaultId, JsonDocument.Parse("123").RootElement),
            new WorldSnapshot(0, new Dictionary<string, object?>()),
            CancellationToken.None);

        result.Delta.Log.Should().Contain(l => l.Contains("agentId is required"));
    }

    [Fact]
    public async Task InvokeAsync_unknown_agent_returns_not_found()
    {
        var tool = CreateTool(Mock.Of<IBackgroundAgentRegistry>(), CreateConfigLoader());
        var result = await tool.InvokeAsync(
            Call("missing-agent"),
            new WorldSnapshot(0, new Dictionary<string, object?>()),
            CancellationToken.None);

        result.Delta.Log.Should().Contain(l => l.Contains("not found in configuration"));
    }

    [Fact]
    public async Task InvokeAsync_re_registers_agent_from_config()
    {
        var registry = new Mock<IBackgroundAgentRegistry>();
        registry.Setup(r => r.RegisterAsync(It.IsAny<IAgent>(), It.IsAny<BackgroundAgentConfig>(), AgentRegistrationOrigin.Authored, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var tool = CreateTool(registry.Object, CreateConfigLoader());
        var result = await tool.InvokeAsync(
            Call("cfg-agent"),
            new WorldSnapshot(0, new Dictionary<string, object?>()),
            CancellationToken.None);

        result.Delta.Log.Should().Contain(l => l.Contains("Re-registered"));
        registry.Verify(
            r => r.RegisterAsync(It.IsAny<IAgent>(), It.Is<BackgroundAgentConfig>(c => c.Id == "cfg-agent"), AgentRegistrationOrigin.Authored, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task InvokeAsync_loader_failure_returns_error_payload()
    {
        var badLoader = new BackgroundAgentConfigLoader(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["BackgroundAgents:Agents:0:Id"] = "",
                    ["BackgroundAgents:Agents:0:Name"] = "Broken",
                    ["BackgroundAgents:Agents:0:Role"] = "monitor",
                    ["BackgroundAgents:Agents:0:MaxDataSensitivity"] = "Public",
                    ["BackgroundAgents:Agents:0:Commands:0"] = "ping",
                    ["BackgroundAgents:Agents:0:Schedule:Type"] = "Interval",
                    ["BackgroundAgents:Agents:0:Schedule:Interval"] = "00:01:00",
                })
                .Build(),
            new DataSensitivityRegistry(),
            null);

        var tool = CreateTool(Mock.Of<IBackgroundAgentRegistry>(), badLoader);
        var result = await tool.InvokeAsync(
            Call("cfg-agent"),
            new WorldSnapshot(0, new Dictionary<string, object?>()),
            CancellationToken.None);

        result.Delta.Log.Should().Contain(l => l.Contains("Agent ID is required"));
    }

    private static UpdateAgentConfigTool CreateTool(IBackgroundAgentRegistry registry, BackgroundAgentConfigLoader loader)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var sp = services.BuildServiceProvider();
        var agentFactory = new AgentFactory(sp.GetRequiredService<ILogger<AgentFactory>>(), sp);
        var agentCreator = new Ashlar.Orchestration.Adapters.AgentCreatorAdapter(agentFactory, sp.GetRequiredService<ILogger<Ashlar.Orchestration.Adapters.AgentCreatorAdapter>>());
        return new UpdateAgentConfigTool(
            registry,
            loader,
            new BackgroundAgentSpecBuilder(new DataSensitivityRegistry(), null),
            agentCreator);
    }

    /// <summary>Creates config loader.</summary>
    private static BackgroundAgentConfigLoader CreateConfigLoader() =>
        new(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["BackgroundAgents:Agents:0:Id"] = "cfg-agent",
                    ["BackgroundAgents:Agents:0:Name"] = "Config Agent",
                    ["BackgroundAgents:Agents:0:Role"] = "monitor",
                    ["BackgroundAgents:Agents:0:Enabled"] = "true",
                    ["BackgroundAgents:Agents:0:MaxDataSensitivity"] = "Public",
                    ["BackgroundAgents:Agents:0:Commands:0"] = "ping",
                    ["BackgroundAgents:Agents:0:Schedule:Type"] = "Interval",
                    ["BackgroundAgents:Agents:0:Schedule:Interval"] = "00:01:00",
                })
                .Build(),
            new DataSensitivityRegistry(),
            null);

    /// <summary>Call.</summary>
    /// <param name="agentId">Agent id.</param>
    private static ToolCall Call(string agentId) =>
        new(UpdateAgentConfigTool.DefaultId, JsonSerializer.SerializeToElement(new { agentId }));
}
