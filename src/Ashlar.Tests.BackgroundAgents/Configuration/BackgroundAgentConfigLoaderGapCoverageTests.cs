using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Ashlar.BackgroundAgents.Configuration;
using Ashlar.BackgroundAgents.DataSensitivity;
using System.Reflection;
using System.Text;
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

    // REPLACES LoadAsync_AppliesDefaultExfiltrationPolicyWhenUnset, which was a tripwire: it passed
    // only because it WROTE an ExfiltrationPolicy section with a whitespace MaxAllowedLevel, the one
    // shape that reached the derivation. An agent that genuinely omits the section never did -- the
    // default ExfiltrationPolicy's MaxAllowedLevel is "Public", never blank -- so a TopSecret agent
    // with no section ran with every Block flag false. This one omits the section.
    [Fact]
    public async Task LoadAsync_WithNoExfiltrationPolicySection_DerivesTheRestrictionsItsLevelImplies()
    {
        var values = ValidAgentBase();
        values["BackgroundAgents:Agents:0:Schedule:Type"] = "Interval";
        values["BackgroundAgents:Agents:0:Schedule:Interval"] = "00:05:00";
        values["BackgroundAgents:Agents:0:MaxDataSensitivity"] = "TopSecret";

        // Arrange-step guard: the section really is absent, so the derivation below is the
        // loader's doing and not a key this test wrote.
        values.Keys.Should().NotContain(k => k.Contains("ExfiltrationPolicy"));

        var configs = await CreateLoader(new ConfigurationBuilder().AddInMemoryCollection(values).Build())
            .LoadAsync(default);

        var policy = configs.Should().ContainSingle().Subject.ExfiltrationPolicy;
        policy.MaxAllowedLevel.Should().Be("TopSecret");
        policy.BlockExternalLLMs.Should().BeTrue();
        policy.BlockWebSearch.Should().BeTrue();
        policy.BlockNetworkExports.Should().BeTrue();
        policy.RequireLocalOnly.Should().BeTrue();
    }

    // The derivation follows the level: Confidential blocks external LLMs and network exports but
    // allows web search and is not local-only (DataSensitivityLevels.Confidential), so the result is
    // the level's own flags and not "everything true".
    [Fact]
    public async Task LoadAsync_WithNoExfiltrationPolicySection_DerivesExactlyTheLevelsFlags()
    {
        var values = ValidAgentBase();
        values["BackgroundAgents:Agents:0:Schedule:Type"] = "Interval";
        values["BackgroundAgents:Agents:0:Schedule:Interval"] = "00:05:00";
        values["BackgroundAgents:Agents:0:MaxDataSensitivity"] = "Confidential";

        var configs = await CreateLoader(new ConfigurationBuilder().AddInMemoryCollection(values).Build())
            .LoadAsync(default);

        var policy = configs[0].ExfiltrationPolicy;
        policy.MaxAllowedLevel.Should().Be("Confidential");
        policy.BlockExternalLLMs.Should().BeTrue();
        policy.BlockNetworkExports.Should().BeTrue();
        policy.BlockWebSearch.Should().BeFalse();
        policy.RequireLocalOnly.Should().BeFalse();
    }

    // Each agent is judged on ITS OWN section: of two TopSecret agents, the one that writes a
    // permissive section keeps it, and the one that writes none is derived.
    [Fact]
    public async Task LoadAsync_DetectsTheSectionPerAgent()
    {
        var values = new Dictionary<string, string?>();
        foreach (var (index, id) in new[] { (0, "explicit"), (1, "silent") })
        {
            values[$"BackgroundAgents:Agents:{index}:Id"] = id;
            values[$"BackgroundAgents:Agents:{index}:Name"] = id;
            values[$"BackgroundAgents:Agents:{index}:Role"] = "monitor";
            values[$"BackgroundAgents:Agents:{index}:Commands:0"] = "ping";
            values[$"BackgroundAgents:Agents:{index}:Schedule:Type"] = "Continuous";
            values[$"BackgroundAgents:Agents:{index}:MaxDataSensitivity"] = "TopSecret";
        }

        values["BackgroundAgents:Agents:0:ExfiltrationPolicy:BlockWebSearch"] = "true";
        values["BackgroundAgents:Agents:0:ExfiltrationPolicy:MaxAllowedLevel"] = "TopSecret";

        var configs = await CreateLoader(new ConfigurationBuilder().AddInMemoryCollection(values).Build())
            .LoadAsync(default);

        configs.Select(c => c.Id).Should().Equal("explicit", "silent");
        configs[0].ExfiltrationPolicy.BlockWebSearch.Should().BeTrue();
        configs[0].ExfiltrationPolicy.BlockExternalLLMs.Should().BeFalse("a written section is the operator's choice");
        configs[1].ExfiltrationPolicy.BlockExternalLLMs.Should().BeTrue();
        configs[1].ExfiltrationPolicy.RequireLocalOnly.Should().BeTrue();
    }

    // The old trigger still derives, so no configuration that used to be restricted loses it: a
    // WRITTEN section whose four flags are false and whose MaxAllowedLevel is blank.
    [Fact]
    public async Task LoadAsync_WrittenSectionThatStatesNothing_StillDerives()
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

    // One agent whose configuration the binder cannot convert is LEFT OUT, and the agents next to it
    // still load -- what section.Bind(list) did before the loader bound agents one at a time. The
    // list bind swallowed the binder's exception; Get<T> throws it, so without the loader's catch one
    // mistyped value failed every LoadAsync caller (the daemon, background-agent list/show/execute,
    // ConfigCommand, UpdateAgentConfigTool). The drop is now logged with the element's path, where it
    // used to be silent. The third row is a malformed EXFILTRATION flag on a TopSecret agent: that
    // agent does not run at all, which is the closed outcome.
    [Theory]
    [InlineData("Enabled", "notabool")]
    [InlineData("Schedule:Type", "Weekly")]
    [InlineData("ExfiltrationPolicy:BlockExternalLLMs", "yes")]
    public async Task LoadAsync_AnAgentThatCannotBeBound_IsLeftOutWithAWarning_AndTheOthersLoad(string key, string badValue)
    {
        var values = TwoAgents("bad", "good");
        values["BackgroundAgents:Agents:0:MaxDataSensitivity"] = "TopSecret";
        values[$"BackgroundAgents:Agents:0:{key}"] = badValue;
        var logger = new ListLogger<BackgroundAgentConfigLoader>();

        var configs = await new BackgroundAgentConfigLoader(
                new ConfigurationBuilder().AddInMemoryCollection(values).Build(),
                new DataSensitivityRegistry(),
                logger)
            .LoadAsync(default);

        configs.Select(c => c.Id).Should().Equal("good");
        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning)
            .Which.Message.Should().Contain("BackgroundAgents:Agents:0").And.Contain(badValue);
    }

    // POSITIVE CONTROL for the row above: the same two agents with nothing malformed both load and
    // nothing is logged, so the drop above is the bad value's doing.
    [Fact]
    public async Task LoadAsync_TwoWellFormedAgents_BothLoad_AndNothingIsLogged()
    {
        var logger = new ListLogger<BackgroundAgentConfigLoader>();

        var configs = await new BackgroundAgentConfigLoader(
                new ConfigurationBuilder().AddInMemoryCollection(TwoAgents("bad", "good")).Build(),
                new DataSensitivityRegistry(),
                logger)
            .LoadAsync(default);

        configs.Select(c => c.Id).Should().Equal("bad", "good");
        logger.Entries.Should().BeEmpty();
    }

    // A SCALAR where an agent object belongs binds to nothing; the list bind left it out and the
    // other agents loaded. Still so, and now logged.
    [Theory]
    [InlineData("json-string")]
    [InlineData("memory-value")]
    public async Task LoadAsync_AScalarWhereAnAgentBelongs_IsLeftOutWithAWarning(string shape)
    {
        var logger = new ListLogger<BackgroundAgentConfigLoader>();

        var configs = await new BackgroundAgentConfigLoader(FirstAgentElementThenAGoodOne(shape), new DataSensitivityRegistry(), logger)
            .LoadAsync(default);

        configs.Select(c => c.Id).Should().Equal("good");
        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning)
            .Which.Message.Should().Contain("BackgroundAgents:Agents:0").And.Contain("not-an-agent");
    }

    // An EMPTY element is NOT an element that binds to nothing, measured: section.Bind(list) bound
    // {} or null to a DEFAULT BackgroundAgentConfig, which ValidateConfig refuses, so the whole load
    // failed with "Agent ID is required". Get<T> returns null for it instead; skipping that null --
    // what the per-agent loop first did -- quietly turned the refusal into a pass. Still refused.
    [Theory]
    [InlineData("json-empty-object")]
    [InlineData("json-null")]
    [InlineData("memory-empty")]
    [InlineData("memory-null")]
    public async Task LoadAsync_AnEmptyAgentElement_IsRefused_AsTheListBindRefusedIt(string shape)
    {
        var loader = new BackgroundAgentConfigLoader(FirstAgentElementThenAGoodOne(shape), new DataSensitivityRegistry());

        var act = () => loader.LoadAsync(default);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Agent ID is required");
    }

    private const string GoodAgentJson =
        """{"Id":"good","Name":"good","Role":"monitor","Commands":["ping"],"Schedule":{"Type":"Continuous"},"MaxDataSensitivity":"Public"}""";

    // Agents[0] is the shape under test and Agents[1] a well-formed agent "good".
    private static IConfiguration FirstAgentElementThenAGoodOne(string shape)
    {
        string? json = shape switch
        {
            "json-string" => "\"not-an-agent\"",
            "json-empty-object" => "{}",
            "json-null" => "null",
            _ => null,
        };
        if (json != null)
        {
            var document = "{\"BackgroundAgents\":{\"Agents\":[" + json + "," + GoodAgentJson + "]}}";
            return new ConfigurationBuilder()
                .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(document)))
                .Build();
        }

        var values = TwoAgents("placeholder", "good");
        foreach (var key in values.Keys.Where(k => k.StartsWith("BackgroundAgents:Agents:0:", StringComparison.Ordinal)).ToList())
            values.Remove(key);
        values["BackgroundAgents:Agents:0"] = shape switch
        {
            "memory-value" => "not-an-agent",
            "memory-empty" => "",
            "memory-null" => null,
            _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, "unknown shape"),
        };
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static Dictionary<string, string?> TwoAgents(string first, string second)
    {
        var values = new Dictionary<string, string?>();
        foreach (var (index, id) in new[] { (0, first), (1, second) })
        {
            values[$"BackgroundAgents:Agents:{index}:Id"] = id;
            values[$"BackgroundAgents:Agents:{index}:Name"] = id;
            values[$"BackgroundAgents:Agents:{index}:Role"] = "monitor";
            values[$"BackgroundAgents:Agents:{index}:Commands:0"] = "ping";
            values[$"BackgroundAgents:Agents:{index}:Schedule:Type"] = "Continuous";
            values[$"BackgroundAgents:Agents:{index}:MaxDataSensitivity"] = "Public";
        }

        return values;
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
        private sealed class NullScope : IDisposable { public static readonly NullScope Instance = new(); public void Dispose() { } }
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

        var act = () => process.Invoke(loader, new object[] { config, false });
        act.Should().Throw<TargetInvocationException>()
            .WithInnerException<InvalidOperationException>()
            .WithMessage("*Unknown sensitivity level*");
    }
}
