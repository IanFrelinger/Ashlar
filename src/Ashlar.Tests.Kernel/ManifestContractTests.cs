using FluentAssertions;
using Ashlar.Manifest;
using Xunit;

namespace Ashlar.Tests.Kernel;

/// <summary>
/// Pins the boundary between the project manifest and the operator-owned policy.
///
/// <para>These are not schema-shape tests. Each one fixes a rule the self-extension safety
/// model rests on: that an application cannot describe its own envelope, and that a policy
/// which cannot be fully understood is a rejection rather than a permissive default.</para>
/// </summary>
public sealed class ManifestContractTests
{
    private static readonly string ValidManifest = """
        apiVersion: ashlar/v1
        kind: Application
        metadata:
          name: invoice-triage
          version: 0.4.1
        agents:
          - id: classifier
            tools: [repo.fs.read, model.infer]
            gates: [tests, security]
        bricks:
          - id: invoice.parse
            version: 2.1.0
            certified: ed25519:9f3c
        targets:
          - name: prod-aws
            platform: aws.fargate
            profile: full
        """.ReplaceLineEndings("\n");

    private static readonly string ValidPolicy = """
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
          gatesRequired: [sandbox, tests, security, provenance]
        never:
          - modify_gate
          - widen_sandbox
          - access_signing_keys
          - truncate_ledger
          - grant_capability
        """.ReplaceLineEndings("\n");

    // ---------------------------------------------------------------- manifest

    [Fact]
    public void Valid_manifest_loads()
    {
        ManifestLoader.TryLoad(ValidManifest, out var m, out var reason).Should().BeTrue(reason);
        m!.Metadata.Name.Should().Be("invoice-triage");
        m.Agents.Should().ContainSingle().Which.Tools.Should().Contain("model.infer");
        m.Targets.Should().ContainSingle().Which.Platform.Should().Be("aws.fargate");
    }

    [Theory]
    [InlineData("sandbox:\n  root: /")]
    [InlineData("selfExtend:\n  mode: self-extending")]
    [InlineData("self_extend:\n  mode: self-extending")]
    [InlineData("never: []")]
    public void Manifest_declaring_policy_owned_keys_is_rejected(string extraKey)
    {
        var yaml = ValidManifest + "\n" + extraKey + "\n";

        ManifestLoader.TryLoad(yaml, out var m, out var reason).Should().BeFalse(
            "the envelope is not the application's to set; ignoring the key would let a proposed "
            + "edit appear to widen it and succeed");
        m.Should().BeNull();
        reason.Should().Contain("policy-owned");
    }

    [Fact]
    public void Manifest_with_unknown_apiVersion_is_rejected()
    {
        var yaml = ValidManifest.Replace("ashlar/v1", "ashlar/v2");

        ManifestLoader.TryLoad(yaml, out _, out var reason).Should().BeFalse();
        reason.Should().Contain("apiVersion");
    }

    [Fact]
    public void Manifest_with_duplicate_agent_ids_is_rejected()
    {
        // Insert into the agents block rather than appending to the document, which would
        // attach the entry to whatever section happens to be last.
        var yaml = ValidManifest.Replace(
            "agents:\n  - id: classifier",
            "agents:\n  - id: classifier\n    tools: []\n    gates: []\n  - id: classifier");

        ManifestLoader.TryLoad(yaml, out _, out var reason).Should().BeFalse();
        reason.Should().Contain("duplicate");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_manifest_is_rejected(string? yaml)
    {
        ManifestLoader.TryLoad(yaml, out _, out _).Should().BeFalse();
    }

    // ---------------------------------------------------------------- policy

    [Fact]
    public void Valid_policy_loads()
    {
        PolicyLoader.TryLoad(ValidPolicy, out var p, out var reason).Should().BeTrue(reason);
        p!.Sandbox.Root.Should().Be("/srv/app");
        p.SelfExtend.Mode.Should().Be(SelfExtendMode.Proposing);
        p.SelfExtend.Budget.Extensions.Should().Be(3);
        p.Never.Should().Contain(PolicyLoader.RequiredNeverEntries);
    }

    [Theory]
    [InlineData("modify_gate")]
    [InlineData("widen_sandbox")]
    [InlineData("access_signing_keys")]
    [InlineData("truncate_ledger")]
    [InlineData("grant_capability")]
    public void Policy_omitting_any_mandatory_never_entry_fails_to_load(string omitted)
    {
        // Drop by line, not by Replace: the last never-entry has no trailing newline in the
        // raw string literal, so a "  - x\n" match would silently fail to remove it and the
        // test would pass for the wrong reason.
        var yaml = string.Join("\n", ValidPolicy
            .Split('\n')
            .Where(l => l.Trim() != "- " + omitted));

        PolicyLoader.TryLoad(yaml, out var p, out var reason).Should().BeFalse(
            "an unparseable or incomplete envelope must never resolve to an absent one");
        p.Should().BeNull();
        reason.Should().Contain(omitted);
    }

    [Theory]
    [InlineData("tool")]
    [InlineData("capability")]
    [InlineData("policy")]
    public void Policy_permitting_the_app_to_add_envelope_widening_kinds_is_rejected(string kind)
    {
        var yaml = ValidPolicy.Replace("mayAdd: [brick]", $"mayAdd: [brick, {kind}]");

        PolicyLoader.TryLoad(yaml, out _, out var reason).Should().BeFalse(
            "a brick adds capability inside the envelope; a tool or capability widens it");
        reason.Should().Contain(kind);
    }

    [Fact]
    public void Policy_without_a_sandbox_root_is_rejected()
    {
        var yaml = ValidPolicy.Replace("  root: /srv/app\n", string.Empty);

        PolicyLoader.TryLoad(yaml, out _, out var reason).Should().BeFalse();
        reason.Should().Contain("sandbox.root");
    }

    [Fact]
    public void Policy_with_an_unknown_mode_is_rejected()
    {
        var yaml = ValidPolicy.Replace("mode: proposing", "mode: yolo");

        PolicyLoader.TryLoad(yaml, out _, out var reason).Should().BeFalse();
        reason.Should().Contain("yolo");
    }

    [Theory]
    [InlineData(SelfExtendMode.Proposing)]
    [InlineData(SelfExtendMode.SelfExtending)]
    public void Policy_that_can_admit_extensions_must_declare_gates(string mode)
    {
        var yaml = ValidPolicy
            .Replace("mode: proposing", "mode: " + mode)
            .Replace("  gatesRequired: [sandbox, tests, security, provenance]\n", string.Empty);

        PolicyLoader.TryLoad(yaml, out _, out var reason).Should().BeFalse(
            "an extension path with no gates is not a gate");
        reason.Should().Contain("gatesRequired");
    }

    [Fact]
    public void Sealed_policy_needs_no_gates_because_it_admits_nothing()
    {
        var yaml = ValidPolicy
            .Replace("mode: proposing", "mode: sealed")
            .Replace("  gatesRequired: [sandbox, tests, security, provenance]\n", string.Empty);

        PolicyLoader.TryLoad(yaml, out var p, out var reason).Should().BeTrue(reason);
        p!.SelfExtend.Mode.Should().Be(SelfExtendMode.Sealed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_policy_is_rejected_rather_than_treated_as_no_constraints(string? yaml)
    {
        PolicyLoader.TryLoad(yaml, out var p, out var reason).Should().BeFalse();
        p.Should().BeNull();
        reason.Should().Contain("REJECTED");
    }

    public static IEnumerable<object[]> InvalidBudgets()
    {
        foreach (var mode in SelfExtendMode.All)
        {
            foreach (var budget in new[]
            {
                "budget: { extensions: -1, window: 24h }",
                "budget: { extensions: -2147483648, window: 24h }",
                "budget: { extensions: 1, window: abc }",
                "budget: { extensions: 1, window: '' }",
                "budget: { extensions: 1, window: null }",
                "budget: { extensions: 1, window: -1h }",
                "budget: { extensions: 1, window: 0m }",
                "budget: { extensions: 1, window: 2147483647d }",
                "budget: { extensions: 1, window: 256204779h }",
                "budget: { extensions: 1, window: 99999999999999999999999m }",
                "budget: { extensions: 1, window: '+1h' }",
                "budget: { extensions: 1, window: ' 1h' }",
                "budget: { extensions: 1 }",
                "budget: { extensions: 1, window: [24h] }",
                "budget: { extensions: 1, window: { hours: 24 } }",
                "budget: null",
                "budget: []",
                "",
            })
            {
                yield return [mode, budget];
            }
        }
    }

    [Theory]
    [MemberData(nameof(InvalidBudgets))]
    public void Invalid_budgets_are_rejected_in_every_mode(string mode, string budget)
    {
        var yaml = ValidPolicy.Replace("mode: proposing", "mode: " + mode)
            .Replace("budget:\n    extensions: 3\n    window: 24h", budget);
        PolicyLoader.TryLoad(yaml, out var policy, out var reason).Should().BeFalse();
        policy.Should().BeNull();
        reason.Should().StartWith("REJECTED:");
    }

    [Theory]
    [InlineData(SelfExtendMode.Sealed)]
    [InlineData(SelfExtendMode.Proposing)]
    [InlineData(SelfExtendMode.SelfExtending)]
    public void Zero_budget_is_valid_and_keeps_automatic_admission_disabled(string mode)
    {
        var yaml = ValidPolicy.Replace("mode: proposing", "mode: " + mode)
            .Replace("extensions: 3", "extensions: 0");
        PolicyLoader.TryLoad(yaml, out var policy, out var reason).Should().BeTrue(reason);
        policy!.SelfExtend.Budget.Extensions.Should().Be(0);
        policy.SelfExtend.Budget.Window.Should().Be("24h");
    }

    [Fact]
    public void Null_self_extend_is_rejected_without_an_exception()
    {
        var yaml = ValidPolicy.Replace(
            "selfExtend:\n  mode: proposing\n  budget:\n    extensions: 3\n    window: 24h\n  mayAdd: [brick]\n  gatesRequired: [sandbox, tests, security, provenance]",
            "selfExtend: null");
        PolicyLoader.TryLoad(yaml, out var policy, out var reason).Should().BeFalse();
        policy.Should().BeNull();
        reason.Should().Contain("selfExtend");
    }

    // ── mutation-run harvest: gaps the Stryker survivors correctly exposed ──

    [Fact]
    public void Policy_with_unknown_apiVersion_is_rejected()
    {
        // The manifest had this test; the policy did not — a real gap the mutation run found.
        var yaml = ValidPolicy.Replace("ashlar/v1", "ashlar/v2");

        PolicyLoader.TryLoad(yaml, out _, out var reason).Should().BeFalse();
        reason.Should().Contain("apiVersion");
    }

    [Fact]
    public void Policy_with_wrong_kind_is_rejected()
    {
        var yaml = ValidPolicy.Replace("kind: Policy", "kind: Application");

        PolicyLoader.TryLoad(yaml, out _, out var reason).Should().BeFalse();
        reason.Should().Contain("kind");
    }

    [Fact]
    public void Manifest_with_wrong_kind_is_rejected()
    {
        var yaml = ValidManifest.Replace("kind: Application", "kind: Policy");

        ManifestLoader.TryLoad(yaml, out _, out var reason).Should().BeFalse();
        reason.Should().Contain("kind");
    }

    [Fact]
    public void Agent_model_parses_and_is_optional()
    {
        // The run-verb slice: agents may declare the model they run on; absent means the
        // zero-setup mock fallback (decided by the CLI, not the loader).
        var yaml = ValidManifest.Replace(
            "  - id: classifier",
            "  - id: classifier\n    model:\n      provider: ollama\n      id: llama3");

        ManifestLoader.TryLoad(yaml, out var m, out var reason).Should().BeTrue(reason);
        var agent = m!.Agents.Single();
        agent.Model.Should().NotBeNull();
        agent.Model!.Provider.Should().Be("ollama");
        agent.Model.Id.Should().Be("llama3");

        // And the original, model-less form still loads with a null Model.
        ManifestLoader.TryLoad(ValidManifest, out var plain, out _).Should().BeTrue();
        plain!.Agents.Single().Model.Should().BeNull();
    }

    [Fact]
    public void Scaffolded_agent_declares_the_mock_provider()
    {
        // `ashlar run` must work on a fresh project before any API key exists; the scaffold
        // pins that by shipping the offline mock provider explicitly, with the pointer to a
        // real one in the comment.
        ProjectScaffold.TryScaffold("demo", out var manifestYaml, out _, out _).Should().BeTrue();
        ManifestLoader.TryLoad(manifestYaml, out var m, out var reason).Should().BeTrue(reason);

        m!.Agents.Single().Model!.Provider.Should().Be("mock");
    }

    [Theory]
    [InlineData("null")]
    [InlineData("~")]
    public void A_yaml_null_document_is_rejected_as_contentless_by_both_loaders(string yaml)
    {
        // YAML "null"/"~" parses successfully to a null object — the parsed-is-null branch,
        // previously untested in both loaders.
        PolicyLoader.TryLoad(yaml, out _, out var pReason).Should().BeFalse();
        pReason.Should().Contain("no content");
        ManifestLoader.TryLoad(yaml, out _, out var mReason).Should().BeFalse();
        mReason.Should().Contain("no content");
    }
}
