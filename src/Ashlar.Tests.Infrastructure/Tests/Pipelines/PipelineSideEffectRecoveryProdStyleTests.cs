using System.Collections.Concurrent;
using Ashlar.Core.Application.Pipelines.Models;
using Ashlar.Core.Application.Pipelines.Ports;
using Ashlar.Core.Application.Persistence;
using Ashlar.Infrastructure.Pipelines;
using Ashlar.Tests.Infrastructure.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using LiteDB;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Pipelines;

/// <summary>Fault injection at the external-effect/checkpoint boundary using the real executor and durable run store.</summary>
[Trait("Category", "ProdStyle")]
public sealed class PipelineSideEffectRecoveryProdStyleTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ashlar-effect-recovery-" + Guid.NewGuid().ToString("N"));
    private string DatabasePath => Path.Combine(_directory, "runs.db");

    [Fact(Timeout = TestTimeouts.Integration)]
    public async Task Lost_completion_checkpoint_resumes_with_original_key_and_connector_deduplicates_concurrent_recovery()
    {
        var ledger = new ExternalLedger();
        var requests = new ConcurrentQueue<PipelineStageExecutionRequest>();
        var initialStore = DurableStore();
        var template = Template(PipelineStageEffects.Idempotent);
        var executor = new Adapter(PipelineWorkerType.Deterministic, request =>
        {
            requests.Enqueue(request);
            return Task.FromResult(ledger.Accept(request));
        });
        var failingStore = new FaultingStore(initialStore, run => run.StageRuns.Any(s => s.State == PipelineStageRunState.Completed));

        await Assert.ThrowsAsync<IOException>(() => Host(failingStore, executor).RunAsync(Request("source", template)));
        Assert.Equal(1, ledger.EffectCount);
        var checkpoint = Assert.Single((await DurableStore().GetAsync("source"))!.StageRuns);
        Assert.Equal(PipelineStageRunState.Running, checkpoint.State);
        Assert.Equal(1, checkpoint.Attempt);
        Assert.NotNull(checkpoint.IdempotencyKey);
        Assert.NotNull(checkpoint.ExecutionFingerprint);

        // Independent hosts and destination IDs can resume the same snapshot. The connector,
        // not a checkpoint lock, must enforce one external effect for the shared operation.
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrivals = 0;
        var recovering = new Adapter(PipelineWorkerType.Deterministic, async request =>
        {
            requests.Enqueue(request);
            if (Interlocked.Increment(ref arrivals) == 2) ready.TrySetResult();
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
            return ledger.Accept(request);
        });
        var resumed = await Task.WhenAll(
            Host(DurableStore(), recovering).RunAsync(Request("resumed-a", template, "source")),
            Host(DurableStore(), recovering).RunAsync(Request("resumed-b", template, "source")));

        Assert.All(resumed, run => Assert.Equal(PipelineRunState.Completed, run.State));
        Assert.All(resumed, run => Assert.Equal("source", run.ResumeSourceRunId));
        Assert.All(resumed, run => Assert.Equal(2, Assert.Single(run.StageRuns).Attempt));
        Assert.Equal(1, ledger.EffectCount);
        Assert.Equal(3, requests.Count);
        Assert.All(requests, request => Assert.Equal(checkpoint.IdempotencyKey, request.IdempotencyKey));
        Assert.Equal(PipelineStageRunState.Running, Assert.Single((await DurableStore().GetAsync("source"))!.StageRuns).State);
    }

    [Theory(Timeout = TestTimeouts.Integration)]
    [InlineData(PipelineStageEffects.Unspecified)]
    [InlineData(PipelineStageEffects.NonIdempotent)]
    public async Task Lost_completion_of_unsafe_effect_refuses_resume_before_creating_destination(PipelineStageEffects effects)
    {
        var invocations = 0;
        var adapter = new Adapter(PipelineWorkerType.Deterministic, _ =>
        {
            Interlocked.Increment(ref invocations); // Represents an irreversible external write.
            return Task.FromResult(Success());
        });
        var template = Template(effects);
        var store = DurableStore();
        var failing = new FaultingStore(store, run => run.StageRuns.Any(s => s.State == PipelineStageRunState.Completed));
        await Assert.ThrowsAsync<IOException>(() => Host(failing, adapter).RunAsync(Request("source", template)));
        var checkpoint = Assert.Single((await DurableStore().GetAsync("source"))!.StageRuns);
        Assert.Equal(PipelineStageRunState.Running, checkpoint.State);
        Assert.True(checkpoint.ReconciliationRequired);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Host(DurableStore(), adapter).RunAsync(Request("resumed", template, "source")));
        Assert.Contains("stage-reconciliation-required", error.Message);
        Assert.Equal(1, invocations);
        Assert.Null(await DurableStore().GetAsync("resumed"));
    }

    [Fact(Timeout = TestTimeouts.Integration)]
    public async Task A_lost_acknowledgement_after_completion_commit_does_not_roll_back_completed_stage()
    {
        var invocations = 0;
        var adapter = new Adapter(PipelineWorkerType.Deterministic, _ =>
        {
            invocations++;
            return Task.FromResult(Success());
        });
        var template = Template(PipelineStageEffects.NonIdempotent);
        var store = DurableStore();
        var failing = new FaultingStore(store, run => run.StageRuns.Any(s => s.State == PipelineStageRunState.Completed), afterCommit: true);
        await Assert.ThrowsAsync<IOException>(() => Host(failing, adapter).RunAsync(Request("source", template)));
        Assert.Equal(PipelineStageRunState.Completed, Assert.Single((await DurableStore().GetAsync("source"))!.StageRuns).State);
        var resumed = await Host(DurableStore(), adapter).RunAsync(Request("resumed", template, "source"));
        Assert.Equal(PipelineRunState.Completed, resumed.State);
        Assert.Equal(1, invocations);
    }

    [Theory(Timeout = TestTimeouts.Integration)]
    [InlineData(PipelineStageEffects.Unspecified, true)]
    [InlineData(PipelineStageEffects.NonIdempotent, true)]
    [InlineData(PipelineStageEffects.NonIdempotent, false)]
    public async Task Unsafe_failed_result_stops_retry_and_fallback_even_when_noncritical_failures_are_allowed(
        PipelineStageEffects effects, bool retryable)
    {
        var primaryCalls = 0;
        var fallbackCalls = 0;
        var primary = new Adapter(PipelineWorkerType.Deterministic, _ =>
        {
            primaryCalls++;
            return Task.FromResult(new PipelineStageExecutionResult { Succeeded = false, Retryable = retryable, Error = "response lost after write" });
        });
        var fallback = new Adapter(PipelineWorkerType.Agentic, _ =>
        {
            fallbackCalls++;
            return Task.FromResult(Success());
        });
        var options = new PipelineExecutionOptions
        {
            MaxRetryAttempts = 3, RetryDelayMs = 0,
            CompletionPolicy = PipelineCompletionPolicy.AllowNonCriticalStageFailures
        };
        var run = await Host(DurableStore(), primary, fallback, options).RunAsync(Request("unsafe", Template(effects, hybrid: true)));
        Assert.Equal(PipelineRunState.Failed, run.State);
        Assert.True(Assert.Single(run.StageRuns).ReconciliationRequired);
        Assert.Contains("stage-reconciliation-required", Assert.Single(run.StageRuns).Error!);
        Assert.Equal(1, primaryCalls);
        Assert.Equal(0, fallbackCalls);
        options.ResumeFailedStages = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Host(DurableStore(), primary, fallback, options)
            .RunAsync(Request("resumed", Template(effects, hybrid: true), "unsafe") with { ResumeFailedStages = false }));
        Assert.Equal(1, primaryCalls);
        Assert.Equal(0, fallbackCalls);
    }

    [Fact(Timeout = TestTimeouts.Integration)]
    public async Task Idempotent_retry_and_fallback_share_one_external_operation_key()
    {
        var keys = new List<string?>();
        var ledger = new ExternalLedger();
        var primary = new Adapter(PipelineWorkerType.Deterministic, request =>
        {
            keys.Add(request.IdempotencyKey);
            ledger.Accept(request);
            return Task.FromResult(new PipelineStageExecutionResult { Succeeded = false, Retryable = true, Error = "lost response" });
        });
        var fallback = new Adapter(PipelineWorkerType.Agentic, request =>
        {
            keys.Add(request.IdempotencyKey);
            return Task.FromResult(ledger.Accept(request));
        });
        var run = await Host(DurableStore(), primary, fallback).RunAsync(Request("retry", Template(PipelineStageEffects.Idempotent, hybrid: true)));
        Assert.Equal(PipelineRunState.Completed, run.State);
        Assert.Equal(3, Assert.Single(run.StageRuns).Attempt);
        Assert.Equal(1, ledger.EffectCount);
        Assert.Equal(3, keys.Count);
        Assert.Single(keys.Distinct());
        Assert.Equal(Assert.Single(run.StageRuns).IdempotencyKey, keys[0]);
    }

    [Theory(Timeout = TestTimeouts.Integration)]
    [InlineData("inputs")]
    [InlineData("declaration")]
    [InlineData("binding")]
    public async Task Changed_work_cannot_reuse_an_operation_key(string change)
    {
        var invocations = 0;
        var adapter = new Adapter(PipelineWorkerType.Deterministic, _ =>
        {
            invocations++;
            return Task.FromResult(Success());
        });
        var template = Template(PipelineStageEffects.Idempotent);
        var store = DurableStore();
        await Host(store, adapter).RunAsync(Request("source", template));
        var request = Request("resumed", template, "source");
        if (change == "inputs") request = request with { Inputs = new Dictionary<string, string> { ["amount"] = "changed" } };
        if (change == "declaration") request = request with { Template = Template(PipelineStageEffects.None) };
        if (change == "binding") request = request with
        {
            Template = template with
            {
                Stages = [template.Stages[0] with { WorkerBinding = new PipelineWorkerBinding { DeterministicPoolId = "different" } }]
            }
        };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Host(store, adapter).RunAsync(request));
        Assert.Contains("stage-reconciliation-required", error.Message);
        Assert.Equal(1, invocations);
        Assert.Null(await store.GetAsync("resumed"));
    }

    [Theory(Timeout = TestTimeouts.Integration)]
    [InlineData(PipelineStageRunState.Pending)]
    [InlineData(PipelineStageRunState.Running)]
    [InlineData(PipelineStageRunState.Failed)]
    public async Task Legacy_checkpoint_cannot_be_upgraded_to_idempotent_replay(PipelineStageRunState state)
    {
        var store = DurableStore();
        await store.PutAsync(new PipelineRun
        {
            RunId = "legacy", TemplateId = "effects", StageRuns = [new PipelineStageRun { StageId = "write", State = state, Attempt = 1 }]
        });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Host(store, NeverExecute()).RunAsync(Request("resumed", Template(PipelineStageEffects.Idempotent), "legacy")));
        Assert.Contains("original persisted key", error.Message);
        Assert.Null(await store.GetAsync("resumed"));
    }

    [Theory(Timeout = TestTimeouts.Integration)]
    [InlineData(PipelineStageEffects.None)]
    [InlineData(PipelineStageEffects.Idempotent)]
    [InlineData(PipelineStageEffects.Unspecified)]
    [InlineData(PipelineStageEffects.NonIdempotent)]
    public async Task Completed_legacy_output_cannot_be_reused_without_proof_of_its_inputs(PipelineStageEffects effects)
    {
        var store = DurableStore();
        await store.PutAsync(new PipelineRun
        {
            RunId = "legacy", TemplateId = "effects",
            StageRuns = [new PipelineStageRun { StageId = "write", State = PipelineStageRunState.Completed, Output = "old input result", Attempt = 1 }]
        });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Host(store, NeverExecute()).RunAsync(Request("resumed", Template(effects), "legacy")));
        Assert.Contains("Completed output lacks", error.Message);
        Assert.Null(await store.GetAsync("resumed"));
    }

    [Fact(Timeout = TestTimeouts.Integration)]
    public async Task Caller_mutation_after_checkpoint_cannot_change_the_persisted_stage_contract_or_inputs()
    {
        var chain = new[] { PipelineWorkerType.Deterministic, PipelineWorkerType.Agentic };
        var stages = new[] { Template(PipelineStageEffects.NonIdempotent, hybrid: true).Stages[0] with
        {
            FallbackChain = chain, WorkerBinding = new PipelineWorkerBinding { DeterministicPoolId = "original" }
        } };
        var edges = new List<PipelineEdge>();
        var template = new PipelineTemplate { TemplateId = "effects", Stages = stages, Edges = edges };
        var inputs = new Dictionary<string, string> { ["amount"] = "original" };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var primary = new Adapter(PipelineWorkerType.Deterministic, request =>
        {
            calls++;
            Assert.Equal(PipelineStageEffects.NonIdempotent, request.Stage.Effects);
            Assert.Equal("original", request.Stage.WorkerBinding.DeterministicPoolId);
            Assert.Equal("original", request.Inputs["amount"]);
            Assert.Equal(PipelineWorkerType.Deterministic, request.Stage.FallbackChain[0]);
            Assert.Empty(request.Node.Predecessors);
            return Task.FromResult(new PipelineStageExecutionResult { Succeeded = false, Retryable = true });
        });
        var fallback = new Adapter(PipelineWorkerType.Agentic, _ => throw new InvalidOperationException("Unexpected fallback."));
        var store = new PausingStore(DurableStore(), entered, release);
        var execution = Host(store, primary, fallback).RunAsync(new PipelineExecutionRequest
        {
            RunId = "snapshot", Template = template, Inputs = inputs
        });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            stages[0] = stages[0] with { Effects = PipelineStageEffects.None, WorkerBinding = new PipelineWorkerBinding { DeterministicPoolId = "changed" } };
            chain[0] = PipelineWorkerType.Agentic;
            edges.Add(new PipelineEdge("write", "write"));
            inputs["amount"] = "changed";
        }
        finally { release.TrySetResult(); }
        var run = await execution;
        Assert.Equal(1, calls);
        Assert.Equal(PipelineRunState.Failed, run.State);
        Assert.True(Assert.Single(run.StageRuns).ReconciliationRequired);
    }

    [Theory(Timeout = TestTimeouts.Integration)]
    [InlineData(PipelineStageEffects.Unspecified)]
    [InlineData(PipelineStageEffects.NonIdempotent)]
    public async Task Unsafe_pending_snapshot_is_not_an_exclusive_claim_on_the_source_operation(PipelineStageEffects effects)
    {
        var store = DurableStore();
        var template = Template(effects);
        var failing = new FaultingStore(store, run => run.StageRuns.Any(s => s.State == PipelineStageRunState.Running));
        await Assert.ThrowsAsync<IOException>(() => Host(failing, NeverExecute()).RunAsync(Request("source", template)));
        Assert.Equal(PipelineStageRunState.Pending, Assert.Single((await store.GetAsync("source"))!.StageRuns).State);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Host(store, NeverExecute()).RunAsync(Request("resumed", template, "source")));
        Assert.Contains("stage-reconciliation-required", error.Message);
    }

    [Fact(Timeout = TestTimeouts.Integration)]
    public async Task An_exception_after_an_external_write_leaves_durable_uncertainty_without_automatic_replay()
    {
        var effects = 0;
        var adapter = new Adapter(PipelineWorkerType.Deterministic, _ =>
        {
            effects++;
            throw new IOException("connection ended after remote acceptance");
        });
        var template = Template(PipelineStageEffects.Unspecified);
        var store = DurableStore();
        await Assert.ThrowsAsync<IOException>(() => Host(store, adapter).RunAsync(Request("source", template)));
        var stage = Assert.Single((await DurableStore().GetAsync("source"))!.StageRuns);
        Assert.Equal(PipelineStageRunState.Running, stage.State);
        Assert.True(stage.ReconciliationRequired);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Host(store, adapter).RunAsync(Request("resumed", template, "source")));
        Assert.Equal(1, effects);
    }

    [Fact(Timeout = TestTimeouts.Integration)]
    public async Task Invalid_effect_declaration_is_rejected_before_persistence_or_dispatch()
    {
        var store = DurableStore();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Host(store, NeverExecute()).RunAsync(Request("invalid", Template((PipelineStageEffects)99))));
        Assert.Null(await store.GetAsync("invalid"));
    }

    [Theory(Timeout = TestTimeouts.Integration)]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    public async Task Missing_duplicate_or_unknown_checkpoint_stages_cannot_allocate_new_operation_keys(string corruption)
    {
        var store = DurableStore();
        var template = Template(PipelineStageEffects.NonIdempotent);
        await Host(store, new Adapter(PipelineWorkerType.Deterministic, _ => Task.FromResult(Success())))
            .RunAsync(Request("source", template));
        await store.MergeAsync("source", prior => prior! with
        {
            StageRuns = corruption switch
            {
                "missing" => [],
                "duplicate" => [prior!.StageRuns[0], prior.StageRuns[0]],
                _ => [prior!.StageRuns[0], new PipelineStageRun { StageId = "unknown" }]
            }
        });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Host(store, NeverExecute()).RunAsync(Request("resumed", template, "source")));
        Assert.Contains("stage identities must match", error.Message);
        Assert.Null(await store.GetAsync("resumed"));
    }

    [Theory(Timeout = TestTimeouts.Integration)]
    [InlineData("run", "invalid")]
    [InlineData("run", "999")]
    [InlineData("stage", "invalid")]
    [InlineData("stage", "999")]
    [InlineData("effects", "999")]
    public async Task Invalid_persisted_state_or_effects_is_refused_instead_of_becoming_pending(string field, string value)
    {
        var store = DurableStore();
        var template = Template(PipelineStageEffects.None);
        await Host(store, new Adapter(PipelineWorkerType.Deterministic, _ => Task.FromResult(Success())))
            .RunAsync(Request("source", template));
        using (var db = new LiteDatabase(LiteDbConnectionString.ForSharedAccess(DatabasePath)))
        {
            var collection = db.GetCollection("pipeline_runs");
            var document = collection.FindById("source");
            if (field == "run") document["State"] = value;
            else document["StageRuns"].AsArray[0].AsDocument[field == "effects" ? "Effects" : "State"] = value;
            collection.Update(document);
        }
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Host(DurableStore(), NeverExecute()).RunAsync(Request("resumed", template, "source")));
        Assert.Contains("stage-reconciliation-required", error.Message);
        Assert.Null(await store.GetAsync("resumed"));
    }

    [Theory(Timeout = TestTimeouts.Integration)]
    [InlineData(0, 0, PipelineCompletionPolicy.FailOnAnyStageFailure)]
    [InlineData(1, -1, PipelineCompletionPolicy.FailOnAnyStageFailure)]
    [InlineData(1, 0, (PipelineCompletionPolicy)99)]
    public async Task Invalid_execution_options_are_rejected(int attempts, int delay, PipelineCompletionPolicy completion)
    {
        await Task.CompletedTask;
        Assert.Throws<ArgumentException>(() => Host(new InMemoryPipelineRunStore(), NeverExecute(), options: new PipelineExecutionOptions
        {
            MaxRetryAttempts = attempts, RetryDelayMs = delay, CompletionPolicy = completion
        }));
    }

    private LiteDbPipelineRunStore DurableStore()
    {
        Directory.CreateDirectory(_directory);
        return new LiteDbPipelineRunStore(DatabasePath);
    }

    private static PipelineTemplate Template(PipelineStageEffects effects, bool hybrid = false) => new()
    {
        TemplateId = "effects",
        Stages = [new PipelineStageDefinition
        {
            Id = "write", Effects = effects,
            Mode = hybrid ? PipelineExecutionMode.Hybrid : PipelineExecutionMode.Deterministic,
            FallbackChain = hybrid ? [PipelineWorkerType.Deterministic, PipelineWorkerType.Agentic] : [],
        }]
    };

    private static PipelineExecutionRequest Request(string runId, PipelineTemplate template, string? resume = null) => new()
    {
        RunId = runId, Template = template, ResumeRunId = resume, ResumeFailedStages = true,
        Inputs = new Dictionary<string, string> { ["amount"] = "original" }
    };

    private static PipelineOrchestrator Host(IPipelineRunStore store, Adapter primary, Adapter? fallback = null, PipelineExecutionOptions? options = null)
    {
        var validator = new PipelineTemplateValidator();
        var executionOptions = Options.Create(options ?? new PipelineExecutionOptions { MaxRetryAttempts = 2, RetryDelayMs = 0 });
        var adapterOptions = Options.Create(new PipelineExecutionAdapterOptions());
        var adapters = fallback == null ? new IPipelineStageExecutionAdapter[] { primary } : [primary, fallback];
        var executors = new List<IPipelineStageExecutor>
        {
            new DeterministicPipelineStageExecutor(NullLogger<DeterministicPipelineStageExecutor>.Instance, adapters, adapterOptions, executionOptions)
        };
        if (fallback != null)
            executors.Add(new AgenticPipelineStageExecutor(NullLogger<AgenticPipelineStageExecutor>.Instance, adapters, adapterOptions, executionOptions));
        return new PipelineOrchestrator(validator, new PipelineDecomposer(validator), new PipelineScheduler(), store,
            new ThresholdScalingPolicy(), executors, executionOptions, NullLogger<PipelineOrchestrator>.Instance);
    }

    private static PipelineStageExecutionResult Success() => new() { Succeeded = true, WorkerId = "external-service", Output = "accepted" };
    private static Adapter NeverExecute() => new(PipelineWorkerType.Deterministic, _ => throw new InvalidOperationException("Unexpected external dispatch."));

    private sealed class Adapter(PipelineWorkerType workerType, Func<PipelineStageExecutionRequest, Task<PipelineStageExecutionResult>> execute)
        : IPipelineStageExecutionAdapter
    {
        public string AdapterKey => "default";
        public PipelineWorkerType WorkerType => workerType;
        public Task<PipelineStageExecutionResult> ExecuteAsync(PipelineStageExecutionRequest request, CancellationToken cancellationToken = default)
            => execute(request);
    }

    // Independent of run checkpoints and host instances, as an external deduplicating service is.
    private sealed class ExternalLedger
    {
        private readonly ConcurrentDictionary<string, PipelineStageExecutionResult> _operations = new(StringComparer.Ordinal);
        public int EffectCount => _operations.Count;
        public PipelineStageExecutionResult Accept(PipelineStageExecutionRequest request)
        {
            Assert.False(string.IsNullOrWhiteSpace(request.IdempotencyKey));
            return _operations.GetOrAdd(request.IdempotencyKey!, _ => Success());
        }
    }

    private sealed class FaultingStore(IPipelineRunStore inner, Func<PipelineRun, bool> failWhen, bool afterCommit = false) : IPipelineRunStore
    {
        private bool _armed = true;
        public Task<PipelineRun?> GetAsync(string runId, CancellationToken cancellationToken = default)
            => inner.GetAsync(runId, cancellationToken);
        public async Task<PipelineRun> MergeAsync(string runId, Func<PipelineRun?, PipelineRun> merge, CancellationToken cancellationToken = default)
        {
            var loseAcknowledgement = false;
            var result = await inner.MergeAsync(runId, stored =>
            {
                var next = merge(stored);
                if (_armed && failWhen(next))
                {
                    _armed = false;
                    if (!afterCommit) throw new IOException("Injected lost checkpoint before commit.");
                    loseAcknowledgement = true;
                }
                return next;
            }, cancellationToken);
            if (loseAcknowledgement) throw new IOException("Injected lost acknowledgement after commit.");
            return result;
        }
    }

    private sealed class PausingStore(IPipelineRunStore inner, TaskCompletionSource entered, TaskCompletionSource release) : IPipelineRunStore
    {
        private bool _pause = true;
        public Task<PipelineRun?> GetAsync(string runId, CancellationToken cancellationToken = default)
            => inner.GetAsync(runId, cancellationToken);
        public async Task<PipelineRun> MergeAsync(string runId, Func<PipelineRun?, PipelineRun> merge, CancellationToken cancellationToken = default)
        {
            var result = await inner.MergeAsync(runId, merge, cancellationToken);
            if (_pause)
            {
                _pause = false;
                entered.TrySetResult();
                await release.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            }
            return result;
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
