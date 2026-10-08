using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using Ashlar.Abstractions.Security;
using Ashlar.Abstractions.Security.Egress;
using Ashlar.Infrastructure.Egress;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>SPEC-007 PR 4.3b: regression twins for inward redirects, origin boundaries, handler chains and factory binding.</summary>
[Trait("Category", "Certification")]
public sealed class EgressRedirectHardeningTests
{
    private const string FollowerTypeName = "EgressRedirectHandler";
    private static readonly EgressGuard Guard = new("full");

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Factory_filter_order_warns_only_when_client_configuration_removed_the_guard(bool factoryFirst, bool clear)
    {
        var name = "filter-order-" + NewId();
        var logs = new CapturingLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(logs));
        if (factoryFirst)
            services.AddHttpClient();
        services.AddAshlarEgressGuard();
        var builder = services.AddHttpClient(name);
        if (clear)
            builder.ConfigureAdditionalHttpMessageHandlers((handlers, _) => handlers.Clear());
        using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(name);

        var warnings = logs.Entries.Where(e => e.Level == LogLevel.Warning && e.Message.Contains(name, StringComparison.Ordinal)).ToArray();
        warnings.Should().HaveCount(clear ? 1 : 0, "the factory logging scope is not a removed or moved Ashlar handler");
        if (clear)
            warnings[0].EventId.Id.Should().Be(7303);
        Chain(HandlerOf(client)).Count(h => h.GetType().Name == "EgressGuardHandler").Should().Be(1);
    }

    [Theory]
    [InlineData("http://same.example:8080/a", "http://same.example:8081/b")]
    [InlineData("http://same.example/a", "https://same.example/b")]
    public async Task P2_does_not_forward_API_key_headers_to_another_scheme_or_port(string from, string to)
    {
        var recorder = new Recorder(_ => false);
        var primary = new RedirectingClientHandler(recorder, request => request.RequestUri!.AbsolutePath == "/a"
            ? Redirect(HttpStatusCode.TemporaryRedirect, to) : null);
        using var client = EgressHttp.CreateClient(primary, EgressFamilies.Http, NewSite(), Guard);
        using var request = new HttpRequestMessage(HttpMethod.Post, from) { Content = new StringContent("private-body") };
        request.Headers.Add("X-Api-Key", "test-key");
        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.TemporaryRedirect);
        primary.Sends.Should().ContainSingle("P2 limits the full origin, even when the host name is unchanged");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Credentialed_primaries_keep_the_runtime_redirect_policy(bool sockets)
    {
        using HttpMessageHandler primary = sockets
            ? new SocketsHttpHandler { Credentials = new NetworkCredential("user", "secret") }
            : new HttpClientHandler { Credentials = new NetworkCredential("user", "secret") };
        using var client = EgressHttp.CreateClient(primary, EgressFamilies.Http, NewSite(), Guard);

        (primary is SocketsHttpHandler s ? s.AllowAutoRedirect : ((HttpClientHandler)primary).AllowAutoRedirect).Should().BeTrue();
        Follower(Chain(HandlerOf(client))[1]).Follows.Should().BeFalse("the runtime owns credential forwarding semantics");
    }

    [Theory]
    [InlineData("us-west-2", "us-west-2")]
    [InlineData(" us-west-2 ", "us-west-2")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void CloudBedrock_TheRuntimeClientsConfigurationNeverFollowsARedirect(string? region, string? expectedRegion)
    {
        var seam = typeof(Ashlar.AI.Pipeline.Clients.AwsBedrockChatClientFactory)
            .GetMethod("RuntimeConfig", BindingFlags.NonPublic | BindingFlags.Static);
        seam.Should().NotBeNull("the SDK-owned transport has a configuration seam without contacting AWS");
        var config = (Amazon.BedrockRuntime.AmazonBedrockRuntimeConfig)seam!.Invoke(null, [region])!;

        config.AllowAutoRedirect.Should().BeFalse();
        config.RegionEndpoint?.SystemName.Should().Be(expectedRegion);
    }

    [Fact]
    public async Task Under_an_enforcing_guard_a_redirect_to_a_remote_host_is_decided_refused_before_the_hop_is_sent()
    {
        var id = NewId();
        var name = "enforce-" + id;
        var recorder = new Recorder(d => d.Site == "factory:" + name);
        using var subscription = EgressDecisionLog.Subscribe(recorder);
        var stub = new RedirectingClientHandler(recorder, request =>
            request.RequestUri!.IsLoopback ? Redirect(HttpStatusCode.TemporaryRedirect, $"http://remote-{id}.example/b") : null);

        var services = new ServiceCollection();
        services.AddSingleton<IEgressGuard>(new EgressGuard("full", "enforce"));
        services.AddAshlarEgressGuard();
        services.AddHttpClient(name).ConfigurePrimaryHttpMessageHandler(() => stub);
        using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(name);

        using var response = await client.GetAsync(new Uri("http://127.0.0.1:5999/a"));

        recorder.Decisions.Should().HaveCount(2);
        var hop1 = recorder.Decisions[0];
        hop1.Destination.Should().Be("http://127.0.0.1:5999");
        hop1.Mode.Should().Be("enforce");
        hop1.Refused.Should().BeFalse("hop 1 is inside the host boundary");

        var hop2 = recorder.Decisions[1];
        hop2.Destination.Should().Be($"http://remote-{id}.example");
        hop2.Mode.Should().Be("enforce");
        hop2.Refused.Should().BeTrue("with no subject the current label is SystemHigh, which may not go to a network export");
        hop2.Access.Reason.Should().Be(AccessDenialReason.SystemHighData);
        stub.Sends.Should().HaveCount(2, "until SPEC-007 PR 4.7 no route acts on a refusal, so the hop is still sent");
        stub.Sends[1].DecisionsBefore.Should().Be(2, "the refusal was decided before the hop was sent, which is where PR 4.7 stops it");
    }

    [Fact]
    public void A_primary_with_credentials_or_of_an_unknown_type_is_left_as_it_is()
    {
        var site = NewSite();
        var withCredentials = new HttpClientHandler { Credentials = new NetworkCredential("u", "p") };
        using var credentialed = EgressHttp.CreateClient(withCredentials, EgressFamilies.Http, site, Guard);

        withCredentials.AllowAutoRedirect.Should().BeTrue(
            "the runtime does not send credentials to a redirect target, and a follower above the primary could not stop it");
        Follower(Chain(HandlerOf(credentialed))[1]).Follows.Should().BeFalse();

        using var unknown = EgressHttp.CreateClient(new RecordingPrimary(new Recorder(_ => false)), EgressFamilies.Http, site, Guard);
        var chain = Chain(HandlerOf(unknown));
        chain.Select(h => h.GetType().Name).Should().Equal("EgressGuardHandler", FollowerTypeName, nameof(RecordingPrimary));
        Follower(chain[1]).Follows.Should().BeFalse("an unknown primary is not followed for; it is checked after the send");
    }

    [Fact]
    public void A_handler_that_already_has_a_follower_gets_no_second_one()
    {
        using var nested = EgressHttp.Wrap(EgressHttp.Wrap(new HttpClientHandler(), EgressFamilies.Grpc, NewSite(), Guard), EgressFamilies.Grpc, NewSite(), Guard);

        Chain(nested).Count(h => h.GetType().Name == FollowerTypeName).Should().Be(1);
    }

    [Fact]
    public void On_AirGapped_an_unknown_factory_primary_is_named_in_a_warning_and_on_Full_it_is_not()
    {
        foreach (var (profile, expected) in new[] { ("air-gapped", 1), ("full", 0) })
        {
            var name = "unknown-" + NewId();
            var logs = new CapturingLoggerProvider();
            var services = new ServiceCollection();
            services.AddLogging(b => b.AddProvider(logs));
            services.AddSingleton<IEgressGuard>(new EgressGuard(profile));
            services.AddAshlarEgressGuard();
            services.AddHttpClient(name).ConfigurePrimaryHttpMessageHandler(() => new RecordingPrimary(new Recorder(_ => false)));
            using var provider = services.BuildServiceProvider();

            using (provider.GetRequiredService<IHttpClientFactory>().CreateClient(name))
            {
            }

            var warnings = logs.Entries.Where(e => e.EventId.Id == 7304 && e.Message.Contains(name, StringComparison.Ordinal)).ToList();
            warnings.Should().HaveCount(expected, "profile {0}", profile);
            if (expected == 1)
            {
                warnings[0].Level.Should().Be(LogLevel.Warning);
                warnings[0].Message.Should().Contain(typeof(RecordingPrimary).FullName!);
            }
        }
    }

    [Fact]
    public async Task A_change_of_scheme_or_port_alone_is_another_origin_on_both_routes()
    {
        var id = NewId();

        // A redirect that changes only the scheme is another origin, returned by an EgressHttp client (P2)...
        var site = NewSite();
        var recorder = new Recorder(d => d.Site == site);
        using var subscription = EgressDecisionLog.Subscribe(recorder);
        var stub = new RedirectingClientHandler(recorder, request =>
            request.RequestUri!.Scheme == Uri.UriSchemeHttp ? Redirect(HttpStatusCode.Found, $"https://same-{id}.example/a") : null);
        using var client = EgressHttp.CreateClient(stub, EgressFamilies.Http, site, Guard);

        using (var response = await client.GetAsync(new Uri($"http://same-{id}.example/a")))
            response.StatusCode.Should().Be(HttpStatusCode.Found, "another scheme is another origin");

        stub.Sends.Should().ContainSingle();
        recorder.Decisions.Select(d => d.Destination).Should().Equal($"http://same-{id}.example");

        // ...and a rewrite that changes only the scheme, or only the port, is decided again on a factory client.
        foreach (var (rewrite, expected) in new (Func<Uri, Uri> Rewrite, string Expected)[]
        {
            (uri => new UriBuilder(uri) { Scheme = Uri.UriSchemeHttps, Port = 443 }.Uri, $"https://asked-{id}.example"),
            (uri => new UriBuilder(uri) { Port = 8443 }.Uri, $"http://asked-{id}.example:8443"),
        })
        {
            var name = "origin-" + NewId();
            var factoryRecorder = new Recorder(d => d.Site == "factory:" + name);
            using var factorySubscription = EgressDecisionLog.Subscribe(factoryRecorder);
            var primary = new RecordingPrimary(factoryRecorder);
            var services = new ServiceCollection();
            services.AddAshlarEgressGuard();
            services.AddHttpClient(name).AddHttpMessageHandler(() => new Rewriter(rewrite)).ConfigurePrimaryHttpMessageHandler(() => primary);
            using var provider = services.BuildServiceProvider();
            using var factoryClient = provider.GetRequiredService<IHttpClientFactory>().CreateClient(name);

            using (await factoryClient.GetAsync(new Uri($"http://asked-{id}.example/x")))
            {
            }

            factoryRecorder.Decisions.Select(d => d.Destination).Should().Equal($"http://asked-{id}.example", expected);
            primary.Sends.Should().ContainSingle().Which.DecisionsBefore.Should().Be(2, "decided before the primary sent it");
        }
    }

    [Fact]
    public async Task A_handler_that_sends_a_fresh_request_is_still_decided_at_the_cost_of_a_duplicate()
    {
        // A hedging or retry handler that builds a new HttpRequestMessage per attempt (headers copied, Options not)
        // drops the note the guard handler wrote. The follower treats a request with no note as never decided and
        // decides it (fail closed), so the same authority is recorded twice; and a rewritten one is recorded a third
        // time by the guard handler's post-send check, since its own note never saw it.
        var id = NewId();
        foreach (var (host, expected) in new (string? Host, string[] Expected)[]
        {
            (null, [$"https://asked-{id}.example", $"https://asked-{id}.example"]),
            ($"other-{id}.example", [$"https://asked-{id}.example", $"https://other-{id}.example", $"https://other-{id}.example"]),
        })
        {
            var name = "cloned-" + NewId();
            var recorder = new Recorder(d => d.Site == "factory:" + name);
            using var subscription = EgressDecisionLog.Subscribe(recorder);
            var primary = new RecordingPrimary(recorder);

            var services = new ServiceCollection();
            services.AddAshlarEgressGuard();
            services.AddHttpClient(name).AddHttpMessageHandler(() => new Cloner(host)).ConfigurePrimaryHttpMessageHandler(() => primary);
            using var provider = services.BuildServiceProvider();
            using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(name);

            using (await client.GetAsync(new Uri($"https://asked-{id}.example/x")))
            {
            }

            recorder.Decisions.Select(d => d.Destination).Should().Equal(expected, "cloned to {0}", host ?? "the same host");
            primary.Sends.Should().ContainSingle().Which.DecisionsBefore.Should().Be(2, "the fresh request was decided before the primary sent it");
        }
    }

    [Fact]
    public async Task A_composite_factory_primary_is_followed_at_its_tail_and_its_own_rewrite_is_decided()
    {
        // ConfigurePrimaryHttpMessageHandler(() => new Rewriter { InnerHandler = new HttpClientHandler() }) is a legal
        // primary. The follower goes directly above the tail, under the rewriter, so the rewrite is decided before the
        // tail sends it, the tail's own following is turned off, and the tail's 307 is followed and decided.
        var id = NewId();
        var name = "composite-" + id;
        var recorder = new Recorder(d => d.Site == "factory:" + name);
        using var subscription = EgressDecisionLog.Subscribe(recorder);
        var tail = new RedirectingClientHandler(recorder, request =>
            request.RequestUri!.AbsolutePath == "/a" ? Redirect(HttpStatusCode.TemporaryRedirect, $"https://remote-{id}.example/b") : null);
        var logs = new CapturingLoggerProvider();

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(logs));
        services.AddSingleton<IEgressGuard>(new EgressGuard("air-gapped"));
        services.AddAshlarEgressGuard();
        services.AddHttpClient(name).ConfigurePrimaryHttpMessageHandler(() => new Rewriter($"rewritten-{id}.example", tail));
        using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(name);

        using var response = await client.GetAsync(new Uri($"https://asked-{id}.example/a"));

        response.StatusCode.Should().Be(HttpStatusCode.OK, "the follower under the composite's rewriter follows the tail's 307");
        tail.AllowAutoRedirect.Should().BeFalse("the tail is the primary whose following is turned off");
        tail.Sends.Select(s => s.Uri).Should().Equal($"https://rewritten-{id}.example/a", $"https://remote-{id}.example/b");
        recorder.Decisions.Select(d => d.Destination).Should().Equal($"https://asked-{id}.example", $"https://rewritten-{id}.example", $"https://remote-{id}.example");
        tail.Sends.Select(s => s.DecisionsBefore).Should().Equal(new[] { 2, 3 }, "each authority was decided before the tail sent to it");
        logs.Entries.Should().NotContain(e => e.EventId.Id == 7304 && e.Message.Contains(name, StringComparison.Ordinal), "the tail is a known type, so nothing is named on AirGapped");
    }

    [Theory]
    [InlineData("http://127.0.0.1:11434/api/chat", "http://127.0.0.1:11434", EgressDestinationClass.Host)]
    [InlineData("http://[::1]:11434/x", "http://[::1]:11434", EgressDestinationClass.Host)]
    [InlineData("http://localhost/x", "http://localhost", EgressDestinationClass.Host)]
    [InlineData("unix:///var/run/agent.sock", "unix://", EgressDestinationClass.Host)]
    [InlineData("npipe://./pipe/agent", "npipe://.", EgressDestinationClass.Host)]
    [InlineData("http://svc.localhost/x", "http://svc.localhost", EgressDestinationClass.Host)]
    [InlineData("http://169.254.169.254/latest/meta-data/", "http://169.254.169.254", EgressDestinationClass.NetworkExport)]
    [InlineData("http://[fe80::1]/x", "http://[fe80::1]", EgressDestinationClass.NetworkExport)]
    public async Task A_redirect_from_outside_the_host_boundary_into_it_is_returned_unfollowed_with_the_hop_recorded(string location, string destination, EgressDestinationClass destinationClass)
    {
        // Owner decision 2026-10-06 (O2): a remote peer must not be able to bounce a request, body included, to a local
        // service (loopback, *.localhost, or a link-local address, which a decision records as a network export). The
        // 3xx is returned on both routes; the hop is decided so the record shows the attempt.
        var id = NewId();
        var name = "inward-" + id;
        var recorder = new Recorder(d => d.Site == "factory:" + name);
        using var subscription = EgressDecisionLog.Subscribe(recorder);
        var stub = new RedirectingClientHandler(recorder, request =>
            request.RequestUri!.Host.StartsWith("remote-", StringComparison.Ordinal) ? Redirect(HttpStatusCode.TemporaryRedirect, location) : null);

        var services = new ServiceCollection();
        services.AddAshlarEgressGuard();
        services.AddHttpClient(name).ConfigurePrimaryHttpMessageHandler(() => stub);
        using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(name);

        using (var response = await client.PostAsync(new Uri($"http://remote-{id}.example/a"), new StringContent("body")))
            response.StatusCode.Should().Be(HttpStatusCode.TemporaryRedirect, "a hop into the host boundary is returned, never followed (P1 too)");

        stub.Sends.Should().ContainSingle("the body never went into the boundary").Which.Uri.Should().Be($"http://remote-{id}.example/a");
        recorder.Decisions.Select(d => d.Destination).Should().Equal(new[] { $"http://remote-{id}.example", destination }, "the attempted hop is recorded");
        recorder.Decisions[1].DestinationClass.Should().Be(destinationClass);

        // The same through an EgressHttp client (P2), which also records the attempt.
        var site = NewSite();
        var rawRecorder = new Recorder(d => d.Site == site);
        using var rawSubscription = EgressDecisionLog.Subscribe(rawRecorder);
        var rawStub = new RedirectingClientHandler(rawRecorder, request =>
            request.RequestUri!.Host.StartsWith("remote-", StringComparison.Ordinal) ? Redirect(HttpStatusCode.TemporaryRedirect, location) : null);
        using var raw = EgressHttp.CreateClient(rawStub, EgressFamilies.Http, site, Guard);

        using (var response = await raw.PostAsync(new Uri($"http://remote-{id}.example/a"), new StringContent("body")))
            response.StatusCode.Should().Be(HttpStatusCode.TemporaryRedirect);

        rawStub.Sends.Should().ContainSingle();
        rawRecorder.Decisions.Select(d => d.Destination).Should().Equal($"http://remote-{id}.example", destination);
    }

    [Fact]
    public async Task A_redirect_inside_the_host_boundary_or_out_of_it_is_still_followed_by_a_factory_client()
    {
        // The owner's rule is for hops into the boundary from outside only: loopback to loopback on another port, and
        // loopback out to a remote host, keep P1 (followed and decided before the send).
        var id = NewId();
        var name = "within-" + id;
        var recorder = new Recorder(d => d.Site == "factory:" + name);
        using var subscription = EgressDecisionLog.Subscribe(recorder);
        var stub = new RedirectingClientHandler(recorder, request => request.RequestUri!.AbsolutePath switch
        {
            "/a" => Redirect(HttpStatusCode.TemporaryRedirect, "http://127.0.0.1:11434/b"),
            "/b" => Redirect(HttpStatusCode.TemporaryRedirect, $"http://remote-{id}.example/c"),
            _ => null,
        });

        var services = new ServiceCollection();
        services.AddAshlarEgressGuard();
        services.AddHttpClient(name).ConfigurePrimaryHttpMessageHandler(() => stub);
        using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(name);

        using var response = await client.GetAsync(new Uri("http://127.0.0.1:5999/a"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        stub.Sends.Select(s => s.Uri).Should().Equal("http://127.0.0.1:5999/a", "http://127.0.0.1:11434/b", $"http://remote-{id}.example/c");
        recorder.Decisions.Select(d => d.Destination).Should().Equal("http://127.0.0.1:5999", "http://127.0.0.1:11434", $"http://remote-{id}.example");
        stub.Sends.Select(s => s.DecisionsBefore).Should().Equal(new[] { 1, 2, 3 });
    }

    [Fact]
    public async Task Every_hop_is_decided_by_the_guard_the_guard_handler_uses_not_the_process_default()
    {
        var id = NewId();
        var name = "guard-" + id;
        var guard = new RecordingGuard();
        var stub = new RedirectingClientHandler(new Recorder(_ => false), request =>
            request.RequestUri!.AbsolutePath == "/a" ? Redirect(HttpStatusCode.TemporaryRedirect, $"https://remote-{id}.example/b") : null);

        var services = new ServiceCollection();
        services.AddSingleton<IEgressGuard>(guard);
        services.AddAshlarEgressGuard();
        services.AddHttpClient(name).ConfigurePrimaryHttpMessageHandler(() => stub);
        using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(name);

        using (await client.GetAsync(new Uri($"https://origin-{id}.example/a")))
        {
        }

        guard.Requests.Where(r => r.Site == "factory:" + name).Select(r => (r.Family, r.Destination!.ToString())).Should().Equal(
            (EgressFamilies.HttpFactory, $"https://origin-{id}.example/a"),
            (EgressFamilies.HttpFactory, $"https://remote-{id}.example/b"));
    }

    [Fact]
    public async Task Userinfo_in_a_Location_is_never_recorded_and_the_fragment_is_kept()
    {
        var id = NewId();
        var name = "userinfo-" + id;
        var recorder = new Recorder(d => d.Site == "factory:" + name);
        using var subscription = EgressDecisionLog.Subscribe(recorder);
        var stub = new RedirectingClientHandler(recorder, request =>
            request.RequestUri!.AbsolutePath == "/a" ? Redirect(HttpStatusCode.TemporaryRedirect, $"https://user:pw@remote-{id}.example/b#frag") : null);

        var services = new ServiceCollection();
        services.AddAshlarEgressGuard();
        services.AddHttpClient(name).ConfigurePrimaryHttpMessageHandler(() => stub);
        using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(name);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://origin-{id}.example/a");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "token-" + id);

        using var response = await client.SendAsync(request);

        recorder.Decisions.Select(d => d.Destination).Should().Equal($"https://origin-{id}.example", $"https://remote-{id}.example");
        response.RequestMessage!.RequestUri!.Fragment.Should().Be("#frag");
        stub.Sends.Select(s => s.HadAuthorization).Should().Equal(new[] { true, false });
    }

    [Fact]
    public async Task A_relative_uri_below_the_guard_handler_is_decided_as_a_fault_and_nothing_throws()
    {
        var id = NewId();
        var site = NewSite();
        var recorder = new Recorder(d => d.Site == site);
        using var subscription = EgressDecisionLog.Subscribe(recorder);
        var stub = new RecordingPrimary(recorder);
        using var invoker = new HttpMessageInvoker(EgressHttp.Wrap(new Rewriter(_ => new Uri("/b", UriKind.Relative), stub), EgressFamilies.Http, site, Guard));
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://asked-{id}.example/x");

        using var response = await invoker.SendAsync(request, CancellationToken.None);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        recorder.Decisions.Should().HaveCount(2, "the relative URI is one authority of its own, decided once");
        recorder.Decisions[0].Destination.Should().Be($"https://asked-{id}.example");
        recorder.Decisions[1].Fault.Should().NotBeNull("a relative URI names no host, so its decision is a fault");
        recorder.Decisions[1].Destination.Should().Be("unknown");
        stub.Sends.Should().ContainSingle().Which.DecisionsBefore.Should().Be(2);
    }

    [Fact]
    public async Task A_token_cancelled_after_the_first_hop_stops_the_follower_before_the_next_hop_is_sent_or_decided()
    {
        var id = NewId();
        var name = "cancel-" + id;
        var recorder = new Recorder(d => d.Site == "factory:" + name);
        using var subscription = EgressDecisionLog.Subscribe(recorder);
        using var cts = new CancellationTokenSource();
        var stub = new RedirectingClientHandler(recorder, request =>
        {
            if (request.RequestUri!.AbsolutePath != "/a")
                return null;

            cts.Cancel();
            return Redirect(HttpStatusCode.TemporaryRedirect, $"https://remote-{id}.example/b");
        });

        var services = new ServiceCollection();
        services.AddAshlarEgressGuard();
        services.AddHttpClient(name).ConfigurePrimaryHttpMessageHandler(() => stub);
        using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(name);

        var act = () => client.GetAsync(new Uri($"https://origin-{id}.example/a"), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        stub.Sends.Should().ContainSingle("nothing is sent after the caller gave up");
        recorder.Decisions.Select(d => d.Destination).Should().Equal(new[] { $"https://origin-{id}.example" }, "a hop that is never sent is not decided");
    }

    [Fact]
    public async Task An_intermediate_redirect_response_is_disposed_when_the_hop_is_followed()
    {
        var id = NewId();
        var site = NewSite();
        var recorder = new Recorder(d => d.Site == site);
        using var subscription = EgressDecisionLog.Subscribe(recorder);
        var intermediate = new TrackingContent();
        var stub = new RedirectingClientHandler(recorder, request =>
        {
            if (request.RequestUri!.AbsolutePath != "/a")
                return null;

            var redirect = Redirect(HttpStatusCode.Found, "/b");
            redirect.Content = intermediate;
            return redirect;
        });
        using var client = EgressHttp.CreateClient(stub, EgressFamilies.Http, site, Guard);

        using var response = await client.GetAsync(new Uri($"https://same-{id}.example/a"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        stub.Sends.Should().HaveCount(2);
        intermediate.Disposed.Should().BeTrue("the response the follower does not return is disposed, as the runtime's follower disposes it");
    }

    [Fact]
    public async Task Wrap_over_a_delegating_handler_whose_inner_is_set_later_treats_it_as_unknown_and_decides_after_the_send()
    {
        // The chain has no primary at wrap time (the caller sets InnerHandler afterwards), so there is nothing to turn
        // off: the follower only decides, and whatever is attached later follows on its own and is decided after the
        // send (Known limit). Not a production shape: DefaultGrpcChannelFactory wraps a configured HttpClientHandler.
        var id = NewId();
        var site = NewSite();
        var recorder = new Recorder(d => d.Site == site);
        using var subscription = EgressDecisionLog.Subscribe(recorder);
        var late = new Rewriter(uri => uri);
        using var wrapped = EgressHttp.Wrap(late, EgressFamilies.Http, site, Guard);

        var chain = Chain(wrapped);
        chain.Select(h => h.GetType().Name).Should().Equal("EgressGuardHandler", FollowerTypeName, nameof(Rewriter));
        Follower(chain[1]).Follows.Should().BeFalse("no primary was reachable at wrap time");

        late.InnerHandler = new SelfFollowingPrimary($"moved-{id}.example");
        using var invoker = new HttpMessageInvoker(wrapped, disposeHandler: false);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://asked-{id}.example/x");
        using var response = await invoker.SendAsync(request, CancellationToken.None);

        recorder.Decisions.Select(d => d.Destination).Should().Equal(
            new[] { $"https://asked-{id}.example", $"https://moved-{id}.example" },
            "the primary attached later followed on its own, and the authority it reached is decided after the send");
    }

    private static string NewId() => Guid.NewGuid().ToString("N");

    private static string NewSite() => "twin:redirect:" + NewId();

    private static HttpResponseMessage Redirect(HttpStatusCode status, string location)
    {
        var response = new HttpResponseMessage(status);
        response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return response;
    }

    /// <summary>The follower's <c>Follows</c>, <c>MaxRedirects</c> and <c>FollowsAcrossOrigins</c>, by reflection (no InternalsVisibleTo).</summary>
    private static (bool Follows, int MaxRedirects, bool FollowsAcrossOrigins) Follower(HttpMessageHandler handler)
    {
        handler.GetType().Name.Should().Be(FollowerTypeName);
        var settings = handler.GetType().GetField("_settings", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(handler)!;
        object? Read(string property) => settings.GetType().GetProperty(property, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(settings);
        return ((bool)Read("Follow")!, (int)Read("MaxAutomaticRedirections")!, (bool)Read("FollowCrossHost")!);
    }

    private static List<HttpMessageHandler> Chain(HttpMessageHandler outer)
    {
        var chain = new List<HttpMessageHandler>();
        for (HttpMessageHandler? handler = outer; handler is not null && chain.Count < 10; handler = (handler as DelegatingHandler)?.InnerHandler)
            chain.Add(handler);
        return chain;
    }

    /// <summary>The handler an <see cref="HttpMessageInvoker"/> sends through, which it keeps in a private field.</summary>
    private static HttpMessageHandler HandlerOf(HttpMessageInvoker invoker)
    {
        var field = typeof(HttpMessageInvoker)
            .GetFields(BindingFlags.NonPublic | BindingFlags.Instance)
            .Single(f => typeof(HttpMessageHandler).IsAssignableFrom(f.FieldType));
        return (HttpMessageHandler)field.GetValue(invoker)!;
    }

    /// <summary>Keeps the decisions the filter accepts.</summary>
    private sealed class Recorder(Func<EgressDecision, bool> keep) : IEgressDecisionSink
    {
        private readonly ConcurrentQueue<EgressDecision> _decisions = new();

        public IReadOnlyList<EgressDecision> Decisions => _decisions.ToArray();

        public void Record(EgressDecision decision)
        {
            if (keep(decision))
                _decisions.Enqueue(decision);
        }
    }

    private sealed record Send(string Uri, int DecisionsBefore, bool HadAuthorization);

    /// <summary>
    /// A primary handler of a known type (it derives from <see cref="HttpClientHandler"/>) that answers from a script:
    /// the script's response, or 200. It never sends anything and never follows on its own.
    /// </summary>
    private sealed class RedirectingClientHandler(Recorder recorder, Func<HttpRequestMessage, HttpResponseMessage?> script) : HttpClientHandler
    {
        private readonly ConcurrentQueue<Send> _sends = new();

        public IReadOnlyList<Send> Sends => _sends.ToArray();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _sends.Enqueue(new Send(request.RequestUri!.ToString(), recorder.Decisions.Count, request.Headers.Authorization is not null));
            var response = script(request) ?? new HttpResponseMessage(HttpStatusCode.OK);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }

    /// <summary>A primary of an unknown type that answers 200 and notes how many decisions preceded each send.</summary>
    private sealed class RecordingPrimary(Recorder recorder) : HttpMessageHandler
    {
        private readonly ConcurrentQueue<Send> _sends = new();

        public IReadOnlyList<Send> Sends => _sends.ToArray();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _sends.Enqueue(new Send(request.RequestUri!.ToString(), recorder.Decisions.Count, request.Headers.Authorization is not null));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request });
        }
    }

    /// <summary>
    /// A primary of an unknown type that "follows a redirect itself": it moves the request to another host (or answers
    /// for a new request there) and returns 200, as a primary following internally would.
    /// </summary>
    private sealed class SelfFollowingPrimary(string movedHost, bool replaceRequest = false) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Answer(request));

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken) => Answer(request);

        private HttpResponseMessage Answer(HttpRequestMessage request)
        {
            var moved = new UriBuilder(request.RequestUri!) { Host = movedHost }.Uri;
            if (replaceRequest)
                return new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = new HttpRequestMessage(request.Method, moved) };

            request.RequestUri = moved;
            return new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request };
        }
    }

    /// <summary>A handler between the guard handler and the primary that rewrites every request's URI (to another host, by default).</summary>
    private sealed class Rewriter : DelegatingHandler
    {
        private readonly Func<Uri, Uri> _rewrite;

        public Rewriter(string host) => _rewrite = uri => new UriBuilder(uri) { Host = host }.Uri;

        public Rewriter(string host, HttpMessageHandler inner)
            : base(inner) => _rewrite = uri => new UriBuilder(uri) { Host = host }.Uri;

        public Rewriter(Func<Uri, Uri> rewrite) => _rewrite = rewrite;

        public Rewriter(Func<Uri, Uri> rewrite, HttpMessageHandler inner)
            : base(inner) => _rewrite = rewrite;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.RequestUri = _rewrite(request.RequestUri!);
            return base.SendAsync(request, cancellationToken);
        }
    }

    /// <summary>
    /// A handler between the guard handler and the primary that sends a fresh request each time (method and URI
    /// copied, options not), as hedging and retry handlers do; optionally to another host.
    /// </summary>
    private sealed class Cloner(string? host) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = host is null ? request.RequestUri! : new UriBuilder(request.RequestUri!) { Host = host }.Uri;
            var fresh = new HttpRequestMessage(request.Method, uri);
            return base.SendAsync(fresh, cancellationToken);
        }
    }

    /// <summary>Content that remembers whether it was disposed.</summary>
    private sealed class TrackingContent : HttpContent
    {
        public bool Disposed { get; private set; }

        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) => Task.CompletedTask;

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return true;
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    /// <summary>A host's own guard: it remembers every request it was asked about and decides as the explicit full-profile guard does.</summary>
    private sealed class RecordingGuard : IEgressGuard
    {
        private readonly ConcurrentQueue<EgressRequest> _requests = new();

        public IReadOnlyList<EgressRequest> Requests => _requests.ToArray();

        public EgressDecision Evaluate(EgressRequest request)
        {
            _requests.Enqueue(request);
            return Guard.Evaluate(request);
        }
    }

    private sealed record LogEntry(string Category, LogLevel Level, EventId EventId, string Message);

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<LogEntry> _entries = new();

        public IReadOnlyList<LogEntry> Entries => _entries.ToArray();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _entries);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(string category, ConcurrentQueue<LogEntry> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                entries.Enqueue(new LogEntry(category, logLevel, eventId, formatter(state, exception)));
        }
    }
}
