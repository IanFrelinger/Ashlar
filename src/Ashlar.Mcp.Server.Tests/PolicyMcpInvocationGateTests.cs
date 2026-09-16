using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Ashlar.Abstractions;
using Xunit;

namespace Ashlar.Mcp.Server.Tests;

public sealed class PolicyMcpInvocationGateTests
{
    private sealed class StubPolicy : IPolicy
    {
        private readonly bool _approve;
        private readonly string _reason;

        public StubPolicy(bool approve, string reason = "nope") => (_approve, _reason) = (approve, reason);

        public bool Approve(ToolCall toolCall, WorldSnapshot s, out string reason)
        {
            reason = _approve ? string.Empty : _reason;
            return _approve;
        }
    }

    private static readonly WorldSnapshot Snapshot = WorldSnapshot.ForRepo("X:/repo");

    private static PolicyMcpInvocationGate Create(params IPolicy[] policies)
        => new(policies, NullLogger<PolicyMcpInvocationGate>.Instance);

    [Fact]
    public void No_registered_policies_allows_the_call()
    {
        var decision = Create().Authorize(new ToolCall("repo.fs.read", Json.Object()), Snapshot);

        decision.Allowed.Should().BeTrue("the allowlist has already constrained reachability");
    }

    [Fact]
    public void All_approving_policies_allow_the_call()
    {
        var gate = Create(new StubPolicy(true), new StubPolicy(true));

        gate.Authorize(new ToolCall("repo.fs.read", Json.Object()), Snapshot).Allowed.Should().BeTrue();
    }

    /// <summary>
    /// No host registers an <see cref="IPolicy"/> in DI, so this gate's set is empty in every
    /// shipped composition. The tools' governance floor bounds WHAT a write may touch; nothing then
    /// bounds WHERE it lands, so the mutating ids fail closed until a write policy is registered.
    /// The read ids keep the permissive behaviour — the shipped host exposes them.
    /// </summary>
    [Fact]
    public void An_empty_policy_set_refuses_a_write_but_still_allows_a_read()
    {
        var gate = Create();

        foreach (var id in new[] { "repo.fs.write", "repo.fs.search_replace", "repo.fs.ensure_file", "docs.update", "repo.git.commit" })
        {
            var decision = gate.Authorize(new ToolCall(id, Json.Object()), Snapshot);
            decision.Allowed.Should().BeFalse("'{0}' mutates the repository and nothing bounds where", id);
            decision.Reason.Should().Contain("no IPolicy is registered");
        }

        foreach (var id in new[] { "repo.fs.read", "repo.fs.list", "dotnet.build" })
        {
            gate.Authorize(new ToolCall(id, Json.Object()), Snapshot).Allowed.Should().BeTrue("'{0}' does not mutate the repository", id);
        }
    }

    [Fact]
    public void A_registered_write_policy_lifts_the_fail_closed_default()
    {
        var gate = Create(new StubPolicy(true));

        gate.Authorize(new ToolCall("repo.fs.write", Json.Object()), Snapshot).Allowed.Should().BeTrue(
            "once a host registers a policy, that policy — not the empty-set guard — decides");
    }

    [Fact]
    public void Any_denying_policy_denies_with_its_reason()
    {
        var gate = Create(new StubPolicy(true), new StubPolicy(false, "write access is not permitted"));

        var decision = gate.Authorize(new ToolCall("repo.fs.write", Json.Object()), Snapshot);

        decision.Allowed.Should().BeFalse();
        decision.Reason.Should().Contain("StubPolicy").And.Contain("write access is not permitted");
    }
}
