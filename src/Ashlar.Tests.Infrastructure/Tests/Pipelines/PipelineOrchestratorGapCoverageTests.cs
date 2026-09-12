using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Ashlar.Core.Application.Pipelines.Models;
using Ashlar.Core.Application.Pipelines.Ports;
using Ashlar.Infrastructure.Pipelines;
using Ashlar.Tests.Infrastructure.Helpers;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Pipelines;

/// <summary>Tests for pipeline orchestrator gap coverage.</summary>
public sealed class PipelineOrchestratorGapCoverageTests
{
    [Fact]
    public async Task RunAsync_null_request_throws()
    {
        var orchestrator = PipelineOrchestratorTests.BuildOrchestratorForGap(out _);
        var act = () => orchestrator.RunAsync(null!, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task RunAsync_invalid_template_throws()
    {
        var orchestrator = PipelineOrchestratorTests.BuildOrchestratorForGap(out _);
        var template = new PipelineTemplate
        {
            TemplateId = "invalid",
            Stages = Array.Empty<PipelineStageDefinition>(),
        };

        var act = () => orchestrator.RunAsync(new PipelineExecutionRequest { Template = template }, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Pipeline template invalid*");
    }

    [Fact]
    public async Task RunAsync_generates_run_id_when_missing()
    {
        var orchestrator = PipelineOrchestratorTests.BuildOrchestratorForGap(out var runStore);
        var template = SingleStageTemplate("auto-id");

        var run = await orchestrator.RunAsync(new PipelineExecutionRequest { Template = template }, CancellationToken.None);

        run.RunId.Should().NotBeNullOrWhiteSpace();
        (await runStore.GetAsync(run.RunId)).Should().NotBeNull();
    }

    [Fact]
    public async Task RunAsync_stalls_when_downstream_never_becomes_ready()
    {
        var orchestrator = PipelineOrchestratorTests.BuildOrchestratorForGap(
            out _,
            new PipelineExecutionOptions
            {
                MaxRetryAttempts = 1,
                RetryDelayMs = 1,
                EnableTestHooks = true,
            });

        var template = new PipelineTemplate
        {
            TemplateId = "stall",
            Stages = new[]
            {
                new PipelineStageDefinition { Id = "upstream", Name = "Upstream", Mode = PipelineExecutionMode.Deterministic },
                new PipelineStageDefinition { Id = "blocked", Name = "Blocked", Mode = PipelineExecutionMode.Deterministic },
            },
            Edges = new[] { new PipelineEdge("upstream", "blocked") },
        };

        var run = await orchestrator.RunAsync(new PipelineExecutionRequest
        {
            RunId = "run-stall",
            Template = template,
            Inputs = new Dictionary<string, string> { ["fail:upstream:deterministic"] = "true" },
        }, CancellationToken.None);

        run.State.Should().Be(PipelineRunState.Failed);
        run.StageRuns.Single(x => x.StageId == "upstream").State.Should().Be(PipelineStageRunState.Failed);
        run.StageRuns.Single(x => x.StageId == "blocked").State.Should().Be(PipelineStageRunState.Pending);
    }

    [Fact]
    public async Task RunAsync_quorum_fan_in_waits_for_required_completed_predecessors()
    {
        var orchestrator = PipelineOrchestratorTests.BuildOrchestratorForGap(
            out _,
            new PipelineExecutionOptions
            {
                MaxRetryAttempts = 1,
                RetryDelayMs = 1,
                EnableTestHooks = true,
                CompletionPolicy = PipelineCompletionPolicy.AllowNonCriticalStageFailures,
            });

        var template = new PipelineTemplate
        {
            TemplateId = "quorum",
            Stages = new[]
            {
                new PipelineStageDefinition { Id = "a", Name = "A", Mode = PipelineExecutionMode.Deterministic },
                new PipelineStageDefinition { Id = "b", Name = "B", Mode = PipelineExecutionMode.Deterministic },
                new PipelineStageDefinition { Id = "c", Name = "C", Mode = PipelineExecutionMode.Deterministic },
                new PipelineStageDefinition
                {
                    Id = "join",
                    Name = "Join",
                    StageType = PipelineStageType.FanIn,
                    JoinStrategy = PipelineJoinStrategy.Quorum,
                    QuorumCount = 2,
                    Mode = PipelineExecutionMode.Deterministic,
                },
            },
            Edges = new[]
            {
                new PipelineEdge("a", "join"),
                new PipelineEdge("b", "join"),
                new PipelineEdge("c", "join"),
            },
        };

        var run = await orchestrator.RunAsync(new PipelineExecutionRequest
        {
            RunId = "run-quorum",
            Template = template,
            Inputs = new Dictionary<string, string> { ["fail:c:deterministic"] = "true" },
        }, CancellationToken.None);

        run.State.Should().Be(PipelineRunState.Completed);
        run.StageRuns.Single(x => x.StageId == "join").State.Should().Be(PipelineStageRunState.Completed);
    }

    [Fact]
    public async Task RunAsync_retries_retryable_failures_before_failing_stage()
    {
        var deterministic = new Mock<IPipelineStageExecutor>();
        deterministic.SetupGet(x => x.WorkerType).Returns(PipelineWorkerType.Deterministic);
        deterministic
            .SetupSequence(x => x.ExecuteAsync(It.IsAny<PipelineStageExecutionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineStageExecutionResult { Succeeded = false, Retryable = true, Error = "transient" })
            .ReturnsAsync(new PipelineStageExecutionResult { Succeeded = true, WorkerId = "retry-ok", Output = "ok" });

        var orchestrator = BuildOrchestratorWithExecutors(
            new[] { deterministic.Object },
            new PipelineExecutionOptions { MaxRetryAttempts = 2, RetryDelayMs = 1 });

        var run = await orchestrator.RunAsync(new PipelineExecutionRequest
        {
            RunId = "run-retry",
            Template = SingleStageTemplate("retry-stage"),
        }, CancellationToken.None);

        run.State.Should().Be(PipelineRunState.Completed);
        deterministic.Verify(
            x => x.ExecuteAsync(It.IsAny<PipelineStageExecutionRequest>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task RunAsync_agentic_only_stage_uses_agentic_executor()
    {
        var orchestrator = PipelineOrchestratorTests.BuildOrchestratorForGap(
            out _,
            new PipelineExecutionOptions { MaxRetryAttempts = 1, RetryDelayMs = 1 });

        var template = new PipelineTemplate
        {
            TemplateId = "agentic-only",
            Stages = new[]
            {
                new PipelineStageDefinition { Id = "think", Name = "Think", Mode = PipelineExecutionMode.Agentic },
            },
        };

        var run = await orchestrator.RunAsync(new PipelineExecutionRequest
        {
            RunId = "run-agentic",
            Template = template,
        }, CancellationToken.None);

        run.State.Should().Be(PipelineRunState.Completed);
        run.StageRuns.Single().WorkerType.Should().Be(PipelineWorkerType.Agentic);
    }

    [Fact]
    public async Task RunAsync_missing_executor_for_worker_chain_fails_stage()
    {
        var orchestrator = BuildOrchestratorWithExecutors(
            Array.Empty<IPipelineStageExecutor>(),
            new PipelineExecutionOptions { MaxRetryAttempts = 1, RetryDelayMs = 1 });

        var run = await orchestrator.RunAsync(new PipelineExecutionRequest
        {
            RunId = "run-no-exec",
            Template = SingleStageTemplate("lonely"),
        }, CancellationToken.None);

        run.State.Should().Be(PipelineRunState.Failed);
        run.StageRuns.Single().State.Should().Be(PipelineStageRunState.Failed);
    }

    [Fact]
    public async Task RunAsync_honors_resume_failed_stages_from_options()
    {
        var orchestrator = PipelineOrchestratorTests.BuildOrchestratorForGap(
            out var runStore,
            new PipelineExecutionOptions
            {
                MaxRetryAttempts = 1,
                RetryDelayMs = 1,
                ResumeFailedStages = true,
            });

        var template = new PipelineTemplate
        {
            TemplateId = "resume-options",
            Stages = new[]
            {
                new PipelineStageDefinition { Id = "a", Name = "A", Mode = PipelineExecutionMode.Deterministic },
                new PipelineStageDefinition { Id = "b", Name = "B", Mode = PipelineExecutionMode.Deterministic },
            },
            Edges = new[] { new PipelineEdge("a", "b") },
        };

        await runStore.PutAsync(new PipelineRun
        {
            RunId = "prior-options",
            TemplateId = "resume-options",
            State = PipelineRunState.Failed,
            StageRuns = new[]
            {
                new PipelineStageRun { StageId = "a", State = PipelineStageRunState.Completed, Attempt = 1, WorkerType = PipelineWorkerType.Deterministic },
                new PipelineStageRun { StageId = "b", State = PipelineStageRunState.Failed, Attempt = 1, WorkerType = PipelineWorkerType.Deterministic, Error = "x" },
            },
        });

        var run = await orchestrator.RunAsync(new PipelineExecutionRequest
        {
            RunId = "resumed-options",
            ResumeRunId = "prior-options",
            Template = template,
        }, CancellationToken.None);

        run.State.Should().Be(PipelineRunState.Completed);
    }

    [Fact]
    public async Task RunAsync_cancellation_stops_execution()
    {
        var orchestrator = PipelineOrchestratorTests.BuildOrchestratorForGap(out _);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => orchestrator.RunAsync(new PipelineExecutionRequest
        {
            Template = SingleStageTemplate("cancel"),
        }, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task RunAsync_hybrid_stage_uses_custom_fallback_chain()
    {
        var orchestrator = PipelineOrchestratorTests.BuildOrchestratorForGap(
            out _,
            new PipelineExecutionOptions
            {
                MaxRetryAttempts = 1,
                RetryDelayMs = 1,
                EnableTestHooks = true,
            });

        var template = new PipelineTemplate
        {
            TemplateId = "hybrid-fallback",
            Stages = new[]
            {
                new PipelineStageDefinition
                {
                    Id = "hybrid",
                    Name = "Hybrid",
                    Mode = PipelineExecutionMode.Hybrid,
                    FallbackChain = new[] { PipelineWorkerType.Agentic, PipelineWorkerType.Deterministic },
                },
            },
        };

        var run = await orchestrator.RunAsync(new PipelineExecutionRequest
        {
            RunId = "run-hybrid-fallback",
            Template = template,
            Inputs = new Dictionary<string, string> { ["fail:hybrid:agentic"] = "true" },
        }, CancellationToken.None);

        run.State.Should().Be(PipelineRunState.Completed);
        run.StageRuns.Single().WorkerType.Should().Be(PipelineWorkerType.Deterministic);
    }

    [Fact]
    public async Task RunAsync_non_retryable_failure_fails_immediately()
    {
        var deterministic = new Mock<IPipelineStageExecutor>();
        deterministic.SetupGet(x => x.WorkerType).Returns(PipelineWorkerType.Deterministic);
        deterministic
            .Setup(x => x.ExecuteAsync(It.IsAny<PipelineStageExecutionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineStageExecutionResult
            {
                Succeeded = false,
                Retryable = false,
                Error = null,
                WorkerId = "fail-fast",
            });

        var orchestrator = BuildOrchestratorWithExecutors(
            new[] { deterministic.Object },
            new PipelineExecutionOptions { MaxRetryAttempts = 3, RetryDelayMs = 1 });

        var run = await orchestrator.RunAsync(new PipelineExecutionRequest
        {
            RunId = "run-no-retry",
            Template = SingleStageTemplate("fail-fast"),
        }, CancellationToken.None);

        run.State.Should().Be(PipelineRunState.Failed);
        run.StageRuns.Single().Error.Should().Contain("fail-fast");
        deterministic.Verify(
            x => x.ExecuteAsync(It.IsAny<PipelineStageExecutionRequest>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RunAsync_resume_skips_unknown_prior_stages_not_in_graph()
    {
        var orchestrator = PipelineOrchestratorTests.BuildOrchestratorForGap(
            out var runStore,
            new PipelineExecutionOptions { MaxRetryAttempts = 1, RetryDelayMs = 1, ResumeFailedStages = true });

        var template = new PipelineTemplate
        {
            TemplateId = "resume-trim",
            Stages = new[] { new PipelineStageDefinition { Id = "only", Name = "Only", Mode = PipelineExecutionMode.Deterministic } },
        };

        await runStore.PutAsync(new PipelineRun
        {
            RunId = "prior-trim",
            TemplateId = "resume-trim",
            State = PipelineRunState.Failed,
            StageRuns = new[]
            {
                new PipelineStageRun { StageId = "only", State = PipelineStageRunState.Completed, Attempt = 1, WorkerType = PipelineWorkerType.Deterministic },
                new PipelineStageRun { StageId = "removed", State = PipelineStageRunState.Failed, Attempt = 1, WorkerType = PipelineWorkerType.Deterministic },
            },
        });

        var run = await orchestrator.RunAsync(new PipelineExecutionRequest
        {
            RunId = "resume-trim-run",
            ResumeRunId = "prior-trim",
            Template = template,
        }, CancellationToken.None);

        run.State.Should().Be(PipelineRunState.Completed);
        run.StageRuns.Should().ContainSingle(x => x.StageId == "only");
    }

    [Fact]
    public async Task RunAsync_fails_when_no_executors_registered_for_worker_chain()
    {
        var orchestrator = BuildOrchestratorWithExecutors(
            Array.Empty<IPipelineStageExecutor>(),
            new PipelineExecutionOptions { MaxRetryAttempts = 1, RetryDelayMs = 1 });

        var run = await orchestrator.RunAsync(new PipelineExecutionRequest
        {
            RunId = "run-no-executors",
            Template = SingleStageTemplate("solo"),
        }, CancellationToken.None);

        run.State.Should().Be(PipelineRunState.Failed);
        run.StageRuns.Single().Error.Should().Contain("exhausted retries and fallback chain");
    }

    [Fact]
    public async Task RunAsync_uses_first_executor_when_duplicate_worker_types_registered()
    {
        var first = new Mock<IPipelineStageExecutor>();
        first.SetupGet(x => x.WorkerType).Returns(PipelineWorkerType.Deterministic);
        first.Setup(x => x.ExecuteAsync(It.IsAny<PipelineStageExecutionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineStageExecutionResult
            {
                Succeeded = true,
                Retryable = false,
                WorkerId = "first-executor",
                Output = "from-first",
            });

        var second = new Mock<IPipelineStageExecutor>();
        second.SetupGet(x => x.WorkerType).Returns(PipelineWorkerType.Deterministic);
        second.Setup(x => x.ExecuteAsync(It.IsAny<PipelineStageExecutionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineStageExecutionResult
            {
                Succeeded = true,
                Retryable = false,
                WorkerId = "second-executor",
                Output = "from-second",
            });

        var orchestrator = BuildOrchestratorWithExecutors(
            new IPipelineStageExecutor[] { first.Object, second.Object },
            new PipelineExecutionOptions { MaxRetryAttempts = 1, RetryDelayMs = 1 });

        var run = await orchestrator.RunAsync(new PipelineExecutionRequest
        {
            RunId = "run-dup-executor",
            Template = SingleStageTemplate("dup"),
        }, CancellationToken.None);

        run.State.Should().Be(PipelineRunState.Completed);
        run.StageRuns.Single().WorkerId.Should().Be("first-executor");
        second.Verify(
            x => x.ExecuteAsync(It.IsAny<PipelineStageExecutionRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RunAsync_retries_retryable_failure_before_succeeding()
    {
        var attempts = 0;
        var deterministic = new Mock<IPipelineStageExecutor>();
        deterministic.SetupGet(x => x.WorkerType).Returns(PipelineWorkerType.Deterministic);
        deterministic
            .Setup(x => x.ExecuteAsync(It.IsAny<PipelineStageExecutionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                attempts++;
                return attempts == 1
                    ? new PipelineStageExecutionResult
                    {
                        Succeeded = false,
                        Retryable = true,
                        WorkerId = "retry-once",
                        Error = "transient",
                    }
                    : new PipelineStageExecutionResult
                    {
                        Succeeded = true,
                        Retryable = false,
                        WorkerId = "retry-once",
                        Output = "ok-after-retry",
                    };
            });

        var orchestrator = BuildOrchestratorWithExecutors(
            new[] { deterministic.Object },
            new PipelineExecutionOptions { MaxRetryAttempts = 2, RetryDelayMs = 1 });

        var run = await orchestrator.RunAsync(new PipelineExecutionRequest
        {
            RunId = "run-retry-success",
            Template = SingleStageTemplate("retry"),
        }, CancellationToken.None);

        run.State.Should().Be(PipelineRunState.Completed);
        attempts.Should().Be(2);
        deterministic.Verify(
            x => x.ExecuteAsync(It.IsAny<PipelineStageExecutionRequest>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [Fact]
    public void Constructor_null_dependencies_throw()
    {
        var validator = new PipelineTemplateValidator();
        var decomposer = new PipelineDecomposer(validator);
        var scheduler = new PipelineScheduler();
        var runStore = new InMemoryPipelineRunStore();
        var scaling = new ThresholdScalingPolicy();
        var options = Options.Create(new PipelineExecutionOptions());
        var executors = Array.Empty<IPipelineStageExecutor>();

        var act = () => new PipelineOrchestrator(null!, decomposer, scheduler, runStore, scaling, executors, options, NullLogger<PipelineOrchestrator>.Instance);
        act.Should().Throw<ArgumentNullException>();
    }

    private static PipelineOrchestrator BuildOrchestratorWithExecutors(
        IReadOnlyList<IPipelineStageExecutor> executors,
        PipelineExecutionOptions options)
    {
        var validator = new PipelineTemplateValidator();
        var decomposer = new PipelineDecomposer(validator);
        return new PipelineOrchestrator(
            validator,
            decomposer,
            new PipelineScheduler(),
            new InMemoryPipelineRunStore(),
            new ThresholdScalingPolicy(),
            executors,
            Options.Create(options),
            NullLogger<PipelineOrchestrator>.Instance);
    }

    /// <summary>Single stage template.</summary>
    /// <param name="stageId">Stage id.</param>
    private static PipelineTemplate SingleStageTemplate(string stageId) => new()
    {
        TemplateId = stageId + "-template",
        Stages = new[] { new PipelineStageDefinition { Id = stageId, Name = stageId, Mode = PipelineExecutionMode.Deterministic } },
    };
}
