using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Ashlar.Abstractions;
using Ashlar.BackgroundAgents.HostRunners;
using Ashlar.Policies.Dev;
using Ashlar.Runtime;
using Ashlar.Tests.Infrastructure.Helpers;
using Ashlar.Tools.Dev;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>
/// The governance floor as the self-extend cycle meets it: through the policy chain
/// <c>RepoFsToolboxFactory</c> composes, and then under the one configuration the sandbox guide
/// used to prescribe. <see cref="ToolEdgeGovernanceFloorTests"/> pins the tool edge; this pins
/// that the chain refuses the same writes as DENIALS (the shape the admission bridge counts) and
/// that <c>ASHLAR_PATH_ALLOWLIST_EXTRA</c> cannot widen the chain back onto governance.
///
/// <para>Mutates a process-global environment variable, so it joins the non-parallel
/// <c>EnvironmentVariables</c> collection (<c>ProcessGlobalEnvironmentConventionTests</c>).</para>
/// </summary>
[Trait("Category", "Certification")]
[Collection("EnvironmentVariables")]
public sealed class SelfExtendGovernanceFloorTests : IDisposable
{
    private const string ExtraVar = "ASHLAR_PATH_ALLOWLIST_EXTRA";
    private readonly string _root;

    public SelfExtendGovernanceFloorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ashlar-sx-floor-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private WorldSnapshot Snapshot() => WorldSnapshot.ForRepo(_root);

    private static ToolCall WriteCall(string path) =>
        new("repo.fs.write", JsonSerializer.SerializeToElement(new { path, content = "{}" }));

    /// <summary>Reflection twin of the BackgroundAgents test support helper: the engine hides its list.</summary>
    private static IReadOnlyList<IPolicy> PoliciesOf(PolicyEngine engine) =>
        (IReadOnlyList<IPolicy>)typeof(PolicyEngine)
            .GetField("_policies", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(engine)!;

    [Fact]
    public void The_default_policy_chain_denies_a_governance_write_and_still_admits_an_authoring_one()
    {
        using var unset = EnvironmentVariableScope.Unset(ExtraVar);
        var (_, policies, _) = RepoFsToolboxFactory.CreateWithBuildTest();

        policies.Approve(WriteCall(".ashlar/gates/ext-1.json"), Snapshot(), out var ledger).Should().BeFalse();
        ledger.Should().NotBeNullOrEmpty();
        policies.Approve(WriteCall("src/.editorconfig"), Snapshot(), out var tooling).Should().BeFalse(
            "src/ is on the allowlist, so only the floor policy can refuse this");
        tooling.Should().Contain("Governance floor");
        policies.Approve(WriteCall("scripts/run.sh"), Snapshot(), out _).Should().BeFalse();

        policies.Approve(WriteCall("docs/notes.md"), Snapshot(), out var docs).Should().BeTrue();
        docs.Should().Be("OK");
        policies.Approve(WriteCall("docs/UiDomainDemoGenerated/host/UiDemoHost.csproj"), Snapshot(), out _)
            .Should().BeTrue("a project file is authorable — the offline demo scaffolds four of them");
    }

    /// <summary>
    /// <c>PolicyEngine.Approve</c> returns on the first refusal, so order is observable in denial
    /// reasons: <c>SelfExtendInvariantACertGateTests</c> asserts the certification policy's reason
    /// for an uncertified brick. Last position is what keeps every existing reason where it was.
    /// </summary>
    [Fact]
    public void The_floor_policy_is_last_in_both_chains_so_no_existing_denial_reason_moves()
    {
        var (_, minimal) = RepoFsToolboxFactory.CreateMinimal();
        var (_, full, _) = RepoFsToolboxFactory.CreateWithBuildTest();

        PoliciesOf(minimal).Last().Should().BeOfType<GovernanceFloorPolicy>();
        PoliciesOf(full).Last().Should().BeOfType<GovernanceFloorPolicy>();
    }

    /// <summary>
    /// Removing <c>.ashlar/</c> from the defaults closed nothing on its own: the constructor merges
    /// <c>ASHLAR_PATH_ALLOWLIST_EXTRA</c> unconditionally, and the sandbox guide prescribed exactly
    /// this value. The extras filter is what makes the documented hardening variable structurally
    /// unable to reach governance — and the tool edge never read the variable at all.
    /// </summary>
    [Fact]
    public async Task And_not_even_with_the_documented_hardening_env_set()
    {
        using var hardening = new EnvironmentVariableScope(ExtraVar, ".ashlar/host_apps/,.ashlar/agents/workspaces/");

        var allowlist = new PathAllowlist();
        allowlist.RejectedExtras.Should().BeEquivalentTo(new[] { ".ashlar/host_apps/", ".ashlar/agents/workspaces/" },
            "a host must be able to see what its configuration asked for and did not get");
        allowlist.Approve(WriteCall(".ashlar/host_apps/projects/a.cs"), Snapshot(), out var hostApps).Should().BeFalse();
        hostApps.Should().Contain("Path not allowed");
        allowlist.Approve(WriteCall(".ashlar/agents/workspaces/w/x.cs"), Snapshot(), out _).Should().BeFalse();

        var (_, policies, _) = RepoFsToolboxFactory.CreateWithBuildTest();
        policies.Approve(WriteCall(".ashlar/host_apps/projects/a.cs"), Snapshot(), out _).Should().BeFalse(
            "the chain the runner composes reads the same variable");

        var result = await new RepoFsWriteTool().InvokeAsync(
            WriteCall(".ashlar/host_apps/projects/a.cs"), Snapshot(), CancellationToken.None);
        result.Delta.Log.Should().ContainSingle().Which.Should().Contain("REJECTED");
        Directory.Exists(Path.Combine(_root, ".ashlar")).Should().BeFalse("the tool edge never read the variable");
    }

    /// <summary>
    /// The same hardening variable, spelled with a leading './'. IsAuthoringGovernancePath keys on
    /// the FIRST segment, so an un-collapsed './.ashlar/gates/' presents as '.' and was neither
    /// dropped nor reported — the operator read an empty RejectedExtras and believed the prefix had
    /// been honoured, which is the opposite of what the sandbox guide promises. No write escaped
    /// either way, and this asserts both halves: the prefix is reported as dropped, AND the write
    /// still never lands.
    /// </summary>
    [Fact]
    public async Task A_dotted_spelling_of_a_governance_prefix_is_dropped_and_reported()
    {
        using var hardening = new EnvironmentVariableScope(ExtraVar, "./.ashlar/gates/");

        var allowlist = new PathAllowlist();
        allowlist.RejectedExtras.Should().ContainSingle().Which.Should().Contain(".ashlar/gates",
            "a prefix that resolves to governance is dropped however it is spelled");

        var result = await new RepoFsWriteTool().InvokeAsync(
            WriteCall("./.ashlar/gates/forged.json"), Snapshot(), CancellationToken.None);
        result.Delta.Log.Should().ContainSingle().Which.Should().Contain("REJECTED");
        Directory.Exists(Path.Combine(_root, ".ashlar")).Should().BeFalse();
    }

    /// <summary>The control: a benign extra still widens, so the filter is a filter and not a switch.</summary>
    [Fact]
    public void A_benign_extra_prefix_still_widens_the_allowlist()
    {
        using var benign = new EnvironmentVariableScope(ExtraVar, "generated/");

        var allowlist = new PathAllowlist();

        allowlist.RejectedExtras.Should().BeEmpty();
        allowlist.Approve(WriteCall("generated/output.txt"), Snapshot(), out var reason).Should().BeTrue();
        reason.Should().Be("OK");
    }
}
