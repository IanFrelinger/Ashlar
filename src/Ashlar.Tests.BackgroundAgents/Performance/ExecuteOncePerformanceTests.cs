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
/// Performance-oriented tests for single-agent ExecuteOnceAsync: latency and throughput.
/// These assert baseline expectations (e.g. complete within a timeout) rather than strict benchmarks.
/// </summary>
public class ExecuteOncePerformanceTests
{
    [Fact]
    public async Task ExecuteOnceAsync_CompletesWithinReasonableTime()
    {
        var (registry, agentId) = await CreateRegistryWithOneAgentAsync();
        const int iterations = 20;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++)
            await registry.ExecuteOnceAsync(agentId, default);
        stopwatch.Stop();
        var elapsedMs = stopwatch.ElapsedMilliseconds;
        var avgMsPerCall = elapsedMs / (double)iterations;
        avgMsPerCall.Should().BeLessThan(500, "each ExecuteOnceAsync should complete in under 500ms (no LLM call)");
    }

    [Fact]
    public async Task ExecuteOnceAsync_Throughput_SustainsMultipleCalls()
    {
        var (registry, agentId) = await CreateRegistryWithOneAgentAsync();
        const int iterations = 50;
        var tasks = Enumerable.Range(0, iterations)
            .Select(_ => registry.ExecuteOnceAsync(agentId, default))
            .ToList();
        await Task.WhenAll(tasks);
        var instance = registry.GetAgent(agentId);
        instance.Should().NotBeNull();
        instance!.ExecutionCount.Should().Be(iterations);
        instance.SuccessCount.Should().Be(iterations);
    }

    private static async Task<(IBackgroundAgentRegistry Registry, string AgentId)> CreateRegistryWithOneAgentAsync()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BackgroundAgents:Agents:0:Id"] = "perf-agent",
                ["BackgroundAgents:Agents:0:Name"] = "Perf Agent",
                ["BackgroundAgents:Agents:0:Role"] = "monitor",
                ["BackgroundAgents:Agents:0:Enabled"] = "true",
                ["BackgroundAgents:Agents:0:MaxDataSensitivity"] = "Public",
                ["BackgroundAgents:Agents:0:Commands:0"] = "ping",
                ["BackgroundAgents:Agents:0:Schedule:Type"] = "Interval",
                ["BackgroundAgents:Agents:0:Schedule:Interval"] = "00:01:00"
            })
            .Build();

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
        var agentConfig = configs[0];
        var spec = specBuilder.BuildSpec(agentConfig);
        var logger = sp.GetRequiredService<ILogger<GenericAgent>>();
        var agent = new GenericAgent(spec.ToOrchestrationSpec(), logger);
        await registry.RegisterAuthoredAsync(agent, agentConfig);
        return (registry, agentConfig.Id);
    }
}
