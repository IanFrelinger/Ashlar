using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Ashlar.Core.Application.Pipelines.Models;
using Ashlar.Core.Application.Pipelines.Ports;
using Ashlar.Infrastructure.Pipelines;
using Ashlar.Tests.Infrastructure.Helpers;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Pipelines;

/// <summary>Tests for pipeline orchestrator.</summary>
public sealed class PipelineOrchestratorTests
{
    [Fact]
    public async Task RunAsync_HappyPath_CompletesRun()
    {
        var orchestrator = BuildOrchestrator(
            out var runStore,
            executionOptions: new PipelineExecutionOptions
            {
                MaxRetryAttempts = 2,
                RetryDelayMs = 1
            });

        var template = new PipelineTemplate
        {
            TemplateId = "happy-path",
            Stages = new[]
            {
                new PipelineStageDefinition { Id = "ingest", Name = "Ingest", Mode = PipelineExecutionMode.Deterministic },
                new PipelineStageDefinition { Id = "enrich", Name = "Enrich", Mode = PipelineExecutionMode.Agentic },
                new PipelineStageDefinition
                {
                    Id = "merge",
                    Name = "Merge",
                    StageType = PipelineStageType.FanIn,
                    JoinStrategy = PipelineJoinStrategy.All,
                    Mode = PipelineExecutionMode.Hybrid,
                    FallbackChain = new[] { PipelineWorkerType.Deterministic, PipelineWorkerType.Agentic }
                }
            },
            Edges = new[]
            {
                new PipelineEdge("ingest", "enrich"),
                new PipelineEdge("enrich", "merge")
            }
        };

        var run = await orchestrator.RunAsync(new PipelineExecutionRequest
        {
            RunId = "run-happy",
            Template = template
        });

        run.State.Should().Be(PipelineRunState.Completed);
        run.StageRuns.Should().OnlyContain(x => x.State == PipelineStageRunState.Completed);

        var saved = await runStore.GetAsync("run-happy");
        saved.Should().NotBeNull();
        saved!.State.Should().Be(PipelineRunState.Completed);
    }

    [Fact]
    public async Task RunAsync_WhenDeterministicFails_HybridFallsBackToAgentic()
    {
        var orchestrator = BuildOrchestrator(
            out _,
            executionOptions: new PipelineExecutionOptions
            {
                MaxRetryAttempts = 1,
                RetryDelayMs = 1,
                EnableTestHooks = true
            });

        var template = new PipelineTemplate
        {
            TemplateId = "fallback",
            Stages = new[]
            {
                new PipelineStageDefinition { Id = "root", Name = "Root", Mode = PipelineExecutionMode.Deterministic },
                new PipelineStageDefinition
                {
                    Id = "hybrid-stage",
                    Name = "Hybrid",
                    Mode = PipelineExecutionMode.Hybrid,
                    FallbackChain = new[] { PipelineWorkerType.Deterministic, PipelineWorkerType.Agentic }
                }
            },
            Edges = new[]
            {
                new PipelineEdge("root", "hybrid-stage")
            }
        };

        var run = await orchestrator.RunAsync(new PipelineExecutionRequest
        {
            RunId = "run-fallback",
            Template = template,
            Inputs = new Dictionary<string, string>
            {
                ["fail:hybrid-stage:deterministic"] = "true"
            }
        });

        run.State.Should().Be(PipelineRunState.Completed);
        var hybrid = run.StageRuns.Single(x => x.StageId == "hybrid-stage");
        hybrid.State.Should().Be(PipelineStageRunState.Completed);
        hybrid.WorkerType.Should().Be(PipelineWorkerType.Agentic);
    }

    [Fact]
    public async Task RunAsync_WhenCriticalStageFails_FailsFast()
    {
        var orchestrator = BuildOrchestrator(
            out _,
            executionOptions: new PipelineExecutionOptions
            {
                MaxRetryAttempts = 1,
                RetryDelayMs = 1,
                EnableTestHooks = true
            });

        var template = new PipelineTemplate
        {
            TemplateId = "critical-fail-fast",
            Stages = new[]
            {
                new PipelineStageDefinition
                {
                    Id = "critical",
                    Name = "Critical",
                    Mode = PipelineExecutionMode.Deterministic,
                    Constraints = new PipelineStageConstraints
                    {
                        Critical = true,
                        RequiresPostValidation = true
                    }
                },
                new PipelineStageDefinition
                {
                    Id = "downstream",
                    Name = "Downstream",
                    Mode = PipelineExecutionMode.Agentic
                }
            },
            Edges = new[]
            {
                new PipelineEdge("critical", "downstream")
            }
        };

        var run = await orchestrator.RunAsync(new PipelineExecutionRequest
        {
            RunId = "run-critical",
            Template = template,
            Inputs = new Dictionary<string, string>
            {
                ["fail:critical:deterministic"] = "true"
            }
        });

        run.State.Should().Be(PipelineRunState.Failed);
        var critical = run.StageRuns.Single(x => x.StageId == "critical");
        critical.State.Should().Be(PipelineStageRunState.Failed);
        var downstream = run.StageRuns.Single(x => x.StageId == "downstream");
        downstream.State.Should().Be(PipelineStageRunState.Pending);
    }

    [Fact]
    public async Task RunAsync_WithResumeRunId_CarriesForwardCompletedStages()
    {
        var orchestrator = BuildOrchestrator(
            out var runStore,
            executionOptions: new PipelineExecutionOptions
            {
                MaxRetryAttempts = 1,
                RetryDelayMs = 1
            });

        var template = new PipelineTemplate
        {
            TemplateId = "resume-template",
            Stages = new[]
            {
                new PipelineStageDefinition { Id = "a", Name = "A", Mode = PipelineExecutionMode.Deterministic },
                new PipelineStageDefinition { Id = "b", Name = "B", Mode = PipelineExecutionMode.Agentic },
                new PipelineStageDefinition { Id = "c", Name = "C", Mode = PipelineExecutionMode.Deterministic }
            },
            Edges = new[]
            {
                new PipelineEdge("a", "b"),
                new PipelineEdge("b", "c")
            }
        };

        await runStore.PutAsync(new PipelineRun
        {
            RunId = "prior-run",
            TemplateId = "resume-template",
            State = PipelineRunState.Failed,
            StageRuns = new[]
            {
                new PipelineStageRun
                {
                    StageId = "a",
                    State = PipelineStageRunState.Completed,
                    Attempt = 1,
                    WorkerType = PipelineWorkerType.Deterministic
                },
                new PipelineStageRun
                {
                    StageId = "b",
                    State = PipelineStageRunState.Failed,
                    Attempt = 1,
                    WorkerType = PipelineWorkerType.Agentic,
                    Error = "prior failure"
                },
                new PipelineStageRun
                {
                    StageId = "c",
                    State = PipelineStageRunState.Pending,
                    Attempt = 0
                }
            }
        });

        var run = await orchestrator.RunAsync(new PipelineExecutionRequest
        {
            RunId = "resumed-run",
            ResumeRunId = "prior-run",
            ResumeFailedStages = true,
            Template = template
        });

        run.State.Should().Be(PipelineRunState.Completed);
        Assert.All(run.StageRuns, s => Assert.Equal(PipelineStageRunState.Completed, s.State));
        Assert.Equal(1, run.StageRuns.Single(x => x.StageId == "a").Attempt);
    }

    [Fact]
    public async Task RunAsync_WhenFanInFirstSuccess_ProceedsAfterSingleCompletedPredecessor()
    {
        var orchestrator = BuildOrchestrator(
            out _,
            executionOptions: new PipelineExecutionOptions
            {
                MaxRetryAttempts = 1,
                RetryDelayMs = 1,
                EnableTestHooks = true,
                CompletionPolicy = PipelineCompletionPolicy.AllowNonCriticalStageFailures
            });

        var template = new PipelineTemplate
        {
            TemplateId = "fanin-first-success",
            Stages = new[]
            {
                new PipelineStageDefinition { Id = "a", Name = "A", Mode = PipelineExecutionMode.Deterministic },
                new PipelineStageDefinition { Id = "b", Name = "B", Mode = PipelineExecutionMode.Deterministic },
                new PipelineStageDefinition
                {
                    Id = "join",
                    Name = "Join",
                    StageType = PipelineStageType.FanIn,
                    JoinStrategy = PipelineJoinStrategy.FirstSuccess,
                    Mode = PipelineExecutionMode.Deterministic
                }
            },
            Edges = new[]
            {
                new PipelineEdge("a", "join"),
                new PipelineEdge("b", "join")
            }
        };

        var run = await orchestrator.RunAsync(new PipelineExecutionRequest
        {
            RunId = "run-fanin-first",
            Template = template,
            Inputs = new Dictionary<string, string>
            {
                ["fail:b:deterministic"] = "true"
            }
        });

        run.State.Should().Be(PipelineRunState.Completed);
        run.StageRuns.Single(x => x.StageId == "join").State.Should().Be(PipelineStageRunState.Completed);
    }

    [Fact]
    public async Task RunAsync_WhenNonCriticalStageFails_DefaultPolicyFailsRun()
    {
        var orchestrator = BuildOrchestrator(
            out _,
            executionOptions: new PipelineExecutionOptions
            {
                MaxRetryAttempts = 1,
                RetryDelayMs = 1,
                EnableTestHooks = true
            });

        var template = new PipelineTemplate
        {
            TemplateId = "fail-closed-default",
            Stages = new[]
            {
                new PipelineStageDefinition { Id = "a", Name = "A", Mode = PipelineExecutionMode.Deterministic },
                new PipelineStageDefinition { Id = "b", Name = "B", Mode = PipelineExecutionMode.Deterministic },
                new PipelineStageDefinition
                {
                    Id = "join",
                    Name = "Join",
                    StageType = PipelineStageType.FanIn,
                    JoinStrategy = PipelineJoinStrategy.FirstSuccess,
                    Mode = PipelineExecutionMode.Deterministic
                }
            },
            Edges = new[]
            {
                new PipelineEdge("a", "join"),
                new PipelineEdge("b", "join")
            }
        };

        var run = await orchestrator.RunAsync(new PipelineExecutionRequest
        {
            RunId = "run-fail-closed-default",
            Template = template,
            Inputs = new Dictionary<string, string>
            {
                ["fail:b:deterministic"] = "true"
            }
        });

        run.State.Should().Be(PipelineRunState.Failed);
        run.StageRuns.Single(x => x.StageId == "join").State.Should().Be(PipelineStageRunState.Completed);
        run.StageRuns.Single(x => x.StageId == "b").State.Should().Be(PipelineStageRunState.Failed);
    }

    [Fact]
    public async Task RunAsync_WithMissingResumeSource_ThrowsByDefault()
    {
        var orchestrator = BuildOrchestrator(
            out _,
            executionOptions: new PipelineExecutionOptions
            {
                MaxRetryAttempts = 1,
                RetryDelayMs = 1
            });

        var template = new PipelineTemplate
        {
            TemplateId = "resume-missing-fails",
            Stages = new[]
            {
                new PipelineStageDefinition { Id = "only", Name = "Only", Mode = PipelineExecutionMode.Deterministic }
            }
        };

        var act = async () => await orchestrator.RunAsync(new PipelineExecutionRequest
        {
            RunId = "resume-missing-fails-run",
            ResumeRunId = "does-not-exist",
            Template = template
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*no prior run was found*");
    }

    [Fact]
    public async Task RunAsync_WithMissingResumeSource_AllowedByOptionStartsFresh()
    {
        var orchestrator = BuildOrchestrator(
            out _,
            executionOptions: new PipelineExecutionOptions
            {
                MaxRetryAttempts = 1,
                RetryDelayMs = 1,
                AllowMissingResumeSource = true
            });

        var template = new PipelineTemplate
        {
            TemplateId = "resume-missing-allowed",
            Stages = new[]
            {
                new PipelineStageDefinition { Id = "only", Name = "Only", Mode = PipelineExecutionMode.Deterministic }
            }
        };

        var run = await orchestrator.RunAsync(new PipelineExecutionRequest
        {
            RunId = "resume-missing-allowed-run",
            ResumeRunId = "does-not-exist",
            Template = template
        });

        run.State.Should().Be(PipelineRunState.Completed);
        run.StageRuns.Single(x => x.StageId == "only").State.Should().Be(PipelineStageRunState.Completed);
    }

    internal static PipelineOrchestrator BuildOrchestratorForGap(
        out IPipelineRunStore runStore,
        PipelineExecutionOptions? executionOptions = null) =>
        /// <summary>Creates orchestrator.</summary>
        /// <param name="runStore">Run store.</param>
        BuildOrchestrator(out runStore, executionOptions);

    private static PipelineOrchestrator BuildOrchestrator(
        out IPipelineRunStore runStore,
        PipelineExecutionOptions? executionOptions = null)
    {
        runStore = new InMemoryPipelineRunStore();
        var validator = new PipelineTemplateValidator();
        var decomposer = new PipelineDecomposer(validator);
        var scheduler = new PipelineScheduler();
        var scaling = new ThresholdScalingPolicy();
        var options = executionOptions ?? new PipelineExecutionOptions();
        var adapters = new IPipelineStageExecutionAdapter[]
        {
            new TestDeterministicAdapter(),
            new TestAgenticAdapter()
        };
        var adapterOptions = Options.Create(new PipelineExecutionAdapterOptions
        {
            DeterministicAdapter = "default",
            AgenticAdapter = "default"
        });
        var executionOptionsValue = Options.Create(options);
        var executors = new IPipelineStageExecutor[]
        {
            new DeterministicPipelineStageExecutor(
                NullLogger<DeterministicPipelineStageExecutor>.Instance,
                adapters,
                adapterOptions,
                executionOptionsValue),
            new AgenticPipelineStageExecutor(
                NullLogger<AgenticPipelineStageExecutor>.Instance,
                adapters,
                adapterOptions,
                executionOptionsValue)
        };
        var optionsWrapper = Options.Create(options);
        return new PipelineOrchestrator(
            validator,
            decomposer,
            scheduler,
            runStore,
            scaling,
            executors,
            optionsWrapper,
            NullLogger<PipelineOrchestrator>.Instance);
    }

    /// <summary>Tests for test deterministic adapter.</summary>
    private sealed class TestDeterministicAdapter : IPipelineStageExecutionAdapter
    {
        public string AdapterKey => "default";
        public PipelineWorkerType WorkerType => PipelineWorkerType.Deterministic;

        public Task<PipelineStageExecutionResult> ExecuteAsync(
            PipelineStageExecutionRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new PipelineStageExecutionResult
            {
                Succeeded = true,
                Retryable = false,
                WorkerId = "deterministic-default",
                Output = $"deterministic:{request.StageId}:ok"
            });
        }
    }

    /// <summary>Tests for test agentic adapter.</summary>
    private sealed class TestAgenticAdapter : IPipelineStageExecutionAdapter
    {
        public string AdapterKey => "default";
        public PipelineWorkerType WorkerType => PipelineWorkerType.Agentic;

        public Task<PipelineStageExecutionResult> ExecuteAsync(
            PipelineStageExecutionRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new PipelineStageExecutionResult
            {
                Succeeded = true,
                Retryable = false,
                WorkerId = "agentic-default",
                Output = $"agentic:{request.StageId}:ok"
            });
        }
    }
}
