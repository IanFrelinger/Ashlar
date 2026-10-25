using System.Net;
using System.Text;
using Ashlar.Abstractions;
using Ashlar.Agents.TestKit;
using Ashlar.AI.Pipeline;
using Ashlar.AI.Pipeline.Clients;
using Ashlar.AI.Pipeline.Governance;
using Ashlar.Core.Application.Execution;
using Ashlar.Core.Application.Pipelines.Models;
using Ashlar.Core.Application.Pipelines.Ports;
using Ashlar.Hosting;
using Ashlar.Infrastructure.Pipelines;
using Ashlar.Tests.Infrastructure.Helpers;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Hosting;

[Trait("Category", "ProdStyle")]
[Collection("EnvironmentVariables")]
public sealed class MeaiUsageAccountingProdStyleTests : IDisposable
{
    private const string Reported = "{\"model\":\"probe\",\"message\":{\"content\":\"answer\"},\"done\":true,\"prompt_eval_count\":12,\"eval_count\":5}";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ashlar-meai-usage-" + Guid.NewGuid().ToString("N"));
    private readonly string? _previousFlag = Environment.GetEnvironmentVariable("ASHLAR_USE_MEAI_PIPELINE");

    public MeaiUsageAccountingProdStyleTests()
    {
        Directory.CreateDirectory(_directory);
        Environment.SetEnvironmentVariable("ASHLAR_USE_MEAI_PIPELINE", null);
    }

    [Theory(Timeout = TestTimeouts.HostTouching)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Default_host_model_records_one_logical_call_and_preserves_custom_auditor(bool registerAuditorLast)
    {
        using var handler = new StubHttpMessageHandler(_ => Response(Reported));
        using var http = Client(handler);
        var auditor = new InMemoryChatInvocationAuditor();
        await using var services = Services(() => new OllamaHttpChatClient(http, "probe"), auditor, registerAuditorLast);
        var model = services.GetRequiredService<IModel>();
        var path = Path.Combine(_directory, "runs.db");
        var validator = new PipelineTemplateValidator();
        var pipeline = new PipelineOrchestrator(validator, new PipelineDecomposer(validator), new PipelineScheduler(),
            new LiteDbPipelineRunStore(path), new ThresholdScalingPolicy(), [new ModelExecutor(model)],
            Options.Create(new PipelineExecutionOptions { MaxRetryAttempts = 1, RetryDelayMs = 0 }), NullLogger<PipelineOrchestrator>.Instance);

        var run = await pipeline.RunAsync(new PipelineExecutionRequest
        {
            RunId = "meai-run",
            Template = new PipelineTemplate
            {
                TemplateId = "meai", Stages = [new PipelineStageDefinition { Id = "infer", Effects = PipelineStageEffects.None }]
            }
        });

        Assert.Equal("answer", Assert.Single(run.StageRuns).Output);
        var saved = (await new LiteDbPipelineRunStore(path).GetAsync(run.RunId))!;
        var record = Assert.Single(saved.ProviderUsageAttempts);
        Assert.Equal(ProviderUsageBoundary.LogicalCall, record.Boundary);
        Assert.Equal("meai-run", record.CorrelationId);
        Assert.Equal(MeaiTargetKeys.LocalOllama, record.Provider);
        Assert.Equal(12, record.Usage.InputTokenCount);
        Assert.Equal(5, record.Usage.OutputTokenCount);
        Assert.Equal(ProviderUsageSource.ProviderReported, record.Usage.Source);
        Assert.Null(record.HttpStatusCode);
        Assert.Equal(0, saved.ProviderUsage.AttemptCount);
        Assert.Equal(1, saved.ProviderUsage.LogicalCallCount);
        Assert.False(saved.ProviderUsage.HasCompleteReportedInputAndOutput);
        Assert.Null(saved.ProviderUsage.ReportedInputTokenCount);
        Assert.Equal(12, saved.ProviderUsage.LogicalReportedInputTokenCount);
        Assert.Single(handler.Requests);
        Assert.Same(auditor, services.GetRequiredService<IChatInvocationAuditor>());
        Assert.Contains(auditor.Records, r => r.TargetKey == "router:default" && r.Outcome == "success");
        Assert.Single(auditor.Records, r => r.IsProviderInvocation);
    }

    [Fact(Timeout = TestTimeouts.Integration)]
    public async Task Streaming_terminal_chunk_with_no_text_retains_reported_usage()
    {
        var body = "{\"model\":\"probe\",\"message\":{\"content\":\"answer\"},\"done\":false}\n"
                   + "{\"model\":\"probe\",\"done\":true,\"prompt_eval_count\":12,\"eval_count\":5}\n";
        using var handler = new StubHttpMessageHandler(_ => Response(body));
        using var http = Client(handler);
        await using var services = Services(() => new OllamaHttpChatClient(http, "probe"));
        using var scope = new ProviderUsageScope("stream");
        var chunks = new List<ChatResponseUpdate>();
        await foreach (var chunk in services.GetRequiredService<IChatClient>().GetStreamingResponseAsync("hello")) chunks.Add(chunk);

        Assert.Equal("answer", string.Concat(chunks.Select(chunk => chunk.Text)));
        Assert.Single(chunks.SelectMany(chunk => chunk.Contents).OfType<UsageContent>());
        var record = Assert.Single(scope.Snapshot());
        Assert.Equal(12, record.Usage.InputTokenCount);
        Assert.Equal(5, record.Usage.OutputTokenCount);
        Assert.Equal(ProviderAttemptOutcome.Succeeded, record.Outcome);
        Assert.Single(handler.Requests);
    }

    [Theory(Timeout = TestTimeouts.Integration)]
    [InlineData("", null, null, false)]
    [InlineData(",\"prompt_eval_count\":null,\"eval_count\":null", null, null, false)]
    [InlineData(",\"prompt_eval_count\":0,\"eval_count\":0", 0L, 0L, false)]
    [InlineData(",\"prompt_eval_count\":7", 7L, null, false)]
    [InlineData(",\"prompt_eval_count\":-1,\"eval_count\":5", null, 5L, true)]
    [InlineData(",\"prompt_eval_count\":1.5,\"eval_count\":\"5\"", null, null, true)]
    [InlineData(",\"prompt_eval_count\":9223372036854775808", null, null, true)]
    public async Task Missing_or_malformed_native_counts_remain_unknown(string counts, long? expectedInput, long? expectedOutput, bool invalid)
    {
        using var handler = new StubHttpMessageHandler(_ => Response("{\"message\":{\"content\":\"answer\"}" + counts + "}"));
        using var http = Client(handler);
        await using var services = Services(() => new OllamaHttpChatClient(http, "probe"));
        using var scope = new ProviderUsageScope("counts");

        Assert.Equal("answer", (await services.GetRequiredService<IChatClient>().GetResponseAsync("hello")).Text);

        var record = Assert.Single(scope.Snapshot());
        Assert.Equal(expectedInput, record.Usage.InputTokenCount);
        Assert.Equal(expectedOutput, record.Usage.OutputTokenCount);
        Assert.Equal(invalid, record.Usage.HasInvalidCounts);
        Assert.Equal(expectedInput.HasValue || expectedOutput.HasValue ? ProviderUsageSource.ProviderReported : ProviderUsageSource.Unknown,
            record.Usage.Source);
        Assert.Single(handler.Requests);
    }

    [Fact(Timeout = TestTimeouts.Integration)]
    public async Task Plain_usage_with_unknown_provenance_is_not_labeled_provider_reported()
    {
        await using var services = Services(() => new FixedClient(new UsageDetails { InputTokenCount = 20, OutputTokenCount = 4 }));
        using var scope = new ProviderUsageScope("estimate");
        await services.GetRequiredService<IChatClient>().GetResponseAsync("hello");
        var record = Assert.Single(scope.Snapshot());
        Assert.Equal(ProviderUsageSource.Unknown, record.Usage.Source);
        Assert.Equal(20, record.Usage.InputTokenCount);
        Assert.Null(ProviderUsageSummary.FromAttempts(scope.Snapshot()).LogicalReportedInputTokenCount);
    }

    [Fact(Timeout = TestTimeouts.Integration)]
    public async Task Optional_observer_failure_does_not_change_response_or_repeat_http()
    {
        using var handler = new StubHttpMessageHandler(_ => Response(Reported));
        using var http = Client(handler);
        await using var services = Services(() => new OllamaHttpChatClient(http, "probe"), observer: new ThrowingObserver());
        using var scope = new ProviderUsageScope("observer-failure");
        Assert.Equal("answer", (await services.GetRequiredService<IChatClient>().GetResponseAsync("hello")).Text);
        Assert.Single(scope.Snapshot());
        Assert.Single(handler.Requests);
    }

    [Fact(Timeout = TestTimeouts.Integration)]
    public async Task Custom_auditor_failure_does_not_double_count_terminal_events()
    {
        using var handler = new StubHttpMessageHandler(_ => Response(Reported));
        using var http = Client(handler);
        await using var services = Services(() => new OllamaHttpChatClient(http, "probe"), new ThrowingTerminalAuditor());
        using var scope = new ProviderUsageScope("audit-failure");
        await Assert.ThrowsAsync<InvalidOperationException>(() => services.GetRequiredService<IChatClient>().GetResponseAsync("hello"));
        var record = Assert.Single(scope.Snapshot());
        Assert.Equal(12, record.Usage.InputTokenCount);
        Assert.Single(handler.Requests);
    }

    [Fact(Timeout = TestTimeouts.Integration)]
    public async Task Failed_provider_call_has_unknown_usage_and_does_not_retry()
    {
        using var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        using var http = Client(handler);
        await using var services = Services(() => new OllamaHttpChatClient(http, "probe"));
        using var scope = new ProviderUsageScope("fault");
        await Assert.ThrowsAsync<HttpRequestException>(() => services.GetRequiredService<IChatClient>().GetResponseAsync("hello"));
        var record = Assert.Single(scope.Snapshot());
        Assert.Equal(ProviderAttemptOutcome.Failed, record.Outcome);
        Assert.Equal(ProviderUsageSource.Unknown, record.Usage.Source);
        Assert.Single(handler.Requests);
    }

    [Fact(Timeout = TestTimeouts.Integration)]
    public async Task Abandoning_a_stream_records_one_cancelled_logical_call_with_unknown_counts()
    {
        var body = "{\"message\":{\"content\":\"partial\"},\"done\":false}\n"
                   + "{\"done\":true,\"prompt_eval_count\":12,\"eval_count\":5}\n";
        using var handler = new StubHttpMessageHandler(_ => Response(body));
        using var http = Client(handler);
        await using var services = Services(() => new OllamaHttpChatClient(http, "probe"));
        using var scope = new ProviderUsageScope("abandoned");
        await foreach (var _ in services.GetRequiredService<IChatClient>().GetStreamingResponseAsync("hello")) break;
        var record = Assert.Single(scope.Snapshot());
        Assert.Equal(ProviderAttemptOutcome.Cancelled, record.Outcome);
        Assert.Null(record.Usage.InputTokenCount);
        Assert.Single(handler.Requests);
    }

    [Fact(Timeout = TestTimeouts.Integration)]
    public async Task Abandoning_after_terminal_text_keeps_usage_already_received_in_that_chunk()
    {
        using var handler = new StubHttpMessageHandler(_ => Response(Reported + "\n"));
        using var http = Client(handler);
        await using var services = Services(() => new OllamaHttpChatClient(http, "probe"));
        using var scope = new ProviderUsageScope("terminal-text");
        await foreach (var chunk in services.GetRequiredService<IChatClient>().GetStreamingResponseAsync("hello"))
        {
            Assert.Equal("answer", chunk.Text);
            Assert.Single(chunk.Contents.OfType<UsageContent>());
            break;
        }
        var record = Assert.Single(scope.Snapshot());
        Assert.Equal(12, record.Usage.InputTokenCount);
        Assert.Equal(5, record.Usage.OutputTokenCount);
        Assert.Equal(ProviderUsageSource.ProviderReported, record.Usage.Source);
        Assert.Single(handler.Requests);
    }

    private ServiceProvider Services(Func<IChatClient> inner, IChatInvocationAuditor? auditor = null,
        bool registerAuditorLast = true, IChatInvocationUsageObserver? observer = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        if (auditor is not null && !registerAuditorLast) services.AddSingleton(auditor);
        services.AddAshlar(options =>
        {
            options.DeploymentProfile = AshlarDeploymentProfile.System;
            options.DisableObservationPipeline = true;
            options.ConfigPath = Path.Combine(_directory, "config.json");
        });
        // Keep the host's default IModel/router/governance graph; substitute only its transport.
        services.AddAshlarMeaiPipeline(ollamaInnerFactory: _ => inner());
        if (auditor is not null && registerAuditorLast) services.AddSingleton(auditor);
        if (observer is not null) services.AddSingleton(observer);
        return services.BuildServiceProvider();
    }

    private static HttpClient Client(HttpMessageHandler handler) => new(handler) { BaseAddress = new Uri("http://127.0.0.1:11434/") };
    private static HttpResponseMessage Response(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class ModelExecutor(IModel model) : IPipelineStageExecutor
    {
        public PipelineWorkerType WorkerType => PipelineWorkerType.Deterministic;
        public async Task<PipelineStageExecutionResult> ExecuteAsync(PipelineStageExecutionRequest request, CancellationToken cancellationToken = default)
        {
            var output = await model.CompleteAsync(new ModelInput([("system", "ashlar.model.provider=ollama"), ("user", "hello")]), cancellationToken);
            return new PipelineStageExecutionResult { Succeeded = true, Output = output.Text };
        }
    }

    private sealed class FixedClient(UsageDetails usage) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "answer")) { Usage = usage });
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class ThrowingObserver : IChatInvocationUsageObserver
    {
        public void Observe(ChatInvocationAuditRecord record) => throw new InvalidOperationException("Optional observation sink unavailable.");
    }

    private sealed class ThrowingTerminalAuditor : IChatInvocationAuditor
    {
        public void Record(ChatInvocationAuditRecord record)
        {
            if (record.Outcome != "routed") throw new InvalidOperationException("Audit sink unavailable.");
        }
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("ASHLAR_USE_MEAI_PIPELINE", _previousFlag);
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
