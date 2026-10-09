using System.Collections.Concurrent;
using System.Net;
using Ashlar.Abstractions.Security.Egress;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Ashlar.Commercial.Tests.Fleet.Host;

/// <summary>
/// SPEC-007 PR 3b, the behavioural twin of the Fleet registration's route: Fleet.Host's own
/// <see cref="IHttpClientFactory"/> puts the report-only guard handler on the <c>mesh-lab-worker-executor</c> client
/// that <c>Ashlar.Commercial.Fleet.Infrastructure</c> names, once.
/// </summary>
/// <remarks>
/// <para>Fleet.Infrastructure cannot reach <c>AddAshlarEgressGuard</c> (its project does not reference
/// Ashlar.Infrastructure), so its registration is pinned <c>Upstream:</c> Fleet.Host's Program.cs, where
/// <c>AddAshlar</c> installs the guard on every factory client and the explicit call after it adds nothing. This
/// proves Fleet.Host binds that client NAME: one send through it is one <c>factory:mesh-lab-worker-executor</c>
/// decision. It does not enable the MeshLab worker, whose registration only runs when
/// <c>Ashlar:MeshLab:WorkerExecutor:Enabled</c> is true; the factory builds a client by name either way.</para>
/// <para><b>Redirects</b> (SPEC-007 PR 4.3). The <c>ashlar-sns-signing</c> client never follows one: against a
/// loopback server that answers 302, over the factory's own primary handler, it returns the 302.</para>
/// <para><b>Isolation.</b> The decision log is process-wide, so the assertion filters by a destination host unique
/// to the test. Hermetic: a test-only client default puts a stub primary handler (a new one per handler build) under
/// every client, so nothing leaves the process.</para>
/// </remarks>
[Trait("Category", "CommercialFleetHost")]
public sealed class EgressFleetHostTwinTests
{
    private const string TestApiKey = "fleet-host-egress-test-key";
    private const string MeshLabWorkerClient = "mesh-lab-worker-executor";
    private const string SnsSigningClient = "ashlar-sns-signing";

    [Fact]
    public async Task TheMeshLabWorkerClient_FromTheFleetHostFactory_RecordsOneDecision_WithItsFactorySite()
    {
        var host = $"egress-twin-{Guid.NewGuid():N}.example";
        var recorder = new DecisionRecorder(host);
        using var subscription = EgressDecisionLog.Subscribe(recorder);
        var sends = 0;

        await using var factory = new WebApplicationFactory<FleetHostProgram>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Ashlar:Security:ApiKey", TestApiKey);
            builder.ConfigureTestServices(services => services.ConfigureHttpClientDefaults(
                b => b.ConfigurePrimaryHttpMessageHandler(() => new StubPrimaryHandler(request =>
                {
                    // The stub sits under every client in the host; count only the sends to this test's host.
                    if (string.Equals(request.RequestUri?.Host, host, StringComparison.OrdinalIgnoreCase))
                        Interlocked.Increment(ref sends);
                }))));
        });

        using (var health = factory.CreateClient())
        {
            (await health.GetAsync("/health")).StatusCode.Should().Be(HttpStatusCode.OK, "the host started");
        }

        using var client = factory.Services.GetRequiredService<IHttpClientFactory>().CreateClient(MeshLabWorkerClient);
        using (var response = await client.GetAsync(new Uri($"https://{host}/api/mesh/tasks?x=1")))
            response.StatusCode.Should().Be(HttpStatusCode.NoContent, "the stub primary handler answers every send");

        Volatile.Read(ref sends).Should().Be(1, "the stub saw the one send to this test's host");
        var decision = recorder.Decisions.Should().ContainSingle(
            "one guard handler on the client: AddAshlar installed it, and the Program.cs call added none").Which;
        decision.Site.Should().Be("factory:" + MeshLabWorkerClient);
        decision.Family.Should().Be(EgressFamilies.HttpFactory);
        decision.Destination.Should().Be($"https://{host}");
        decision.DestinationClass.Should().Be(EgressDestinationClass.NetworkExport);
        decision.Fault.Should().BeNull();
    }

    [Fact]
    public async Task TheSnsSigningClient_NeverFollowsARedirect_OverTheFactorysOwnPrimaryHandler()
    {
        // SPEC-007 PR 4.3: the signing-certificate fetch is checked against its host before the send, so a redirect
        // would reach a host nobody checked. A loopback server answers 302; the client returns it and never asks for
        // the target. The primary handler is the factory's own (no test default replaces it), so the runtime would
        // follow on its own, and the egress redirect follower would follow for it, unless Program.cs turns it off.
        var paths = new ConcurrentQueue<string>();
        var serverBuilder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production", ContentRootPath = AppContext.BaseDirectory });
        serverBuilder.Logging.ClearProviders();
        serverBuilder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0));
        await using var server = serverBuilder.Build();
        server.Run(context =>
        {
            paths.Enqueue(context.Request.Path.Value ?? string.Empty);
            if (context.Request.Path == "/a")
            {
                context.Response.StatusCode = StatusCodes.Status302Found;
                context.Response.Headers.Location = "/b";
            }

            return Task.CompletedTask;
        });
        await server.StartAsync();
        var port = new Uri(server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()).Port;

        await using var factory = new WebApplicationFactory<FleetHostProgram>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Ashlar:Security:ApiKey", TestApiKey);
        });
        using (var health = factory.CreateClient())
        {
            (await health.GetAsync("/health")).StatusCode.Should().Be(HttpStatusCode.OK, "the host started");
        }

        using var client = factory.Services.GetRequiredService<IHttpClientFactory>().CreateClient(SnsSigningClient);
        using var response = await client.GetAsync(new Uri($"http://127.0.0.1:{port}/a"));

        response.StatusCode.Should().Be(HttpStatusCode.Found, "the SNS signing client returns the redirect");
        paths.Should().Equal(new[] { "/a" }, "the redirect target is never asked for");
        await server.StopAsync();
    }

    /// <summary>Keeps the decisions whose destination names this test's unique host.</summary>
    private sealed class DecisionRecorder : IEgressDecisionSink
    {
        private readonly string _host;
        private readonly ConcurrentQueue<EgressDecision> _decisions = new();

        public DecisionRecorder(string host) => _host = host;

        public IReadOnlyList<EgressDecision> Decisions => _decisions.ToArray();

        public void Record(EgressDecision decision)
        {
            if (decision.Destination.Contains(_host, StringComparison.Ordinal))
                _decisions.Enqueue(decision);
        }
    }

    /// <summary>The primary handler: answers 204 to every send, so nothing leaves the process.</summary>
    private sealed class StubPrimaryHandler : HttpMessageHandler
    {
        private readonly Action<HttpRequestMessage> _onSend;

        public StubPrimaryHandler(Action<HttpRequestMessage> onSend) => _onSend = onSend;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _onSend(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent) { RequestMessage = request });
        }
    }
}
