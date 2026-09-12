using LiteDB;
using Ashlar.Core.Application.Persistence;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Ashlar.Core.Application.Pipelines.Models;
using Ashlar.Core.Application.Pipelines.Ports;
using Ashlar.Core.Domain;
using Ashlar.Infrastructure.Pipelines;
using Ashlar.Tests.Infrastructure.Helpers;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Pipelines;

/// <summary>
/// E2E lifecycle tests for the pipeline system. Covers defaults alignment with
/// <see cref="AshlarDefaults"/>, happy path, resume, concurrent execution, store
/// persistence, and fan-in topology.
/// </summary>
[Trait("Category", "E2E")]
public sealed class PipelineLifecycleE2ETests
{
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
                adapters, adapterOptions, executionOptionsValue),
            new AgenticPipelineStageExecutor(
                NullLogger<AgenticPipelineStageExecutor>.Instance,
                adapters, adapterOptions, executionOptionsValue)
        };
        return new PipelineOrchestrator(
            validator, decomposer, scheduler, runStore, scaling, executors,
            Options.Create(options),
            NullLogger<PipelineOrchestrator>.Instance);
    }

    private static PipelineTemplate SingleStageTemplate(string id) =>
        new()
        {
            TemplateId = id,
            Stages = new[]
            {
                new PipelineStageDefinition { Id = "stage1", Name = "Stage1", Mode = PipelineExecutionMode.Deterministic }
            },
            Edges = Array.Empty<PipelineEdge>()
        };

    [Fact(Timeout = TestTimeouts.E2E)]
    public async Task DefaultOptions_UseAshlarDefaults()
    {
        await Task.CompletedTask;
        var opts = new PipelineExecutionOptions();
        Assert.Equal(AshlarDefaults.PipelineMaxRetryAttempts, opts.MaxRetryAttempts);
        Assert.Equal(AshlarDefaults.PipelineRetryDelayMs, opts.RetryDelayMs);
        opts.ResumeFailedStages.Should().BeTrue();
        opts.CompletionPolicy.Should().Be(PipelineCompletionPolicy.FailOnAnyStageFailure);
    }

    [Fact(Timeout = TestTimeouts.E2E)]
    public async Task HappyPath_SingleStage_Completes()
    {
        var orchestrator = BuildOrchestrator(out var store, new PipelineExecutionOptions { RetryDelayMs = 1 });
        var run = await orchestrator.RunAsync(new PipelineExecutionRequest
        {
            RunId = "run-happy-lifecycle",
            Template = SingleStageTemplate("happy-lifecycle")
        });

        run.State.Should().Be(PipelineRunState.Completed);
        var saved = await store.GetAsync("run-happy-lifecycle");
        saved.Should().NotBeNull();
        saved!.State.Should().Be(PipelineRunState.Completed);
    }

    [Fact(Timeout = TestTimeouts.E2E)]
    public async Task MultiStage_LinearPipeline_AllComplete()
    {
        var orchestrator = BuildOrchestrator(out _, new PipelineExecutionOptions { RetryDelayMs = 1 });
        var template = new PipelineTemplate
        {
            TemplateId = "linear-lifecycle",
            Stages = new[]
            {
                new PipelineStageDefinition { Id = "a", Name = "A", Mode = PipelineExecutionMode.Deterministic },
                new PipelineStageDefinition { Id = "b", Name = "B", Mode = PipelineExecutionMode.Agentic },
                new PipelineStageDefinition { Id = "c", Name = "C", Mode = PipelineExecutionMode.Deterministic }
            },
            Edges = new[] { new PipelineEdge("a", "b"), new PipelineEdge("b", "c") }
        };

        var run = await orchestrator.RunAsync(new PipelineExecutionRequest
        {
            RunId = "run-linear-lifecycle",
            Template = template
        });

        run.State.Should().Be(PipelineRunState.Completed);
        run.StageRuns.Should().HaveCount(3);
        run.StageRuns.Should().OnlyContain(s => s.State == PipelineStageRunState.Completed);
    }

    [Fact(Timeout = TestTimeouts.E2E)]
    public async Task MissingResumeSource_WhenNotAllowed_Throws()
    {
        var orchestrator = BuildOrchestrator(out _, new PipelineExecutionOptions
        {
            AllowMissingResumeSource = false, RetryDelayMs = 1
        });

        var act = async () => await orchestrator.RunAsync(new PipelineExecutionRequest
        {
            RunId = "run-missing-lifecycle",
            Template = SingleStageTemplate("missing-lifecycle"),
            ResumeRunId = "nonexistent-run"
        });

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact(Timeout = TestTimeouts.E2E)]
    public async Task MissingResumeSource_WhenAllowed_StartsFreshRun()
    {
        var orchestrator = BuildOrchestrator(out _, new PipelineExecutionOptions
        {
            AllowMissingResumeSource = true, RetryDelayMs = 1
        });

        var run = await orchestrator.RunAsync(new PipelineExecutionRequest
        {
            RunId = "run-fresh-lifecycle",
            Template = SingleStageTemplate("fresh-lifecycle"),
            ResumeRunId = "nonexistent-run"
        });

        run.State.Should().Be(PipelineRunState.Completed);
    }

    [Fact(Timeout = TestTimeouts.E2E)]
    public async Task ConcurrentPipelineRuns_DoNotInterfere()
    {
        var orchestrator = BuildOrchestrator(out var store, new PipelineExecutionOptions { RetryDelayMs = 1 });
        var tasks = Enumerable.Range(0, 10).Select(i =>
            orchestrator.RunAsync(new PipelineExecutionRequest
            {
                RunId = $"run-concurrent-lifecycle-{i}",
                Template = SingleStageTemplate($"concurrent-lifecycle-{i}")
            }));

        var runs = await Task.WhenAll(tasks);
        runs.Should().AllSatisfy(r => r.State.Should().Be(PipelineRunState.Completed));

        for (var i = 0; i < 10; i++)
        {
            var saved = await store.GetAsync($"run-concurrent-lifecycle-{i}");
            saved.Should().NotBeNull();
        }
    }

    [Fact(Timeout = TestTimeouts.E2E)]
    public async Task FanInPipeline_JoinAll_CompletesAllBranches()
    {
        var orchestrator = BuildOrchestrator(out _, new PipelineExecutionOptions { RetryDelayMs = 1 });
        var template = new PipelineTemplate
        {
            TemplateId = "fan-in-lifecycle",
            Stages = new[]
            {
                new PipelineStageDefinition { Id = "src1", Name = "Src1", Mode = PipelineExecutionMode.Deterministic },
                new PipelineStageDefinition { Id = "src2", Name = "Src2", Mode = PipelineExecutionMode.Deterministic },
                new PipelineStageDefinition
                {
                    Id = "merge", Name = "Merge",
                    StageType = PipelineStageType.FanIn,
                    JoinStrategy = PipelineJoinStrategy.All,
                    Mode = PipelineExecutionMode.Hybrid,
                    FallbackChain = new[] { PipelineWorkerType.Deterministic, PipelineWorkerType.Agentic }
                }
            },
            Edges = new[] { new PipelineEdge("src1", "merge"), new PipelineEdge("src2", "merge") }
        };

        var run = await orchestrator.RunAsync(new PipelineExecutionRequest
        {
            RunId = "run-fan-in-lifecycle",
            Template = template
        });

        run.State.Should().Be(PipelineRunState.Completed);
        run.StageRuns.Should().HaveCount(3);
    }

    [Fact(Timeout = TestTimeouts.E2E)]
    public async Task StoreRetrieval_AfterCompletion_ReturnsCorrectData()
    {
        var orchestrator = BuildOrchestrator(out var store, new PipelineExecutionOptions { RetryDelayMs = 1 });
        await orchestrator.RunAsync(new PipelineExecutionRequest
        {
            RunId = "run-persist-lifecycle",
            Template = SingleStageTemplate("persist-lifecycle")
        });

        var saved = await store.GetAsync("run-persist-lifecycle");
        saved.Should().NotBeNull();
        saved!.RunId.Should().Be("run-persist-lifecycle");
        saved.TemplateId.Should().Be("persist-lifecycle");
        saved.State.Should().Be(PipelineRunState.Completed);
        saved.StageRuns.Should().ContainSingle(s => s.StageId == "stage1");
    }

    [Fact(Timeout = TestTimeouts.E2E)]
    public async Task CompletionPolicy_EnumValues()
    {
        await Task.CompletedTask;
        Enum.GetValues<PipelineCompletionPolicy>().Should().HaveCount(2);
    }

    /// <summary>A claimed destination refuses another execution before any duplicate side effect.</summary>
    [Theory(Timeout = TestTimeouts.E2E)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_existing_destination_is_refused_before_an_executor_runs(bool durable)
    {
        var path = Path.Combine(Path.GetTempPath(), $"pipeline-owner-{Guid.NewGuid():N}.db");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<PipelineRun>? first = null;
        try
        {
            IPipelineRunStore ownerStore = durable ? new LiteDbPipelineRunStore(path) : new InMemoryPipelineRunStore();
            IPipelineRunStore contenderStore = durable ? new LiteDbPipelineRunStore(path) : ownerStore;
            var owner = new CountingAdapter(async request =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(TimeSpan.FromSeconds(20));
                return new PipelineStageExecutionResult { Succeeded = true, WorkerId = "owner", Output = "owner-output" };
            });
            var contender = new CountingAdapter(request => Task.FromResult(
                new PipelineStageExecutionResult { Succeeded = true, WorkerId = "contender", Output = "wrong-output" }));
            var template = OwnershipTemplate("a");
            first = OwnershipOrchestrator(ownerStore, owner).RunAsync(new PipelineExecutionRequest
            {
                RunId = "owned", Template = template,
            });
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(20));

            var duplicate = () => OwnershipOrchestrator(contenderStore, contender).RunAsync(
                new PipelineExecutionRequest { RunId = "owned", Template = template });
            await duplicate.Should().ThrowAsync<InvalidOperationException>().WithMessage("*already exists*fresh --run-id*");
            contender.Invocations.Should().Be(0, "refusal must precede every executor call");

            release.TrySetResult();
            var finished = await first;
            finished.State.Should().Be(PipelineRunState.Completed);
            owner.Invocations.Should().Be(1, "the accepted execution must actually do its work");
            var persisted = await contenderStore.GetAsync("owned");
            persisted!.StageRuns.Should().BeEquivalentTo(finished.StageRuns);
            persisted.StageRuns.Should().ContainSingle().Which.Output.Should().Be("owner-output");

            // Completed IDs are also occupied; same-ID resume must not quietly rewrite history.
            var sameIdResume = () => OwnershipOrchestrator(contenderStore, contender).RunAsync(
                new PipelineExecutionRequest { RunId = "owned", ResumeRunId = "owned", Template = template });
            await sameIdResume.Should().ThrowAsync<InvalidOperationException>().WithMessage("*already exists*");
            contender.Invocations.Should().Be(0);
            (await contenderStore.GetAsync("owned"))!.Should().BeEquivalentTo(persisted);
        }
        finally
        {
            release.TrySetResult();
            if (first != null) await first;
            if (File.Exists(path)) File.Delete(path);
        }
    }

    /// <summary>Two concurrent starts persist one complete execution, never a union of results.</summary>
    [Theory(Timeout = TestTimeouts.E2E)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Concurrent_starts_execute_exactly_one_complete_run(bool durable)
    {
        var path = Path.Combine(Path.GetTempPath(), $"pipeline-start-race-{Guid.NewGuid():N}.db");
        try
        {
            var inMemory = new InMemoryPipelineRunStore();
            for (var round = 0; round < 8; round++)
            {
                var id = $"race-{round}";
                var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var ready = new CountdownEvent(2);
                var adapters = new[] { "left", "right" }.Select(worker => new CountingAdapter(request =>
                    Task.FromResult(new PipelineStageExecutionResult
                    {
                        Succeeded = request.StageId != (worker == "left" ? "a" : "b"),
                        Retryable = false,
                        WorkerId = worker,
                        Output = request.StageId != (worker == "left" ? "a" : "b") ? $"{worker}:{request.StageId}" : null,
                        Error = request.StageId == (worker == "left" ? "a" : "b") ? $"{worker}-failure" : null,
                    }))).ToArray();
                var tasks = adapters.Select(adapter => Task.Run(async () =>
                {
                    IPipelineRunStore store = durable ? new LiteDbPipelineRunStore(path) : inMemory;
                    var orchestrator = OwnershipOrchestrator(store, adapter);
                    ready.Signal();
                    await start.Task;
                    try
                    {
                        return await orchestrator.RunAsync(new PipelineExecutionRequest
                        {
                            RunId = id, Template = OwnershipTemplate("a", "b", "c", "d"),
                        });
                    }
                    catch (InvalidOperationException ex) when (ex.Message.StartsWith($"Pipeline run '{id}' already exists.", StringComparison.Ordinal))
                    {
                        // Only the explicit ID conflict is a legitimate loser. Database/file
                        // exceptions propagate; none can stand in for ownership enforcement.
                        return null;
                    }
                })).ToArray();
                try
                {
                    (await Task.Run(() => ready.Wait(TimeSpan.FromSeconds(20)))).Should().BeTrue();
                }
                finally { start.TrySetResult(); }
                var results = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(30));
                var winner = results.Where(run => run != null).Should().ContainSingle().Subject!;
                adapters.Select(adapter => adapter.Invocations).OrderBy(count => count).Should().Equal(0, 4);
                winner.State.Should().Be(PipelineRunState.Failed);
                winner.StageRuns.Count(row => row.State == PipelineStageRunState.Completed).Should().Be(3);
                winner.StageRuns.Count(row => row.State == PipelineStageRunState.Failed).Should().Be(1);

                IPipelineRunStore reader = durable ? new LiteDbPipelineRunStore(path) : inMemory;
                var saved = await reader.GetAsync(id);
                saved!.State.Should().Be(winner.State);
                saved.TemplateId.Should().Be(winner.TemplateId);
                saved.StageRuns.Should().BeEquivalentTo(winner.StageRuns, "every output, error, worker and attempt belongs to the same winner");
                if (durable)
                {
                    using var db = new LiteDatabase(LiteDbConnectionString.ForSharedAccess(path));
                    var document = db.GetCollection("pipeline_runs").FindById(new BsonValue(id));
                    document["State"].AsString.Should().Be(nameof(PipelineRunState.Failed));
                    document["StageRuns"].AsArray.Count(row => row["State"].AsString == nameof(PipelineStageRunState.Completed)).Should().Be(3);
                    document["StageRuns"].AsArray.Select(row => row["WorkerId"].AsString).Distinct().Should().ContainSingle();
                }
            }
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    /// <summary>Recovery reads a source without reopening or rewriting its execution.</summary>
    [Theory(Timeout = TestTimeouts.E2E)]
    [InlineData(false, PipelineRunState.Failed)]
    [InlineData(true, PipelineRunState.Failed)]
    [InlineData(false, PipelineRunState.Running)]
    [InlineData(true, PipelineRunState.Running)]
    public async Task Resume_uses_a_fresh_destination_and_preserves_the_source(bool durable, PipelineRunState sourceState)
    {
        var path = Path.Combine(Path.GetTempPath(), $"pipeline-resume-owner-{Guid.NewGuid():N}.db");
        try
        {
            IPipelineRunStore store = durable ? new LiteDbPipelineRunStore(path) : new InMemoryPipelineRunStore();
            await store.PutAsync(new PipelineRun
            {
                RunId = "source", TemplateId = "ownership", State = sourceState,
                StageRuns =
                [
                    new PipelineStageRun { StageId = "a", State = PipelineStageRunState.Completed, Output = "prior-output", Attempt = 2 },
                    new PipelineStageRun { StageId = "b", State = PipelineStageRunState.Failed, Error = "prior-error", Attempt = 1 },
                ],
            });
            var before = await store.GetAsync("source");
            var adapter = new CountingAdapter(request => Task.FromResult(new PipelineStageExecutionResult
            {
                Succeeded = true, WorkerId = "new-worker", Output = $"resumed:{request.StageId}",
            }));
            var resumed = await OwnershipOrchestrator(store, adapter).RunAsync(new PipelineExecutionRequest
            {
                ResumeRunId = "source", ResumeFailedStages = true, Template = OwnershipTemplate("a", "b"),
            });
            resumed.RunId.Should().NotBe("source");
            resumed.State.Should().Be(PipelineRunState.Completed);
            adapter.Invocations.Should().Be(1);
            resumed.StageRuns.Single(row => row.StageId == "a").Output.Should().Be("prior-output");
            resumed.StageRuns.Single(row => row.StageId == "b").Output.Should().Be("resumed:b");
            (await store.GetAsync("source"))!.Should().BeEquivalentTo(before);
            (await store.GetAsync(resumed.RunId))!.StageRuns.Should().BeEquivalentTo(resumed.StageRuns);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static PipelineTemplate OwnershipTemplate(params string[] stages) => new()
    {
        TemplateId = "ownership",
        Stages = stages.Select(id => new PipelineStageDefinition
        {
            Id = id, Name = id, Mode = PipelineExecutionMode.Deterministic,
            Constraints = new PipelineStageConstraints { Critical = false },
        }).ToArray(),
        Edges = Array.Empty<PipelineEdge>(),
    };

    private static PipelineOrchestrator OwnershipOrchestrator(IPipelineRunStore store, CountingAdapter adapter)
    {
        var validator = new PipelineTemplateValidator();
        var options = Options.Create(new PipelineExecutionOptions { RetryDelayMs = 1, MaxRetryAttempts = 1 });
        var executor = new DeterministicPipelineStageExecutor(
            NullLogger<DeterministicPipelineStageExecutor>.Instance, new[] { adapter },
            Options.Create(new PipelineExecutionAdapterOptions { DeterministicAdapter = "default" }), options);
        return new PipelineOrchestrator(validator, new PipelineDecomposer(validator), new PipelineScheduler(),
            store, new ThresholdScalingPolicy(), new[] { executor }, options, NullLogger<PipelineOrchestrator>.Instance);
    }

    private sealed class CountingAdapter(Func<PipelineStageExecutionRequest, Task<PipelineStageExecutionResult>> execute)
        : IPipelineStageExecutionAdapter
    {
        private int _invocations;
        public int Invocations => Volatile.Read(ref _invocations);
        public string AdapterKey => "default";
        public PipelineWorkerType WorkerType => PipelineWorkerType.Deterministic;
        public Task<PipelineStageExecutionResult> ExecuteAsync(PipelineStageExecutionRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _invocations);
            return execute(request);
        }
    }

    // ── Test adapters ────────────────────────────────────────────────

    private sealed class TestDeterministicAdapter : IPipelineStageExecutionAdapter
    {
        public string AdapterKey => "default";
        public PipelineWorkerType WorkerType => PipelineWorkerType.Deterministic;
        public Task<PipelineStageExecutionResult> ExecuteAsync(PipelineStageExecutionRequest request, CancellationToken ct = default) =>
            Task.FromResult(new PipelineStageExecutionResult { Succeeded = true, WorkerId = "det", Output = $"det:{request.StageId}" });
    }

    private sealed class TestAgenticAdapter : IPipelineStageExecutionAdapter
    {
        public string AdapterKey => "default";
        public PipelineWorkerType WorkerType => PipelineWorkerType.Agentic;
        public Task<PipelineStageExecutionResult> ExecuteAsync(PipelineStageExecutionRequest request, CancellationToken ct = default) =>
            Task.FromResult(new PipelineStageExecutionResult { Succeeded = true, WorkerId = "agt", Output = $"agt:{request.StageId}" });
    }
}
