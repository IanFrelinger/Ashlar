using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Ashlar.Abstractions;
using Ashlar.BackgroundAgents.Configuration;
using Ashlar.BackgroundAgents.DataSensitivity;
using Ashlar.BackgroundAgents.Registry;
using Ashlar.BackgroundAgents.Services;
using Ashlar.Orchestration.Agents;
using Xunit;

namespace Ashlar.Tests.BackgroundAgents.Services;

/// <summary>Tests for background agent service.</summary>
public sealed class BackgroundAgentServiceTests
{
    [Fact]
    public void Constructor_rejects_null_dependencies()
    {
        var sensitivity = new DataSensitivityRegistry();
        var loader = CreateLoaderForAgent("probe-agent");
        var builder = new BackgroundAgentSpecBuilder(sensitivity, null);
        var factory = CreateAgentFactory();
        var registry = Mock.Of<IBackgroundAgentRegistry>();
        var logger = Mock.Of<ILogger<BackgroundAgentService>>();

        var act = () => new BackgroundAgentService(null!, builder, factory, registry, sensitivity, logger);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task ExecuteAsync_registers_enabled_agents_and_starts_registry()
    {
        var registry = new Mock<IBackgroundAgentRegistry>();
        registry.Setup(r => r.RegisterAsync(It.IsAny<IAgent>(), It.IsAny<BackgroundAgentConfig>(), AgentRegistrationOrigin.Authored, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        registry.Setup(r => r.StartAllAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var service = CreateService(registry.Object, CreateLoaderForAgent("enabled-agent"));
        await RunUntilRegistryStartedAsync(service, registry);

        registry.Verify(r => r.RegisterAsync(
            It.IsAny<IAgent>(),
            It.Is<BackgroundAgentConfig>(c => c.Id == "enabled-agent"),
            AgentRegistrationOrigin.Authored,
            It.IsAny<CancellationToken>()), Times.Once);
        registry.Verify(r => r.StartAllAsync(It.IsAny<CancellationToken>()), Times.Once);
        registry.Verify(r => r.StopAllAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_skips_disabled_agents()
    {
        var registry = new Mock<IBackgroundAgentRegistry>();
        registry.Setup(r => r.RegisterAsync(It.IsAny<IAgent>(), It.IsAny<BackgroundAgentConfig>(), AgentRegistrationOrigin.Authored, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        registry.Setup(r => r.StartAllAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BackgroundAgents:Agents:0:Id"] = "disabled-agent",
                ["BackgroundAgents:Agents:0:Name"] = "Disabled",
                ["BackgroundAgents:Agents:0:Role"] = "monitor",
                ["BackgroundAgents:Agents:0:Enabled"] = "false",
                ["BackgroundAgents:Agents:0:MaxDataSensitivity"] = "Public",
                ["BackgroundAgents:Agents:0:Commands:0"] = "ping",
                ["BackgroundAgents:Agents:0:Schedule:Type"] = "Interval",
                ["BackgroundAgents:Agents:0:Schedule:Interval"] = "00:01:00",
            })
            .Build();

        var service = CreateService(registry.Object, CreateLoader(config));
        await RunUntilRegistryStartedAsync(service, registry);

        registry.Verify(
            r => r.RegisterAsync(It.IsAny<IAgent>(), It.IsAny<BackgroundAgentConfig>(), AgentRegistrationOrigin.Authored, It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_continues_when_one_agent_registration_fails()
    {
        var registry = new Mock<IBackgroundAgentRegistry>();
        registry
            .Setup(r => r.RegisterAsync(It.IsAny<IAgent>(), It.Is<BackgroundAgentConfig>(c => c.Id == "bad-agent"), AgentRegistrationOrigin.Authored, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("register failed"));
        registry
            .Setup(r => r.RegisterAsync(It.IsAny<IAgent>(), It.Is<BackgroundAgentConfig>(c => c.Id == "good-agent"), AgentRegistrationOrigin.Authored, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        registry.Setup(r => r.StartAllAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BackgroundAgents:Agents:0:Id"] = "bad-agent",
                ["BackgroundAgents:Agents:0:Name"] = "Bad",
                ["BackgroundAgents:Agents:0:Role"] = "monitor",
                ["BackgroundAgents:Agents:0:Enabled"] = "true",
                ["BackgroundAgents:Agents:0:MaxDataSensitivity"] = "Public",
                ["BackgroundAgents:Agents:0:Commands:0"] = "ping",
                ["BackgroundAgents:Agents:0:Schedule:Type"] = "Interval",
                ["BackgroundAgents:Agents:0:Schedule:Interval"] = "00:01:00",
                ["BackgroundAgents:Agents:1:Id"] = "good-agent",
                ["BackgroundAgents:Agents:1:Name"] = "Good",
                ["BackgroundAgents:Agents:1:Role"] = "monitor",
                ["BackgroundAgents:Agents:1:Enabled"] = "true",
                ["BackgroundAgents:Agents:1:MaxDataSensitivity"] = "Public",
                ["BackgroundAgents:Agents:1:Commands:0"] = "ping",
                ["BackgroundAgents:Agents:1:Schedule:Type"] = "Interval",
                ["BackgroundAgents:Agents:1:Schedule:Interval"] = "00:01:00",
            })
            .Build();

        var service = CreateService(registry.Object, CreateLoader(config));
        await RunUntilRegistryStartedAsync(service, registry);

        registry.Verify(
            r => r.RegisterAsync(It.IsAny<IAgent>(), It.Is<BackgroundAgentConfig>(c => c.Id == "good-agent"), AgentRegistrationOrigin.Authored, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_loader_failure_rethrows_and_stops_service()
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

        var service = CreateService(Mock.Of<IBackgroundAgentRegistry>(), badLoader);

        await service.StartAsync(CancellationToken.None);
        var act = async () => await service.ExecuteTask!;
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Agent ID is required*");
        await service.StopAsync(CancellationToken.None);
    }

    /// <summary>
    /// Hosted <see cref="BackgroundService.StartAsync"/> links the caller's token into
    /// <c>ExecuteAsync</c>. Canceling that token (or racing a short <c>Task.Delay</c>
    /// against it) surfaces <see cref="TaskCanceledException"/> from <c>ExecuteTask</c>
    /// on a loaded Windows runner. Drive the loop with <see cref="CancellationToken.None"/>
    /// and stop through <c>StopAsync</c> once the registry start has been observed.
    /// </summary>
    private static async Task RunUntilRegistryStartedAsync(
        BackgroundAgentService service,
        Mock<IBackgroundAgentRegistry> registry,
        TimeSpan? timeout = null)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        registry.Setup(r => r.StartAllAsync(It.IsAny<CancellationToken>()))
            .Callback<CancellationToken>(_ => started.TrySetResult())
            .Returns(Task.CompletedTask);

        await service.StartAsync(CancellationToken.None);
        await started.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(5));
        await service.StopAsync(CancellationToken.None);
        if (service.ExecuteTask is { } exec)
        {
            try { await exec; }
            catch (OperationCanceledException) { /* host shutdown */ }
        }
    }

    private static BackgroundAgentService CreateService(IBackgroundAgentRegistry registry, BackgroundAgentConfigLoader loader)
    {
        var sensitivity = new DataSensitivityRegistry();
        return new BackgroundAgentService(
            loader,
            new BackgroundAgentSpecBuilder(sensitivity, null),
            CreateAgentFactory(),
            registry,
            sensitivity,
            Mock.Of<ILogger<BackgroundAgentService>>());
    }

    private static BackgroundAgentConfigLoader CreateLoaderForAgent(string agentId)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BackgroundAgents:Agents:0:Id"] = agentId,
                ["BackgroundAgents:Agents:0:Name"] = "Service Agent",
                ["BackgroundAgents:Agents:0:Role"] = "monitor",
                ["BackgroundAgents:Agents:0:Enabled"] = "true",
                ["BackgroundAgents:Agents:0:MaxDataSensitivity"] = "Public",
                ["BackgroundAgents:Agents:0:Commands:0"] = "ping",
                ["BackgroundAgents:Agents:0:Schedule:Type"] = "Interval",
                ["BackgroundAgents:Agents:0:Schedule:Interval"] = "00:01:00",
            })
            .Build();
        /// <summary>Creates loader.</summary>
        return CreateLoader(config);
    }

    /// <summary>Creates loader.</summary>
    /// <param name="config">Config.</param>
    private static BackgroundAgentConfigLoader CreateLoader(IConfiguration config) =>
        new(config, new DataSensitivityRegistry(), null);

    private static Ashlar.Core.Application.Orchestration.Ports.IAgentCreator CreateAgentFactory()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var sp = services.BuildServiceProvider();
        var agentFactory = new AgentFactory(sp.GetRequiredService<ILogger<AgentFactory>>(), sp);
        return new Ashlar.Orchestration.Adapters.AgentCreatorAdapter(agentFactory, sp.GetRequiredService<ILogger<Ashlar.Orchestration.Adapters.AgentCreatorAdapter>>());
    }
}
