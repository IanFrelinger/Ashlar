using FluentAssertions;
using Ashlar.BackgroundAgents.Configuration;
using Ashlar.BackgroundAgents.DataSensitivity;
using Ashlar.BackgroundAgents.Registry;
using Ashlar.BackgroundAgents.Security;
using Xunit;

namespace Ashlar.Tests.BackgroundAgents.Security;

/// <summary>
/// Isolates the LEVEL compare in <see cref="AgentPolicyNarrowingValidator"/> (the
/// <c>childLevel.SensitivityValue &gt; parentLevel.SensitivityValue</c> test in
/// EnsureLevelNotBroader). The existing rejection test,
/// SelfExtendInvariantBPolicyNarrowingTests.Rejection_spawn_spec_with_broader_envelope_than_creator_is_refused,
/// broadens the flags AND the levels at once and is refused on BlockExternalLLMs before any level is
/// compared -- so deleting the level compare left the whole suite green. Here parent and child carry
/// IDENTICAL flags, so a level is the only thing that can refuse.
/// </summary>
public sealed class AgentPolicyNarrowingValidatorTests
{
    [Fact]
    public void A_child_whose_MaxAllowedLevel_exceeds_the_parents_is_refused_on_that_field()
    {
        var parent = Config("parent", parentId: null, maxData: "Internal", maxAllowed: "Internal");
        var child = Config("child", parentId: "parent", maxData: "Internal", maxAllowed: "Secret");

        var act = () => Validate(child, parent);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*MaxAllowedLevel 'Secret' exceeds parent 'parent' envelope 'Internal'*");
    }

    [Fact]
    public void A_child_whose_MaxDataSensitivity_exceeds_the_parents_is_refused_on_that_field()
    {
        var parent = Config("parent", parentId: null, maxData: "Internal", maxAllowed: "Internal");
        var child = Config("child", parentId: "parent", maxData: "Confidential", maxAllowed: "Internal");

        var act = () => Validate(child, parent);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*MaxDataSensitivity 'Confidential' exceeds parent 'parent' envelope 'Internal'*");
    }

    // POSITIVE CONTROL: the same shapes one step down -- equal and narrower levels -- are accepted,
    // so the refusals above are the compare and not something else about the fixture (an
    // unresolvable level name, a missing parent, a flag) that would refuse any child.
    [Theory]
    [InlineData("Internal", "Internal")]
    [InlineData("Public", "Public")]
    [InlineData("Internal", "Public")]
    public void An_equal_or_narrower_level_is_accepted(string childMaxData, string childMaxAllowed)
    {
        var parent = Config("parent", parentId: null, maxData: "Internal", maxAllowed: "Internal");
        var child = Config("child", parentId: "parent", maxData: childMaxData, maxAllowed: childMaxAllowed);

        var act = () => Validate(child, parent);

        act.Should().NotThrow();
    }

    private static void Validate(BackgroundAgentConfig child, BackgroundAgentConfig parent) =>
        AgentPolicyNarrowingValidator.ValidateOrThrow(
            child,
            AgentRegistrationOrigin.Machine,
            id => id == parent.Id ? new BackgroundAgentInstance { Config = parent } : null,
            new DataSensitivityRegistry());

    // Every flag identical and permissive on both sides: none of the four flag checks can refuse.
    private static BackgroundAgentConfig Config(string id, string? parentId, string maxData, string maxAllowed) => new()
    {
        Id = id,
        ParentId = parentId,
        Role = "extender",
        Commands = ["extend"],
        MaxDataSensitivity = maxData,
        ExfiltrationPolicy = new ExfiltrationPolicy
        {
            MaxAllowedLevel = maxAllowed,
            BlockExternalLLMs = false,
            BlockWebSearch = false,
            BlockNetworkExports = false,
            RequireLocalOnly = false,
        },
    };
}
