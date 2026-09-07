using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Ashlar.CLI.Commands.BackgroundAgent;
using Ashlar.BackgroundAgents.Configuration;
using Ashlar.BackgroundAgents.DataSensitivity;
using Ashlar.BackgroundAgents.Registry;
using Ashlar.Core.Application.Testing.Abstractions;
using Ashlar.Core.Application.Testing.Models;
using Ashlar.Orchestration.Agents;

namespace Ashlar.Tests.CLI.Tests.Commands;

/// <summary>Tests for background agent command.</summary>
public class BackgroundAgentCommandTests : UnitTestBase
{
    public override async Task<TestResult> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            /// <summary>Test list empty.</summary>
            await TestListEmpty();
            /// <summary>Test list format json.</summary>
            await TestListFormatJson();
            /// <summary>Test daemon rejects invalid duration.</summary>
            await TestDaemonRejectsInvalidDuration();
            /// <summary>Test daemon rejects missing config.</summary>
            await TestDaemonRejectsMissingConfig();
            /// <summary>Test autoscale stops idle surplus auto agents.</summary>
            await TestAutoscaleStopsIdleSurplusAutoAgents();
            /// <summary>Test autoscale restarts stopped auto agent when demand increases.</summary>
            await TestAutoscaleRestartsStoppedAutoAgentWhenDemandIncreases();
            /// <summary>Test calculate desired agent count.</summary>
            await TestCalculateDesiredAgentCount();
            return new TestResult
            {
                Name = nameof(BackgroundAgentCommandTests),
                Category = "CLI",
                Passed = true,
                Message = "All BackgroundAgentCommand tests passed"
            };
        }
        catch (AssertionException ex)
        {
            return new TestResult
            {
                Name = nameof(BackgroundAgentCommandTests),
                Category = "CLI",
                Passed = false,
                ErrorMessage = $"Assertion failed: {ex.Message}",
                StackTrace = ex.StackTrace
            };
        }
        catch (Exception ex)
        {
            return new TestResult
            {
                Name = nameof(BackgroundAgentCommandTests),
                Category = "CLI",
                Passed = false,
                ErrorMessage = ex.Message,
                StackTrace = ex.StackTrace
            };
        }
    }

    private static IConfiguration CreateEmptyConfiguration()
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();
    }

    private static IConfiguration CreateRoleConfiguration(string role = "extender")
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BackgroundAgents:Agents:0:Id"] = $"base-{role}",
                ["BackgroundAgents:Agents:0:Name"] = $"Base {role}",
                ["BackgroundAgents:Agents:0:Role"] = role,
                ["BackgroundAgents:Agents:0:Enabled"] = "true",
                ["BackgroundAgents:Agents:0:Commands:0"] = "extend",
                ["BackgroundAgents:Agents:0:Schedule:Type"] = "Continuous",
                ["BackgroundAgents:Agents:0:MaxDataSensitivity"] = "Public"
            })
            .Build();
    }

    private async Task TestListEmpty()
    {
        var config = CreateEmptyConfiguration();
        var sensitivityRegistry = new DataSensitivityRegistry();
        var configLoader = new BackgroundAgentConfigLoader(config, sensitivityRegistry, null);
        var registry = new Mock<IBackgroundAgentRegistry>();
        registry.Setup(r => r.GetAll()).Returns(Array.Empty<BackgroundAgentInstance>().ToList());
        var specBuilder = new BackgroundAgentSpecBuilder(sensitivityRegistry, null);
        var loggerFactory = new Mock<ILogger<AgentFactory>>();
        var serviceProvider = new Mock<IServiceProvider>();
        var agentFactory = new AgentFactory(loggerFactory.Object, serviceProvider.Object);
        var logger = new Mock<ILogger<BackgroundAgentCommand>>();

        var command = new BackgroundAgentCommand(
            configLoader,
            registry.Object,
            specBuilder,
            agentFactory,
            logger.Object);

        var exitCode = await command.ListAsync(false, null, null, null);
        /// <summary>Assert equal.</summary>
        AssertEqual(0, exitCode);
    }

    private async Task TestListFormatJson()
    {
        var config = CreateEmptyConfiguration();
        var sensitivityRegistry = new DataSensitivityRegistry();
        var configLoader = new BackgroundAgentConfigLoader(config, sensitivityRegistry, null);
        var registry = new Mock<IBackgroundAgentRegistry>();
        registry.Setup(r => r.GetAll()).Returns(Array.Empty<BackgroundAgentInstance>().ToList());
        var specBuilder = new BackgroundAgentSpecBuilder(sensitivityRegistry, null);
        var loggerFactory = new Mock<ILogger<AgentFactory>>();
        var serviceProvider = new Mock<IServiceProvider>();
        var agentFactory = new AgentFactory(loggerFactory.Object, serviceProvider.Object);
        var logger = new Mock<ILogger<BackgroundAgentCommand>>();

        var command = new BackgroundAgentCommand(
            configLoader,
            registry.Object,
            specBuilder,
            agentFactory,
            logger.Object);

        var exitCode = await command.ListAsync(true, null, null, null);
        /// <summary>Assert equal.</summary>
        AssertEqual(0, exitCode);
    }

    private async Task TestAutoscaleStopsIdleSurplusAutoAgents()
    {
        var config = CreateRoleConfiguration("extender");
        var sensitivityRegistry = new DataSensitivityRegistry();
        var configLoader = new BackgroundAgentConfigLoader(config, sensitivityRegistry, null);
        var now = DateTimeOffset.UtcNow;
        var instances = new List<BackgroundAgentInstance>
        {
            new BackgroundAgentInstance
            {
                Config = new BackgroundAgentConfig { Id = "base-extender", Name = "Base", Role = "extender", Commands = ["extend"], Schedule = new BackgroundAgentSchedule { Type = ScheduleType.Continuous } },
                State = BackgroundAgentState.Running,
                LastCompletedAt = now
            },
            new BackgroundAgentInstance
            {
                Config = new BackgroundAgentConfig { Id = "autoscale-extender-1", Name = "Auto 1", Role = "extender", Commands = ["extend"], Schedule = new BackgroundAgentSchedule { Type = ScheduleType.Continuous } },
                State = BackgroundAgentState.Running,
                LastCompletedAt = now.AddMinutes(-10)
            },
            new BackgroundAgentInstance
            {
                Config = new BackgroundAgentConfig { Id = "autoscale-extender-2", Name = "Auto 2", Role = "extender", Commands = ["extend"], Schedule = new BackgroundAgentSchedule { Type = ScheduleType.Continuous } },
                State = BackgroundAgentState.Running,
                LastCompletedAt = now.AddMinutes(-8)
            }
        };

        var registry = new Mock<IBackgroundAgentRegistry>();
        registry.Setup(r => r.GetAll()).Returns(instances);
        registry.Setup(r => r.StopAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var specBuilder = new BackgroundAgentSpecBuilder(sensitivityRegistry, null);
        var loggerFactory = new Mock<ILogger<AgentFactory>>();
        var serviceProvider = new Mock<IServiceProvider>();
        var agentFactory = new AgentFactory(loggerFactory.Object, serviceProvider.Object);
        var logger = new Mock<ILogger<BackgroundAgentCommand>>();
        var command = new BackgroundAgentCommand(configLoader, registry.Object, specBuilder, agentFactory, logger.Object);

        var exitCode = await command.AutoScaleAsync(
            role: "extender",
            demand: 0,
            minAgents: 0,
            maxAgents: 5,
            unitsPerAgent: 1,
            idleSeconds: 0,
            formatJson: true);

        /// <summary>Assert equal.</summary>
        AssertEqual(0, exitCode);
        registry.Verify(r => r.StopAsync("autoscale-extender-1", It.IsAny<CancellationToken>()), Times.Once);
        registry.Verify(r => r.StopAsync("autoscale-extender-2", It.IsAny<CancellationToken>()), Times.Once);
    }

    private async Task TestAutoscaleRestartsStoppedAutoAgentWhenDemandIncreases()
    {
        var config = CreateRoleConfiguration("extender");
        var sensitivityRegistry = new DataSensitivityRegistry();
        var configLoader = new BackgroundAgentConfigLoader(config, sensitivityRegistry, null);
        var instances = new List<BackgroundAgentInstance>
        {
            new BackgroundAgentInstance
            {
                Config = new BackgroundAgentConfig { Id = "base-extender", Name = "Base", Role = "extender", Commands = ["extend"], Schedule = new BackgroundAgentSchedule { Type = ScheduleType.Continuous } },
                State = BackgroundAgentState.Running
            },
            new BackgroundAgentInstance
            {
                Config = new BackgroundAgentConfig { Id = "autoscale-extender-1", Name = "Auto 1", Role = "extender", Commands = ["extend"], Schedule = new BackgroundAgentSchedule { Type = ScheduleType.Continuous } },
                State = BackgroundAgentState.Stopped
            }
        };

        var registry = new Mock<IBackgroundAgentRegistry>();
        registry.Setup(r => r.GetAll()).Returns(instances);
        registry.Setup(r => r.StartAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var specBuilder = new BackgroundAgentSpecBuilder(sensitivityRegistry, null);
        var loggerFactory = new Mock<ILogger<AgentFactory>>();
        var serviceProvider = new Mock<IServiceProvider>();
        var agentFactory = new AgentFactory(loggerFactory.Object, serviceProvider.Object);
        var logger = new Mock<ILogger<BackgroundAgentCommand>>();
        var command = new BackgroundAgentCommand(configLoader, registry.Object, specBuilder, agentFactory, logger.Object);

        var exitCode = await command.AutoScaleAsync(
            role: "extender",
            demand: 2,
            minAgents: 0,
            maxAgents: 5,
            unitsPerAgent: 1,
            idleSeconds: 0,
            formatJson: true);

        /// <summary>Assert equal.</summary>
        AssertEqual(0, exitCode);
        registry.Verify(r => r.StartAsync("autoscale-extender-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    private async Task TestDaemonRejectsInvalidDuration()
    {
        var command = new BackgroundAgentDaemonCommand();
        var exitCode = await command.RunAsync(
            configPath: null,
            duration: "invalid",
            patternStorePath: null,
            disableObservation: false,
            formatJson: true);
        /// <summary>Assert equal.</summary>
        AssertEqual(1, exitCode);
    }

    private async Task TestDaemonRejectsMissingConfig()
    {
        var command = new BackgroundAgentDaemonCommand();
        var missingPath = Path.Combine(Path.GetTempPath(), $"ashlar-missing-{Guid.NewGuid():N}.json");
        var exitCode = await command.RunAsync(
            configPath: missingPath,
            duration: "1s",
            patternStorePath: null,
            disableObservation: false,
            formatJson: true);
        /// <summary>Assert equal.</summary>
        AssertEqual(1, exitCode);
    }

    private Task TestCalculateDesiredAgentCount()
    {
        AssertEqual(0, BackgroundAgentCommand.CalculateDesiredAgentCount(demand: 0, minAgents: 0, maxAgents: 5, unitsPerAgent: 2));
        AssertEqual(1, BackgroundAgentCommand.CalculateDesiredAgentCount(demand: 1, minAgents: 0, maxAgents: 5, unitsPerAgent: 2));
        AssertEqual(2, BackgroundAgentCommand.CalculateDesiredAgentCount(demand: 3, minAgents: 0, maxAgents: 5, unitsPerAgent: 2));
        AssertEqual(5, BackgroundAgentCommand.CalculateDesiredAgentCount(demand: 99, minAgents: 0, maxAgents: 5, unitsPerAgent: 2));
        return Task.CompletedTask;
    }
}
