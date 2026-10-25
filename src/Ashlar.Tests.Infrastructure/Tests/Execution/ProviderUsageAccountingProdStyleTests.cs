using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Ashlar.Agents.TestKit;
using Ashlar.Core.Application.Execution;
using Ashlar.Core.Application.NodeCapabilityRuntime.Models;
using Ashlar.Core.Application.Pipelines.Models;
using Ashlar.Core.Application.Pipelines.Ports;
using Ashlar.Core.Application.Resilience.Ports;
using Ashlar.Infrastructure.Execution;
using Ashlar.Infrastructure.NodeCapabilityRuntime.Backends;
using Ashlar.Infrastructure.Pipelines;
using Ashlar.Infrastructure.Resilience;
using Ashlar.Tests.Infrastructure.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Execution;

[Trait("Category", "ProdStyle")]
[Collection("EnvironmentVariables")]
public sealed class ProviderUsageAccountingProdStyleTests
{
    private const string OpenAiBody = """
        {"choices":[{"message":{"content":"answer"}}],"usage":{"prompt_tokens":12,"completion_tokens":5,
        "prompt_tokens_details":{"cached_tokens":7},"completion_tokens_details":{"reasoning_tokens":3}}}
        """;
    private const string OllamaBody = """
        {"message":{"content":"answer"},"prompt_eval_count":12,"eval_count":5}
        """;

    [Theory(Timeout = TestTimeouts.Integration)]
    [InlineData("openai")]
    [InlineData("openai_compat")]
    [InlineData("azure")]
    [InlineData("ollama")]
    public async Task Existing_string_api_preserves_reported_usage_without_a_second_request(string provider)
    {
        await using var server = new ProviderServer((200, provider == "ollama" ? OllamaBody : OpenAiBody));
        using var environment = Configure(server.BaseUrl);
        using var factory = CreateFactory();
        await factory.OllamaWarmup;
        using var scope = new ProviderUsageScope("run-42");

        var text = await factory.ExecuteLLMAsync(provider, "private system prompt", "private input", new { model = "probe:latest" });

        Assert.Equal("answer", text);
        Assert.Equal(1, server.InferenceRequests);
        var record = Assert.Single(scope.Snapshot());
        Assert.Equal(provider, record.Provider);
        Assert.Equal("run-42", record.CorrelationId);
        Assert.Equal(1, record.Attempt);
        Assert.Equal(200, record.HttpStatusCode);
        Assert.Equal(ProviderAttemptOutcome.Succeeded, record.Outcome);
        Assert.Equal(12, record.Usage.InputTokenCount);
        Assert.Equal(5, record.Usage.OutputTokenCount);
        Assert.Equal(ProviderUsageSource.ProviderReported, record.Usage.Source);
        Assert.False(record.Usage.HasInvalidCounts);
        if (provider != "ollama")
        {
            Assert.Equal(7, record.Usage.CachedInputTokenCount);
            Assert.Equal(3, record.Usage.ReasoningTokenCount);
        }
        var stored = JsonSerializer.Serialize(record);
        Assert.DoesNotContain("private", stored);
        Assert.DoesNotContain("secret-api-key", stored);
        Assert.DoesNotContain(server.BaseUrl, stored);
        Assert.DoesNotContain("answer", stored);
        var summary = ProviderUsageSummary.FromAttempts(scope.Snapshot());
        Assert.True(summary.HasCompleteReportedInputAndOutput);
        Assert.Equal(12, summary.ReportedInputTokenCount);
        Assert.Equal(5, summary.ReportedOutputTokenCount);
    }

    [Theory(Timeout = TestTimeouts.Integration)]
    [InlineData("", false)]
    [InlineData(",\"usage\":null", false)]
    [InlineData(",\"usage\":{}", false)]
    [InlineData(",\"usage\":\"bad\"", true)]
    [InlineData(",\"usage\":{\"prompt_tokens\":-1,\"completion_tokens\":1.5}", true)]
    [InlineData(",\"usage\":{\"prompt_tokens\":\"12\",\"completion_tokens\":9223372036854775808}", true)]
    public async Task Missing_or_malformed_counts_are_unknown_without_changing_string_result(string usage, bool invalid)
    {
        await using var server = new ProviderServer((200, "{\"choices\":[{\"message\":{\"content\":\"answer\"}}]" + usage + "}"));
        using var environment = Configure(server.BaseUrl);
        using var factory = CreateFactory();
        await factory.OllamaWarmup;
        using var scope = new ProviderUsageScope("unknown");

        Assert.Equal("answer", await factory.ExecuteLLMAsync("openai_compat", "s", "u", new { }));

        var record = Assert.Single(scope.Snapshot());
        Assert.Null(record.Usage.InputTokenCount);
        Assert.Null(record.Usage.OutputTokenCount);
        Assert.Equal(ProviderUsageSource.Unknown, record.Usage.Source);
        Assert.Equal(invalid, record.Usage.HasInvalidCounts);
        var summary = ProviderUsageSummary.FromAttempts(scope.Snapshot());
        Assert.Null(summary.ReportedInputTokenCount);
        Assert.Null(summary.ReportedOutputTokenCount);
        Assert.False(summary.HasCompleteReportedInputAndOutput);
        Assert.Equal(1, server.InferenceRequests);
    }

    [Theory(Timeout = TestTimeouts.Integration)]
    [InlineData("openai_compat")]
    [InlineData("azure")]
    [InlineData("ollama")]
    public async Task Retry_preserves_failed_attempt_usage_and_request_identity(string provider)
    {
        var failed = provider == "ollama" ? "{\"prompt_eval_count\":4,\"eval_count\":1}" : "{\"usage\":{\"prompt_tokens\":4,\"completion_tokens\":1}}";
        await using var server = new ProviderServer((429, failed), (200, provider == "ollama" ? OllamaBody : OpenAiBody));
        using var environment = Configure(server.BaseUrl);
        using var factory = CreateFactory();
        await factory.OllamaWarmup;
        using var scope = new ProviderUsageScope("retry");

        Assert.Equal("answer", await factory.ExecuteLLMAsync(provider, "s", "u", new { model = "probe:latest" }));

        var records = scope.Snapshot();
        Assert.Equal(2, records.Count);
        Assert.Equal(2, server.InferenceRequests);
        Assert.Equal(records[0].RequestId, records[1].RequestId);
        Assert.Equal(new[] { 1, 2 }, records.Select(r => r.Attempt));
        Assert.Equal(ProviderAttemptOutcome.Failed, records[0].Outcome);
        Assert.Equal(429, records[0].HttpStatusCode);
        Assert.Equal(4, records[0].Usage.InputTokenCount);
        Assert.Equal(ProviderAttemptOutcome.Succeeded, records[1].Outcome);
        var summary = ProviderUsageSummary.FromAttempts(records);
        Assert.Equal(16, summary.ReportedInputTokenCount);
        Assert.Equal(6, summary.ReportedOutputTokenCount);
    }

    [Theory(Timeout = TestTimeouts.Integration)]
    [InlineData(400, "{\"usage\":{\"prompt_tokens\":8}}")]
    [InlineData(200, "{\"choices\":[],\"usage\":{\"prompt_tokens\":8}}")]
    [InlineData(200, "invalid json")]
    public async Task Failed_or_invalid_responses_are_recorded_without_replaying_successful_http(int status, string body)
    {
        await using var server = new ProviderServer((status, body));
        using var environment = Configure(server.BaseUrl);
        using var factory = CreateFactory();
        await factory.OllamaWarmup;
        using var scope = new ProviderUsageScope("failure");

        await Assert.ThrowsAnyAsync<Exception>(() => factory.ExecuteLLMAsync("openai_compat", "s", "u", new { }));

        var record = Assert.Single(scope.Snapshot());
        Assert.Equal(1, server.InferenceRequests);
        Assert.Equal(status, record.HttpStatusCode);
        Assert.Equal(ProviderAttemptOutcome.Failed, record.Outcome);
        if (body != "invalid json") Assert.Equal(8, record.Usage.InputTokenCount);
        Assert.Null(record.Usage.OutputTokenCount);
        Assert.False(ProviderUsageSummary.FromAttempts(scope.Snapshot()).HasCompleteReportedInputAndOutput);
    }

    [Theory(Timeout = TestTimeouts.Integration)]
    [InlineData("openai_compat")]
    [InlineData("azure")]
    public async Task Vision_uses_the_same_usage_accounting(string provider)
    {
        await using var server = new ProviderServer((200, OpenAiBody));
        using var environment = Configure(server.BaseUrl);
        using var factory = CreateFactory();
        await factory.OllamaWarmup;
        using var scope = new ProviderUsageScope("vision");

        Assert.Equal("answer", await factory.ExecuteVisionAsync(provider, "s", "u", [1, 2, 3], new { model = "probe:latest" }));

        var record = Assert.Single(scope.Snapshot());
        Assert.Equal(provider, record.Provider);
        Assert.Equal(12, record.Usage.InputTokenCount);
        Assert.Equal(1, server.InferenceRequests);
    }

    [Theory(Timeout = TestTimeouts.Quick)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Ncr_uses_reported_output_or_marks_legacy_estimate_explicitly(bool reported)
    {
        using var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(JsonResponse(reported ? OllamaBody : "{\"message\":{\"content\":\"answer\"}}")));
        using var http = new HttpClient(handler);
        var backend = CreateBackend(http);
        using var scope = new ProviderUsageScope("ncr");

        var result = await backend.RunInferenceAsync(new InferenceRequest { ModelId = "probe", Prompt = "secret" });

        Assert.Equal(reported ? 5 : 1, result.TokenCount);
        Assert.Equal(reported ? ProviderUsageSource.ProviderReported : ProviderUsageSource.Estimated, result.TokenCountSource);
        Assert.Equal(reported ? 5L : null, result.Usage.OutputTokenCount);
        Assert.Equal(result.Usage, Assert.Single(scope.Snapshot()).Usage);
        Assert.Single(handler.Requests);
    }

    [Fact(Timeout = TestTimeouts.Quick)]
    public async Task Cancellation_records_terminal_attempt_without_using_cancelled_token_for_collection()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new StubHttpMessageHandler(async (_, ct) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return JsonResponse(OllamaBody);
        });
        using var http = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        using var scope = new ProviderUsageScope("cancelled");
        var task = CreateBackend(http).RunInferenceAsync(new InferenceRequest { ModelId = "probe" }, cancellation.Token);
        await started.Task;
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);

        var record = Assert.Single(scope.Snapshot());
        Assert.Equal(ProviderAttemptOutcome.Cancelled, record.Outcome);
        Assert.Null(record.HttpStatusCode);
        Assert.Null(record.Usage.OutputTokenCount);
    }

    [Fact(Timeout = TestTimeouts.Quick)]
    public async Task Parallel_child_scopes_propagate_once_and_keep_their_own_correlation()
    {
        using var parent = new ProviderUsageScope("workflow");
        var children = await Task.WhenAll(Enumerable.Range(0, 8).Select(async index =>
        {
            using var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(JsonResponse(OllamaBody)));
            using var http = new HttpClient(handler);
            using var child = new ProviderUsageScope("stage-" + index);
            await Task.Yield();
            await CreateBackend(http).RunInferenceAsync(new InferenceRequest { ModelId = "probe" });
            return Assert.Single(child.Snapshot());
        }));
        Assert.Equal(8, parent.Snapshot().Count);
        Assert.Equal(8, parent.Snapshot().Select(r => r.RequestId).Distinct().Count());
        Assert.Equal(children.OrderBy(r => r.CorrelationId), parent.Snapshot().OrderBy(r => r.CorrelationId));
        parent.Dispose();
        using var finalHandler = new StubHttpMessageHandler((_, _) => Task.FromResult(JsonResponse(OllamaBody)));
        using var finalHttp = new HttpClient(finalHandler);
        await CreateBackend(finalHttp).RunInferenceAsync(new InferenceRequest { ModelId = "probe" });
        Assert.Equal(8, parent.Snapshot().Count);
    }

    [Fact(Timeout = TestTimeouts.Quick)]
    public async Task Cancellation_after_http_completion_keeps_buffered_reported_usage()
    {
        using var scope = new ProviderUsageScope("completed-http");
        using var cancellation = new CancellationTokenSource();
        using var response = JsonResponse(OpenAiBody);
        await response.Content.LoadIntoBufferAsync();
        using (var attempt = new ProviderUsageRequest().Begin("openai_compat", "probe", cancellation.Token))
        {
            cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => attempt.ReadBodyAsync(response, cancellation.Token));
        }
        var record = Assert.Single(scope.Snapshot());
        Assert.Equal(ProviderAttemptOutcome.Cancelled, record.Outcome);
        Assert.Equal(200, record.HttpStatusCode);
        Assert.Equal(12, record.Usage.InputTokenCount);
        Assert.Equal(5, record.Usage.OutputTokenCount);
    }

    [Fact(Timeout = TestTimeouts.Quick)]
    public async Task Transport_failure_is_retained_with_unknown_usage()
    {
        using var handler = StubHttpMessageHandler.Throws(new HttpRequestException("transport unavailable"));
        using var http = new HttpClient(handler);
        using var scope = new ProviderUsageScope("transport");

        await Assert.ThrowsAsync<HttpRequestException>(() => CreateBackend(http).RunInferenceAsync(new InferenceRequest { ModelId = "probe" }));

        var record = Assert.Single(scope.Snapshot());
        Assert.Equal(ProviderAttemptOutcome.Failed, record.Outcome);
        Assert.Null(record.HttpStatusCode);
        Assert.Equal(ProviderUsageSource.Unknown, record.Usage.Source);
        Assert.Single(handler.Requests);
    }

    [Fact(Timeout = TestTimeouts.Integration)]
    public async Task Zero_counts_are_reported_but_invalid_detail_subsets_are_not_added_to_totals()
    {
        const string body = """
            {"choices":[{"message":{"content":"answer"}}],"usage":{"prompt_tokens":0,"completion_tokens":0,
            "prompt_tokens_details":{"cached_tokens":1},"completion_tokens_details":{"reasoning_tokens":-1}}}
            """;
        await using var server = new ProviderServer((200, body));
        using var environment = Configure(server.BaseUrl);
        using var factory = CreateFactory();
        await factory.OllamaWarmup;
        using var scope = new ProviderUsageScope("zero");

        Assert.Equal("answer", await factory.ExecuteLLMAsync("openai_compat", "s", "u", new { }));

        var usage = Assert.Single(scope.Snapshot()).Usage;
        Assert.Equal(0, usage.InputTokenCount);
        Assert.Equal(0, usage.OutputTokenCount);
        Assert.Null(usage.CachedInputTokenCount);
        Assert.Null(usage.ReasoningTokenCount);
        Assert.True(usage.HasInvalidCounts);
        Assert.Equal(ProviderUsageSource.ProviderReported, usage.Source);
    }

    [Fact(Timeout = TestTimeouts.Quick)]
    public async Task Summary_does_not_invent_zero_or_overflow_into_a_valid_total()
    {
        await Task.CompletedTask;
        var empty = ProviderUsageSummary.FromAttempts([]);
        Assert.Null(empty.ReportedInputTokenCount);
        Assert.False(empty.HasCompleteReportedInputAndOutput);
        var known = new ProviderUsageRecord { Usage = new ProviderTokenUsage { Source = ProviderUsageSource.ProviderReported, InputTokenCount = long.MaxValue, OutputTokenCount = 0 } };
        var overflow = ProviderUsageSummary.FromAttempts([known, known]);
        Assert.Null(overflow.ReportedInputTokenCount);
        Assert.Equal(0, overflow.ReportedOutputTokenCount);
        Assert.True(overflow.HasOverflow);
        Assert.False(overflow.HasCompleteReportedInputAndOutput);
        var malformed = ProviderUsageSummary.FromAttempts([new ProviderUsageRecord
        {
            Usage = new ProviderTokenUsage
            {
                Source = ProviderUsageSource.ProviderReported, InputTokenCount = -1, OutputTokenCount = 5, ReasoningTokenCount = 6
            }
        }]);
        Assert.Null(malformed.ReportedInputTokenCount);
        Assert.Equal(5, malformed.ReportedOutputTokenCount);
        Assert.Null(malformed.ReportedReasoningTokenCount);
        Assert.True(malformed.HasInvalidCounts);
        Assert.False(malformed.HasCompleteReportedInputAndOutput);
    }

    [Fact(Timeout = TestTimeouts.Quick)]
    public async Task Logical_observations_deduplicate_terminal_audits_and_do_not_double_sum_nested_http_usage()
    {
        await Task.CompletedTask;
        using var scope = new ProviderUsageScope("mixed");
        var logical = new ProviderUsageRecord
        {
            RequestId = Guid.NewGuid(), Attempt = 1, Boundary = ProviderUsageBoundary.LogicalCall, Provider = "sdk", Model = "probe",
            Usage = new ProviderTokenUsage { Source = ProviderUsageSource.ProviderReported, InputTokenCount = 12, OutputTokenCount = 5 }
        };
        ProviderUsageScope.RecordCurrent(logical);
        ProviderUsageScope.RecordCurrent(logical with { Outcome = ProviderAttemptOutcome.Failed });
        var logicalOnly = ProviderUsageSummary.FromAttempts(scope.Snapshot());
        Assert.Equal(1, logicalOnly.ObservationCount);
        Assert.Equal(0, logicalOnly.AttemptCount);
        Assert.Equal(1, logicalOnly.LogicalCallCount);
        Assert.Null(logicalOnly.ReportedInputTokenCount);
        Assert.Equal(12, logicalOnly.LogicalReportedInputTokenCount);
        Assert.False(logicalOnly.HasCompleteReportedInputAndOutput);

        // A custom SDK target may internally use the instrumented legacy factory. The terminal
        // SDK audit cannot establish which HTTP observations it covers; totals must stay separate.
        using (var child = new ProviderUsageScope("nested-http"))
        {
            ProviderUsageScope.RecordCurrent(logical with { RequestId = Guid.NewGuid(), Boundary = ProviderUsageBoundary.HttpAttempt });
        }
        var summary = ProviderUsageSummary.FromAttempts(scope.Snapshot());
        Assert.Equal(2, summary.ObservationCount);
        Assert.Equal(1, summary.AttemptCount);
        Assert.Equal(1, summary.LogicalCallCount);
        Assert.Equal(12, summary.ReportedInputTokenCount);
        Assert.Equal(12, summary.LogicalReportedInputTokenCount);
        Assert.Equal(5, summary.ReportedOutputTokenCount);
        Assert.Equal(5, summary.LogicalReportedOutputTokenCount);
        Assert.False(summary.HasCompleteReportedInputAndOutput);
        Assert.Equal("nested-http", scope.Snapshot()[1].CorrelationId);
    }

    [Fact(Timeout = TestTimeouts.Integration)]
    public async Task Durable_pipeline_usage_survives_reopening_and_resume_does_not_duplicate_source_usage()
    {
        await using var server = new ProviderServer((200, OpenAiBody));
        using var environment = Configure(server.BaseUrl);
        using var factory = CreateFactory();
        await factory.OllamaWarmup;
        using var directory = new TestDirectory();
        var executor = new ProviderStageExecutor(async ct => await factory.ExecuteLLMAsync("openai_compat", "s", "u", new { }, ct));
        var template = UsageTemplate();
        var first = await CreatePipeline(new LiteDbPipelineRunStore(directory.DatabasePath), executor)
            .RunAsync(new PipelineExecutionRequest { RunId = "source", Template = template });
        var reopened = (await new LiteDbPipelineRunStore(directory.DatabasePath).GetAsync("source"))!;

        Assert.Equal(PipelineRunState.Completed, reopened.State);
        Assert.Equal(Assert.Single(first.ProviderUsageAttempts), Assert.Single(reopened.ProviderUsageAttempts));
        Assert.Equal("source", reopened.ProviderUsageAttempts[0].CorrelationId);
        Assert.Equal(12, reopened.ProviderUsage.ReportedInputTokenCount);
        Assert.Equal(5, reopened.ProviderUsage.ReportedOutputTokenCount);
        Assert.True(reopened.ProviderUsage.HasCompleteReportedInputAndOutput);

        var resumed = await CreatePipeline(new LiteDbPipelineRunStore(directory.DatabasePath), executor)
            .RunAsync(new PipelineExecutionRequest { RunId = "resumed", ResumeRunId = "source", Template = template });

        Assert.Equal(PipelineRunState.Completed, resumed.State);
        Assert.Equal("source", resumed.ResumeSourceRunId);
        Assert.Empty(resumed.ProviderUsageAttempts);
        Assert.Null(resumed.ProviderUsage.ReportedInputTokenCount);
        Assert.False(resumed.ProviderUsage.HasCompleteReportedInputAndOutput);
        Assert.Single((await new LiteDbPipelineRunStore(directory.DatabasePath).GetAsync("source"))!.ProviderUsageAttempts);
        Assert.Equal(1, server.InferenceRequests);
    }

    [Fact(Timeout = TestTimeouts.Integration)]
    public async Task Interrupted_pipeline_persists_failed_provider_usage_before_rethrowing()
    {
        await using var server = new ProviderServer((400, "{\"usage\":{\"prompt_tokens\":9}}"));
        using var environment = Configure(server.BaseUrl);
        using var factory = CreateFactory();
        await factory.OllamaWarmup;
        using var directory = new TestDirectory();
        var executor = new ProviderStageExecutor(async ct => await factory.ExecuteLLMAsync("openai_compat", "s", "u", new { }, ct));
        var pipeline = CreatePipeline(new LiteDbPipelineRunStore(directory.DatabasePath), executor);

        await Assert.ThrowsAsync<HttpRequestException>(() => pipeline.RunAsync(new PipelineExecutionRequest { RunId = "failed", Template = UsageTemplate() }));

        var reopened = (await new LiteDbPipelineRunStore(directory.DatabasePath).GetAsync("failed"))!;
        var attempt = Assert.Single(reopened.ProviderUsageAttempts);
        Assert.Equal("failed", attempt.CorrelationId);
        Assert.Equal(ProviderAttemptOutcome.Failed, attempt.Outcome);
        Assert.Equal(9, reopened.ProviderUsage.ReportedInputTokenCount);
        Assert.Null(reopened.ProviderUsage.ReportedOutputTokenCount);
        Assert.False(reopened.ProviderUsage.HasCompleteReportedInputAndOutput);
        Assert.Equal(1, server.InferenceRequests);
    }

    private static PipelineTemplate UsageTemplate() => new()
    {
        TemplateId = "usage-probe",
        Stages = [new PipelineStageDefinition { Id = "call", Name = "Provider call", Mode = PipelineExecutionMode.Deterministic, Effects = PipelineStageEffects.None }]
    };

    private static PipelineOrchestrator CreatePipeline(IPipelineRunStore store, IPipelineStageExecutor executor)
    {
        var validator = new PipelineTemplateValidator();
        return new PipelineOrchestrator(validator, new PipelineDecomposer(validator), new PipelineScheduler(), store,
            new ThresholdScalingPolicy(), [executor], Options.Create(new PipelineExecutionOptions { MaxRetryAttempts = 1, RetryDelayMs = 0 }),
            NullLogger<PipelineOrchestrator>.Instance);
    }

    private sealed class ProviderStageExecutor(Func<CancellationToken, Task<string>> execute) : IPipelineStageExecutor
    {
        public PipelineWorkerType WorkerType => PipelineWorkerType.Deterministic;
        public async Task<PipelineStageExecutionResult> ExecuteAsync(PipelineStageExecutionRequest request, CancellationToken cancellationToken = default) =>
            new() { Succeeded = true, Output = await execute(cancellationToken), WorkerId = "provider-fixture" };
    }

    private sealed class TestDirectory : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), "ashlar-usage-" + Guid.NewGuid().ToString("N"));
        internal string DatabasePath => Path.Combine(_path, "runs.db");
        internal TestDirectory() => Directory.CreateDirectory(_path);
        public void Dispose() => Directory.Delete(_path, recursive: true);
    }

    private static ProviderFactory CreateFactory() => new(NullLogger<ProviderFactory>.Instance, resilientExecutor: new ImmediateRetryExecutor());
    private static OllamaModelServingBackend CreateBackend(HttpClient http) => new(http, Options.Create(new OllamaBackendOptions { BaseUrl = "http://127.0.0.1:11434" }));
    private static HttpResponseMessage JsonResponse(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static EnvironmentScope Configure(string url) => new(new Dictionary<string, string?>
    {
        ["OPENAI_API_KEY"] = "secret-api-key", ["OPENAI_BASE_URL"] = url, ["OPENAI_MODEL"] = "probe:latest",
        ["OPENAI_COMPAT_API_KEY"] = "secret-api-key", ["OPENAI_COMPAT_BASE_URL"] = url, ["OPENAI_COMPAT_MODEL"] = "probe:latest",
        ["AZURE_OPENAI_API_KEY"] = "secret-api-key", ["AZURE_OPENAI_ENDPOINT"] = url, ["AZURE_OPENAI_DEPLOYMENT"] = "probe",
        ["ASHLAR_OLLAMA_BASE_URL"] = url, ["OLLAMA_MODEL"] = "probe:latest", ["ASHLAR_LLM_RETRY_COUNT"] = "1",
        ["ASHLAR_EPHEMERAL_MODELS"] = "0"
    });

    private sealed class EnvironmentScope : IDisposable
    {
        private readonly Dictionary<string, string?> _previous;
        internal EnvironmentScope(Dictionary<string, string?> values)
        {
            _previous = values.ToDictionary(v => v.Key, v => Environment.GetEnvironmentVariable(v.Key));
            foreach (var value in values) Environment.SetEnvironmentVariable(value.Key, value.Value);
        }
        public void Dispose()
        {
            foreach (var value in _previous) Environment.SetEnvironmentVariable(value.Key, value.Value);
        }
    }

    private sealed class ImmediateRetryExecutor : IResilientExecutor
    {
        public Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, RetryPolicy policy, CancellationToken cancellationToken = default) =>
            new ResilientExecutor().ExecuteAsync(operation, policy with { BaseDelay = TimeSpan.Zero }, cancellationToken);
    }

    private sealed class ProviderServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Queue<(int Status, string Body)> _responses;
        private readonly Task _loop;
        private int _inferenceRequests;
        internal string BaseUrl { get; }
        internal int InferenceRequests => Volatile.Read(ref _inferenceRequests);

        internal ProviderServer(params (int Status, string Body)[] responses)
        {
            _responses = new(responses);
            _listener.Start();
            BaseUrl = "http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port;
            _loop = RunAsync(_stop.Token);
        }

        private async Task RunAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    using var client = await _listener.AcceptTcpClientAsync(ct);
                    await using var stream = client.GetStream();
                    var header = new StringBuilder();
                    var one = new byte[1];
                    while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                    {
                        if (await stream.ReadAsync(one, ct) == 0) throw new IOException("Request ended before headers.");
                        header.Append((char)one[0]);
                    }
                    var headers = header.ToString();
                    var lengthLine = headers.Split("\r\n").FirstOrDefault(h => h.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
                    var length = lengthLine is null ? 0 : int.Parse(lengthLine.Split(':')[1].Trim());
                    var buffer = new byte[4096];
                    while (length > 0)
                    {
                        var read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(length, buffer.Length)), ct);
                        if (read == 0) throw new IOException("Request ended before body.");
                        length -= read;
                    }
                    (int Status, string Body) response;
                    if (headers.StartsWith("GET /api/tags", StringComparison.Ordinal))
                        response = (200, "{\"models\":[{\"name\":\"probe:latest\",\"size\":1}]}");
                    else
                    {
                        Interlocked.Increment(ref _inferenceRequests);
                        response = _responses.Count > 0 ? _responses.Dequeue() : (500, "{}");
                    }
                    var body = Encoding.UTF8.GetBytes(response.Body);
                    var preamble = Encoding.ASCII.GetBytes($"HTTP/1.1 {response.Status} Test\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(preamble, ct);
                    await stream.WriteAsync(body, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (SocketException) when (ct.IsCancellationRequested) { }
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            await _loop;
            _stop.Dispose();
        }
    }
}
