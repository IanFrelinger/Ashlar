using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Ashlar.Abstractions;
using Ashlar.BackgroundAgents.Configuration;
using Ashlar.BackgroundAgents.DataSensitivity;
using Ashlar.BackgroundAgents.Extending;
using Ashlar.BackgroundAgents.Registry;
using Ashlar.BackgroundAgents.Scheduling;
using Ashlar.Orchestration.Agents;
using Xunit;
using Ashlar.Tests.BackgroundAgents.Registry;
using Ashlar.Tests.BackgroundAgents.TestHelpers;

namespace Ashlar.Tests.BackgroundAgents.Registry;

/// <summary>
/// Regression tests for the parameter passthrough between
/// <see cref="BackgroundAgentRegistry"/> and <see cref="ISelfExtendRunner"/>.
///
/// Previously, the registry called the single-arg <c>RunAsync(repoRoot, ct)</c>
/// overload, silently dropping the agent's <c>Objective</c>, <c>AgentName</c>,
/// <c>ModelProvider</c>, and <c>ModelName</c>. That caused planner agents to
/// fall back to the deterministic echo model and rationally produce zero actions.
/// </summary>
public sealed class SelfExtendParameterFlowTests
{
    [Fact]
    public async Task Extender_role_passes_full_config_quad_to_runner()
    {
        var fakeRunner = new RecordingSelfExtendRunner();
        var registry = BuildRegistry(fakeRunner);

        var config = new BackgroundAgentConfig
        {
            Id = "planner-1",
            Name = "Planner One",
            Role = "extender",
            Enabled = true,
            ModelProvider = "ollama",
            ModelName = "llama3.1:latest",
            Parameters = new Dictionary<string, object>
            {
                ["RepoRoot"] = "C:/some/repo",
                ["Objective"] = "Improve telemetry coverage",
                ["AgentName"] = "runtime-planner"
            }
        };

        var agent = new GenericAgent(BuildSpec(config), NullLogger<GenericAgent>.Instance);
        await registry.RegisterAuthoredAsync(agent, config);

        await registry.ExecuteOnceAsync(config.Id);

        fakeRunner.Calls.Should().ContainSingle();
        var call = fakeRunner.Calls[0];
        call.RepoRoot.Should().Be("C:/some/repo");
        call.Objective.Should().Be("Improve telemetry coverage");
        call.AgentName.Should().Be("runtime-planner");
        call.ModelProvider.Should().Be("ollama");
        call.ModelName.Should().Be("llama3.1:latest");
        call.AgentId.Should().Be("planner-1");
    }

    [Fact]
    public async Task Extender_role_falls_back_to_agent_id_when_AgentName_missing()
    {
        var fakeRunner = new RecordingSelfExtendRunner();
        var registry = BuildRegistry(fakeRunner);

        var config = new BackgroundAgentConfig
        {
            Id = "planner-2",
            Role = "extender",
            Enabled = true,
            ModelProvider = "ollama",
            ModelName = "llama3.1:latest",
            Parameters = new Dictionary<string, object>
            {
                ["RepoRoot"] = "C:/some/repo"
            }
        };

        var agent = new GenericAgent(BuildSpec(config), NullLogger<GenericAgent>.Instance);
        await registry.RegisterAuthoredAsync(agent, config);
        await registry.ExecuteOnceAsync(config.Id);

        var call = fakeRunner.Calls.Single();
        call.AgentName.Should().Be("planner-2");
        call.AgentId.Should().Be("planner-2");
        call.Objective.Should().BeNull();
    }

    [Fact]
    public async Task Whitespace_only_provider_and_model_become_null_routing()
    {
        var fakeRunner = new RecordingSelfExtendRunner();
        var registry = BuildRegistry(fakeRunner);

        var config = new BackgroundAgentConfig
        {
            Id = "planner-3",
            Role = "extender",
            Enabled = true,
            ModelProvider = "   ",
            ModelName = "  ",
            Parameters = new Dictionary<string, object>
            {
                ["RepoRoot"] = "C:/some/repo",
                ["Objective"] = "noop"
            }
        };
        var agent = new GenericAgent(BuildSpec(config), NullLogger<GenericAgent>.Instance);
        await registry.RegisterAuthoredAsync(agent, config);
        await registry.ExecuteOnceAsync(config.Id);

        var call = fakeRunner.Calls.Single();
        call.ModelProvider.Should().BeNull();
        call.ModelName.Should().BeNull();
    }

    private static IBackgroundAgentRegistry BuildRegistry(ISelfExtendRunner runner)
    {
        var modeStore = new InMemoryAggressivenessModeStore();
        modeStore.SetMode(BackgroundAgentAggressivenessMode.Active);
        var scheduler = new AgentScheduler(new ScheduleExecutor(), NullLogger<AgentScheduler>.Instance);
        return new BackgroundAgentRegistry(
            scheduler,
            NullLogger<BackgroundAgentRegistry>.Instance,
            logStore: null,
            codeAnalysisRunner: null,
            testRunRunner: null,
            selfExtendRunner: runner,
            selfImprovementLoop: null,
            modeStore: modeStore,
            sensitivityRegistry: new DataSensitivityRegistry());
    }

    private static Orchestration.Architect.Models.AgentSpawnSpec BuildSpec(BackgroundAgentConfig c)
    {
        var sensitivity = new DataSensitivityRegistry();
        var builder = new BackgroundAgentSpecBuilder(sensitivity, null);
        return builder.BuildSpec(c).ToOrchestrationSpec();
    }

    private sealed class RecordingSelfExtendRunner : ISelfExtendRunner
    {
        public List<RecordedCall> Calls { get; } = new();

        public Task<SelfExtendRunResult> RunAsync(string repoRoot, CancellationToken cancellationToken = default)
        {
            Calls.Add(new RecordedCall(repoRoot, null, null, null, null));
            return Task.FromResult(new SelfExtendRunResult(true, 0, 0, "noop"));
        }

        public Task<SelfExtendRunResult> RunAsync(
            string repoRoot,
            string? objective,
            string? agentName,
            CancellationToken cancellationToken = default)
        {
            // The registry should never reach this 3-arg overload either; it must call the 5-arg one.
            Calls.Add(new RecordedCall(repoRoot, objective, agentName, null, null));
            return Task.FromResult(new SelfExtendRunResult(true, 0, 0, "noop"));
        }

        public Task<SelfExtendRunResult> RunAsync(
            string repoRoot,
            string? objective,
            string? agentName,
            string? modelProvider,
            string? modelName,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(new RecordedCall(repoRoot, objective, agentName, modelProvider, modelName));
            return Task.FromResult(new SelfExtendRunResult(true, 0, 0, "noop"));
        }

        public Task<SelfExtendRunResult> RunAsync(
            string repoRoot,
            string? objective,
            string? agentName,
            string? modelProvider,
            string? modelName,
            string? agentId,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(new RecordedCall(repoRoot, objective, agentName, modelProvider, modelName, agentId));
            return Task.FromResult(new SelfExtendRunResult(true, 0, 0, "noop"));
        }

        public sealed record RecordedCall(
            string RepoRoot,
            string? Objective,
            string? AgentName,
            string? ModelProvider,
            string? ModelName,
            string? AgentId = null);
    }
}
