using System.Collections.Concurrent;
using System.Reflection;
using Ashlar.Abstractions.Security.Egress;
using Ashlar.Infrastructure.Execution;
using Ashlar.Infrastructure.Execution.Ollama;
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
/// one: another test may drive the same product client at the same refused port concurrently.</para>
/// <para>The A2A (EG-XPT-01) and MCP (EG-XPT-06) twins live in their own suites: on net8.0, the cert-gate framework,
/// this project reaches Ashlar.Transport.A2A and Ashlar.Mcp.Client only through Ashlar.API, which ships on net10.0 only.</para>
/// </remarks>
[Trait("Category", "Certification")]
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
        using var sink = SiteSink.Subscribe("EG-MDL-07");
        var factory = new ProviderFactory(NullLogger<ProviderFactory>.Instance);
        await factory.OllamaWarmup;
        var provider = (OllamaProvider)typeof(ProviderFactory)
            .GetMethod("GetOrCreateOllamaProvider", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(factory, [Refused])!;

        var health = await provider.CheckHealthAsync(CancellationToken.None);

        (health.IsSuccess && health.Value).Should().BeFalse("nothing listens on the refused port");
        sink.Seen.Should().NotBeEmpty().And.OnlyContain(d => d.Family == EgressFamilies.ModelLegacy);
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
