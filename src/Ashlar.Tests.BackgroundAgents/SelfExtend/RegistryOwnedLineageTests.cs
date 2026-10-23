using Ashlar.BackgroundAgents.Agents;
using Ashlar.BackgroundAgents.Configuration;
using Ashlar.BackgroundAgents.Extending;
using Ashlar.BackgroundAgents.Registry;
using Ashlar.Tests.BackgroundAgents.Registry;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Ashlar.Tests.BackgroundAgents.SelfExtend;

public sealed class RegistryOwnedLineageTests
{
    [Theory(Timeout = 10000)]
    [InlineData("self")]
    [InlineData("ancestor")]
    [InlineData("identity")]
    public async Task Mutating_published_configuration_cannot_restore_descendant_authority(string mutation)
    {
        await Task.Yield();
        var (registry, runner) = Create();
        var root = Config("root");
        var child = Config("child", "root");
        var grandchild = Config("grandchild", "child");
        await Register(registry, root);
        await Register(registry, child);
        await Register(registry, grandchild);
        if (mutation == "ancestor") child.ParentId = null;
        else grandchild.ParentId = null;
        if (mutation == "identity") grandchild.Id = "root";

        await registry.ExecuteOnceAsync("grandchild");
        runner.CallCount.Should().Be(0, "registered lineage cannot be shortened through mutable configuration");
        registry.GetExtensionLedger("grandchild").UnattendedCycles.Should().Be(0);
        await registry.ExecuteOnceAsync("root");
        await registry.ExecuteOnceAsync("child");
        runner.CallCount.Should().Be(2, "valid root and child remain executable");
    }

    [Fact(Timeout = 10000)]
    public async Task Re_registering_a_descendant_under_a_shallower_parent_cannot_reset_its_depth()
    {
        await Task.Yield();
        var (registry, runner) = Create();
        await Register(registry, Config("root"));
        await Register(registry, Config("child", "root"));
        await Register(registry, Config("grandchild", "child"));
        var replacement = Config("grandchild", "root");
        await registry.RegisterAsync(Agent(replacement), replacement, AgentRegistrationOrigin.Machine);
        await registry.ExecuteOnceAsync("grandchild");
        runner.CallCount.Should().Be(0);
        registry.GetExtensionLedger("grandchild").UnattendedCycles.Should().Be(0);
    }

    [Fact(Timeout = 10000)]
    public async Task Registering_a_previously_unknown_parent_updates_existing_descendants()
    {
        await Task.Yield();
        var (registry, runner) = Create();
        await Register(registry, Config("root"));
        await Register(registry, Config("child", "parent"));
        await registry.ExecuteOnceAsync("child");
        runner.CallCount.Should().Be(1, "an unknown parent counts as one hop");
        await Register(registry, Config("parent", "root"));
        await registry.ExecuteOnceAsync("child");
        runner.CallCount.Should().Be(1, "the now-known second hop must refuse the next cycle");
    }

    [Fact(Timeout = 10000)]
    public async Task Re_registering_an_ancestor_cannot_shorten_existing_or_future_descendants()
    {
        await Task.Yield();
        var (registry, runner) = Create();
        await Register(registry, Config("root"));
        await Register(registry, Config("child", "root"));
        await Register(registry, Config("grandchild", "child"));
        await Register(registry, Config("child"));
        await Register(registry, Config("new-grandchild", "child"));
        await registry.ExecuteOnceAsync("grandchild");
        await registry.ExecuteOnceAsync("new-grandchild");
        runner.CallCount.Should().Be(0);
    }

    [Fact(Timeout = 10000)]
    public async Task Concurrent_replacements_cannot_lower_depth_and_cycles_terminate()
    {
        await Task.Yield();
        var (registry, runner) = Create();
        await Register(registry, Config("root"));
        await Register(registry, Config("child", "root"));
        await Task.WhenAll(
            Task.Run(() => Register(registry, Config("descendant", "root"))),
            Task.Run(() => Register(registry, Config("descendant", "child"))));
        await registry.ExecuteOnceAsync("descendant");
        runner.CallCount.Should().Be(0);
        await Register(registry, Config("a", "b"));
        await Register(registry, Config("b", "a"));
        await registry.ExecuteOnceAsync("a");
        await registry.ExecuteOnceAsync("b");
        runner.CallCount.Should().Be(0);
    }

    private static (BackgroundAgentRegistry, SelfExtendAuditTestSupport.CountingSelfExtendRunner) Create()
    {
        var runner = new SelfExtendAuditTestSupport.CountingSelfExtendRunner();
        return (SelfExtendAuditTestSupport.CreateRegistry(
            selfExtendRunner: runner,
            modeStore: SelfExtendAuditTestSupport.ActiveModeStore(),
            extensionCeiling: new ExtensionCeiling(1, 100, 100)), runner);
    }

    private static BackgroundAgentConfig Config(string id, string? parent = null)
    {
        var config = SelfExtendAuditTestSupport.ExtenderConfig(id, Environment.CurrentDirectory);
        config.ParentId = parent;
        return config;
    }

    private static GenericAgent Agent(BackgroundAgentConfig config) =>
        new(SelfExtendAuditTestSupport.BuildSpec(config), NullLogger<GenericAgent>.Instance);

    private static Task Register(BackgroundAgentRegistry registry, BackgroundAgentConfig config) =>
        registry.RegisterAuthoredAsync(Agent(config), config);
}
