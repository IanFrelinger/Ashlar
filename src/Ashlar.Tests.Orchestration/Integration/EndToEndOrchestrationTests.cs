using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Ashlar.Abstractions;
using Ashlar.Abstractions.Routing;
using Ashlar.Core.Application.Common.Ports;
using Ashlar.Orchestration.Agents;
using Ashlar.Abstractions.Agents;
using Ashlar.Orchestration.Architect;
using Ashlar.Orchestration.Architect.Models;
using Ashlar.Orchestration.Architect.Parsers;
using Ashlar.Orchestration.Architect.Prompts;
using Ashlar.Orchestration.Communication;
using Ashlar.Orchestration.Communication.Models;
using Ashlar.Orchestration.Validation;
using Xunit;

namespace Ashlar.Tests.Orchestration.Integration;

/// <summary>
/// End-to-end tests for the complete orchestration flow: request → Architect → spawn agents → outputs.
///
/// AgentBus.PublishAsync does not invoke subscribers inline: it schedules each matching
/// handler via a fire-and-forget Task.Run and returns Task.CompletedTask immediately.
/// Handlers therefore run on a thread-pool thread at some later point, so the bus test below
/// synchronizes on an explicit signal completed by the handler rather than sleeping.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Category", "ProdStyle")]
public class EndToEndOrchestrationTests
{
    /// <summary>
    /// Upper bound for waiting on a delivery that is expected to happen. A genuine bug
    /// (handler never invoked) fails fast with a TimeoutException instead of hanging.
    /// </summary>
    private static readonly TimeSpan DeliveryTimeout = TimeSpan.FromSeconds(10);

    private readonly IServiceProvider _serviceProvider;

    public EndToEndOrchestrationTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        _serviceProvider = services.BuildServiceProvider();
    }

    [Fact]
    public async Task EndToEnd_RequestToAgents_CompletesSuccessfully()
    {
        // Arrange
        var request = "Design an extraction shooter game with combat, economy, and AI systems";

        // Mock model that returns a valid decomposition
        var decompositionJson = """
            {
              "agents": [
                {
                  "agentId": "combat-1",
                  "domain": "Combat",
                  "goal": "Design weapon and damage system",
                  "description": "Create weapon mechanics and damage calculations",
                  "dependencies": [],
                  "priority": 0
                },
                {
                  "agentId": "economy-1",
                  "domain": "Economy",
                  "goal": "Design loot and extraction system",
                  "description": "Create loot tables and extraction mechanics",
                  "dependencies": ["combat-1"],
                  "priority": 0
                },
                {
                  "agentId": "ai-1",
                  "domain": "AI",
                  "goal": "Design enemy AI behaviors",
                  "description": "Create AI patrol patterns and combat behaviors",
                  "dependencies": ["combat-1"],
                  "priority": 0
                }
              ],
              "reasoning": "Decomposed into Combat (foundational), Economy and AI (dependent on Combat)",
              "confidence": 0.9
            }
            """;

        var modelMock = new Mock<IModel>();
        modelMock
            .Setup(m => m.CompleteAsync(It.IsAny<ModelInput>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ModelOutput(decompositionJson));

        var cacheMock = new Mock<ICacheStrategy>();
        cacheMock
            .Setup(c => c.GetAsync<List<string>>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((List<string>?)null);

        // Create Architect Agent
        var domainLogger = _serviceProvider.GetRequiredService<ILogger<DomainRecognizer>>();
        var retrieverLogger = _serviceProvider.GetRequiredService<ILogger<DecompositionRetriever>>();
        var domainRecognizer = new DomainRecognizer(domainLogger);
        var retriever = new DecompositionRetriever(cacheMock.Object, domainRecognizer, retrieverLogger);
        var promptBuilder = new DecompositionPromptBuilder();
        var parserLogger = _serviceProvider.GetRequiredService<ILogger<DecompositionJsonParser>>();
        var parser = new DecompositionJsonParser(parserLogger);
        var validators = new List<IValidator>
        {
            new SchemaValidator(_serviceProvider.GetRequiredService<ILogger<SchemaValidator>>()),
            new DependencyAnalyzer(_serviceProvider.GetRequiredService<ILogger<DependencyAnalyzer>>())
        };
        var architectLogger = _serviceProvider.GetRequiredService<ILogger<ArchitectAgent>>();
        var router = new Mock<IEndpointRouter>();
        router
            .Setup(r => r.ResolveAsync(It.IsAny<EndpointRoutingContext>(), It.IsAny<CancellationToken>()))
            .Returns(ValueTask.FromResult<string?>(null));
        var architect = new ArchitectAgent(
            modelMock.Object,
            retriever,
            domainRecognizer,
            validators,
            promptBuilder,
            parser,
            router.Object,
            architectLogger);

        // Create Agent Factory and Lifecycle Manager
        var factoryLogger = _serviceProvider.GetRequiredService<ILogger<AgentFactory>>();
        var factory = new AgentFactory(factoryLogger, _serviceProvider);
        var healthMonitor = new HealthMonitor(_serviceProvider.GetRequiredService<ILogger<HealthMonitor>>());
        var lifecycleLogger = _serviceProvider.GetRequiredService<ILogger<LifecycleManager>>();
        var lifecycleManager = new LifecycleManager(lifecycleLogger, healthMonitor);

        // Act: Step 1 - Decompose request
        var decomposition = await architect.DecomposeAsync(request);

        // Assert: Decomposition is valid
        decomposition.Should().NotBeNull();
        decomposition.Agents.Should().HaveCount(3);
        decomposition.IsValid.Should().BeTrue();

        // Act: Step 2 - Spawn agents
        var containers = decomposition.Agents
            .Select(spec => factory.CreateContainer(spec))
            .ToList();

        foreach (var container in containers)
        {
            await lifecycleManager.RegisterAgentAsync(container);
        }

        // Assert: Agents are registered
        lifecycleManager.GetActiveAgents().Should().HaveCount(3);

        // Act: Step 3 - Execute agents in dependency order
        var outputs = new Dictionary<string, object>();

        // Execute combat agent first (no dependencies)
        var combatContainer = containers.First(c => c.AgentId == "combat-1");
        var combatOutput = await lifecycleManager.ExecuteAgentAsync("combat-1");
        outputs["combat-1"] = combatOutput;

        // Execute economy and AI agents (depend on combat)
        var economyContainer = containers.First(c => c.AgentId == "economy-1");
        var economyOutput = await lifecycleManager.ExecuteAgentAsync("economy-1", outputs);
        outputs["economy-1"] = economyOutput;

        var aiContainer = containers.First(c => c.AgentId == "ai-1");
        var aiOutput = await lifecycleManager.ExecuteAgentAsync("ai-1", outputs);
        outputs["ai-1"] = aiOutput;

        // Assert: All agents completed
        combatContainer.State.Should().Be(AgentState.Completed);
        economyContainer.State.Should().Be(AgentState.Completed);
        aiContainer.State.Should().Be(AgentState.Completed);
        outputs.Should().HaveCount(3);

        // Act: Step 4 - Shutdown
        await lifecycleManager.ShutdownAllAsync();

        // Assert: All agents terminated
        lifecycleManager.GetActiveAgents().Should().BeEmpty();
    }

    [Fact]
    public async Task EndToEnd_WithAgentBus_AgentsCommunicate()
    {
        // Arrange
        var busLogger = _serviceProvider.GetRequiredService<ILogger<AgentBus>>();
        var bus = new AgentBus(busLogger);

        var messageReceived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await bus.SubscribeAsync("OutputEmitted", (msg, ct) =>
        {
            messageReceived.TrySetResult(true);
            return Task.CompletedTask;
        });

        // Create and execute an agent
        var factoryLogger = _serviceProvider.GetRequiredService<ILogger<AgentFactory>>();
        var factory = new AgentFactory(factoryLogger, _serviceProvider);
        var spec = new AgentSpawnSpec
        {
            AgentId = "test-agent",
            Domain = "Combat",
            Goal = "Test agent"
        };

        var container = factory.CreateContainer(spec);
        var healthMonitor = new HealthMonitor(_serviceProvider.GetRequiredService<ILogger<HealthMonitor>>());
        var lifecycleManager = new LifecycleManager(
            _serviceProvider.GetRequiredService<ILogger<LifecycleManager>>(),
            healthMonitor);

        await lifecycleManager.RegisterAgentAsync(container);
        await lifecycleManager.ExecuteAgentAsync("test-agent");

        // Publish output message
        var message = new OutputEmitted
        {
            MessageId = "msg-1",
            FromAgentId = "test-agent",
            MessageType = "OutputEmitted",
            Output = container.Agent.Output ?? new { Result = "No output" }
        };

        await bus.PublishAsync(message);

        // Assert
        // The handler is dispatched on the thread pool after PublishAsync returns, so wait
        // for the handler's own signal instead of sleeping.
        var received = await messageReceived.Task.WaitAsync(DeliveryTimeout);
        received.Should().BeTrue();
    }
}

