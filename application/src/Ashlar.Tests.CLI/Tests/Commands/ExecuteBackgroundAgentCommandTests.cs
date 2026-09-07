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

/// <summary>Tests for execute background agent command.</summary>
public class ExecuteBackgroundAgentCommandTests : UnitTestBase
{
    public override async Task<TestResult> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            /// <summary>Test execute once succeeds.</summary>
            await TestExecuteOnceSucceeds();
            return new TestResult
            {
                Name = nameof(ExecuteBackgroundAgentCommandTests),
                Category = "CLI",
                Passed = true,
                Message = "All ExecuteBackgroundAgentCommand tests passed"
            };
        }
        catch (AssertionException ex)
        {
            return new TestResult
            {
                Name = nameof(ExecuteBackgroundAgentCommandTests),
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
                Name = nameof(ExecuteBackgroundAgentCommandTests),
                Category = "CLI",
                Passed = false,
                ErrorMessage = ex.Message,
                StackTrace = ex.StackTrace
            };
        }
    }

    private async Task TestExecuteOnceSucceeds()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BackgroundAgents:Agents:0:Id"] = "test-agent",
                ["BackgroundAgents:Agents:0:Name"] = "Test",
                ["BackgroundAgents:Agents:0:Role"] = "test",
                ["BackgroundAgents:Agents:0:Enabled"] = "true"
            })
            .Build();
        var sensitivityRegistry = new DataSensitivityRegistry();
        var configLoader = new BackgroundAgentConfigLoader(config, sensitivityRegistry, null);
        var registry = new Mock<IBackgroundAgentRegistry>();
        var instance = new BackgroundAgentInstance
        {
            Config = new BackgroundAgentConfig { Id = "test-agent", Name = "Test", Role = "test" }
        };
        registry.Setup(r => r.GetAgent("test-agent")).Returns(instance);
        registry.Setup(r => r.ExecuteOnceAsync("test-agent", It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var specBuilder = new BackgroundAgentSpecBuilder(sensitivityRegistry, null);
        var loggerFactory = new Mock<ILogger<AgentFactory>>();
        var agentFactory = new AgentFactory(loggerFactory.Object, new Mock<IServiceProvider>().Object);
        var logger = new Mock<ILogger<ExecuteBackgroundAgentCommand>>();

        var command = new ExecuteBackgroundAgentCommand(configLoader, registry.Object, specBuilder, agentFactory, logger.Object);
        var exitCode = await command.ExecuteAsync("test-agent", runAsync: false, formatJson: false);
        /// <summary>Assert equal.</summary>
        AssertEqual(0, exitCode);
        registry.Verify(r => r.ExecuteOnceAsync("test-agent", It.IsAny<CancellationToken>()), Times.Once);
    }
}
