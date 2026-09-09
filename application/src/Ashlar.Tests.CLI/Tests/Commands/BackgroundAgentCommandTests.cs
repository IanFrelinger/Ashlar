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
            /// <summary>Test daemon parks on invalid duration.</summary>
            await TestDaemonParksOnInvalidDuration();
            /// <summary>Test daemon parks on missing config.</summary>
            await TestDaemonParksOnMissingConfig();
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
        var adapterLogger = new Mock<ILogger<Ashlar.Orchestration.Adapters.AgentCreatorAdapter>>();
        var agentCreator = new Ashlar.Orchestration.Adapters.AgentCreatorAdapter(agentFactory, adapterLogger.Object);
        var logger = new Mock<ILogger<BackgroundAgentCommand>>();

        var command = new BackgroundAgentCommand(
            configLoader,
            registry.Object,
            specBuilder,
            agentCreator,
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
        var adapterLogger = new Mock<ILogger<Ashlar.Orchestration.Adapters.AgentCreatorAdapter>>();
        var agentCreator = new Ashlar.Orchestration.Adapters.AgentCreatorAdapter(agentFactory, adapterLogger.Object);
        var logger = new Mock<ILogger<BackgroundAgentCommand>>();

        var command = new BackgroundAgentCommand(
            configLoader,
            registry.Object,
            specBuilder,
            agentCreator,
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
        var adapterLogger = new Mock<ILogger<Ashlar.Orchestration.Adapters.AgentCreatorAdapter>>();
        var agentCreator = new Ashlar.Orchestration.Adapters.AgentCreatorAdapter(agentFactory, adapterLogger.Object);
        var logger = new Mock<ILogger<BackgroundAgentCommand>>();
        var command = new BackgroundAgentCommand(configLoader, registry.Object, specBuilder, agentCreator, logger.Object);

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
        var adapterLogger = new Mock<ILogger<Ashlar.Orchestration.Adapters.AgentCreatorAdapter>>();
        var agentCreator = new Ashlar.Orchestration.Adapters.AgentCreatorAdapter(agentFactory, adapterLogger.Object);
        var logger = new Mock<ILogger<BackgroundAgentCommand>>();
        var command = new BackgroundAgentCommand(configLoader, registry.Object, specBuilder, agentCreator, logger.Object);

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

    private async Task TestDaemonParksOnInvalidDuration()
    {
        var run = await RunDaemonUntilCancelledAsync(configPath: null, duration: "invalid");

        AssertNotNull(run.ParkedReason, "daemon must report a parked status on stdout for an invalid --duration");
        AssertTrue(run.ParkedReason!.Contains("invalid --duration", StringComparison.Ordinal),
            $"parked reason must name the bad option, got: {run.ParkedReason}");
        AssertTrue(run.SawStopped, "cancelling the parked daemon must produce the stopped status");
        AssertEqual(0, run.ExitCode, "a daemon stopped by cancellation exits cleanly");
    }

    private async Task TestDaemonParksOnMissingConfig()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), $"ashlar-missing-{Guid.NewGuid():N}.json");
        var run = await RunDaemonUntilCancelledAsync(configPath: missingPath, duration: "1s");

        AssertNotNull(run.ParkedReason, "daemon must report a parked status on stdout for a missing config file");
        AssertTrue(run.ParkedReason!.Contains("config file not found", StringComparison.Ordinal),
            $"parked reason must say the config is missing, got: {run.ParkedReason}");
        AssertTrue(run.SawStopped, "cancelling the parked daemon must produce the stopped status");
        AssertEqual(0, run.ExitCode, "a daemon stopped by cancellation exits cleanly");
    }

    /// <summary>What a cancelled daemon run left on stdout, decoded from its JSON lines.</summary>
    private sealed record DaemonRun(int ExitCode, string? ParkedReason, bool SawStopped, string Stdout);

    /// <summary>
    /// Runs the daemon with <c>--format json</c> under a short cancellation window and reads back
    /// the JSON lines it printed.
    ///
    /// The daemon PARKS on a failed precondition instead of exiting (#420): <c>RunAsync</c> writes
    /// a <c>{"status":"parked",...}</c> line, then loops on a 5s → 60s backoff and only returns once
    /// its token is cancelled. Awaiting it with a default token therefore never completes — long
    /// enough to trip VSTest's blame inactivity window, which hang-kills the whole test host and
    /// silently drops every CLI suite scheduled after this one. The backoff is
    /// <c>Task.Delay(backoff, cancellationToken)</c>, so cancellation is honoured within the first
    /// 5s window; a 2s timeout ends the run before that first retry.
    /// </summary>
    private static async Task<DaemonRun> RunDaemonUntilCancelledAsync(string? configPath, string duration)
    {
        // Parking writes a heartbeat under the default state directory (<repo>/.ashlar/state, gitignored).
        // Deliberately NOT redirected via ASHLAR_STATE_DIR: environment variables are process-wide and
        // inherited by child processes, so tests in other collections that spawn `dotnet` during this
        // window (e.g. ProposalsBackgroundAgentCommandTests) would inherit a temp dir that is then deleted.
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        int exitCode;
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);

            exitCode = await new BackgroundAgentDaemonCommand().RunAsync(
                configPath,
                duration,
                patternStorePath: null,
                disableObservation: false,
                formatJson: true,
                cts.Token);
        }
        finally
        {
            Console.SetOut(ConsoleCapture.Out);
            Console.SetError(ConsoleCapture.Error);
        }

        string? parkedReason = null;
        var sawStopped = false;
        var output = stdout.ToString();
        foreach (var raw in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (!line.StartsWith('{'))
                continue;

            using var doc = System.Text.Json.JsonDocument.Parse(line);
            if (!doc.RootElement.TryGetProperty("status", out var status))
                continue;

            switch (status.GetString())
            {
                case "parked":
                    parkedReason ??= doc.RootElement.GetProperty("reason").GetString();
                    break;
                case "stopped":
                    sawStopped = true;
                    break;
            }
        }

        return new DaemonRun(exitCode, parkedReason, sawStopped, output);
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
