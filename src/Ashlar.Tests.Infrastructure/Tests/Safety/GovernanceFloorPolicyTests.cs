using System.Text.Json;
using CsCheck;
using FluentAssertions;
using Ashlar.Abstractions;
using Ashlar.BackgroundAgents.HostRunners;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Safety;

/// <summary>
/// Property-based safety for <see cref="GovernanceFloorPolicy"/>: the policy-chain twin of the
/// tool-edge floor. A policy that throws does not fail one call — <c>PolicyEngine.Approve</c> has no
/// try/catch and the approval sits inside <c>ToolCallingAgent.RunCycleAsync</c>'s outer handler, so
/// it ends the whole cycle. And a refusal with an empty reason is a denial the planner cannot act on.
/// Same discipline as <see cref="PathAllowlistPropertyTests"/>.
/// </summary>
[Trait("Category", "Safety")]
[Trait("Category", "Unit")]
public sealed class GovernanceFloorPolicyTests
{
    private static readonly string[] WriteToolIds =
    [
        "repo.fs.write", "repo.fs.search_replace", "repo.fs.ensure_file", "docs.update", "repo.git.commit",
    ];

    private static readonly WorldSnapshot EmptySnapshot = new(0, new Dictionary<string, object?>());

    /// <summary>A root that does not exist: the policy must not care, because it must not look.</summary>
    private static readonly WorldSnapshot NonExistentRoot = WorldSnapshot.ForRepo(
        Path.Combine(Path.GetTempPath(), "ashlar-does-not-exist-" + Guid.NewGuid().ToString("N")));

    private static ToolCall Call(string id, string path) =>
        new(id, JsonSerializer.SerializeToElement(new { path, content = "x" }));

    [Fact]
    public void Approve_never_throws_and_always_gives_a_reason()
    {
        var policy = new GovernanceFloorPolicy();

        Gen.String[0, 500].Sample(path =>
        {
            foreach (var id in WriteToolIds)
            {
                foreach (var snapshot in new[] { EmptySnapshot, NonExistentRoot })
                {
                    var call = Call(id, path ?? string.Empty);
                    string reason = null!;
                    var act = () => policy.Approve(call, snapshot, out reason);

                    act.Should().NotThrow("a policy that throws ends the whole cycle, for {0} '{1}'", id, path);
                    reason.Should().NotBeNullOrEmpty("every verdict needs a reason the planner can read, for {0} '{1}'", id, path);
                }
            }
        });
    }

    /// <summary>
    /// The inputs a property sample might miss: an embedded null (which <c>Path.GetFullPath</c>
    /// throws on — the reason the policy must not reach for the probing floor), an over-long
    /// path, and the ledger itself.
    /// </summary>
    [Theory]
    [InlineData("\0")]
    [InlineData("src/\0.cs")]
    [InlineData(".ashlar/gates/x.json")]
    public void Approve_survives_a_hostile_path_with_a_reason(string path)
    {
        var policy = new GovernanceFloorPolicy();
        var call = Call("repo.fs.write", path);

        string reason = null!;
        var act = () => policy.Approve(call, EmptySnapshot, out reason);

        act.Should().NotThrow();
        reason.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Approve_survives_an_over_long_path_with_a_reason()
    {
        var policy = new GovernanceFloorPolicy();
        var call = Call("repo.fs.write", "src/" + new string('a', 40_000) + ".cs");

        string reason = null!;
        var act = () => policy.Approve(call, EmptySnapshot, out reason);

        act.Should().NotThrow();
        reason.Should().NotBeNullOrEmpty();
    }

    /// <summary>
    /// Every write tool id is judged the same way. The refusal says the path is governance and not
    /// writable from a cycle — and does not offer <c>forge.propose_change</c> as the remedy, because
    /// the forge door refuses the same targets and naming it would steer the planner into a call
    /// that is itself refused.
    /// </summary>
    [Theory]
    [InlineData("repo.fs.write")]
    [InlineData("repo.fs.search_replace")]
    [InlineData("repo.fs.ensure_file")]
    [InlineData("docs.update")]
    [InlineData("repo.git.commit")]
    public void Approve_refuses_governance_paths_and_admits_authoring_paths_for_every_write_tool_id(string id)
    {
        var policy = new GovernanceFloorPolicy();

        policy.Approve(Call(id, ".ashlar/gates/x.json"), EmptySnapshot, out var ledger).Should().BeFalse(id);
        ledger.Should().Contain(".ashlar/").And.Contain("not writable from a cycle");
        policy.Approve(Call(id, "src/.editorconfig"), EmptySnapshot, out var tooling).Should().BeFalse(id);
        tooling.Should().Contain("not writable from a cycle")
            .And.NotContain("forge.propose_change", "the forge door refuses the same targets; naming it would steer the planner into a refused call");
        policy.Approve(Call(id, "src/build/Directory.Build.props"), EmptySnapshot, out _).Should().BeFalse(id);
        policy.Approve(Call(id, "src/./x.cs"), EmptySnapshot, out var unsafeShape).Should().BeFalse(id);
        unsafeShape.Should().Contain("not a safe");

        policy.Approve(Call(id, "src/Feature/Handler.cs"), EmptySnapshot, out var ok).Should().BeTrue(id);
        ok.Should().Be("OK");
        policy.Approve(Call(id, "src/Foo/Foo.csproj"), EmptySnapshot, out _).Should().BeTrue("a project file is authorable, for {0}", id);
    }

    /// <summary>
    /// Judged by tool id, not by argument shape. The read-intent tools carry a <c>path</c> too, and
    /// a planner that cannot list the repository root or read a build import cannot plan. A tool the
    /// write inventory does not name is not judged here either: the tool edge is the floor for it,
    /// and <c>WriteToolResolverConventionTests</c> keeps the inventory and this policy's id list
    /// coupled, so "not named" can only mean "does not write".
    /// </summary>
    [Theory]
    [InlineData("repo.fs.read", ".ashlar/runtime-studio/notes.md")]
    [InlineData("repo.fs.read", "Directory.Build.props")]
    [InlineData("repo.fs.list", ".")]
    [InlineData("repo.fs.list", ".ashlar")]
    [InlineData("some.future.tool", ".ashlar/gates/x.json")]
    [InlineData("dotnet.build", "src/build/Directory.Build.props")]
    public void Approve_judges_only_the_write_tool_ids(string id, string path)
    {
        new GovernanceFloorPolicy().Approve(Call(id, path), EmptySnapshot, out var reason).Should().BeTrue();
        reason.Should().Be("OK");
    }

    [Fact]
    public void Approve_ignores_calls_without_a_string_path()
    {
        var policy = new GovernanceFloorPolicy();

        policy.Approve(new ToolCall("dotnet.build", JsonSerializer.SerializeToElement(new { project = "x" })), EmptySnapshot, out _).Should().BeTrue();
        policy.Approve(new ToolCall("repo.fs.write", JsonSerializer.SerializeToElement(new { path = 42 })), EmptySnapshot, out _).Should().BeTrue();
        policy.Approve(new ToolCall("repo.fs.write", JsonSerializer.SerializeToElement(new[] { 1, 2 })), EmptySnapshot, out _).Should().BeTrue();
        policy.Approve(new ToolCall("repo.fs.write", default), EmptySnapshot, out var reason).Should().BeTrue();
        reason.Should().Be("OK");
    }
}
