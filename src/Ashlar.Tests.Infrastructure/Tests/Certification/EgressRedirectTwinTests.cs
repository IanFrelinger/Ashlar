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

/// <summary>
/// SPEC-007 PR 4.3, the behavioural twins of redirects and of every other way a request can reach an authority the
/// guard handler never decided (PR 4 design §2.6 gap 2: R-a, R-b, R-c; defaults D32 and D33).
/// </summary>
/// <remarks>
/// <para><b>What is pinned.</b> A factory client over an <see cref="HttpClientHandler"/> follows a redirect to
/// another host, and the new authority is decided before the hop is sent (the stub twin; P1). A client
/// <see cref="EgressHttp"/> builds follows a same-origin redirect, clearing <c>Authorization</c>, and returns a
/// cross-origin one to the caller (P2). A handler between the guard handler and the primary that rewrites the URI is
/// decided again, on both routes (the rewrite twins). A client that clears its additional handlers gets the guard
/// handler back, with a Warning (the <c>Clear()</c> twin). A primary that moves the request itself is decided after
/// the send (the post-send check). Under an enforcing guard the hop to a remote host is decided, refused, before it is
/// sent (the enforcement twin; acting on that refusal is SPEC-007 PR 4.7's). The shapes: the follower sits directly
/// above the primary, which no longer follows on its own; a shared primary keeps its original setting; a primary with
/// credentials is left alone; a chain that already has a follower gets no second one. On AirGapped an unknown primary
/// type is named in a Warning. The adversarial twins: a change of scheme or port alone is another origin; a handler
/// that sends a fresh request (no note) is still decided, at the cost of a duplicate; a composite primary is followed
/// at its tail and its own rewrite decided; a remote first hop into the host boundary is followed and allowed (a Known
/// limit of P1, pinned); every hop is decided by the guard handler's own guard; userinfo in a <c>Location</c> is never
/// recorded and the fragment is kept; a relative URI below the guard handler is decided once, as a fault.</para>
/// <para><b>Isolation.</b> The decision log is process-wide and other classes decide in parallel, so every assertion
/// filters by a site or host unique to the test. Hermetic: every primary is a stub, so nothing leaves the process.
/// The differential twin against the runtime's own follower, over loopback Kestrel, is
/// <see cref="EgressRedirectDifferentialTests"/>. No environment variable or process egress state is touched: every
/// guard here is built with an explicit profile.</para>
/// </remarks>
[Trait("Category", "Certification")]
public sealed class EgressRedirectTwinTests
{
    private const string FollowerTypeName = "EgressRedirectHandler";

    private static readonly EgressGuard Guard = new("full");

    // ---------------------------------------------------------------------------------------------------------
    // The stub twin (P1): a factory client follows, and decides each hop before it is sent
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_factory_client_over_an_HttpClientHandler_follows_a_307_to_another_host_and_decides_it_before_sending_it()
    {
        var id = NewId();
        var name = "redirect-" + id;
        var recorder = new Recorder(d => d.Site == "factory:" + name);
        using var subscription = EgressDecisionLog.Subscribe(recorder);
        var stub = new RedirectingClientHandler(recorder, request =>
            request.RequestUri!.AbsolutePath == "/a" ? Redirect(HttpStatusCode.TemporaryRedirect, $"https://remote-{id}.example/b") : null);

        var services = new ServiceCollection();
        services.AddAshlarEgressGuard();
        services.AddHttpClient(name).ConfigurePrimaryHttpMessageHandler(() => stub);
        using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(name);

        using var response = await client.GetAsync(new Uri($"https://origin-{id}.example/a"));

        response.StatusCode.Should().Be(HttpStatusCode.OK, "the factory client follows the 307 (P1, default D33)");
        stub.Sends.Select(s => s.Uri).Should().Equal($"https://origin-{id}.example/a", $"https://remote-{id}.example/b");
        recorder.Decisions.Select(d => d.Destination).Should().Equal(
            new[] { $"https://origin-{id}.example", $"https://remote-{id}.example" },
            "each authority the request is sent to is decided once");
        stub.Sends[1].DecisionsBefore.Should().Be(2, "the hop to the remote host was decided before it was sent");
        recorder.Decisions.Should().OnlyContain(d => d.Family == EgressFamilies.HttpFactory && d.Fault == null);
    }

    // ---------------------------------------------------------------------------------------------------------
    // P2: a client EgressHttp builds follows a same-origin redirect and returns a cross-origin one
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_EgressHttp_client_follows_a_same_origin_redirect_and_clears_Authorization()
    {
        var id = NewId();
        var site = NewSite();
        var recorder = new Recorder(d => d.Site == site);
        using var subscription = EgressDecisionLog.Subscribe(recorder);
        var stub = new RedirectingClientHandler(recorder, request =>
            request.RequestUri!.AbsolutePath == "/a" ? Redirect(HttpStatusCode.Found, "/a2?x=1") : null);
        using var client = EgressHttp.CreateClient(stub, EgressFamilies.Http, site, Guard);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://same-{id}.example/a");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "token-" + id);

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK, "a same-origin redirect is followed");
        stub.Sends.Select(s => s.Uri).Should().Equal($"https://same-{id}.example/a", $"https://same-{id}.example/a2?x=1");
        stub.Sends.Select(s => s.HadAuthorization).Should().Equal(new[] { true, false }, "each hop clears Authorization, as the runtime does");
        recorder.Decisions.Should().ContainSingle("one origin, one decision").Which.Destination.Should().Be($"https://same-{id}.example");
    }

    [Fact]
    public async Task An_EgressHttp_client_returns_a_cross_origin_redirect_to_the_caller()
    {
        var id = NewId();
        var site = NewSite();
        var recorder = new Recorder(d => d.Site == site);
        using var subscription = EgressDecisionLog.Subscribe(recorder);
        var stub = new RedirectingClientHandler(recorder, request => request.RequestUri!.Host.StartsWith("origin-", StringComparison.Ordinal)
            ? Redirect(HttpStatusCode.TemporaryRedirect, $"https://other-{id}.example/b")
            : null);
        using var client = EgressHttp.CreateClient(stub, EgressFamilies.Http, site, Guard);

        using (var response = await client.GetAsync(new Uri($"https://origin-{id}.example/a")))
            response.StatusCode.Should().Be(HttpStatusCode.TemporaryRedirect, "P2: a cross-origin 3xx is returned to the caller");

        using (var response = await client.GetAsync(new Uri($"https://origin-{id}.example:8443/a")))
            response.StatusCode.Should().Be(HttpStatusCode.TemporaryRedirect, "another port is another origin");

        stub.Sends.Should().HaveCount(2, "neither redirect was followed");
        recorder.Decisions.Select(d => d.Destination).Should().Equal($"https://origin-{id}.example", $"https://origin-{id}.example:8443");
    }

    // ---------------------------------------------------------------------------------------------------------
    // The rewrite twins: a handler between the guard handler and the primary that rewrites the URI
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_handler_inside_an_EgressHttp_client_that_rewrites_the_uri_is_decided_again_before_the_send()
    {
        var id = NewId();
        var site = NewSite();
        var recorder = new Recorder(d => d.Site == site);
        using var subscription = EgressDecisionLog.Subscribe(recorder);
        var stub = new RecordingPrimary(recorder);
        using var invoker = new HttpMessageInvoker(EgressHttp.Wrap(new Rewriter($"rewritten-{id}.example", stub), EgressFamilies.Http, site, Guard));
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://asked-{id}.example/x");

        using var response = await invoker.SendAsync(request, CancellationToken.None);

        stub.Sends.Should().ContainSingle().Which.Uri.Should().Be($"https://rewritten-{id}.example/x");
        recorder.Decisions.Select(d => d.Destination).Should().Equal($"https://asked-{id}.example", $"https://rewritten-{id}.example");
        stub.Sends[0].DecisionsBefore.Should().Be(2, "the rewritten authority was decided before the primary sent it");
    }

    [Fact]
    public async Task A_handler_a_factory_client_adds_that_rewrites_the_uri_is_decided_again_before_the_send()
    {
        var id = NewId();
        var name = "rewrite-" + id;
        var recorder = new Recorder(d => d.Site == "factory:" + name);
        using var subscription = EgressDecisionLog.Subscribe(recorder);
        var stub = new RecordingPrimary(recorder);

        var services = new ServiceCollection();
        services.AddAshlarEgressGuard();
        services.AddHttpClient(name)
            .AddHttpMessageHandler(() => new Rewriter($"rewritten-{id}.example"))
            .ConfigurePrimaryHttpMessageHandler(() => stub);
        using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(name);

        using var response = await client.GetAsync(new Uri($"https://asked-{id}.example/x"));

        stub.Sends.Should().ContainSingle().Which.Uri.Should().Be($"https://rewritten-{id}.example/x");
        recorder.Decisions.Select(d => d.Destination).Should().Equal($"https://asked-{id}.example", $"https://rewritten-{id}.example");
        stub.Sends[0].DecisionsBefore.Should().Be(2, "the rewritten authority was decided before the primary sent it");
    }

    // ---------------------------------------------------------------------------------------------------------
    // The Clear() twin: the guard handler is put back
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_factory_client_that_clears_its_additional_handlers_gets_the_guard_handler_back_with_a_warning()
    {
        var id = NewId();
        var name = "cleared-" + id;
        var recorder = new Recorder(d => d.Site == "factory:" + name);
        using var subscription = EgressDecisionLog.Subscribe(recorder);
        var stub = new RecordingPrimary(recorder);
        var logs = new CapturingLoggerProvider();

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(logs));
        services.AddAshlarEgressGuard();
        services.AddHttpClient(name)
            .ConfigurePrimaryHttpMessageHandler(() => stub)
            .ConfigureAdditionalHttpMessageHandlers((handlers, _) => handlers.Clear());
        using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(name);

        using var response = await client.GetAsync(new Uri($"https://cleared-{id}.example/x"));

        stub.Sends.Should().ContainSingle();
        var decision = recorder.Decisions.Should().ContainSingle("the guard handler was put back, so the send is decided").Which;
        decision.Destination.Should().Be($"https://cleared-{id}.example");
        stub.Sends[0].DecisionsBefore.Should().Be(1, "decided before it was sent");
        var warning = logs.Entries.Should().ContainSingle(e => e.EventId.Id == 7303 && e.Message.Contains(name, StringComparison.Ordinal)).Which;
        warning.Level.Should().Be(LogLevel.Warning);
        warning.Category.Should().Be("Ashlar.Egress");
    }

    // ---------------------------------------------------------------------------------------------------------
    // The post-send check: a primary that moved the request itself
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_primary_that_moves_the_request_itself_is_decided_after_the_send_on_both_paths()
    {
        var id = NewId();
        var site = NewSite();
        var recorder = new Recorder(d => d.Site == site);
        using var subscription = EgressDecisionLog.Subscribe(recorder);
        var stub = new SelfFollowingPrimary($"moved-{id}.example");
        using var invoker = new HttpMessageInvoker(EgressHttp.Wrap(stub, EgressFamilies.Http, site, Guard));

        using (var request = new HttpRequestMessage(HttpMethod.Get, $"https://asked-{id}.example/x"))
        using (await invoker.SendAsync(request, CancellationToken.None))
        {
        }

        using (var request = new HttpRequestMessage(HttpMethod.Get, $"https://asked-{id}.example/y"))
        using (invoker.Send(request, CancellationToken.None))
        {
        }

        recorder.Decisions.Select(d => d.Destination).Should().Equal(
            new[] { $"https://asked-{id}.example", $"https://moved-{id}.example", $"https://asked-{id}.example", $"https://moved-{id}.example" },
            "the response's request names an authority nobody decided, so it is decided after the send");
    }

    [Fact]
    public async Task A_factory_primary_that_answers_for_another_request_is_decided_after_the_send()
    {
        var id = NewId();
        var name = "answers-" + id;
        var recorder = new Recorder(d => d.Site == "factory:" + name);
        using var subscription = EgressDecisionLog.Subscribe(recorder);

        var services = new ServiceCollection();
        services.AddAshlarEgressGuard();
        services.AddHttpClient(name).ConfigurePrimaryHttpMessageHandler(() => new SelfFollowingPrimary($"moved-{id}.example", replaceRequest: true));
        using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(name);

        using (await client.GetAsync(new Uri($"https://asked-{id}.example/x")))
        {
        }

        recorder.Decisions.Select(d => d.Destination).Should().Equal($"https://asked-{id}.example", $"https://moved-{id}.example");
    }

    // ---------------------------------------------------------------------------------------------------------
    // The enforcement twin: the hop is decided, refused, before it is sent
    // ---------------------------------------------------------------------------------------------------------

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

    // ---------------------------------------------------------------------------------------------------------
    // The shapes
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void Every_EgressHttp_client_puts_the_follower_directly_above_the_primary_which_no_longer_follows_on_its_own()
    {
        var site = NewSite();
        using var plain = EgressHttp.CreateClient(EgressFamilies.Mcp, site, Guard);

        var chain = Chain(HandlerOf(plain));
        chain.Select(h => h.GetType().Name).Should().Equal("EgressGuardHandler", FollowerTypeName, nameof(HttpClientHandler));
        ((HttpClientHandler)chain[2]).AllowAutoRedirect.Should().BeFalse("the follower above it follows instead");
        Follower(chain[1]).Should().Be((true, 50, false), "the primary followed, with the runtime's limit; P2 for EgressHttp clients");

        var primary = new HttpClientHandler { MaxAutomaticRedirections = 7 };
        using var wrapped = EgressHttp.Wrap(new Rewriter("unused.example", primary), EgressFamilies.Grpc, site, Guard);
        var spliced = Chain(wrapped);
        spliced.Select(h => h.GetType().Name).Should().Equal("EgressGuardHandler", nameof(Rewriter), FollowerTypeName, nameof(HttpClientHandler));
        spliced[3].Should().BeSameAs(primary);
        primary.AllowAutoRedirect.Should().BeFalse();
        Follower(spliced[2]).Should().Be((true, 7, false), "the follower keeps the primary's own limit");
    }

    [Fact]
    public void A_shared_primary_keeps_its_original_setting_and_one_that_did_not_follow_is_not_followed()
    {
        var site = NewSite();
        var shared = new HttpClientHandler();
        using var first = EgressHttp.CreateClient(shared, EgressFamilies.MeshPull, site, Guard);
        using var second = EgressHttp.CreateClient(shared, EgressFamilies.MeshPull, site, Guard);

        Follower(Chain(HandlerOf(first))[1]).Follows.Should().BeTrue();
        Follower(Chain(HandlerOf(second))[1]).Follows.Should().BeTrue(
            "the second client finds AllowAutoRedirect already off, and follows as the primary originally did");

        using var never = EgressHttp.CreateClient(new HttpClientHandler { AllowAutoRedirect = false }, EgressFamilies.Http, site, Guard);
        Follower(Chain(HandlerOf(never))[1]).Follows.Should().BeFalse("a primary that never followed is never followed for");
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

    // ---------------------------------------------------------------------------------------------------------
    // The adversarial twins: every other way a send could reach an authority nobody decided
    // ---------------------------------------------------------------------------------------------------------

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

    [Fact]
    public async Task A_factory_client_follows_a_remote_first_hop_into_the_host_boundary_which_the_label_model_allows()
    {
        // Known limit (default D33, P1): a remote peer reached over plain http can bounce a factory client's request,
        // body included, to loopback or link-local, and the label model allows a write to the host boundary at every
        // label. Pinned as the design has it, so that a later rule ("never follow into Host from outside it") has a
        // flip to show. From an https first hop the same Location is a downgrade, which is not followed (parity).
        var id = NewId();
        var name = "inward-" + id;
        var recorder = new Recorder(d => d.Site == "factory:" + name);
        using var subscription = EgressDecisionLog.Subscribe(recorder);
        var stub = new RedirectingClientHandler(recorder, request =>
            request.RequestUri!.Host.StartsWith("remote-", StringComparison.Ordinal) ? Redirect(HttpStatusCode.TemporaryRedirect, "http://127.0.0.1:11434/api/chat") : null);

        var services = new ServiceCollection();
        services.AddAshlarEgressGuard();
        services.AddHttpClient(name).ConfigurePrimaryHttpMessageHandler(() => stub);
        using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(name);

        using (var response = await client.PostAsync(new Uri($"http://remote-{id}.example/a"), new StringContent("body")))
            response.StatusCode.Should().Be(HttpStatusCode.OK, "the hop into the host boundary is followed (P1)");

        stub.Sends.Select(s => s.Uri).Should().Equal($"http://remote-{id}.example/a", "http://127.0.0.1:11434/api/chat");
        recorder.Decisions.Should().HaveCount(2);
        var inward = recorder.Decisions[1];
        inward.Destination.Should().Be("http://127.0.0.1:11434");
        inward.DestinationClass.Should().Be(EgressDestinationClass.Host);
        inward.Access.Allowed.Should().BeTrue("the host boundary is SystemHigh, which every label may write to");

        using (var response = await client.PostAsync(new Uri($"https://remote-{id}.example/a"), new StringContent("body")))
            response.StatusCode.Should().Be(HttpStatusCode.TemporaryRedirect, "https to http is a downgrade, never followed");

        stub.Sends.Should().HaveCount(3, "the downgrade was returned, not followed");
        recorder.Decisions.Should().HaveCount(3).And.Subject.Last().Destination.Should().Be($"https://remote-{id}.example");
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

    // ---------------------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------------------

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
        object? Read(string property) =>
            handler.GetType().GetProperty(property, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(handler);
        return ((bool)Read("Follows")!, (int)Read("MaxRedirects")!, (bool)Read("FollowsAcrossOrigins")!);
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
