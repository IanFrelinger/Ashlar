using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ashlar.BackgroundAgents.Configuration;
using Ashlar.BackgroundAgents.DataSensitivity;
using Ashlar.BackgroundAgents.Logging;
using Ashlar.BackgroundAgents.Registry;
using Ashlar.BackgroundAgents.RAG;
using Ashlar.BackgroundAgents.Scheduling;
using Ashlar.Orchestration.Agents;
using Ashlar.Orchestration.Architect.Models;
using Xunit;
using Ashlar.Tests.BackgroundAgents.Registry;
using Ashlar.Tests.BackgroundAgents.TestHelpers;

namespace Ashlar.Tests.BackgroundAgents.Integration;

/// <summary>
/// End-to-end integration tests for the background agents pipeline:
/// config load, register, execute once, logs, metrics, sensitivity, RAG.
/// </summary>
public class BackgroundAgentsE2ETests
{
    [Fact]
    public async Task FullPipeline_LoadConfig_Register_ExecuteOnce_LogsAndMetrics()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BackgroundAgents:Agents:0:Id"] = "e2e-agent",
                ["BackgroundAgents:Agents:0:Name"] = "E2E Test Agent",
                ["BackgroundAgents:Agents:0:Role"] = "monitor",
                ["BackgroundAgents:Agents:0:Enabled"] = "true",
                ["BackgroundAgents:Agents:0:MaxDataSensitivity"] = "Public",
                ["BackgroundAgents:Agents:0:Commands:0"] = "ping",
                ["BackgroundAgents:Agents:0:Schedule:Type"] = "Interval",
                ["BackgroundAgents:Agents:0:Schedule:Interval"] = "00:01:00"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton<IConfiguration>(config);
        var sp = services.BuildServiceProvider();

        var sensitivityRegistry = new DataSensitivityRegistry();
        var configLoader = new BackgroundAgentConfigLoader(config, sensitivityRegistry, null);
        var specBuilder = new BackgroundAgentSpecBuilder(sensitivityRegistry, null);
        var logStore = new InMemoryAgentLogStore();
        var scheduleExecutor = new ScheduleExecutor();
        var scheduler = new AgentScheduler(scheduleExecutor, null);
        var registry = new BackgroundAgentRegistry(scheduler, null, logStore);

        var configs = await configLoader.LoadAsync(default);
        configs.Should().HaveCount(1);
        var agentConfig = configs[0];
        agentConfig.Id.Should().Be("e2e-agent");

        var spec = specBuilder.BuildSpec(agentConfig);
        var logger = sp.GetRequiredService<ILogger<GenericAgent>>();
        var agent = new GenericAgent(spec.ToOrchestrationSpec(), logger);
        await registry.RegisterAuthoredAsync(agent, agentConfig);

        await registry.ExecuteOnceAsync("e2e-agent", default);

        var instance = registry.GetAgent("e2e-agent");
        instance.Should().NotBeNull();
        instance!.ExecutionCount.Should().Be(1);
        instance.SuccessCount.Should().Be(1);

        var logs = logStore.GetRecent("e2e-agent", 10, null, null);
        logs.Should().NotBeEmpty();
        logs.Any(e => e.Message.Contains("Executing") || e.Message.Contains("completed")).Should().BeTrue();
    }

    [Fact]
    public async Task SensitivityAndRAG_IntegratedWithPipeline()
    {
        var sensitivityRegistry = new DataSensitivityRegistry();
        var levels = sensitivityRegistry.GetAll();
        levels.Should().NotBeEmpty();
        sensitivityRegistry.GetByName("Public").Should().NotBeNull();

        var embedding = new TokenEmbeddingGenerator();
        var store = new InMemoryVectorStore(sensitivityRegistry);
        var rag = new RAGService(store, embedding);
        var count = await rag.GetDocumentCountAsync(default);
        count.Should().Be(0);
    }
}
