using System.Collections.Concurrent;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Ashlar.Abstractions;
using Ashlar.Abstractions.Agents;
using Ashlar.Abstractions.Database;
using Ashlar.Core.Application.Common.Ports;
using Ashlar.Orchestration.Agents;
using Ashlar.Orchestration.Agents.Assets;
using Ashlar.Orchestration.Agents.Planning;
using Ashlar.Orchestration.Agents.Templates;
using Ashlar.Orchestration.Architect;
using Ashlar.Orchestration.Architect.Models;
using Ashlar.Orchestration.Assets.Ports;
using Ashlar.Orchestration.Communication;
using Ashlar.Orchestration.Communication.Models;
using Ashlar.Orchestration.Health;
using Ashlar.Orchestration.Resources;
using Xunit;

namespace Ashlar.Tests.Orchestration;

/// <summary>Tests for orchestration factory and communication.</summary>
public class OrchestrationFactoryAndCommunicationTests
{
    /// <summary>Spec.</summary>
    /// <param name="domain">Domain.</param>
    /// <param name="null">Null.</param>
    private static AgentSpawnSpec Spec(string domain, string? agentId = null) => new()
    {
        AgentId = agentId ?? $"{domain}-1",
        Domain = domain,
        Goal = "Test goal",
    };

    [Fact]
    public void CreateAgent_null_spec_throws()
    {
        var factory = new AgentFactory(NullLogger<AgentFactory>.Instance, BuildServices());
        var act = () => factory.CreateAgent((AgentSpawnSpec)null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Theory]
    [InlineData("planning", typeof(PlanningAgent))]
    [InlineData("Infrastructure", typeof(InfrastructureAgent))]
    [InlineData("Security", typeof(SecurityAgent))]
    [InlineData("unknown-domain", typeof(GenericAgent))]
    public void CreateAgent_routes_domains_to_specialized_agents(string domain, Type expectedType)
    {
        var factory = new AgentFactory(NullLogger<AgentFactory>.Instance, BuildServices());
        var agent = factory.CreateAgent(Spec(domain));
        agent.Should().BeOfType(expectedType);
    }

    [Fact]
    public async Task DecompositionExampleSeeder_populates_cache_and_retriever_finds_matches()
    {
        var cache = new TestCacheStrategy();
        var retriever = new DecompositionRetriever(
            cache,
            new DomainRecognizer(NullLogger<DomainRecognizer>.Instance),
            NullLogger<DecompositionRetriever>.Instance);
        var seeder = new DecompositionExampleSeeder(retriever, NullLogger<DecompositionExampleSeeder>.Instance);

        await seeder.SeedAsync();
        var all = await retriever.GetAllExamplesAsync();
        all.Should().HaveCount(20);

        var similar = await retriever.RetrieveSimilarAsync("extraction shooter combat economy loot");
        similar.Should().NotBeEmpty();
    }

    [Fact]
    public async Task ChannelManager_creates_channel_and_routes_messages()
    {
        AgentMessage? published = null;
        var bus = new Mock<IAgentBus>();
        bus.Setup(b => b.PublishAsync(It.IsAny<AgentMessage>(), It.IsAny<CancellationToken>()))
            .Callback<AgentMessage, CancellationToken>((msg, _) => published = msg)
            .Returns(Task.CompletedTask);

        var manager = new ChannelManager(bus.Object, NullLogger<ChannelManager>.Instance);
        var channelId = manager.CreateChannel("agent-a", "agent-b");
        manager.CreateChannel("agent-b", "agent-a").Should().Be(channelId);
        manager.GetChannelsForAgent("agent-a").Should().Contain(channelId);

        await manager.SendAsync(channelId, new DataRequest
        {
            MessageId = "m1",
            FromAgentId = "agent-a",
            MessageType = "DataRequest",
            RequestedDataType = "schema",
        });

        published!.ToAgentId.Should().Be("agent-b");
    }

    [Fact]
    public async Task AgentHealthCheck_reports_healthy_when_agent_initialized()
    {
        var agent = new GenericAgent(Spec("Health"), NullLogger<GenericAgent>.Instance);
        await agent.InitializeAsync();
        var container = new AgentContainer(agent, NullLogger<AgentContainer>.Instance);
        var check = new AgentHealthCheck(container, NullLogger<AgentHealthCheck>.Instance);

        var result = await check.CheckAsync();
        result.IsHealthy.Should().BeTrue();
    }

    [Fact]
    public async Task OrchestrationResourceScopeExtensions_track_resources()
    {
        var scope = new OrchestrationResourceScope();
        var db = new Mock<IIsolatedDatabase>();
        scope.TrackIsolatedDatabase(db.Object);

        var handle = new Mock<IAgentHandle>();
        scope.TrackAgentHandle(handle.Object, NullLogger<ProvisionedAgentResource>.Instance);

        await scope.DisposeAsync();
    }

    private static IServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Mock.Of<IImageGenerator>());
        services.AddSingleton(Mock.Of<IAudioGenerator>());
        services.AddSingleton(Mock.Of<IModel3DGenerator>());
        services.AddSingleton(Mock.Of<IAssetStorage>());
        services.AddSingleton(Mock.Of<IModel>());
        return services.BuildServiceProvider();
    }

    /// <summary>Test cache strategy.</summary>
    private sealed class TestCacheStrategy : ICacheStrategy
    {
        private readonly ConcurrentDictionary<string, object> _store = new();

        public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default) where T : class
        {
            if (_store.TryGetValue(key, out var value) && value is T typed)
                return Task.FromResult<T?>(typed);
            return Task.FromResult<T?>(null);
        }

        public Task SetAsync<T>(string key, T value, TimeSpan? expiration = null, CancellationToken cancellationToken = default) where T : class
        {
            _store[key] = value;
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
        {
            _store.TryRemove(key, out _);
            return Task.CompletedTask;
        }

        public Task ClearAsync(CancellationToken cancellationToken = default)
        {
            _store.Clear();
            return Task.CompletedTask;
        }
    }
}
