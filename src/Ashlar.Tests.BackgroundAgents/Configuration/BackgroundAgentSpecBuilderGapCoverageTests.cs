using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Ashlar.BackgroundAgents.Configuration;
using Ashlar.BackgroundAgents.DataSensitivity;
using Xunit;

namespace Ashlar.Tests.BackgroundAgents.Configuration;

/// <summary>Tests for background agent spec builder gap coverage.</summary>
public class BackgroundAgentSpecBuilderGapCoverageTests
{
    /// <summary>Base config.</summary>
    /// <param name=""monitor"">"monitor".</param>
    private static BackgroundAgentConfig BaseConfig(string role = "monitor") => new()
    {
        Id = "agent-1",
        Name = "Gap Agent",
        Role = role,
        Commands = new List<string> { "inspect", "report" },
        MaxDataSensitivity = "Public",
    };

    [Fact]
    public void Constructor_rejects_null_registry()
    {
        var act = () => new BackgroundAgentSpecBuilder(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void BuildSpec_rejects_null_config()
    {
        var builder = new BackgroundAgentSpecBuilder(new DataSensitivityRegistry());
        var act = () => builder.BuildSpec(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void BuildSpec_rejects_unknown_sensitivity()
    {
        var builder = new BackgroundAgentSpecBuilder(new DataSensitivityRegistry());
        var config = BaseConfig();
        config.MaxDataSensitivity = "DoesNotExist";

        var act = () => builder.BuildSpec(config);
        act.Should().Throw<ArgumentException>().WithMessage("*Unknown sensitivity level*");
    }

    [Theory]
    [InlineData("monitor", "Infrastructure")]
    [InlineData("analyzer", "CodeGeneration")]
    [InlineData("optimizer", "Infrastructure")]
    [InlineData("auditor", "Security")]
    [InlineData("extender", "CodeGeneration")]
    [InlineData("custom-role", "Generic")]
    public void BuildSpec_maps_roles_to_domains(string role, string expectedDomain)
    {
        var builder = new BackgroundAgentSpecBuilder(new DataSensitivityRegistry(), NullLogger<BackgroundAgentSpecBuilder>.Instance);
        var spec = builder.BuildSpec(BaseConfig(role));

        spec.Domain.Should().Be(expectedDomain);
        spec.AgentId.Should().Be("agent-1");
        spec.Goal.Should().Contain(role);
    }

    [Fact]
    public void BuildSpec_includes_parent_dependency_and_enabled_capabilities()
    {
        var builder = new BackgroundAgentSpecBuilder(new DataSensitivityRegistry());
        var config = BaseConfig("monitor");
        config.ParentId = "parent-agent";
        config.RAG = new RAGConfig { Enabled = true, VectorStoreProvider = "in-memory" };
        config.WebSearch = new WebSearchConfig { Enabled = true, SearchProvider = "bing" };

        var spec = builder.BuildSpec(config);

        spec.Dependencies.Should().ContainSingle("parent-agent");
        spec.Description.Should().Contain("RAG: Enabled");
        spec.Description.Should().Contain("Web Search: Enabled");
    }
}
