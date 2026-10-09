using System.Collections.Concurrent;
using System.Net;
using Ashlar.Abstractions.Security.Egress;
using Ashlar.Infrastructure.Egress;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>Per-client no-follow policy must survive the shared primary's remembered redirect settings.</summary>
[Trait("Category", "Certification")]
public sealed class EgressRedirectNeverFollowTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public async Task Never_follow_wins_after_another_wrapper_remembers_the_shared_primary(bool factoryFirst, bool clearHandlers, bool synchronous)
    {
        var name = "never-shared-" + Guid.NewGuid().ToString("N");
        var primary = new RedirectingPrimary();
        using var earlierRaw = factoryFirst ? null : EgressHttp.CreateClient(primary, EgressFamilies.Http, "earlier", new EgressGuard("full"));
        var services = new ServiceCollection();
        services.AddAshlarEgressGuard();
        services.AddHttpClient("ordinary").ConfigurePrimaryHttpMessageHandler(() => primary);
        var restrictedBuilder = services.AddHttpClient(name).ConfigurePrimaryHttpMessageHandler(() => primary).NeverFollowRedirects();
        if (clearHandlers)
            restrictedBuilder.ConfigureAdditionalHttpMessageHandlers((handlers, _) => handlers.Clear());
        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();
        using var ordinary = factory.CreateClient("ordinary");
        using var restricted = factory.CreateClient(name);
        var recorder = new Recorder("factory:" + name);
        using var subscription = EgressDecisionLog.Subscribe(recorder);

        using var stopRequest = new HttpRequestMessage(HttpMethod.Get, "https://first.example/start");
        using var stopped = synchronous ? restricted.Send(stopRequest) : await restricted.SendAsync(stopRequest);

        stopped.StatusCode.Should().Be(HttpStatusCode.TemporaryRedirect, "the client's explicit no-follow policy overrides the primary's remembered original setting");
        primary.Requests.Should().ContainSingle().Which.Should().Be("https://first.example/start");
        recorder.Decisions.Select(d => d.Destination).Should().Equal("https://first.example");

        using var followRequest = new HttpRequestMessage(HttpMethod.Get, "https://first.example/start");
        using var followed = synchronous ? ordinary.Send(followRequest) : await ordinary.SendAsync(followRequest);
        followed.StatusCode.Should().Be(HttpStatusCode.OK, "no-follow belongs to the restricted client, not the shared primary's remembered policy");
        primary.Requests.Should().Equal("https://first.example/start", "https://first.example/start", "https://second.example/target");
    }

    [Fact]
    public async Task Never_follow_on_an_outer_factory_client_restricts_only_its_request_through_an_existing_follower()
    {
        var primary = new RedirectingPrimary();
        var innerServices = new ServiceCollection();
        innerServices.AddAshlarEgressGuard();
        innerServices.AddHttpClient("inner").ConfigurePrimaryHttpMessageHandler(() => primary);
        using var innerProvider = innerServices.BuildServiceProvider();
        var pipeline = innerProvider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler("inner");
        var outerServices = new ServiceCollection();
        outerServices.AddAshlarEgressGuard();
        outerServices.AddHttpClient("outer").ConfigurePrimaryHttpMessageHandler(() => pipeline).NeverFollowRedirects();
        using var outerProvider = outerServices.BuildServiceProvider();
        using var restricted = outerProvider.GetRequiredService<IHttpClientFactory>().CreateClient("outer");

        using var stopped = await restricted.GetAsync("https://first.example/start");

        stopped.StatusCode.Should().Be(HttpStatusCode.TemporaryRedirect, "the existing inner follower must honor this outer client's no-follow policy");
        primary.Requests.Should().ContainSingle();
        using var ordinary = innerProvider.GetRequiredService<IHttpClientFactory>().CreateClient("inner");
        using var followed = await ordinary.GetAsync("https://first.example/start");
        followed.StatusCode.Should().Be(HttpStatusCode.OK, "a separate request through the shared inner client keeps its own policy");
        primary.Requests.Should().HaveCount(3);
    }

    [Fact]
    public async Task Ordinary_clients_still_follow_when_a_shared_primary_was_already_disabled_by_Ashlar()
    {
        var primary = new RedirectingPrimary();
        using var earlier = EgressHttp.CreateClient(primary, EgressFamilies.Http, "earlier", new EgressGuard("full"));
        var services = new ServiceCollection();
        services.AddAshlarEgressGuard();
        services.AddHttpClient("ordinary").ConfigurePrimaryHttpMessageHandler(() => primary);
        using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("ordinary");

        using var response = await client.GetAsync("https://first.example/start");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        primary.Requests.Should().Equal("https://first.example/start", "https://second.example/target");
    }

    private sealed class RedirectingPrimary : HttpClientHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Answer(request));

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken) => Answer(request);

        private HttpResponseMessage Answer(HttpRequestMessage request)
        {
            Requests.Add(request.RequestUri!.AbsoluteUri);
            var response = new HttpResponseMessage(request.RequestUri.Host == "first.example"
                ? HttpStatusCode.TemporaryRedirect : HttpStatusCode.OK) { RequestMessage = request };
            if (response.StatusCode == HttpStatusCode.TemporaryRedirect)
                response.Headers.Location = new Uri("https://second.example/target");
            return response;
        }
    }

    private sealed class Recorder(string site) : IEgressDecisionSink
    {
        public ConcurrentQueue<EgressDecision> Decisions { get; } = new();

        public void Record(EgressDecision decision)
        {
            if (decision.Site == site)
                Decisions.Enqueue(decision);
        }
    }
}
