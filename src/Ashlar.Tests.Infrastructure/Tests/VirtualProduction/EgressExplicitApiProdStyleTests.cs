using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Ashlar.Abstractions.Security.Egress;
using Ashlar.Core.Application.Common.Ports;
using Ashlar.Infrastructure.Ide;
using Ashlar.Infrastructure.Metrics;
using Ashlar.Tests.Infrastructure.Helpers;
using Ashlar.Tests.Infrastructure.Helpers.VirtualProduction;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.VirtualProduction;

[Collection("EnvironmentVariables")]
[Trait("Category", "ProdStyle")]
public sealed class EgressExplicitApiProdStyleTests
{
    [Fact(Timeout = TestTimeouts.HostTouching)]
    public async Task Refused_otlp_skips_both_exporters_and_warns_while_the_host_stays_healthy()
    {
        using var profile = new EnvironmentVariableScope("ASHLAR_DEPLOYMENT_PROFILE", "secure-workstation");
        using var mode = new EnvironmentVariableScope("ASHLAR_EGRESS_MODE", "enforce");
        using var state = new EgressProcessStateScope(reset: true);
        using var logs = new WarningProvider();
        using var stderr = new StringWriter();
        var previous = Console.Error;
        Console.SetError(stderr);
        try
        {
            using var factory = new AshlarApiWebApplicationFactory().WithWebHostBuilder(builder =>
            {
                builder.UseSetting("urls", "http://localhost:5000");
                builder.UseSetting("OTEL_EXPORTER_OTLP_ENDPOINT", "https://private-collector.invalid:4318/secret?token=hidden");
                builder.ConfigureLogging(logging => logging.AddProvider(logs));
            });
            using var client = factory.CreateClient();
            (await client.GetAsync("/health")).StatusCode.Should().Be(HttpStatusCode.OK);
            factory.Services.GetService<TracerProvider>().Should().BeNull();
            factory.Services.GetService<MeterProvider>().Should().BeNull();
            factory.Services.GetRequiredService<IMetricsCollector>().Should().BeOfType<MemoryMetricsCollector>();
            var line = stderr.ToString().Split('\n').Should().ContainSingle(x => x.StartsWith("OTLP export refused:")).Which;
            line.Should().Contain("Egress refused by policy").And.Contain("ref=").And.NotContain("private-collector").And.NotContain("hidden");
            logs.Messages.Where(x => x.StartsWith("OTLP export refused:")).Should().ContainSingle()
                .Which.Trim().Should().Be(line.Trim());
        }
        finally { Console.SetError(previous); }
    }

    [Theory(Timeout = TestTimeouts.HostTouching)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ide_stream_returns_only_fixed_refusal_text_and_reference(bool duringIteration)
    {
        using var state = new EgressProcessStateScope(reset: true);
        var refusal = Refusal();
        using var factory = new AshlarApiWebApplicationFactory().WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddSingleton<IChatClient>(new RefusingChatClient(refusal, duringIteration))));
        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/ide/chat/stream", new { prompt = "hello" });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("event: error").And.NotContain("event: done")
            .And.NotContain("wrapper-secret").And.NotContain("private-destination").And.NotContain("SystemHigh");
        var payload = body.Split('\n').Single(line => line.StartsWith("data: ") && line.Contains("EGRESS_REFUSED"));
        using var document = JsonDocument.Parse(payload[6..]);
        var data = document.RootElement;
        var message = $"egress refused by policy (ref {refusal.Ref})";
        data.GetProperty("message").GetString().Should().Be(message);
        data.GetProperty("errorCode").GetString().Should().Be("EGRESS_REFUSED");
        data.GetProperty("egressRef").GetString().Should().Be(refusal.Ref);
        var run = IdeRunTracker.Instance.Get(data.GetProperty("runId").GetString()!);
        run!.Status.Should().Be("failed");
        run.Error.Should().Be(message, "the remotely readable run record must be redacted too");
    }

    [Fact(Timeout = TestTimeouts.HostTouching)]
    public async Task Ide_optional_model_discovery_logs_a_wrapped_refusal_and_returns_its_local_list()
    {
        using var state = new EgressProcessStateScope(reset: true);
        var refusal = Refusal();
        using var logs = new WarningProvider();
        using var factory = new AshlarApiWebApplicationFactory().WithWebHostBuilder(builder =>
        {
            builder.ConfigureLogging(logging => logging.AddProvider(logs));
            builder.ConfigureTestServices(services => services.ConfigureHttpClientDefaults(http =>
                http.ConfigurePrimaryHttpMessageHandler(() => new RefusingHandler(refusal))));
        });
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/ide/models");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("models").And.NotContain("wrapper-secret").And.NotContain(refusal.Ref);
        logs.Messages.Where(x => x.StartsWith("IDE model discovery egress refused:"))
            .Should().ContainSingle().Which.Should().Contain(refusal.Message).And.NotContain("wrapper-secret");
    }

    private static EgressRefusedException Refusal() => new(new EgressGuard("secure-workstation", "enforce")
        .Evaluate(new EgressRequest(EgressFamilies.ModelLegacy, "EG-MDL-14", "https://private-destination.invalid/secret")));

    private static Exception Wrap(EgressRefusedException refusal) =>
        new AggregateException("wrapper-secret", new Exception("ordinary-secret"), new InvalidOperationException("wrapper-secret", refusal));

    private sealed class RefusingHandler(EgressRefusedException refusal) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => throw Wrap(refusal);
    }

    private sealed class RefusingChatClient(EgressRefusedException refusal, bool duringIteration) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw Wrap(refusal);
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            duringIteration ? Stream(cancellationToken) : throw Wrap(refusal);
        private async IAsyncEnumerable<ChatResponseUpdate> Stream([EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            if (duringIteration) throw Wrap(refusal);
            yield break;
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class WarningProvider : ILoggerProvider
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public ILogger CreateLogger(string categoryName) => new Capture(Messages);
        public void Dispose() { }
        private sealed class Capture(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel level) => true;
            public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (level == LogLevel.Warning) messages.Enqueue(formatter(state, exception));
            }
        }
    }
}
