using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Moq;
using Ashlar.BackgroundAgents.Configuration;
using Ashlar.BackgroundAgents.DataSensitivity;
using System.Reflection;
using Xunit;

namespace Ashlar.Tests.BackgroundAgents.Configuration;

/// <summary>Tests for background agent config loader gap coverage.</summary>
public class BackgroundAgentConfigLoaderGapCoverageTests
{
    private static BackgroundAgentConfigLoader CreateLoader(IConfiguration config)
    {
        var sensitivityRegistry = new DataSensitivityRegistry();
        /// <summary>Background agent config loader.</summary>
        return new BackgroundAgentConfigLoader(config, sensitivityRegistry, null);
    }

    /// <summary>Valid agent base.</summary>
    /// <param name=""agent-1"">"agent-1".</param>
    private static Dictionary<string, string?> ValidAgentBase(string id = "agent-1") => new()
    {
        ["BackgroundAgents:Agents:0:Id"] = id,
        ["BackgroundAgents:Agents:0:Name"] = "Test Agent",
        ["BackgroundAgents:Agents:0:Role"] = "monitor",
        ["BackgroundAgents:Agents:0:Commands:0"] = "ping",
        ["BackgroundAgents:Agents:0:MaxDataSensitivity"] = "Public",
    };

    [Fact]
    public void Constructor_rejects_null_dependencies()
    {
        var config = new ConfigurationBuilder().Build();
        var registry = new DataSensitivityRegistry();

        var actConfig = () => new BackgroundAgentConfigLoader(null!, registry);
        var actRegistry = () => new BackgroundAgentConfigLoader(config, null!);

        actConfig.Should().Throw<ArgumentNullException>();
        actRegistry.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task LoadAsync_MissingRole_Throws()
    {
        var values = ValidAgentBase();
        values["BackgroundAgents:Agents:0:Schedule:Type"] = "Interval";
        values["BackgroundAgents:Agents:0:Schedule:Interval"] = "00:01:00";
        values.Remove("BackgroundAgents:Agents:0:Role");

        var loader = CreateLoader(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        var act = () => loader.LoadAsync(default);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*must have a role*");
    }

    [Fact]
    public async Task LoadAsync_MissingCommands_Throws()
    {
        var values = new Dictionary<string, string?>
        {
            ["BackgroundAgents:Agents:0:Id"] = "agent-1",
            ["BackgroundAgents:Agents:0:Name"] = "Test Agent",
            ["BackgroundAgents:Agents:0:Role"] = "monitor",
            ["BackgroundAgents:Agents:0:MaxDataSensitivity"] = "Public",
            ["BackgroundAgents:Agents:0:Schedule:Type"] = "Interval",
            ["BackgroundAgents:Agents:0:Schedule:Interval"] = "00:01:00",
        };

        var loader = CreateLoader(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        var act = () => loader.LoadAsync(default);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*must have at least one command*");
    }

    [Fact]
    public async Task LoadAsync_WebSearchEnabledWithoutProvider_Throws()
    {
        var values = ValidAgentBase();
        values["BackgroundAgents:Agents:0:Schedule:Type"] = "Interval";
        values["BackgroundAgents:Agents:0:Schedule:Interval"] = "00:01:00";
        values["BackgroundAgents:Agents:0:WebSearch:Enabled"] = "true";

        var loader = CreateLoader(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        var act = () => loader.LoadAsync(default);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*web search enabled but no provider*");
    }

    [Fact]
    public async Task LoadAsync_UnknownScheduleType_Throws()
    {
        var values = ValidAgentBase();
        values["BackgroundAgents:Agents:0:Schedule:Type"] = "99";

        var loader = CreateLoader(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        var act = () => loader.LoadAsync(default);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Unknown schedule type*");
    }

    [Fact]
    public async Task LoadAsync_ContinuousSchedule_ReturnsConfig()
    {
        var values = ValidAgentBase();
        values["BackgroundAgents:Agents:0:Schedule:Type"] = "Continuous";

        var configs = await CreateLoader(new ConfigurationBuilder().AddInMemoryCollection(values).Build())
            .LoadAsync(default);

        configs.Should().ContainSingle();
        configs[0].Schedule.Type.Should().Be(ScheduleType.Continuous);
    }

    [Fact]
    public async Task LoadAsync_ValidCronSchedule_ReturnsConfig()
    {
        var values = ValidAgentBase();
        values["BackgroundAgents:Agents:0:Schedule:Type"] = "Cron";
        values["BackgroundAgents:Agents:0:Schedule:CronExpression"] = "0 */6 * * *";

        var configs = await CreateLoader(new ConfigurationBuilder().AddInMemoryCollection(values).Build())
            .LoadAsync(default);

        configs.Should().ContainSingle();
        configs[0].Schedule.Type.Should().Be(ScheduleType.Cron);
    }

    [Fact]
    public async Task LoadAsync_AppliesDefaultExfiltrationPolicyWhenUnset()
    {
        var values = ValidAgentBase();
        values["BackgroundAgents:Agents:0:Schedule:Type"] = "Interval";
        values["BackgroundAgents:Agents:0:Schedule:Interval"] = "00:05:00";
        values["BackgroundAgents:Agents:0:MaxDataSensitivity"] = "Confidential";
        values["BackgroundAgents:Agents:0:ExfiltrationPolicy:BlockExternalLLMs"] = "false";
        values["BackgroundAgents:Agents:0:ExfiltrationPolicy:BlockWebSearch"] = "false";
        values["BackgroundAgents:Agents:0:ExfiltrationPolicy:BlockNetworkExports"] = "false";
        values["BackgroundAgents:Agents:0:ExfiltrationPolicy:RequireLocalOnly"] = "false";
        values["BackgroundAgents:Agents:0:ExfiltrationPolicy:MaxAllowedLevel"] = "   ";

        var configs = await CreateLoader(new ConfigurationBuilder().AddInMemoryCollection(values).Build())
            .LoadAsync(default);

        configs[0].ExfiltrationPolicy.MaxAllowedLevel.Should().Be("Confidential");
        configs[0].ExfiltrationPolicy.BlockExternalLLMs.Should().BeTrue();
        configs[0].ExfiltrationPolicy.BlockNetworkExports.Should().BeTrue();
    }

    [Fact]
    public async Task LoadAsync_PreservesExplicitExfiltrationPolicy()
    {
        var values = ValidAgentBase();
        values["BackgroundAgents:Agents:0:Schedule:Type"] = "Interval";
        values["BackgroundAgents:Agents:0:Schedule:Interval"] = "00:05:00";
        values["BackgroundAgents:Agents:0:ExfiltrationPolicy:BlockExternalLLMs"] = "true";
        values["BackgroundAgents:Agents:0:ExfiltrationPolicy:MaxAllowedLevel"] = "Confidential";

        var configs = await CreateLoader(new ConfigurationBuilder().AddInMemoryCollection(values).Build())
            .LoadAsync(default);

        configs[0].ExfiltrationPolicy.BlockExternalLLMs.Should().BeTrue();
        configs[0].ExfiltrationPolicy.MaxAllowedLevel.Should().Be("Confidential");
    }

    [Fact]
    public async Task LoadAsync_RegistersCustomSensitivityLevels()
    {
        var values = ValidAgentBase("custom-agent");
        values["BackgroundAgents:Agents:0:Schedule:Type"] = "Interval";
        values["BackgroundAgents:Agents:0:Schedule:Interval"] = "00:05:00";
        values["BackgroundAgents:Agents:0:CustomSensitivityLevels:PartnerSafe:Name"] = "PartnerSafe";
        values["BackgroundAgents:Agents:0:CustomSensitivityLevels:PartnerSafe:SensitivityValue"] = "25";
        values["BackgroundAgents:Agents:0:CustomSensitivityLevels:PartnerSafe:AllowsExternalLLM"] = "false";
        values["BackgroundAgents:Agents:0:CustomSensitivityLevels:PartnerSafe:AllowsWebSearch"] = "true";
        values["BackgroundAgents:Agents:0:CustomSensitivityLevels:PartnerSafe:RequiresLocalOnly"] = "false";
        values["BackgroundAgents:Agents:0:CustomSensitivityLevels:PartnerSafe:AllowsNetworkExports"] = "false";
        values["BackgroundAgents:Agents:0:CustomSensitivityLevels:PartnerSafe:Description"] = "Partner safe data";

        var registry = new DataSensitivityRegistry();
        var loader = new BackgroundAgentConfigLoader(
            new ConfigurationBuilder().AddInMemoryCollection(values).Build(),
            registry);

        var configs = await loader.LoadAsync(default);

        configs.Should().ContainSingle();
        registry.GetByName("PartnerSafe").Should().NotBeNull();
    }

    [Fact]
    public async Task LoadAsync_AllowsRAGAndWebSearchWhenProvidersConfigured()
    {
        var values = ValidAgentBase();
        values["BackgroundAgents:Agents:0:Schedule:Type"] = "Interval";
        values["BackgroundAgents:Agents:0:Schedule:Interval"] = "00:05:00";
        values["BackgroundAgents:Agents:0:RAG:Enabled"] = "true";
        values["BackgroundAgents:Agents:0:RAG:VectorStoreProvider"] = "in-memory";
        values["BackgroundAgents:Agents:0:WebSearch:Enabled"] = "true";
        values["BackgroundAgents:Agents:0:WebSearch:SearchProvider"] = "bing";

        var configs = await CreateLoader(new ConfigurationBuilder().AddInMemoryCollection(values).Build())
            .LoadAsync(default);

        configs.Should().ContainSingle();
        configs[0].RAG!.Enabled.Should().BeTrue();
        configs[0].WebSearch!.Enabled.Should().BeTrue();
    }

    [Fact]
    public async Task LoadAsync_RejectsNullCommands_via_reflection()
    {
        var values = ValidAgentBase();
        values["BackgroundAgents:Agents:0:Schedule:Type"] = "Interval";
        values["BackgroundAgents:Agents:0:Schedule:Interval"] = "00:05:00";

        var loader = CreateLoader(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        var configs = await loader.LoadAsync(default);
        configs[0].Commands = null!;

        var validate = typeof(BackgroundAgentConfigLoader).GetMethod(
            "ValidateConfig",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

        var act = () => validate.Invoke(loader, new object[] { configs[0] });
        act.Should().Throw<TargetInvocationException>()
            .WithInnerException<InvalidOperationException>()
            .WithMessage("*must have at least one command*");
    }

    [Fact]
    public void ProcessConfig_throws_when_sensitivity_level_disappears()
    {
        var registry = new Mock<IDataSensitivityRegistry>();
        registry.Setup(r => r.GetByName("Public")).Returns((IDataSensitivityLevel?)null);
        var loader = new BackgroundAgentConfigLoader(
            new ConfigurationBuilder().Build(),
            registry.Object);

        var config = new BackgroundAgentConfig
        {
            Id = "agent-1",
            Name = "Test",
            Role = "monitor",
            Commands = new List<string> { "ping" },
            MaxDataSensitivity = "Public",
        };

        var process = typeof(BackgroundAgentConfigLoader).GetMethod(
            "ProcessConfig",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        var act = () => process.Invoke(loader, new object[] { config });
        act.Should().Throw<TargetInvocationException>()
            .WithInnerException<InvalidOperationException>()
            .WithMessage("*Unknown sensitivity level*");
    }
}
