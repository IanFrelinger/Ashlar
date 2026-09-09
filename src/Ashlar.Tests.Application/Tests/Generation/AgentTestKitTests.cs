using FluentAssertions;
using Ashlar.Agents.TestKit;
using Ashlar.Core.Application.Execution.Ports;
using Ashlar.Core.Application.Generation;
using Ashlar.Core.Application.Orchestration;
using Ashlar.Core.Domain.Bricks;
using Ashlar.Core.Domain.Bricks.Ports;
using Xunit;

namespace Ashlar.Tests.Application.Tests.Generation;

/// <summary>
/// Part (c): the TestKit itself. These prove the fakes wrap the REAL ports and that
/// the assertion helpers describe control flow — the repair loop is driven for real,
/// only its I/O edges are faked.
/// </summary>
[Collection(OfflineAgentEnvironmentCollection.Name)]
public sealed class AgentTestKitTests
{
    [Fact]
    public async Task Repair_loop_feeds_failure_back_and_stops_on_success_without_a_container()
    {
        // The verification edge fails once with a diagnostic, then passes.
        var runner = FakeSandboxedCommandRunner.FailThenSucceed("error: 'readNextContact' was not declared");
        var drafter = FakeArtifactDrafter.OfContent("first draft", "repaired draft");

        var loop = new GenerationRepairLoop<GeneratedArtifact>(
            async (prior, ct) => await drafter.DraftAsync(
                new GenerationRequest { TargetId = "t" },
                prior.Select(p => p.Reason).ToArray(),
                ct),
            new IPostValidator<GeneratedArtifact>[] { new RunnerBackedValidator(runner) },
            new RepairOptions(3));

        var result = await loop.RunAsync(CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.Artifact!.Content.Should().Be("repaired draft");

        // Control flow, not just the bool: two attempts, and attempt 2 saw the failure.
        AgentFlowAssert.AttemptCount(drafter, 2);
        AgentFlowAssert.FedFailureBack(drafter);
        drafter.FeedbackPerAttempt[0].Should().BeEmpty();
        drafter.FeedbackPerAttempt[1].Should().ContainSingle()
            .Which.Should().Contain("readNextContact");
        runner.Calls.Should().Be(2);
    }

    [Fact]
    public async Task Repair_from_an_empty_first_draft_converges_within_bound()
    {
        var drafter = FakeArtifactDrafter.EmptyThen("#pragma once\nclass Adapter {};\n");

        var loop = new GenerationRepairLoop<GeneratedArtifact>(
            async (prior, ct) => await drafter.DraftAsync(
                new GenerationRequest { TargetId = "t" },
                prior.Select(p => p.Reason).ToArray(),
                ct),
            new IPostValidator<GeneratedArtifact>[] { new NonEmptyValidator() },
            new RepairOptions(3));

        var result = await loop.RunAsync(CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        AgentFlowAssert.ConvergedWithin(drafter, 3);
        AgentFlowAssert.FedFailureBack(drafter);
    }

    [Fact]
    public async Task Rollback_verdict_restores_prior_file_set_and_prior_image()
    {
        var files = new FakeManagedFileSet().Seed("dest", "artifact", "PRIOR_ARTIFACT");
        var target = new FakeDeploymentTarget(
            files,
            Scripted<DeploymentSmokeResult>.Always(DeploymentSmokeResult.Pass("rows=1")));

        var result = await target.ApplyAsync(
            new GeneratedArtifact { Content = "NEW_ARTIFACT" },
            FakeAcceptanceEvaluator.AlwaysRollback("row count below floor"));

        result.Succeeded.Should().BeFalse();

        AgentFlowAssert.RollbackRestoredPriorState(
            target,
            files,
            new Dictionary<string, string> { ["dest/artifact"] = "PRIOR_ARTIFACT" });
    }

    [Fact]
    public async Task Ship_verdict_commits_and_leaves_the_new_artifact_live()
    {
        var files = new FakeManagedFileSet().Seed("dest", "artifact", "PRIOR_ARTIFACT");
        var target = new FakeDeploymentTarget(
            files,
            Scripted<DeploymentSmokeResult>.Always(DeploymentSmokeResult.Pass("rows=99")));

        var result = await target.ApplyAsync(
            new GeneratedArtifact { Content = "NEW_ARTIFACT" },
            FakeAcceptanceEvaluator.AlwaysShip());

        result.Succeeded.Should().BeTrue();
        AgentFlowAssert.Shipped(target);
        files.RollbackCalls.Should().Be(0);
        files.Read("dest", "artifact").Should().Be("NEW_ARTIFACT");
        target.CurrentImage.Should().Be(target.NewImage);
    }

    [Fact]
    public async Task A_scaffold_smoke_failure_does_not_thrash_the_model()
    {
        // The install failed for an environmental reason. Re-drafting cannot fix that,
        // so the model must not be sent back to work.
        var drafter = FakeArtifactDrafter.OfContent("scaffolded adapter");
        var files = new FakeManagedFileSet();
        var target = new FakeDeploymentTarget(
            files,
            Scripted<DeploymentSmokeResult>.Always(DeploymentSmokeResult.Fail("service unhealthy")));

        var artifact = await drafter.DraftAsync(
            new GenerationRequest { TargetId = "t" }, Array.Empty<string>());
        var result = await target.ApplyAsync(artifact, DefaultAcceptanceEvaluator.Instance);

        result.Succeeded.Should().BeFalse();
        AgentFlowAssert.RolledBack(target);
        AgentFlowAssert.DidNotThrashTheModel(drafter);
    }

    [Fact]
    public async Task Human_review_is_routed_to_the_gate()
    {
        var gate = new RecordingApprovalGate(Ashlar.Core.Application.Trust.Ports.ApprovalResult.Approved);

        var verdict = await AcceptanceRouting.EvaluateAsync(
            evaluator: null,
            new AcceptanceContext(
                new GeneratedArtifact { Content = "x" },
                GenerativeProvenance.Create(GenerationStrategy.Model, verified: true, requiresHumanReview: true),
                DeploymentSmokeResult.Pass("ok")),
            gate);

        verdict.Decision.Should().Be(AcceptanceDecision.Ship);
        AgentFlowAssert.RoutedToHumanReview(gate);
    }

    [Fact]
    public async Task A_human_cannot_approve_away_a_failed_smoke()
    {
        var gate = new RecordingApprovalGate(Ashlar.Core.Application.Trust.Ports.ApprovalResult.Approved);

        var verdict = await AcceptanceRouting.EvaluateAsync(
            evaluator: null,
            new AcceptanceContext(
                new GeneratedArtifact { Content = "x" },
                GenerativeProvenance.Create(GenerationStrategy.Templated, verified: true),
                DeploymentSmokeResult.Fail("no output produced")),
            gate);

        verdict.Decision.Should().Be(AcceptanceDecision.Rollback);
        AgentFlowAssert.DidNotRouteToHumanReview(gate);
    }

    [Fact]
    public void Scripted_sequence_advances_per_call_and_can_refuse_to_over_serve()
    {
        var strict = new Scripted<int>(new[] { 1, 2 }, repeatLast: false);
        strict.Next().Should().Be(1);
        strict.Next().Should().Be(2);
        strict.Invoking(s => s.Next()).Should().Throw<InvalidOperationException>()
            .WithMessage("*exhausted*");

        var lenient = Scripted<int>.Of(7);
        lenient.Next().Should().Be(7);
        lenient.Next().Should().Be(7);
    }

    [Fact]
    public void Offline_environment_sets_the_knobs_and_restores_them()
    {
        var before = Environment.GetEnvironmentVariable(OfflineAgentEnvironment.SkipModel);
        string root;

        using (var env = new OfflineAgentEnvironment())
        {
            root = env.Root;
            Directory.Exists(root).Should().BeTrue();
            Environment.GetEnvironmentVariable(OfflineAgentEnvironment.CompileGate).Should().Be("0");
            Environment.GetEnvironmentVariable(OfflineAgentEnvironment.SkipModel).Should().Be("1");
            Environment.GetEnvironmentVariable(OfflineAgentEnvironment.WorkspaceRoot).Should().Be(root);
            OfflineAgentEnvironment.IsOffline.Should().BeTrue();
        }

        Environment.GetEnvironmentVariable(OfflineAgentEnvironment.SkipModel).Should().Be(before);
        Directory.Exists(root).Should().BeFalse("the temp workspace must be cleaned up");
    }

    [Fact]
    public void Fake_provider_factory_is_offline_by_default()
    {
        var providers = new FakeProviderFactory();

        providers.Invoking(p => p.ExecuteLLMAsync("ollama", "sys", "user", new object()).GetAwaiter().GetResult())
            .Should().Throw<InvalidOperationException>().WithMessage("*offline*");
    }

    /// <summary>Bridges the sandbox runner edge into the loop's validator port.</summary>
    private sealed class RunnerBackedValidator : IPostValidator<GeneratedArtifact>
    {
        private readonly ISandboxedCommandRunner _runner;
        public RunnerBackedValidator(ISandboxedCommandRunner runner) => _runner = runner;

        public async ValueTask<(bool ok, string? reason)> ValidateAsync(
            GeneratedArtifact output,
            CancellationToken ct)
        {
            var result = await _runner.RunAsync(
                new SandboxSpec(null, Array.Empty<Mount>(), NetworkAccess.None, new[] { "verify" }),
                ct);
            return result.ExitCode == 0 ? (true, null) : (false, result.StdErr);
        }
    }

    private sealed class NonEmptyValidator : IPostValidator<GeneratedArtifact>
    {
        public ValueTask<(bool ok, string? reason)> ValidateAsync(
            GeneratedArtifact output,
            CancellationToken ct) =>
            ValueTask.FromResult<(bool, string?)>(
                string.IsNullOrWhiteSpace(output.Content)
                    ? (false, "artifact is empty")
                    : (true, null));
    }
}
