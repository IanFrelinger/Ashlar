using Ashlar.Abstractions;
using Ashlar.BackgroundAgents;
using Ashlar.BackgroundAgents.Configuration;
using Ashlar.BackgroundAgents.DataSensitivity;
using Ashlar.BackgroundAgents.RAG;
using Ashlar.BackgroundAgents.Registry;
using Ashlar.BackgroundAgents.Scheduling;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>Provider configuration must describe storage the built-in compositions can honor.</summary>
public class RagProviderContractTests
{
    public static TheoryData<string?> UnsupportedProviders => new()
    {
        null, "", "   ", "sqlite", "postgres", "qdrant", "in-memroy"
    };

    [Theory]
    [MemberData(nameof(UnsupportedProviders))]
    public async Task Loader_refuses_missing_or_unsupported_enabled_provider(string? provider)
    {
        var loader = CreateLoader(enabled: true, provider);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => loader.LoadAsync());
        AssertProviderError(error, provider);
    }

    [Theory]
    [MemberData(nameof(UnsupportedProviders))]
    public void Spec_builder_cannot_bypass_provider_validation(string? provider)
    {
        var builder = new BackgroundAgentSpecBuilder(new DataSensitivityRegistry());
        var error = Assert.Throws<InvalidOperationException>(() => builder.BuildSpec(CreateConfig(provider)));
        AssertProviderError(error, provider);
    }

    [Theory]
    [MemberData(nameof(UnsupportedProviders))]
    public async Task Direct_registration_refuses_provider_before_replacing_a_valid_agent(string? provider)
    {
        var scheduler = new Mock<IAgentScheduler>(MockBehavior.Strict);
        var registry = new BackgroundAgentRegistry(scheduler.Object);
        var accepted = CreateConfig("in-memory");
        var existingAgent = Mock.Of<IAgent>();
        await registry.RegisterAsync(existingAgent, accepted, AgentRegistrationOrigin.Authored);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            registry.RegisterAsync(Mock.Of<IAgent>(), CreateConfig(provider), AgentRegistrationOrigin.Authored));

        AssertProviderError(error, provider);
        registry.GetAll().Should().ContainSingle();
        registry.GetAgent(accepted.Id)!.Agent.Should().BeSameAs(existingAgent);
        registry.GetAgent(accepted.Id)!.Config.Should().BeSameAs(accepted);
        scheduler.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("in-memory")]
    [InlineData("IN-MEMORY")]
    [InlineData("  in-memory  ")]
    public async Task Supported_provider_survives_loading_spec_building_and_registration(string provider)
    {
        var configs = await CreateLoader(enabled: true, provider).LoadAsync();
        var config = configs.Should().ContainSingle().Subject;
        var spec = new BackgroundAgentSpecBuilder(new DataSensitivityRegistry()).BuildSpec(config);
        var registry = new BackgroundAgentRegistry(Mock.Of<IAgentScheduler>());
        await registry.RegisterAsync(Mock.Of<IAgent>(), config, AgentRegistrationOrigin.Authored);

        config.RAG!.VectorStoreProvider.Should().Be(provider);
        spec.Description.Should().Contain("RAG: Enabled");
        registry.GetAll().Should().ContainSingle();
        registry.GetAgent(config.Id)!.Config.Should().BeSameAs(config);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(false, null)]
    [InlineData(false, "sqlite")]
    public async Task Absent_or_disabled_RAG_needs_no_supported_provider(bool? enabled, string? provider)
    {
        var configs = await CreateLoader(enabled, provider).LoadAsync();
        var config = configs.Should().ContainSingle().Subject;
        var spec = new BackgroundAgentSpecBuilder(new DataSensitivityRegistry()).BuildSpec(config);
        var registry = new BackgroundAgentRegistry(Mock.Of<IAgentScheduler>());
        await registry.RegisterAsync(Mock.Of<IAgent>(), config, AgentRegistrationOrigin.Authored);

        spec.Description.Should().Contain("RAG: Disabled");
        registry.GetAll().Should().ContainSingle();
        (config.RAG?.Enabled == true).Should().BeFalse();
    }

    [Fact]
    public async Task Default_RAG_registration_indexes_and_searches_in_process_memory()
    {
        using var services = new ServiceCollection().AddBackgroundAgentsRAG().BuildServiceProvider();
        var rag = services.GetRequiredService<IRAGService>();
        services.GetRequiredService<IVectorStore>().Should().BeOfType<InMemoryVectorStore>();
        await rag.IndexAsync("provider-control", "searchable provider control", "Public");
        (await rag.GetDocumentCountAsync()).Should().Be(1);
        var result = (await rag.SearchAsync("searchable provider control", 1, 0.9, "Public"))
            .Should().ContainSingle().Subject;
        result.Id.Should().Be("provider-control");
        result.Text.Should().Be("searchable provider control");
        result.Score.Should().BeApproximately(1, 0.00001);

        // A new host has a new index. This is an explicit ephemeral-storage control,
        // not a claim that per-agent configuration selects or isolates these stores.
        using var freshServices = new ServiceCollection().AddBackgroundAgentsRAG().BuildServiceProvider();
        (await freshServices.GetRequiredService<IRAGService>().GetDocumentCountAsync()).Should().Be(0);
    }

    private static void AssertProviderError(InvalidOperationException error, string? provider)
    {
        error.Message.Should().Contain("provider-agent");
        if (string.IsNullOrWhiteSpace(provider))
            error.Message.Should().Contain("RAG enabled but no provider specified");
        else
        {
            error.Message.Should().Contain($"'{provider}' is unsupported");
            error.Message.Should().Contain("Only 'in-memory'");
        }
    }

    private static BackgroundAgentConfig CreateConfig(string? provider) => new()
    {
        Id = "provider-agent",
        Name = "Provider control",
        Role = "monitor",
        Commands = new List<string> { "inspect" },
        Schedule = new BackgroundAgentSchedule { Type = ScheduleType.Continuous },
        RAG = new RAGConfig { Enabled = true, VectorStoreProvider = provider }
    };

    private static BackgroundAgentConfigLoader CreateLoader(bool? enabled, string? provider)
    {
        var values = new Dictionary<string, string?>
        {
            ["BackgroundAgents:Agents:0:Id"] = "provider-agent",
            ["BackgroundAgents:Agents:0:Name"] = "Provider control",
            ["BackgroundAgents:Agents:0:Role"] = "monitor",
            ["BackgroundAgents:Agents:0:Commands:0"] = "inspect",
            ["BackgroundAgents:Agents:0:Schedule:Type"] = "Continuous"
        };
        if (enabled.HasValue)
        {
            values["BackgroundAgents:Agents:0:RAG:Enabled"] = enabled.Value.ToString();
            values["BackgroundAgents:Agents:0:RAG:VectorStoreProvider"] = provider;
        }

        return new BackgroundAgentConfigLoader(
            new ConfigurationBuilder().AddInMemoryCollection(values).Build(), new DataSensitivityRegistry());
    }
}
