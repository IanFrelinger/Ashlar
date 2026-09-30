using System.Text.Json;
using FluentAssertions;
using Moq;
using Ashlar.Abstractions;
using Ashlar.BackgroundAgents.Configuration;
using Ashlar.BackgroundAgents.DataSensitivity;
using Ashlar.BackgroundAgents.Registry;
using Ashlar.BackgroundAgents.Security;
using Xunit;

namespace Ashlar.Tests.BackgroundAgents.Security;

/// <summary>Tests for data exfiltration policy.</summary>
public class DataExfiltrationPolicyTests
{
    private static ToolCall ToolCall(string id, object? args = null)
    {
        var json = args != null ? JsonSerializer.SerializeToElement(args) : JsonSerializer.SerializeToElement(new { });
        /// <summary>Tool call.</summary>
        return new ToolCall(id, json);
    }

    private static WorldSnapshot Snapshot(int tick = 0, IReadOnlyDictionary<string, object?>? data = null)
    {
        return new WorldSnapshot(tick, data ?? new Dictionary<string, object?>());
    }

    // INVERTED. This used to be Approve_WhenNoAgentIdInSnapshot_Allows: a caller escaped every
    // exfiltration rule by leaving its id out of the snapshot. An agent the policy cannot identify
    // now gets the MOST restrictive policy.
    [Theory]
    [InlineData("web_search", "web search")]
    [InlineData("complete", "external LLM")]
    [InlineData("export", "network export")]
    public void Approve_WhenNoAgentIdInSnapshot_DeniesEveryExfiltrationTool(string toolId, string reasonFragment)
    {
        var registry = new Mock<IBackgroundAgentRegistry>();
        var sensitivityRegistry = new DataSensitivityRegistry();
        var policy = new DataExfiltrationPolicy(registry.Object, sensitivityRegistry);

        var call = ToolCall(toolId);
        var snapshot = Snapshot(0, new Dictionary<string, object?>());

        policy.Approve(call, snapshot, out var reason).Should().BeFalse();
        reason.Should().Contain(reasonFragment).And.Contain("no agent id");
    }

    // A present-but-unusable id is no id: blank, or not a string at all.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(42)]
    public void Approve_WhenAgentIdIsBlankOrNotAString_DeniesAsIfMissing(object agentId)
    {
        var registry = new Mock<IBackgroundAgentRegistry>();
        var policy = new DataExfiltrationPolicy(registry.Object, new DataSensitivityRegistry());

        var snapshot = Snapshot(0, new Dictionary<string, object?> { ["agentId"] = agentId });

        policy.Approve(ToolCall("web_search"), snapshot, out var reason).Should().BeFalse();
        reason.Should().Contain("no agent id");
    }

    // INVERTED. This used to be Approve_WhenAgentIdNotInRegistry_Allows: a misspelt or unregistered
    // id was waved through with reason "OK".
    [Fact]
    public void Approve_WhenAgentIdNotInRegistry_Denies()
    {
        var registry = new Mock<IBackgroundAgentRegistry>();
        registry.Setup(r => r.GetAgent("agent-1")).Returns((BackgroundAgentInstance?)null);
        var sensitivityRegistry = new DataSensitivityRegistry();
        var policy = new DataExfiltrationPolicy(registry.Object, sensitivityRegistry);

        var call = ToolCall("web_search");
        var snapshot = Snapshot(0, new Dictionary<string, object?> { ["agentId"] = "agent-1" });

        policy.Approve(call, snapshot, out var reason).Should().BeFalse();
        reason.Should().Contain("web search").And.Contain("'agent-1' is not registered");
    }

    // A REGISTERED agent whose config, or whose config's policy, is null has no policy to apply, and
    // gets the most restrictive one -- not the permissive `new ExfiltrationPolicy()` defaults (every
    // Block flag false), which is what an unguarded fallback would hand it. Base code threw a
    // NullReferenceException here; the fallback is new behaviour, so it is pinned per flag.
    [Theory]
    [InlineData(true, "web_search", "web search")]
    [InlineData(false, "web_search", "web search")]
    [InlineData(false, "complete", "external LLM")]
    [InlineData(false, "export", "network export")]
    public void Approve_WhenRegisteredAgentHasNoPolicy_Denies(bool configIsNull, string toolId, string reasonFragment)
    {
        var instance = new BackgroundAgentInstance
        {
            Config = configIsNull
                ? null!
                : new BackgroundAgentConfig { Id = "agent-1", ExfiltrationPolicy = null! },
        };
        var registry = new Mock<IBackgroundAgentRegistry>();
        registry.Setup(r => r.GetAgent("agent-1")).Returns(instance);
        var policy = new DataExfiltrationPolicy(registry.Object, new DataSensitivityRegistry());

        var snapshot = Snapshot(0, new Dictionary<string, object?> { ["agentId"] = "agent-1" });

        policy.Approve(ToolCall(toolId), snapshot, out var reason).Should().BeFalse();
        reason.Should().Contain(reasonFragment).And.Contain("'agent-1' has no exfiltration policy");

        // Control: the same agent is still approved a local tool, so the refusal above is the most
        // restrictive policy applying and not a policy that has stopped approving.
        policy.Approve(ToolCall("repo.fs.read"), snapshot, out var localReason).Should().BeTrue();
        localReason.Should().Be("OK");
    }

    // POSITIVE CONTROL, and the scope of the inversion. This policy only ever refuses the
    // exfiltration-class tool ids; an unidentified agent is held to the most restrictive policy a
    // REGISTERED agent can have, not to "deny everything". A local tool is still approved -- so the
    // denials above are the policy's rules applying, not a policy that has stopped approving.
    [Fact]
    public void Approve_WhenAgentUnidentified_StillApprovesALocalTool()
    {
        var registry = new Mock<IBackgroundAgentRegistry>();
        registry.Setup(r => r.GetAgent(It.IsAny<string>())).Returns((BackgroundAgentInstance?)null);
        var policy = new DataExfiltrationPolicy(registry.Object, new DataSensitivityRegistry());

        policy.Approve(ToolCall("repo.fs.read"), Snapshot(0, new Dictionary<string, object?>()), out var noIdReason)
            .Should().BeTrue();
        noIdReason.Should().Be("OK");
        policy.Approve(ToolCall("repo.fs.read"), Snapshot(0, new Dictionary<string, object?> { ["agentId"] = "ghost" }), out var unknownReason)
            .Should().BeTrue();
        unknownReason.Should().Be("OK");
    }

    [Fact]
    public void Approve_WhenPolicyBlocksWebSearch_Denies()
    {
        var instance = new BackgroundAgentInstance
        {
            Config = new BackgroundAgentConfig
            {
                Id = "agent-1",
                ExfiltrationPolicy = new ExfiltrationPolicy { BlockWebSearch = true }
            }
        };
        var registry = new Mock<IBackgroundAgentRegistry>();
        registry.Setup(r => r.GetAgent("agent-1")).Returns(instance);
        var sensitivityRegistry = new DataSensitivityRegistry();
        var policy = new DataExfiltrationPolicy(registry.Object, sensitivityRegistry);

        var call = ToolCall("web_search");
        var snapshot = Snapshot(0, new Dictionary<string, object?> { ["agentId"] = "agent-1" });

        policy.Approve(call, snapshot, out var reason).Should().BeFalse();
        reason.Should().Contain("web search");
    }

    [Fact]
    public void Approve_WhenPolicyBlocksExternalLLM_Denies()
    {
        var instance = new BackgroundAgentInstance
        {
            Config = new BackgroundAgentConfig
            {
                Id = "agent-1",
                ExfiltrationPolicy = new ExfiltrationPolicy { BlockExternalLLMs = true }
            }
        };
        var registry = new Mock<IBackgroundAgentRegistry>();
        registry.Setup(r => r.GetAgent("agent-1")).Returns(instance);
        var sensitivityRegistry = new DataSensitivityRegistry();
        var policy = new DataExfiltrationPolicy(registry.Object, sensitivityRegistry);

        var call = ToolCall("complete");
        var snapshot = Snapshot(0, new Dictionary<string, object?> { ["agentId"] = "agent-1" });

        policy.Approve(call, snapshot, out var reason).Should().BeFalse();
        reason.Should().Contain("external LLM");
    }

    [Fact]
    public void Approve_WhenPolicyBlocksNetworkExports_Denies()
    {
        var instance = new BackgroundAgentInstance
        {
            Config = new BackgroundAgentConfig
            {
                Id = "agent-1",
                ExfiltrationPolicy = new ExfiltrationPolicy { BlockNetworkExports = true }
            }
        };
        var registry = new Mock<IBackgroundAgentRegistry>();
        registry.Setup(r => r.GetAgent("agent-1")).Returns(instance);
        var sensitivityRegistry = new DataSensitivityRegistry();
        var policy = new DataExfiltrationPolicy(registry.Object, sensitivityRegistry);

        var call = ToolCall("export");
        var snapshot = Snapshot(0, new Dictionary<string, object?> { ["agentId"] = "agent-1" });

        policy.Approve(call, snapshot, out var reason).Should().BeFalse();
        reason.Should().Contain("network export");
    }

    [Fact]
    public void Approve_WhenPolicyAllows_Allows()
    {
        var instance = new BackgroundAgentInstance
        {
            Config = new BackgroundAgentConfig
            {
                Id = "agent-1",
                ExfiltrationPolicy = new ExfiltrationPolicy
                {
                    BlockWebSearch = false,
                    BlockExternalLLMs = false,
                    BlockNetworkExports = false
                }
            }
        };
        var registry = new Mock<IBackgroundAgentRegistry>();
        registry.Setup(r => r.GetAgent("agent-1")).Returns(instance);
        var sensitivityRegistry = new DataSensitivityRegistry();
        var policy = new DataExfiltrationPolicy(registry.Object, sensitivityRegistry);

        var call = ToolCall("web_search");
        var snapshot = Snapshot(0, new Dictionary<string, object?> { ["agentId"] = "agent-1" });

        policy.Approve(call, snapshot, out var reason).Should().BeTrue();
        reason.Should().Be("OK");
    }

    [Fact]
    public void Approve_WhenRequireLocalOnlyAndToolIsWebSearch_Denies()
    {
        var instance = new BackgroundAgentInstance
        {
            Config = new BackgroundAgentConfig
            {
                Id = "agent-1",
                ExfiltrationPolicy = new ExfiltrationPolicy { RequireLocalOnly = true }
            }
        };
        var registry = new Mock<IBackgroundAgentRegistry>();
        registry.Setup(r => r.GetAgent("agent-1")).Returns(instance);
        var sensitivityRegistry = new DataSensitivityRegistry();
        var policy = new DataExfiltrationPolicy(registry.Object, sensitivityRegistry);

        var call = ToolCall("web_search");
        var snapshot = Snapshot(0, new Dictionary<string, object?> { ["agentId"] = "agent-1" });

        policy.Approve(call, snapshot, out var reason).Should().BeFalse();
        reason.Should().Contain("local-only");
    }
}
