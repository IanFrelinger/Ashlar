using System.Collections.Concurrent;
using System.Reflection;
using Ashlar.Abstractions.Security;
using Ashlar.Abstractions.Security.Egress;
using Ashlar.Infrastructure.Execution;
using Ashlar.Infrastructure.Execution.Ollama;
using Ashlar.Tests.Infrastructure.Helpers;
using Ashlar.Transport.Grpc;
using FluentAssertions;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>
/// SPEC-007 PR 3b, behavioural twin of the raw and protocol client routes: each client the product builds itself
/// records a decision under its inventory site when it sends.
/// </summary>
/// <remarks>
/// <para>Each test drives the product's own client construction (no injected client, no test seam) at a loopback
/// port nothing listens on, so the send fails with the connection refused, as it does without the guard, and
/// asserts the decision the guard recorded before the inner handler ran. The decision log is process-wide, so each
/// sink keeps only its own site and the refused destination, and asserts at least one decision rather than exactly
/// one: the count must not depend on whatever else in the process sends to the same port.</para>
/// <para>Where the route turned an object initializer into property assignments (SPEC-007 PR 3b, R8), the twin
/// also pins the preserved value it can reach: the Ollama client's 300 s default timeout. The class sits in the
/// non-parallel <c>EnvironmentVariables</c> collection because that case unsets <c>OLLAMA_TIMEOUT_SECONDS</c>.</para>
/// <para>The Ollama cloud-model twin (SPEC-007 PR 4.1) drives <c>OllamaProvider</c> over an <c>EgressHttp</c> client on a
/// stub handler instead, and counts the decisions made inside its own subject frame.</para>
/// <para>The A2A (EG-XPT-01) and MCP (EG-XPT-06) twins live in their own suites: on net8.0, the cert-gate framework,
/// this project reaches Ashlar.Transport.A2A and Ashlar.Mcp.Client only through Ashlar.API, which ships on net10.0 only.</para>
/// </remarks>
[Trait("Category", "Certification")]
[Collection("EnvironmentVariables")]
public sealed class EgressRawClientTwinTests
{
    private const string Refused = "http://127.0.0.1:1";

    [Fact]
    public async Task ProviderFactory_static_cloud_client_records_EG_MDL_03()
    {
        using var sink = SiteSink.Subscribe("EG-MDL-03");
        var http = (HttpClient)typeof(ProviderFactory).GetField("Http", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

        await FluentActions.Awaiting(() => http.GetAsync(Refused + "/v1/models")).Should().ThrowAsync<HttpRequestException>();

        sink.Seen.Should().NotBeEmpty().And.OnlyContain(d => d.Family == EgressFamilies.ModelLegacy);
    }

    [Fact]
    public async Task ProviderFactory_ollama_client_records_EG_MDL_07()
    {
        using var defaultTimeout = EnvironmentVariableScope.Unset("OLLAMA_TIMEOUT_SECONDS");
        using var sink = SiteSink.Subscribe("EG-MDL-07");
        var factory = new ProviderFactory(NullLogger<ProviderFactory>.Instance);
        await factory.OllamaWarmup;
        var provider = (OllamaProvider)typeof(ProviderFactory)
            .GetMethod("GetOrCreateOllamaProvider", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(factory, [Refused])!;
        var client = (HttpClient)typeof(ProviderFactory)
            .GetField("_ollamaHttpClient", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(factory)!;

        client.Timeout.Should().Be(TimeSpan.FromSeconds(300), "the route keeps the default Ollama timeout (R8)");

        var health = await provider.CheckHealthAsync(CancellationToken.None);

        (health.IsSuccess && health.Value).Should().BeFalse("nothing listens on the refused port");
        sink.Seen.Should().NotBeEmpty().And.OnlyContain(d => d.Family == EgressFamilies.ModelLegacy);
    }

    /// <summary>
    /// SPEC-007 PR 4.1 (D34): a model whose id ends in <c>-cloud</c> or <c>:cloud</c> runs on ollama.com, relayed by the
    /// local daemon, so <c>OllamaProvider</c> records one more decision before it sends the chat: an external model at
    /// <c>https://ollama.com</c>. The handler's own decisions still name the daemon the bytes go to. A local model gets
    /// no such decision. Report-only: the chat is sent either way. The provider runs over an <c>EgressHttp</c> client on
    /// a stub handler, inside a subject frame of its own, so only its decisions are counted and nothing reaches a socket.
    /// </summary>
    [Theory]
    [InlineData("gpt-oss:120b-cloud", true)]
    [InlineData("llama3:CLOUD", true)]
    [InlineData("llama3.1:latest", false)]
    [InlineData("cloudy:7b", false)]
    public async Task OllamaProvider_records_a_cloud_model_as_an_external_model_at_ollama_com(string model, bool cloud)
    {
        var id = "egress-twin-" + Guid.NewGuid().ToString("N");
        var sink = new FrameSink("subject:" + id);
        using var subscription = EgressDecisionLog.Subscribe(sink);
        using var frame = EgressSubject.Enter(id, new HighWaterMark());
        var stub = new OllamaStub(model);
        using var http = EgressHttp.CreateClient(stub, EgressFamilies.ModelLegacy, "EG-MDL-07");
        var provider = new OllamaProvider(http, "http://localhost:11434");

        (await provider.RefreshModelsAsync()).IsSuccess.Should().BeTrue();
        var chat = await provider.ExecuteChatAsync(model, "system", "user", null);

        chat.IsSuccess.Should().BeTrue("report-only: the chat is sent whatever the decision");
        stub.Requests.Should().Equal(new[] { "http://localhost:11434/api/tags", "http://localhost:11434/api/chat" });
        var relayed = sink.Seen.Where(d => d.Destination == "https://ollama.com").ToList();
        if (cloud)
        {
            var decision = relayed.Should().ContainSingle("'{0}' runs on ollama.com", model).Which;
            decision.Site.Should().Be("EG-MDL-07");
            decision.Family.Should().Be(EgressFamilies.ModelLegacy);
            decision.DestinationClass.Should().Be(EgressDestinationClass.ExternalModel);
        }
        else
        {
            relayed.Should().BeEmpty("'{0}' runs on the local daemon", model);
        }

        sink.Seen.Where(d => d.Destination == "http://localhost:11434").Should().HaveCount(
            2, "the handler still records each send to the daemon (tags, then chat)");
    }

    /// <summary>
    /// SPEC-007 PR 4.1 (D34), the resolved-name half of the rule: a config asks for the short name <c>gpt-oss</c>, the
    /// daemon lists only <c>gpt-oss:120b-cloud</c>, and <c>OllamaProvider</c> resolves the one by its single family
    /// prefix and sends the chat with the resolved, cloud name. The requested name is not a cloud id, so only the
    /// resolved name can record the relay: one EG-MDL-07 decision, an external model at <c>https://ollama.com</c>.
    /// </summary>
    [Fact]
    public async Task OllamaProvider_records_a_short_name_that_resolves_to_a_cloud_model_at_ollama_com()
    {
        var id = "egress-twin-" + Guid.NewGuid().ToString("N");
        var sink = new FrameSink("subject:" + id);
        using var subscription = EgressDecisionLog.Subscribe(sink);
        using var frame = EgressSubject.Enter(id, new HighWaterMark());
        var stub = new OllamaStub("gpt-oss:120b-cloud");
        using var http = EgressHttp.CreateClient(stub, EgressFamilies.ModelLegacy, "EG-MDL-07");
        var provider = new OllamaProvider(http, "http://localhost:11434");

        (await provider.RefreshModelsAsync()).IsSuccess.Should().BeTrue();
        OllamaProvider.IsOllamaCloudModel("gpt-oss").Should().BeFalse("the requested name alone is not a cloud id");
        var chat = await provider.ExecuteChatAsync("gpt-oss", "system", "user", null);

        chat.IsSuccess.Should().BeTrue("report-only: the chat is sent whatever the decision");
        stub.ChatModels.Should().Equal(new[] { "gpt-oss:120b-cloud" }, "the chat carries the resolved name");
        var decision = sink.Seen.Where(d => d.Destination == "https://ollama.com").Should()
            .ContainSingle("the resolved model runs on ollama.com").Which;
        decision.Site.Should().Be("EG-MDL-07");
        decision.Family.Should().Be(EgressFamilies.ModelLegacy);
        decision.DestinationClass.Should().Be(EgressDestinationClass.ExternalModel);
    }

    [Fact]
    public async Task Grpc_channel_handler_records_EG_XPT_03()
    {
        using var sink = SiteSink.Subscribe("EG-XPT-03");
        var factory = new DefaultGrpcChannelFactory(Options.Create(new GrpcTransportOptions()), NullLogger<DefaultGrpcChannelFactory>.Instance);
        try
        {
            var channel = factory.GetOrCreate(Refused);
            var method = new Method<byte[], byte[]>(MethodType.Unary, "twin.Svc", "Call",
                Marshallers.Create(b => b, b => b), Marshallers.Create(b => b, b => b));
            using var call = channel.CreateCallInvoker().AsyncUnaryCall(method, null, new CallOptions(deadline: DateTime.UtcNow.AddSeconds(30)), [1]);

            (await FluentActions.Awaiting(() => call.ResponseAsync).Should().ThrowAsync<RpcException>())
                .Which.StatusCode.Should().Be(StatusCode.Unavailable);
        }
        finally
        {
            factory.DisposeAll();
        }

        sink.Seen.Should().NotBeEmpty().And.OnlyContain(d => d.Family == EgressFamilies.Grpc);
    }

    /// <summary>Keeps the decisions made inside one subject frame.</summary>
    private sealed class FrameSink(string basis) : IEgressDecisionSink
    {
        private readonly ConcurrentQueue<EgressDecision> _seen = new();

        public IReadOnlyList<EgressDecision> Seen => _seen.ToArray();

        public void Record(EgressDecision decision)
        {
            if (string.Equals(decision.CurrentBasis, basis, StringComparison.Ordinal))
                _seen.Enqueue(decision);
        }
    }

    /// <summary>A local Ollama daemon that lists one model and answers every chat, keeping the model each chat names.</summary>
    private sealed class OllamaStub(string model) : HttpMessageHandler
    {
        private readonly ConcurrentQueue<string> _requests = new();
        private readonly ConcurrentQueue<string> _chatModels = new();

        public IReadOnlyList<string> Requests => _requests.ToArray();

        public IReadOnlyList<string> ChatModels => _chatModels.ToArray();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _requests.Enqueue(request.RequestUri!.AbsoluteUri);
            var tags = request.RequestUri.AbsolutePath.EndsWith("/api/tags", StringComparison.Ordinal);
            if (!tags && request.Content is not null)
                _chatModels.Enqueue(ModelOf(await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false)));

            var body = tags
                ? "{\"models\":[{\"name\":\"" + model + "\",\"size\":1}]}"
                : "{\"message\":{\"role\":\"assistant\",\"content\":\"ok\"},\"done\":true}";
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            };
        }

        // Reads the first JSON value only, as the daemon's decoder does: the provider's chat payload ends in one more
        // '}' than it opens, which Ollama ignores and a whole-document parse would reject.
        private static string ModelOf(byte[] chatBody)
        {
            var reader = new System.Text.Json.Utf8JsonReader(chatBody);
            using var chat = System.Text.Json.JsonDocument.ParseValue(ref reader);
            return chat.RootElement.GetProperty("model").GetString() ?? string.Empty;
        }
    }

    private sealed class SiteSink : IEgressDecisionSink, IDisposable
    {
        private readonly string _site;
        private readonly ConcurrentQueue<EgressDecision> _seen = new();
        private IDisposable? _subscription;

        private SiteSink(string site) => _site = site;

        public IReadOnlyList<EgressDecision> Seen => _seen.ToArray();

        public static SiteSink Subscribe(string site)
        {
            var sink = new SiteSink(site);
            sink._subscription = EgressDecisionLog.Subscribe(sink);
            return sink;
        }

        public void Record(EgressDecision decision)
        {
            if (decision.Site == _site && decision.Destination == Refused)
                _seen.Enqueue(decision);
        }

        public void Dispose() => _subscription?.Dispose();
    }
}
