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

    /// <summary>
    /// Runs the daemon until it has parked once, and returns what it printed.
    ///
    /// <para>Both daemon facts below used to call <c>RunAsync</c> with no cancellation and assert
    /// <c>exit 1</c>. That contract no longer exists: the daemon PARKS on a failed precondition
    /// instead of exiting, because a non-zero exit under <c>restart: unless-stopped</c> is a
    /// restart loop that writes to the card as fast as the machine allows (the reasoning is at the
    /// top of <c>BackgroundAgentDaemonCommand.RunAsync</c>). So <c>RunAsync</c> never returned, and
    /// this suite hung for the whole 480s test timeout rather than failing — a suite that cannot
    /// finish reports nothing at all.</para>
    ///
    /// <para>The behaviour is right and the assertion was stale, so the assertion moved: a bad
    /// precondition must PARK, must say why, and must keep the node alive. Cancellation is what
    /// ends the loop, exactly as Ctrl+C or <c>docker stop</c> does in production.</para>
    /// </summary>
    private static async Task<(int ExitCode, string Output)> RunDaemonUntilParkedAsync(
        string? configPath, string? duration)
    {
        var command = new BackgroundAgentDaemonCommand();
        var original = Console.Out;
        var captured = new StringWriter();
        // The first park is immediate; the loop then sleeps on a 5s backoff. Cancelling inside that
        // sleep is the only exit, and it is the one an operator has too.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            Console.SetOut(captured);
            var exitCode = await command.RunAsync(
                configPath: configPath,
                duration: duration,
                patternStorePath: null,
                disableObservation: false,
                formatJson: true,
                cancellationToken: cts.Token);
            return (exitCode, captured.ToString());
        }
        finally
        {
            Console.SetOut(original);
        }
    }

    private async Task TestDaemonRejectsInvalidDuration()
    {
        var (exitCode, output) = await RunDaemonUntilParkedAsync(configPath: null, duration: "invalid");

        // Cancellation is a clean stop, so the exit code is 0; the REFUSAL is in the parked report.
        AssertEqual(0, exitCode);
        AssertTrue(output.Contains("\"parked\"", StringComparison.Ordinal),
            $"a malformed --duration must park and say so, got: {output}");
        AssertTrue(output.Contains("\"ok\":false", StringComparison.Ordinal),
            $"a parked node is not ok, got: {output}");
        AssertTrue(output.Contains("invalid --duration value", StringComparison.Ordinal),
            $"the park reason must name the offending value, got: {output}");
        AssertTrue(output.Contains("correct --duration, and start it again", StringComparison.Ordinal),
            $"argv cannot change while the process runs, so the refusal must name how to stop it, got: {output}");
    }

    private async Task TestDaemonRejectsMissingConfig()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), $"ashlar-missing-{Guid.NewGuid():N}.json");

        var (exitCode, output) = await RunDaemonUntilParkedAsync(configPath: missingPath, duration: "1s");

        AssertEqual(0, exitCode);
        AssertTrue(output.Contains("\"parked\"", StringComparison.Ordinal),
            $"a missing config must park - it can appear when a volume finishes mounting, got: {output}");
        AssertTrue(output.Contains("config file not found", StringComparison.Ordinal),
            $"the park reason must name the missing file, got: {output}");
        AssertTrue(output.Contains("\"ok\":false", StringComparison.Ordinal),
            $"a node that never started is not ok, got: {output}");
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
