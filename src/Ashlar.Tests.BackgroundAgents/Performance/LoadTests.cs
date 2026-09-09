using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ashlar.BackgroundAgents.Configuration;
using Ashlar.BackgroundAgents.DataSensitivity;
using Ashlar.BackgroundAgents.Logging;
using Ashlar.BackgroundAgents.Registry;
using Ashlar.BackgroundAgents.Scheduling;
using Ashlar.Orchestration.Agents;
using Ashlar.Orchestration.Architect.Models;
using Xunit;
using Ashlar.Tests.BackgroundAgents.Registry;
using Ashlar.Tests.BackgroundAgents.TestHelpers;

namespace Ashlar.Tests.BackgroundAgents.Performance;

/// <summary>
/// Load tests: multiple agents, concurrent registry operations, log store under volume.
/// </summary>
public class LoadTests
{
    private const int AgentCount = 10;

    [Fact]
    public async Task Registry_RegisterAndExecute_MultipleAgents_Concurrent()
    {
        var (registry, agentIds) = await CreateRegistryWithMultipleAgentsAsync(AgentCount);
        registry.GetAll().Should().HaveCount(AgentCount);

        var tasks = agentIds.Select(id => registry.ExecuteOnceAsync(id, default)).ToList();
        await Task.WhenAll(tasks);

        foreach (var id in agentIds)
        {
            var instance = registry.GetAgent(id);
            instance.Should().NotBeNull();
            instance!.ExecutionCount.Should().Be(1);
            instance.SuccessCount.Should().Be(1);
        }
    }

    [Fact]
    public async Task Registry_ConcurrentExecuteOnceAsync_ForSameAgent_SerializesAndCompletes()
    {
        var (registry, agentIds) = await CreateRegistryWithMultipleAgentsAsync(1);
        var agentId = agentIds[0];
        const int concurrency = 15;
        var tasks = Enumerable.Range(0, concurrency)
            .Select(_ => registry.ExecuteOnceAsync(agentId, default))
            .ToList();
        await Task.WhenAll(tasks);

        var instance = registry.GetAgent(agentId);
        instance.Should().NotBeNull();
        instance!.ExecutionCount.Should().Be(concurrency);
        instance.SuccessCount.Should().Be(concurrency);
    }

    [Fact]
    public void LogStore_HighVolumeAppend_BoundedBufferRespected()
    {
        var logStore = new InMemoryAgentLogStore(maxEntriesPerAgent: 100);
        var agentId = "load-agent";
        for (var i = 0; i < 500; i++)
            logStore.Append(agentId, "Info", $"Message {i}");

        var recent = logStore.GetRecent(agentId, 1000, null, null);
        recent.Should().HaveCount(100, "buffer should cap at 100 entries");
    }

    [Fact]
    public void LogStore_ConcurrentAppend_DoesNotThrow()
    {
        var logStore = new InMemoryAgentLogStore(5000);
        var tasks = Enumerable.Range(0, 100)
            .Select(i => Task.Run(() =>
            {
                for (var j = 0; j < 50; j++)
                    logStore.Append($"agent-{i % 5}", "Info", $"Msg {j}");
            }))
            .ToList();
        var act = () => Task.WhenAll(tasks).GetAwaiter().GetResult();
        act.Should().NotThrow();
        var recent = logStore.GetRecent("agent-0", 1000, null, null);
        recent.Should().NotBeEmpty();
    }

    private static async Task<(IBackgroundAgentRegistry Registry, List<string> AgentIds)> CreateRegistryWithMultipleAgentsAsync(int count)
    {
        var dict = new Dictionary<string, string?>();
        for (var i = 0; i < count; i++)
        {
            var prefix = $"BackgroundAgents:Agents:{i}";
            dict[$"{prefix}:Id"] = $"load-agent-{i}";
            dict[$"{prefix}:Name"] = $"Load Agent {i}";
            dict[$"{prefix}:Role"] = "monitor";
            dict[$"{prefix}:Enabled"] = "true";
            dict[$"{prefix}:MaxDataSensitivity"] = "Public";
            dict[$"{prefix}:Commands:0"] = "ping";
            dict[$"{prefix}:Schedule:Type"] = "Interval";
            dict[$"{prefix}:Schedule:Interval"] = "00:01:00";
        }

        var config = new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
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
        configs.Should().HaveCount(count);
        var agentIds = new List<string>();
        var logger = sp.GetRequiredService<ILogger<GenericAgent>>();
        foreach (var agentConfig in configs)
        {
            var spec = specBuilder.BuildSpec(agentConfig);
            var agent = new GenericAgent(spec.ToOrchestrationSpec(), logger);
            await registry.RegisterAuthoredAsync(agent, agentConfig);
            agentIds.Add(agentConfig.Id);
        }
        return (registry, agentIds);
    }
}
