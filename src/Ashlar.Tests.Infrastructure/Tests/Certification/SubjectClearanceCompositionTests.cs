using Ashlar.Abstractions.Security;
using Ashlar.Hosting;
using Ashlar.Manifest;
using Ashlar.Manifest.Admission;
using Ashlar.Manifest.Packaging;
using Ashlar.Manifest.Signing;
using FluentAssertions;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>
/// SPEC-007 PR 5: an agent is composed with a sealed skill only when the agent's clearance dominates
/// the skill's highest level. The clearance comes from <c>ashlar.policy.yaml</c>. A host ceiling can
/// only tighten it. The skill level rides in the sealed package, not in the signed gate record.
/// </summary>
[Trait("Category", "Certification")]
public sealed class SubjectClearanceCompositionTests : IDisposable
{
    private readonly string _keys;
    private readonly SigningIdentity _sealer;

    public SubjectClearanceCompositionTests()
    {
        _keys = Path.Combine(Path.GetTempPath(), "clearance-" + Guid.NewGuid().ToString("N"));
        _sealer = OperatorKey.Generate(_keys);
    }

    public void Dispose()
    {
        if (Directory.Exists(_keys))
            Directory.Delete(_keys, recursive: true);
    }

    [Fact]
    public void Public_agent_cannot_compose_a_secret_skill()
    {
        var decision = Compose(Policy(), "analyst", host: null, "Secret");

        decision.Allowed.Should().BeFalse();
        decision.Reason.Should().Be(AccessDenialReason.LevelTooLow);
    }

    [Fact]
    public void Secret_agent_can_compose_a_secret_skill()
    {
        var decision = Compose(Policy("Secret", ("analyst", "Secret")), "analyst", host: null, "Secret");

        decision.Allowed.Should().BeTrue();
        decision.Reason.Should().Be(AccessDenialReason.None);
    }

    [Fact]
    public void Unlabelled_skill_is_refused_for_a_public_agent()
    {
        var decision = Compose(Policy("Secret", ("analyst", "Secret")), "analyst", host: null, highestLevel: null);

        decision.Allowed.Should().BeFalse();
        decision.Reason.Should().Be(AccessDenialReason.SystemHighData);
    }

    [Fact]
    public void Host_cannot_add_a_compartment_the_policy_does_not_hold()
    {
        var policy = Policy("Public", ("analyst", "Secret//C:ALPHA"));
        var host = new AshlarHostingOptions { Clearance = "Secret//C:ALPHA,BRAVO" };

        var decision = Compose(policy, "analyst", host.Clearance, "Secret//C:BRAVO");

        decision.Allowed.Should().BeFalse();
        decision.Reason.Should().Be(AccessDenialReason.MissingCompartment);
        decision.Detail.Should().Contain("BRAVO");
    }

    [Fact]
    public void Host_cannot_raise_a_public_policy()
    {
        var host = new AshlarHostingOptions { Clearance = "Secret" };

        var decision = Compose(Policy(), "analyst", host.Clearance, "Secret");

        decision.Allowed.Should().BeFalse();
        decision.Reason.Should().Be(AccessDenialReason.LevelTooLow);
    }

    [Fact]
    public void Unparseable_host_ceiling_fails_closed_to_public()
    {
        var decision = Compose(Policy("Secret", ("analyst", "Secret")), "analyst", " ", "Internal");

        decision.Allowed.Should().BeFalse();
        decision.Reason.Should().Be(AccessDenialReason.LevelTooLow);
    }

    [Fact]
    public void An_agent_without_its_own_row_uses_the_default()
    {
        var policy = Policy("Internal", ("analyst", "Secret"));

        Compose(policy, "other", host: null, "Internal").Allowed.Should().BeTrue();
        Compose(policy, "other", host: null, "Secret").Allowed.Should().BeFalse();
    }

    [Fact]
    public void Policy_without_clearances_loads_and_composes_at_public()
    {
        PolicyLoader.TryLoad(Envelope(), out var policy, out var reason).Should().BeTrue(reason);

        var decision = Compose(policy!, "analyst", host: null, "Internal");

        decision.Allowed.Should().BeFalse();
        decision.Reason.Should().Be(AccessDenialReason.LevelTooLow);
    }

    [Fact]
    public void A_non_canonical_clearance_rejects_the_policy()
    {
        var yaml = Envelope() + "clearances:\n  default: secret\n";

        PolicyLoader.TryLoad(yaml, out _, out var reason).Should().BeFalse();
        reason.Should().Contain("clearances.default");
    }

    [Fact]
    public void Highest_level_is_sealed_and_is_not_part_of_the_gate_record()
    {
        var package = Open(Pack("Secret"));
        package.HighestLevel.Should().Be("Secret");
        package.Record.Proposal.Summary.Should().NotContain("Secret");

        var tampered = Pack("Secret").Replace("\"HighestLevel\": \"Secret\"", "\"HighestLevel\": \"Public\"", StringComparison.Ordinal);
        ExtensionPackaging.TryOpen(tampered, out _, out var reason).Should().BeFalse();
        reason.Should().Contain("seal");
    }

    [Fact]
    public void Pack_refuses_a_non_canonical_highest_level()
    {
        var act = () => Pack("secret");

        act.Should().Throw<InvalidOperationException>().WithMessage("*canonical security label*");
    }

    private AccessDecision Compose(AshlarPolicy policy, string agent, string? host, string? highestLevel)
    {
        ExtensionPackaging.TryOpen(Pack(highestLevel), out var package, out var reason).Should().BeTrue(reason);
        return SkillComposition.Decide(policy, agent, host, package!);
    }

    private string Pack(string? highestLevel)
    {
        var record = AdmittedRecord();
        return ExtensionPackaging.Pack(
            record,
            [new PackageFile { Path = "src/Skill.cs", Content = "sealed skill" }],
            _sealer,
            highestLevel);
    }

    private static ExtensionPackage Open(string json)
    {
        ExtensionPackaging.TryOpen(json, out var package, out var reason).Should().BeTrue(reason);
        return package!;
    }

    private GateRecord AdmittedRecord()
    {
        var unsigned = new GateRecord
        {
            Proposal = new ExtensionProposal
            {
                Id = "skill-1",
                Kind = "brick",
                Summary = "add brick skill",
                ProposedBy = "author",
                ProposedAt = new DateTimeOffset(2026, 10, 10, 0, 0, 0, TimeSpan.Zero),
                Courses = [new CourseResult { Name = "tests", Passed = true, Detail = "ok" }],
            },
            State = ProposalState.Admitted,
            Reason = "admitted",
            Actor = "operator",
            DecidedAt = new DateTimeOffset(2026, 10, 10, 0, 1, 0, TimeSpan.Zero),
            StoreId = "store-pr5",
        };
        return unsigned with
        {
            Sig = _sealer.Sign(CanonicalJson.Bytes(unsigned)),
            Signer = _sealer.PublicKeyBase64,
        };
    }

    private static AshlarPolicy Policy(string? fallback = null, params (string Agent, string Clearance)[] agents)
    {
        var yaml = Envelope();
        if (fallback is not null || agents.Length > 0)
        {
            yaml += "clearances:\n";
            if (fallback is not null)
                yaml += "  default: " + YamlQuote(fallback) + "\n";
            if (agents.Length > 0)
            {
                yaml += "  agents:\n";
                foreach (var (agent, clearance) in agents)
                    yaml += "    " + agent + ": " + YamlQuote(clearance) + "\n";
            }
        }

        PolicyLoader.TryLoad(yaml, out var policy, out var reason).Should().BeTrue(reason);
        return policy!;
    }

    private static string YamlQuote(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static string Envelope() => """
        apiVersion: ashlar/v1
        kind: Policy
        sandbox:
          root: /srv/app
          writable: [/srv/app/bricks]
        selfExtend:
          mode: proposing
          budget:
            extensions: 3
            window: 24h
          mayAdd: [brick]
          gatesRequired: [tests]
        never:
          - modify_gate
          - widen_sandbox
          - access_signing_keys
          - truncate_ledger
          - grant_capability

        """.ReplaceLineEndings("\n");
}
