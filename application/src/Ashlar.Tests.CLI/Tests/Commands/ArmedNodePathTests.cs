using FluentAssertions;
using Ashlar.CLI.Commands;
using Ashlar.Manifest;
using Xunit;

namespace Ashlar.Tests.CLI.Tests.Commands;

/// <summary>
/// The defect: there was no supported path from <c>ashlar init</c> to an armed node.
/// <c>ashlar policy set self_extend proposing</c> — the command the gate's refusal, <c>policy
/// show</c> and the scaffolded policy's own comment all recommend, in three places — was refused on
/// the project the CLI had just written, because the scaffold shipped <c>gatesRequired: []</c>.
///
/// <para>Two halves are pinned here: the whole init → arm → verify loop now works end to end, and
/// where a project's documents genuinely do not permit arming (anything scaffolded before this
/// change), the refusal names the exact YAML to add instead of only naming a rule.</para>
/// </summary>
public sealed class ArmedNodePathTests : IDisposable
{
    private readonly string _dir;

    public ArmedNodePathTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ashlar-arming-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private void ScaffoldInto()
    {
        ProjectScaffold.TryScaffold("arming-demo", out var manifest, out var policy, out var reason)
            .Should().BeTrue(reason);
        File.WriteAllText(Path.Combine(_dir, "ashlar.yaml"), manifest);
        File.WriteAllText(Path.Combine(_dir, "ashlar.policy.yaml"), policy);
    }

    [Fact]
    public void Init_then_arm_then_verify_works_end_to_end()
    {
        ScaffoldInto();
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = PolicyCommand.Set("self_extend", SelfExtendMode.Proposing, _dir, stdout, stderr);

        exit.Should().Be(0, stderr.ToString());
        var verification = ProjectVerifier.Verify(
            File.ReadAllText(Path.Combine(_dir, "ashlar.yaml")),
            File.ReadAllText(Path.Combine(_dir, "ashlar.policy.yaml")),
            _dir);
        verification.Verified.Should().BeTrue(
            "arming must not leave the project unverifiable — a budget of 0 loads but fails the "
            + "envelope course, which is the red wall one step past the refusal: "
            + string.Join(" | ", verification.Courses.Select(c => c.Detail)));
    }

    [Fact]
    public void The_dial_can_be_taken_all_the_way_to_self_extending()
    {
        ScaffoldInto();
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        PolicyCommand.Set("self_extend", SelfExtendMode.SelfExtending, _dir, stdout, stderr)
            .Should().Be(0, stderr.ToString());
        stdout.ToString().Should().Contain("ARMED");
    }

    [Fact]
    public void A_legacy_policy_that_cannot_be_armed_is_refused_with_the_yaml_to_add()
    {
        // Exactly what a project scaffolded before this change looks like.
        File.WriteAllText(Path.Combine(_dir, "ashlar.policy.yaml"), LegacyPolicy);
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = PolicyCommand.Set("self_extend", SelfExtendMode.Proposing, _dir, stdout, stderr);

        exit.Should().Be(1);
        var text = stderr.ToString();
        text.Should().Contain("gatesRequired: [tests]", "the refusal names the fix, not only the rule");
        text.Should().Contain("mayAdd: [brick]");
        text.Should().Contain("extensions: 1");
        text.Should().Contain("run the set again");
        File.ReadAllText(Path.Combine(_dir, "ashlar.policy.yaml")).Should().Contain("mode: sealed",
            "nothing is written when the result would be invalid");
    }

    [Fact]
    public void The_named_terms_are_exactly_the_ones_missing()
    {
        PolicyLoader.TryLoad(LegacyPolicy, out var legacy, out _).Should().BeTrue();
        var lines = string.Join("\n", PolicyCommand.MissingArmingTerms(legacy!));
        lines.Should().Contain("gatesRequired").And.Contain("mayAdd").And.Contain("extensions");

        ProjectScaffold.TryScaffold("demo", out _, out var current, out _).Should().BeTrue();
        PolicyLoader.TryLoad(current, out var currentPolicy, out _).Should().BeTrue();
        PolicyCommand.MissingArmingTerms(currentPolicy!).Should().BeEmpty(
            "a freshly scaffolded project is missing nothing");
    }

    private const string LegacyPolicy = """
        apiVersion: ashlar/v1
        kind: Policy

        sandbox:
          root: .
          writable: []

        selfExtend:
          mode: sealed
          budget:
            extensions: 0
            window: 24h
          mayAdd: []
          gatesRequired: []

        never:
          - modify_gate
          - widen_sandbox
          - access_signing_keys
          - truncate_ledger
          - grant_capability
        """;
}
