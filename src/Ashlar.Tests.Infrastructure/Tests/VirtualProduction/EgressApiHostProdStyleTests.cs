using System.Collections.Concurrent;
using System.Net;
using Ashlar.Abstractions.Security.Egress;
using Ashlar.Infrastructure.Egress;
using Ashlar.Tests.Infrastructure.Helpers;
using Ashlar.Tests.Infrastructure.Helpers.VirtualProduction;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.VirtualProduction;

/// <summary>
/// SPEC-007 PR 3b, ProdStyle twin of the egress routes in the real Ashlar.API host: the factory guard that
/// <c>AddAshlar</c> installs covers the host's own <c>ashlar-sns-signing</c> client, the explicit call in Program.cs
/// adds nothing, and an OTLP endpoint records one <c>EG-TEL-01</c> decision at registration.
/// </summary>
/// <remarks>
/// <para><b>Factory.</b> Program.cs registers <c>ashlar-sns-signing</c> before <c>AddAshlar</c>; the guard binding
/// covers clients registered before and after it, so one send through that client from the host's own
/// <see cref="IHttpClientFactory"/> is one <c>factory:ashlar-sns-signing</c> decision. A test-only client default
/// puts a stub primary handler (a new one per handler build) under it, so nothing leaves the process.</para>
/// <para><b>Idempotent.</b> The host's descriptor list holds one guard marker, one hosted activator and one
/// <see cref="IEgressGuard"/>, the guard <c>AddAshlar</c> composed (SPEC-007 PR 4.6): the Program.cs call after
/// <c>AddAshlar</c> hit the marker and registered nothing.</para>
/// <para><b>EG-TEL-01.</b> The guard is evaluated once, when the host registers OTLP export, with the endpoint as a
/// name; the record keeps scheme, host and port and drops userinfo, path and query. No subject frame flows on the
/// thread that runs Program.cs, so the decision is found by its site and its unique destination host. When the
/// exporters' clients come from the factory they also record <c>factory:</c> decisions to the same host; the site
/// filter leaves those out.</para>
/// <para><b>Redirects</b> (SPEC-007 PR 4.3). The <c>ashlar-sns-signing</c> client never follows one: against a
/// loopback server that answers 302, over the factory's own primary handler, it returns the 302.</para>
/// <para>Program.cs reads every switch through <c>builder.Configuration</c>, so the values are injected with
/// <c>UseSetting</c> only; no process environment variable is touched.</para>
/// </remarks>
[Collection("Integration")]
[Trait("Category", "Integration")]
[Trait("Category", "ProdStyle")]
public sealed class EgressApiHostProdStyleTests
{
    private const string SnsSigningClient = "ashlar-sns-signing";

    [Fact(Timeout = TestTimeouts.HostTouching)]
    public async Task TheApiHost_RegistersTheGuardOnce_SoTheProgramCallAddsNothing()
    {
        IReadOnlyList<ServiceDescriptor>? descriptors = null;
        using var factory = new AshlarApiWebApplicationFactory().WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => descriptors = services.ToList()));
        await AssertHealthyAsync(factory);

        descriptors.Should().NotBeNull("the test services callback runs when the host is built");
        descriptors!.Where(d => !d.IsKeyedService
                && d.ServiceType.Name == "EgressGuardRegistration"
                && d.ServiceType.DeclaringType == typeof(EgressServiceCollectionExtensions))
            .Should().ContainSingle("AddAshlarEgressGuard ran its body once, in AddAshlar; every later call hit the marker");
        descriptors.Where(d => !d.IsKeyedService
                && d.ServiceType == typeof(IHostedService)
                && d.ImplementationType == typeof(EgressDecisionLoggerActivator))
            .Should().ContainSingle();
        descriptors.Where(d => !d.IsKeyedService && d.ServiceType == typeof(IEgressGuard))
            .Should().ContainSingle()
            .Which.ImplementationInstance.Should().BeOfType<EgressGuard>()
            .And.NotBeSameAs(EgressGuard.ProcessDefault, "from SPEC-007 PR 4.6 AddAshlar binds the guard it composed");
    }

    [Fact(Timeout = TestTimeouts.HostTouching)]
    public async Task TheSnsSigningClient_FromTheHostFactory_RecordsOneDecision_WithItsFactorySite()
    {
        var host = UniqueHost();
        var recorder = new DecisionRecorder(d => d.Destination.Contains(host, StringComparison.Ordinal));
        using var subscription = EgressDecisionLog.Subscribe(recorder);
        var sends = new SendCounter(host);

        using var factory = new AshlarApiWebApplicationFactory().WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.ConfigureHttpClientDefaults(
                b => b.ConfigurePrimaryHttpMessageHandler(() => new StubPrimaryHandler(sends)))));
        await AssertHealthyAsync(factory);

        using var client = factory.Services.GetRequiredService<IHttpClientFactory>().CreateClient(SnsSigningClient);
        using (var response = await client.GetAsync(new Uri($"https://{host}/cert.pem?x=1")))
            response.StatusCode.Should().Be(HttpStatusCode.NoContent, "the stub primary handler answers every send");

        sends.Count.Should().Be(1, "the stub saw the one send to this test's host");
        var decision = recorder.Decisions.Should().ContainSingle(
            "one guard handler on the client: AddAshlar installed it, and the Program.cs call added none").Which;
        decision.Site.Should().Be("factory:" + SnsSigningClient);
        decision.Family.Should().Be(EgressFamilies.HttpFactory);
        decision.Destination.Should().Be($"https://{host}");
        decision.DestinationClass.Should().Be(EgressDestinationClass.NetworkExport);
        decision.Fault.Should().BeNull();
    }

    [Fact(Timeout = TestTimeouts.HostTouching)]
    public async Task TheSnsSigningClient_NeverFollowsARedirect_OverTheFactorysOwnPrimaryHandler()
    {
        // SPEC-007 PR 4.3: the signing-certificate fetch is checked against its host before the send, so a redirect
        // would reach a host nobody checked. A loopback server answers 302; the client returns it and never asks for
        // the target. The primary handler is the factory's own (no test default replaces it), so the runtime would
        // follow on its own, and the egress redirect follower would follow for it, unless Program.cs turns it off.
        await using var server = await LoopbackRedirectServer.StartAsync();
        using var factory = new AshlarApiWebApplicationFactory();
        await AssertHealthyAsync(factory);

        using var client = factory.Services.GetRequiredService<IHttpClientFactory>().CreateClient(SnsSigningClient);
        using var response = await client.GetAsync(new Uri($"http://127.0.0.1:{server.Port}/a"));

        response.StatusCode.Should().Be(HttpStatusCode.Found, "the SNS signing client returns the redirect");
        server.Paths.Should().Equal(new[] { "/a" }, "the redirect target is never asked for");
    }

    [Fact(Timeout = TestTimeouts.HostTouching)]
    public async Task AnOtlpEndpoint_RecordsOneEgTel01Decision_WithoutUserinfoPathOrQuery()
    {
        var host = $"egress-twin-{Guid.NewGuid():N}.invalid";
        var recorder = new DecisionRecorder(d =>
            d.Site == "EG-TEL-01" && d.Destination.Contains(host, StringComparison.Ordinal));
        using var subscription = EgressDecisionLog.Subscribe(recorder);

        using var factory = new AshlarApiWebApplicationFactory().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("OTEL_EXPORTER_OTLP_ENDPOINT", $"https://u:p@{host}:4318/v1/x?q=1");
            // Bounds the exporter's per-batch wait so host disposal stays quick when the endpoint cannot be reached.
            builder.UseSetting("OTEL_EXPORTER_OTLP_TIMEOUT", "2000");
        });
        await AssertHealthyAsync(factory);

        var decision = recorder.Decisions.Should().ContainSingle(
            "the guard is evaluated once, when Program.cs registers OTLP export").Which;
        decision.Family.Should().Be(EgressFamilies.Telemetry);
        decision.Destination.Should().Be($"https://{host}:4318", "userinfo, path and query never reach a record");
        decision.DestinationClass.Should().Be(EgressDestinationClass.NetworkExport);
        decision.Fault.Should().BeNull();
    }

    /// <summary>A loopback Kestrel: <c>/a</c> answers 302 to <c>/b</c>, anything else 200; it keeps every path asked for.</summary>
    private sealed class LoopbackRedirectServer : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly ConcurrentQueue<string> _paths;

        private LoopbackRedirectServer(WebApplication app, ConcurrentQueue<string> paths, int port)
        {
            _app = app;
            _paths = paths;
            Port = port;
        }

        public int Port { get; }

        public IReadOnlyList<string> Paths => _paths.ToArray();

        public static async Task<LoopbackRedirectServer> StartAsync()
        {
            var paths = new ConcurrentQueue<string>();
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production", ContentRootPath = AppContext.BaseDirectory });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0));
            var app = builder.Build();
            app.Run(context =>
            {
                paths.Enqueue(context.Request.Path.Value ?? string.Empty);
                if (context.Request.Path == "/a")
                {
                    context.Response.StatusCode = StatusCodes.Status302Found;
                    context.Response.Headers.Location = "/b";
                }

                return Task.CompletedTask;
            });
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            return new LoopbackRedirectServer(app, paths, new Uri(address).Port);
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    private static async Task AssertHealthyAsync(WebApplicationFactory<Program> factory)
    {
        using var client = factory.CreateClient();
        var health = await client.GetAsync("/health");
        health.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static string UniqueHost() => $"egress-twin-{Guid.NewGuid():N}.example";

    /// <summary>Keeps the decisions the test's filter accepts.</summary>
    private sealed class DecisionRecorder : IEgressDecisionSink
    {
        private readonly Func<EgressDecision, bool> _keep;
        private readonly ConcurrentQueue<EgressDecision> _decisions = new();

        public DecisionRecorder(Func<EgressDecision, bool> keep) => _keep = keep;

        public IReadOnlyList<EgressDecision> Decisions => _decisions.ToArray();

        public void Record(EgressDecision decision)
        {
            if (_keep(decision))
                _decisions.Enqueue(decision);
        }
    }

    /// <summary>Counts the sends to this test's host. The stub sits under every client in the host, so a host
    /// component that sends at startup (an NCR probe, say) reaches it too; those sends are not counted.</summary>
    private sealed class SendCounter
    {
        private readonly string _host;
        private int _count;

        public SendCounter(string host) => _host = host;

        public int Count => Volatile.Read(ref _count);

        public void Observe(HttpRequestMessage request)
        {
            if (string.Equals(request.RequestUri?.Host, _host, StringComparison.OrdinalIgnoreCase))
                Interlocked.Increment(ref _count);
        }
    }

    /// <summary>The primary handler: answers 204 to every send, so nothing leaves the process.</summary>
    private sealed class StubPrimaryHandler : HttpMessageHandler
    {
        private readonly SendCounter _sends;

        public StubPrimaryHandler(SendCounter sends) => _sends = sends;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _sends.Observe(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent) { RequestMessage = request });
        }
    }
}
