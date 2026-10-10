using Ashlar.Abstractions.Security.Egress;
using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Ashlar.Abstractions.Transport;
using Xunit;

namespace Ashlar.Transport.A2A.Server.Tests;

/// <summary>
/// Full-wire proof for the A2A phase: the real <see cref="A2AAgentTransport"/> talks JSON-RPC to
/// the really-mapped Ashlar A2A server endpoints over an ASP.NET TestServer, which executes a fake
/// in-process <see cref="IAgentTransport"/>. One test covers both adapter directions plus the
/// scheme convention end to end; the others pin discovery and fail-closed behavior.
/// </summary>
[Collection(DeploymentProfileEnvironmentCollection.Name)]
public sealed class A2AServerRoundTripTests
{
    private sealed class FakeCatalog : IAshlarA2AAgentCatalog
    {
        public IReadOnlyList<AshlarA2AAgentDescriptor> GetAgents() =>
            new[] { AshlarA2AExposurePolicyTests.Agent("echo-agent") };
    }

    private sealed class EchoInProcessTransport : IAgentTransport
    {
        public AgentInvocationRequest? LastRequest { get; private set; }

        public bool Fail { get; set; }
        public Exception? Error { get; set; }
        public AgentResult? Result { get; set; }

        public Task<AgentResult> SendAsync(AgentInvocationRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            if (Error is not null) return Task.FromException<AgentResult>(Error);
            if (Result is not null) return Task.FromResult(Result);
            return Task.FromResult(Fail
                ? new AgentResult(Success: false, ErrorMessage: "agent exploded", ErrorCode: "boom")
                : new AgentResult(
                    Success: true,
                    Output: new Dictionary<string, object?> { ["echoed"] = request.Payload?["message"] },
                    CorrelationId: request.CorrelationId));
        }

        public Task<TransportHealth> CheckHealthAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new TransportHealth(true, "echo"));
    }

    private static async Task<(IHost Host, EchoInProcessTransport Agent)> StartServerAsync(bool enabled = true)
    {
        var agentTransport = new EchoInProcessTransport();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"{AshlarA2AServerOptions.SectionPath}:Enabled"] = enabled ? "true" : "false",
            [$"{AshlarA2AServerOptions.SectionPath}:PublicBaseUrl"] = "http://localhost",
            [$"{AshlarA2AServerOptions.SectionPath}:ExposedAgentIds:0"] = "echo-agent",
        }).Build();

        var host = await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddLogging();
                    services.AddRouting();
                    services.AddAshlarA2AServer(configuration);
                    services.AddSingleton<IAshlarA2AAgentCatalog, FakeCatalog>();
                    services.AddSingleton<IAgentTransport>(agentTransport);
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints => endpoints.MapAshlarA2AEndpoints());
                }))
            .StartAsync();

        return (host, agentTransport);
    }

    [Theory(Timeout = 60000)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Egress_refusal_round_trip_preserves_code_and_reference_without_raw_details(int form)
    {
        var refusal = new EgressRefusedException(new EgressGuard("full", "enforce").Evaluate(
            new EgressRequest(EgressFamilies.Http, "site-canary", new Uri("https://remote.example/private-canary"))));
        var (host, agent) = await StartServerAsync();
        using var ownedHost = host;
        if (form == 0) agent.Error = new AggregateException(new IOException("unrelated-canary"),
            new HttpRequestException("wrapper-canary", refusal));
        else agent.Result = new AgentResult(false, Output: new { secret = "output-canary" },
            ErrorMessage: "detail-canary", ErrorCode: form == 1 ? "EGRESS_REFUSED" : null,
            Metadata: new Dictionary<string, string> { ["errorCode"] = "EGRESS_REFUSED", ["egressRef"] = refusal.Ref });
        var capture = new CaptureResponse { InnerHandler = host.GetTestServer().CreateHandler() };
        using var client = new HttpClient(capture);
        var transport = new A2AAgentTransport(Options.Create(new A2ATransportOptions()), NullLogger<A2AAgentTransport>.Instance)
        { HttpClientFactoryOverride = _ => client };
        var result = await transport.SendAsync(new AgentInvocationRequest("echo-agent", "refusal-test",
            Options: new AgentInvocationOptions(TimeSpan.FromSeconds(20), 0, "a2a+http://localhost/api/a2a/echo-agent")));
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("a2a.egress_refused");
        result.ErrorMessage.Should().Be($"egress refused by policy (ref {refusal.Ref})");
        result.Metadata!["egressRef"].Should().Be(refusal.Ref);
        result.Output.Should().BeNull();
        capture.Body.Should().NotContain("canary").And.Contain(refusal.Ref);
    }

    private sealed class CaptureResponse : DelegatingHandler
    {
        internal string Body { get; private set; } = string.Empty;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            Body = await response.Content.ReadAsStringAsync(cancellationToken);
            return response;
        }
    }

    [Fact(Timeout = 60000)]
    public async Task Root_well_known_card_serves_the_primary_agent()
    {
        var (host, _) = await StartServerAsync();
        using var _ = host;
        var client = host.GetTestServer().CreateClient();

        var response = await client.GetAsync("/.well-known/agent-card.json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var card = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        card.RootElement.GetProperty("name").GetString().Should().Be("echo-agent-name");
        card.RootElement.GetProperty("capabilities").GetProperty("streaming").GetBoolean().Should().BeFalse();
    }

    [Fact(Timeout = 60000)]
    public async Task Per_agent_card_is_served_under_the_agent_prefix()
    {
        var (host, _) = await StartServerAsync();
        using var _ = host;
        var client = host.GetTestServer().CreateClient();

        var response = await client.GetAsync("/api/a2a/echo-agent/.well-known/agent-card.json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact(Timeout = 60000)]
    public async Task Disabled_server_maps_nothing()
    {
        var (host, _) = await StartServerAsync(enabled: false);
        using var _ = host;
        var client = host.GetTestServer().CreateClient();

        (await client.GetAsync("/.well-known/agent-card.json")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.PostAsync("/api/a2a/echo-agent", new StringContent("{}"))).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact(Timeout = 60000)]
    public async Task A2A_transport_round_trips_through_the_mapped_server_to_the_ashlar_agent()
    {
        var (host, agent) = await StartServerAsync();
        using var _ = host;
        var testServer = host.GetTestServer();

        var transport = new A2AAgentTransport(
            Options.Create(new A2ATransportOptions()),
            NullLogger<A2AAgentTransport>.Instance)
        {
            HttpClientFactoryOverride = _ => testServer.CreateClient(),
        };

        var request = new AgentInvocationRequest(
            AgentName: "echo-agent",
            CorrelationId: "corr-e2e",
            Payload: new Dictionary<string, object?> { ["message"] = "ping over a2a" },
            Options: new AgentInvocationOptions(
                TimeSpan.FromSeconds(20),
                MaxRetries: 0,
                TargetEndpoint: "a2a+http://localhost/api/a2a/echo-agent"));

        var result = await transport.SendAsync(request);

        result.Success.Should().BeTrue(result.ErrorMessage);
        result.Output.Should().BeOfType<JsonElement>()
            .Which.GetProperty("echoed").GetString().Should().Be("ping over a2a");

        // The server-side handler received the payload through the standard transport envelope
        // with the correlation id propagated via protocol metadata.
        agent.LastRequest!.AgentName.Should().Be("echo-agent");
        agent.LastRequest.CorrelationId.Should().Be("corr-e2e");
        agent.LastRequest.Payload!["message"].Should().BeOfType<string>().And.Be("ping over a2a");
    }

    [Fact(Timeout = 60000)]
    public async Task Failed_agent_execution_surfaces_as_a_failed_task_result()
    {
        var (host, agent) = await StartServerAsync();
        using var _ = host;
        agent.Fail = true;

        var transport = new A2AAgentTransport(
            Options.Create(new A2ATransportOptions()),
            NullLogger<A2AAgentTransport>.Instance)
        {
            HttpClientFactoryOverride = _ => host.GetTestServer().CreateClient(),
        };

        var result = await transport.SendAsync(new AgentInvocationRequest(
            AgentName: "echo-agent",
            CorrelationId: "corr-fail",
            Options: new AgentInvocationOptions(
                TimeSpan.FromSeconds(20),
                MaxRetries: 0,
                TargetEndpoint: "a2a+http://localhost/api/a2a/echo-agent")));

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("a2a.task.failed");
        result.ErrorMessage.Should().Contain("agent exploded");
    }
}
