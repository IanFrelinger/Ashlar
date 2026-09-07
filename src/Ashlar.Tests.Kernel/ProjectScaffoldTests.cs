using FluentAssertions;
using Ashlar.Manifest;
using Ashlar.Manifest.Admission;
using Xunit;

namespace Ashlar.Tests.Kernel;

/// <summary>
/// Pins what <c>ashlar init</c> hands a new project.
///
/// <para>The load-bearing invariants: the scaffold's own loaders accept what it emits, the
/// default is sealed (self-extension is raised deliberately, by a person, never by a
/// template), and the policy carries the full mandatory never-list.</para>
/// </summary>
public sealed class ProjectScaffoldTests
{
    [Fact]
    public void Scaffold_round_trips_through_its_own_loaders()
    {
        ProjectScaffold.TryScaffold("invoice-triage", out var manifestYaml, out var policyYaml, out var reason)
            .Should().BeTrue(reason);

        ManifestLoader.TryLoad(manifestYaml, out var manifest, out var mReason).Should().BeTrue(mReason);
        PolicyLoader.TryLoad(policyYaml, out var policy, out var pReason).Should().BeTrue(pReason);

        manifest!.Metadata.Name.Should().Be("invoice-triage");
        manifest.Metadata.Version.Should().Be("0.1.0");
        policy!.Sandbox.Root.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void New_projects_start_sealed()
    {
        ProjectScaffold.TryScaffold("demo", out _, out var policyYaml, out _).Should().BeTrue();
        PolicyLoader.TryLoad(policyYaml, out var policy, out _).Should().BeTrue();

        policy!.SelfExtend.Mode.Should().Be(
            SelfExtendMode.Sealed,
            "self-extension is the interesting capability, which is exactly why it must not "
            + "be on for a project someone has had for ninety seconds");
    }

    [Fact]
    public void The_scaffolded_project_can_actually_be_armed()
    {
        // The defect this pins: `ashlar policy set self_extend proposing` — the arming step the
        // gate's refusal, `policy show` and the scaffold itself all recommend, in three places —
        // was REFUSED on the project init had just written, because the scaffold shipped
        // `gatesRequired: []` and a mode that admits must say what it admits against. There was no
        // supported path from init to an armed node at all.
        //
        // Sealed is still the default. What changed is that raising it is now possible.
        ProjectScaffold.TryScaffold("demo", out _, out var policyYaml, out _).Should().BeTrue();

        foreach (var mode in new[] { SelfExtendMode.Proposing, SelfExtendMode.SelfExtending })
        {
            var raised = policyYaml.Replace("mode: sealed", "mode: " + mode);
            PolicyLoader.TryLoad(raised, out var policy, out var reason)
                .Should().BeTrue($"the scaffolded policy must be raisable to '{mode}': {reason}");
            policy!.SelfExtend.Mode.Should().Be(mode);

            // And the raised policy must still VERIFY — a budget of 0 loads fine but fails the
            // envelope course, which would land the documented path on a red wall one step later.
            var dir = Path.Combine(Path.GetTempPath(), "arm-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                ProjectScaffold.TryScaffold("demo", out var manifestYaml, out _, out _).Should().BeTrue();
                var verification = ProjectVerifier.Verify(manifestYaml, raised, dir);
                verification.Verified.Should().BeTrue(
                    $"an armed scaffold must still verify: {string.Join(" | ", verification.Courses.Select(c => c.Detail))}");
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public void The_armed_terms_permit_nothing_while_the_mode_is_sealed()
    {
        // Pre-filling gatesRequired/mayAdd/budget arms nothing by itself: AdmissionGate
        // short-circuits on sealed before reading any of them. This is the safety argument for
        // the change above, so it is a test rather than a comment.
        ProjectScaffold.TryScaffold("demo", out _, out var policyYaml, out _).Should().BeTrue();
        PolicyLoader.TryLoad(policyYaml, out var policy, out _).Should().BeTrue();

        var proposal = new ExtensionProposal
        {
            Id = "p1",
            Kind = "brick",
            Summary = "add brick demo",
            ProposedBy = "main",
            ProposedAt = DateTimeOffset.UtcNow,
            Courses = [new CourseResult { Name = "tests", Passed = true, Detail = "ok" }],
        };

        var outcome = AdmissionGate.Decide(policy!, proposal, admittedInWindow: 0);

        outcome.State.Should().Be(ProposalState.Rejected);
        outcome.Reason.Should().Contain("sealed");
    }

    [Fact]
    public void Scaffolded_policy_carries_the_full_never_list()
    {
        ProjectScaffold.TryScaffold("demo", out _, out var policyYaml, out _).Should().BeTrue();
        PolicyLoader.TryLoad(policyYaml, out var policy, out _).Should().BeTrue();

        policy!.Never.Should().Contain(PolicyLoader.RequiredNeverEntries);
    }

    [Fact]
    public void Scaffolded_manifest_declares_no_policy_owned_keys()
    {
        ProjectScaffold.TryScaffold("demo", out var manifestYaml, out _, out _).Should().BeTrue();

        // The loader would reject them anyway; this pins that the TEMPLATE never drifts into
        // teaching users to put the envelope in the wrong file.
        foreach (var key in ManifestLoader.PolicyOwnedKeys)
        {
            manifestYaml.Should().NotContain(key + ":");
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("9lives")]
    [InlineData("-leading-hyphen")]
    [InlineData("has spaces")]
    [InlineData("has/slash")]
    [InlineData("has.dot")]
    public void Invalid_names_are_refused(string? name)
    {
        ProjectScaffold.TryScaffold(name, out _, out _, out var reason).Should().BeFalse();
        reason.Should().Contain("REJECTED");
    }

    [Theory]
    [InlineData("a")]
    [InlineData("invoice-triage")]
    [InlineData("App2")]
    public void Valid_names_are_accepted(string name)
    {
        ProjectScaffold.TryScaffold(name, out _, out _, out var reason).Should().BeTrue(reason);
    }

    [Fact]
    public void A_name_at_the_length_cap_is_accepted()
    {
        // The boundary: exactly MaxNameLength characters (all valid) still scaffolds.
        var name = "a" + new string('b', ProjectScaffold.MaxNameLength - 1);
        name.Length.Should().Be(ProjectScaffold.MaxNameLength);

        ProjectScaffold.TryScaffold(name, out _, out _, out var reason).Should().BeTrue(reason);
    }

    [Fact]
    public void An_over_long_name_is_refused_even_when_the_charset_is_valid()
    {
        // Charset-valid but pathological: the charset check alone accepted a 100k-char name, which
        // then became a metadata.name and a directory. The length cap refuses it up front.
        var name = "a" + new string('b', ProjectScaffold.MaxNameLength);
        name.Length.Should().Be(ProjectScaffold.MaxNameLength + 1);

        ProjectScaffold.TryScaffold(name, out _, out _, out var reason).Should().BeFalse();
        reason.Should().Contain("REJECTED").And.Contain("maximum");
    }

    [Fact]
    public void A_hundred_thousand_char_name_is_refused()
    {
        var name = new string('a', 100_000);

        ProjectScaffold.TryScaffold(name, out _, out _, out var reason).Should().BeFalse();
        reason.Should().Contain("REJECTED");
    }

    [Fact]
    public void Emitted_documents_use_lf_line_endings_regardless_of_source_checkout()
    {
        // The templates normalize at initialization, so what init writes to disk does not
        // depend on the line endings git happened to give this source file.
        ProjectScaffold.TryScaffold("demo", out var manifestYaml, out var policyYaml, out _).Should().BeTrue();

        manifestYaml.Should().NotContain("\r");
        policyYaml.Should().NotContain("\r");
    }
}
