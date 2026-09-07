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

namespace Ashlar.Tests.BackgroundAgents.Resilience;

/// <summary>
/// Resilience tests: cancellation, failure isolation, unknown agent, registry state.
/// </summary>
public class RegistryResilienceTests
{
    [Fact]
    public async Task ExecuteOnceAsync_AgentNotFound_ThrowsAndDoesNotCorruptRegistry()
    {
        var (registry, _) = await CreateRegistryWithOneAgentAsync();
        var act = () => registry.ExecuteOnceAsync("nonexistent", default);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not found*");

        registry.GetAll().Should().HaveCount(1);
        var instance = registry.GetAgent("res-agent");
        instance.Should().NotBeNull();
        instance!.ExecutionCount.Should().Be(0);
    }

    [Fact]
    public async Task TwoAgents_ConcurrentExecuteOnce_BothSucceedIndependently()
    {
        var (registry, agentIds) = await CreateRegistryWithTwoAgentsAsync();
        agentIds.Should().HaveCount(2);
        var t1 = registry.ExecuteOnceAsync(agentIds[0], default);
        var t2 = registry.ExecuteOnceAsync(agentIds[1], default);
        await Task.WhenAll(t1, t2);

        foreach (var id in agentIds)
        {
            var instance = registry.GetAgent(id);
            instance.Should().NotBeNull();
            instance!.ExecutionCount.Should().Be(1);
            instance.SuccessCount.Should().Be(1);
            instance.FailureCount.Should().Be(0);
        }
    }

    [Fact]
    public async Task ExecuteOnceAsync_WithCancelledToken_Completes()
    {
        var (registry, agentId) = await CreateRegistryWithOneAgentAsync();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var act = () => registry.ExecuteOnceAsync(agentId, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task RegisterAndExecute_EmptyConfigList_ExecuteOnceThrowsForAnyId()
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();
        services.AddSingleton<IConfiguration>(config);
        var sp = services.BuildServiceProvider();

        var sensitivityRegistry = new DataSensitivityRegistry();
        var specBuilder = new BackgroundAgentSpecBuilder(sensitivityRegistry, null);
        var logStore = new InMemoryAgentLogStore();
        var scheduleExecutor = new ScheduleExecutor();
        var scheduler = new AgentScheduler(scheduleExecutor, null);
        var registry = new BackgroundAgentRegistry(scheduler, null, logStore);

        registry.GetAll().Should().BeEmpty();
        var act = () => registry.ExecuteOnceAsync("any-id", default);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not found*");
    }

    private static async Task<(IBackgroundAgentRegistry Registry, string AgentId)> CreateRegistryWithOneAgentAsync()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BackgroundAgents:Agents:0:Id"] = "res-agent",
                ["BackgroundAgents:Agents:0:Name"] = "Res Agent",
                ["BackgroundAgents:Agents:0:Role"] = "monitor",
                ["BackgroundAgents:Agents:0:Enabled"] = "true",
                ["BackgroundAgents:Agents:0:MaxDataSensitivity"] = "Public",
                ["BackgroundAgents:Agents:0:Commands:0"] = "ping",
                ["BackgroundAgents:Agents:0:Schedule:Type"] = "Interval",
                ["BackgroundAgents:Agents:0:Schedule:Interval"] = "00:01:00"
            })
            .Build();
        var (registry, agentIds) = await CreateRegistryFromConfigAsync(config);
        return (registry, agentIds[0]);
    }

    private static async Task<(IBackgroundAgentRegistry Registry, List<string> AgentIds)> CreateRegistryWithTwoAgentsAsync()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BackgroundAgents:Agents:0:Id"] = "res-agent-1",
                ["BackgroundAgents:Agents:0:Name"] = "Res Agent 1",
                ["BackgroundAgents:Agents:0:Role"] = "monitor",
                ["BackgroundAgents:Agents:0:Enabled"] = "true",
                ["BackgroundAgents:Agents:0:MaxDataSensitivity"] = "Public",
                ["BackgroundAgents:Agents:0:Commands:0"] = "ping",
                ["BackgroundAgents:Agents:0:Schedule:Type"] = "Interval",
                ["BackgroundAgents:Agents:0:Schedule:Interval"] = "00:01:00",
                ["BackgroundAgents:Agents:1:Id"] = "res-agent-2",
                ["BackgroundAgents:Agents:1:Name"] = "Res Agent 2",
                ["BackgroundAgents:Agents:1:Role"] = "monitor",
                ["BackgroundAgents:Agents:1:Enabled"] = "true",
                ["BackgroundAgents:Agents:1:MaxDataSensitivity"] = "Public",
                ["BackgroundAgents:Agents:1:Commands:0"] = "ping",
                ["BackgroundAgents:Agents:1:Schedule:Type"] = "Interval",
                ["BackgroundAgents:Agents:1:Schedule:Interval"] = "00:01:00"
            })
            .Build();
        return await CreateRegistryFromConfigAsync(config);
    }

    private static async Task<(IBackgroundAgentRegistry Registry, List<string> AgentIds)> CreateRegistryFromConfigAsync(IConfiguration config)
    {
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
