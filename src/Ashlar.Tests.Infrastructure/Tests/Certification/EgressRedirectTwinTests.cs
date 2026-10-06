using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Ashlar.Abstractions.Security.Egress;
using Ashlar.Infrastructure.Egress;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>
/// SPEC-007 PR 4.3, behavioural twin of redirect handling on the egress guard's HTTP adapter.
/// </summary>
/// <remarks>
/// <para><b>What is pinned.</b> A primary Ashlar builds has <c>AllowAutoRedirect</c> false. An
/// <see cref="EgressHttp"/> client (P2) follows a same-host redirect and records a second decision, and returns a
/// cross-host 3xx without requesting the second host. A factory client (P1) follows a cross-host redirect and
/// re-evaluates it. A handler between the guard and the primary that rewrites <c>RequestUri</c> is evaluated for the
/// rewritten host. <c>Clear()</c> on the additional handlers still leaves exactly one guard handler and one decision.
/// An unknown primary that has already followed is evaluated again from the response URI. Report mode never sets
/// <see cref="EgressDecision.Refused"/>. The follower matches <see cref="SocketsHttpHandler"/> on loopback: status,
/// method, body, fragment, a missing or relative <c>Location</c>, the redirect limit, HTTPS to HTTP, and
/// <c>Authorization</c>.</para>
/// <para><b>Isolation.</b> The decision log is process-wide, so each test keeps only its own site. Stub primaries
/// never leave the process. The differential server listens on <c>127.0.0.1</c> only.</para>
/// </remarks>
[Trait("Category", "Certification")]
public sealed class EgressRedirectTwinTests
{
    private static readonly EgressGuard Guard = new("full");

    private static readonly Lazy<LoopbackRedirectServer> Server = new(LoopbackRedirectServer.Start);

    [Fact]
    public void The_CreateClient_primary_AllowAutoRedirect_reads_false()
    {
        using var client = EgressHttp.CreateClient(EgressFamilies.Http, NewSite(), Guard);

        PrimaryOf(client).Should().BeOfType<HttpClientHandler>()
            .Which.AllowAutoRedirect.Should().BeFalse(
                "Ashlar's follower owns redirects; the primary must not follow on its own");
    }

    [Fact]
    public async Task EgressHttp_same_host_redirect_records_a_second_decision()
    {
        var host = UniqueHost();
        var site = NewSite();
        var sink = new SiteSink(site);
        using var subscription = EgressDecisionLog.Subscribe(sink);
        var stub = new RedirectingStub(uri =>
            uri.AbsolutePath == "/done" ? null : new Uri($"https://{host}/done"));

        using var client = EgressHttp.CreateClient(stub, EgressFamilies.Http, site, Guard);
        using var response = await client.GetAsync(new Uri($"https://{host}/start"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        stub.Requested.Select(u => u.AbsolutePath).Should().Equal("/start", "/done");
        sink.Seen.Select(d => d.Destination).Should().Equal(new[] { $"https://{host}", $"https://{host}" },
            "a same-host redirect is a second hop and a second decision");
        sink.Seen.Should().OnlyContain(d => d.Mode == "report" && d.Refused == false && d.Family == EgressFamilies.Http);
    }

    [Fact]
    public void EgressHttp_same_host_redirect_on_Send_records_a_second_decision()
    {
        var host = UniqueHost();
        var site = NewSite();
        var sink = new SiteSink(site);
        using var subscription = EgressDecisionLog.Subscribe(sink);
        var stub = new RedirectingStub(uri =>
            uri.AbsolutePath == "/done" ? null : new Uri($"https://{host}/done"));

        using var invoker = new HttpMessageInvoker(EgressHttp.Wrap(stub, EgressFamilies.Http, site, Guard));
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://{host}/start");
        using var response = invoker.Send(request, CancellationToken.None);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        sink.Seen.Should().HaveCount(2, "the synchronous Send path re-evaluates the followed hop");
    }

    [Fact]
    public async Task EgressHttp_cross_host_redirect_is_not_followed()
    {
        var host = UniqueHost();
        var other = UniqueHost();
        var site = NewSite();
        var sink = new SiteSink(site);
        using var subscription = EgressDecisionLog.Subscribe(sink);
        var stub = new RedirectingStub(uri =>
            uri.Host == other ? null : new Uri($"https://{other}/landed"));

        using var client = EgressHttp.CreateClient(stub, EgressFamilies.Http, site, Guard);
        using var response = await client.GetAsync(new Uri($"https://{host}/start"));

        response.StatusCode.Should().Be(HttpStatusCode.TemporaryRedirect);
        stub.Requested.Should().NotContain(u => string.Equals(u.Host, other, StringComparison.OrdinalIgnoreCase),
            "P2 returns a cross-host 3xx; the second host is not requested");
        sink.Seen.Should().ContainSingle().Which.Destination.Should().Be($"https://{host}");
    }

    [Fact]
    public async Task Report_mode_P2_hop_2_to_a_different_host_is_not_sent_until_enforce()
    {
        var host = UniqueHost();
        var other = UniqueHost();
        var site = NewSite();
        var sink = new SiteSink(site);
        using var subscription = EgressDecisionLog.Subscribe(sink);
        var stub = new RedirectingStub(uri =>
            uri.Host == other ? null : new Uri($"https://{other}/landed"));

        using var client = EgressHttp.CreateClient(stub, EgressFamilies.Http, site, Guard);
        using var response = await client.GetAsync(new Uri($"https://{host}/start"));

        response.StatusCode.Should().Be(HttpStatusCode.TemporaryRedirect);
        stub.Requested.Count(u => string.Equals(u.Host, other, StringComparison.OrdinalIgnoreCase))
            .Should().Be(0, "report mode still does not send hop 2 to a different host on an EgressHttp client");
        var decision = sink.Seen.Should().ContainSingle().Which;
        decision.Mode.Should().Be("report");
        decision.Refused.Should().BeFalse("Refused stays false until SPEC-007 PR 4.11");
        decision.Destination.Should().Be($"https://{host}");
    }

    [Fact]
    public async Task Factory_same_host_redirect_records_a_second_decision()
    {
        var host = UniqueHost();
        var name = "redirect-same-" + Guid.NewGuid().ToString("N");
        var sink = new SiteSink("factory:" + name);
        using var subscription = EgressDecisionLog.Subscribe(sink);
        var stub = new RedirectingStub(uri =>
            uri.AbsolutePath == "/done" ? null : new Uri($"https://{host}/done"));

        var services = new ServiceCollection();
        services.AddAshlarEgressGuard();
        services.AddHttpClient(name).ConfigurePrimaryHttpMessageHandler(() => stub);
        using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(name);

        using var response = await client.GetAsync(new Uri($"https://{host}/start"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        sink.Seen.Select(d => d.Destination).Should().Equal($"https://{host}", $"https://{host}");
        sink.Seen.Should().OnlyContain(d => d.Site == "factory:" + name && d.Refused == false);
    }

    [Fact]
    public async Task Factory_cross_host_redirect_is_followed_and_re_evaluated()
    {
        var host = UniqueHost();
        var other = UniqueHost();
        var name = "redirect-cross-" + Guid.NewGuid().ToString("N");
        var sink = new SiteSink("factory:" + name);
        using var subscription = EgressDecisionLog.Subscribe(sink);
        var stub = new RedirectingStub(uri =>
            uri.Host == other ? null : new Uri($"https://{other}/landed"));

        var services = new ServiceCollection();
        services.AddAshlarEgressGuard();
        services.AddHttpClient(name).ConfigurePrimaryHttpMessageHandler(() => stub);
        using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(name);

        using var response = await client.GetAsync(new Uri($"https://{host}/start"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        stub.Requested.Should().Contain(u => string.Equals(u.Host, other, StringComparison.OrdinalIgnoreCase));
        sink.Seen.Select(d => d.Destination).Should().Equal(
            new[] { $"https://{host}", $"https://{other}" },
            "P1 follows a cross-host redirect and the second hop is its own decision");
    }

    [Fact]
    public async Task A_rewriter_between_guard_and_primary_is_evaluated_for_the_rewritten_host()
    {
        var original = UniqueHost();
        var rewritten = UniqueHost();
        var site = NewSite();
        var sink = new SiteSink(site);
        using var subscription = EgressDecisionLog.Subscribe(sink);
        var primary = new TerminalHandler();
        var rewriter = new RewritingHandler(primary, new Uri($"https://{rewritten}/landed"));

        using var client = EgressHttp.CreateClient(rewriter, EgressFamilies.Http, site, Guard);
        using var response = await client.GetAsync(new Uri($"https://{original}/start"));

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        primary.LastUri!.Host.Should().Be(rewritten);
        sink.Seen.Select(d => d.Destination).Should().Equal(
            new[] { $"https://{original}", $"https://{rewritten}" },
            "the first decision is the caller's URI; the rewritten URI is evaluated before it is sent");
    }

    [Fact]
    public async Task Clear_still_has_one_guard_handler_and_exactly_one_decision()
    {
        var host = UniqueHost();
        var name = "redirect-clear-" + Guid.NewGuid().ToString("N");
        var sink = new SiteSink("factory:" + name);
        using var subscription = EgressDecisionLog.Subscribe(sink);
        var primary = new NoContentPrimary();

        var services = new ServiceCollection();
        services.AddAshlarEgressGuard();
        services.AddHttpClient(name)
            .ConfigurePrimaryHttpMessageHandler(() => primary)
            .ConfigureAdditionalHttpMessageHandlers((handlers, _) => handlers.Clear());
        using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(name);

        using (await client.GetAsync(new Uri($"https://{host}/")))
        {
        }

        CountNamed(HandlerOf(client), "EgressGuardHandler").Should().Be(1,
            "Clear() removes the guard the defaults inserted; the filter puts it back, once");
        sink.Seen.Should().ContainSingle().Which.Site.Should().Be("factory:" + name);
        primary.Sends.Should().Be(1);
    }

    [Fact]
    public async Task Unknown_primary_followed_authority_is_evaluated_after_the_send()
    {
        var host = UniqueHost();
        var other = UniqueHost();
        var site = NewSite();
        var sink = new SiteSink(site);
        using var subscription = EgressDecisionLog.Subscribe(sink);
        var primary = new FollowingUnknownPrimary(new Uri($"https://{other}/landed"));

        using var client = EgressHttp.CreateClient(primary, EgressFamilies.Http, site, Guard);
        using var response = await client.GetAsync(new Uri($"https://{host}/start"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        sink.Seen.Select(d => d.Destination).Should().Equal(
            new[] { $"https://{host}", $"https://{other}" },
            "a primary Ashlar cannot flip still reports the authority it actually followed");
        sink.Seen.Should().OnlyContain(d => d.Refused == false && d.Mode == "report");
    }

    [Fact]
    public async Task Unknown_primary_logs_a_warning_naming_the_type()
    {
        var name = "redirect-unknown-" + Guid.NewGuid().ToString("N");
        var capture = new WarningCapture();
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning).AddProvider(capture));
        services.AddAshlarEgressGuard();
        services.AddHttpClient(name).ConfigurePrimaryHttpMessageHandler(() => new FollowingUnknownPrimary(new Uri("https://example/landed")));

        using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(name);
        using (await client.GetAsync(new Uri("https://example/start")))
        {
        }

        capture.Warnings.Should().Contain(w =>
            w.Contains(typeof(FollowingUnknownPrimary).FullName!, StringComparison.Ordinal)
            && w.Contains("automatic redirects were not disabled", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_started_handler_does_not_throw_when_AllowAutoRedirect_cannot_be_cleared()
    {
        var handler = new HttpClientHandler();
        using (var probe = new HttpClient(handler, disposeHandler: false))
        using (var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200)))
        {
            try
            {
                using var ignored = await probe.GetAsync(new Uri("http://127.0.0.1:1/"), cts.Token);
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or TaskCanceledException)
            {
            }
        }

        var site = NewSite();
        var act = () => EgressHttp.Wrap(handler, EgressFamilies.Http, site, Guard);
        act.Should().NotThrow("a started handler's AllowAutoRedirect setter throws; Wrap leaves following off");
        handler.Dispose();
    }

    [Fact]
    public async Task A_primary_that_already_refused_redirects_is_not_followed()
    {
        var host = UniqueHost();
        var site = NewSite();
        var sink = new SiteSink(site);
        using var subscription = EgressDecisionLog.Subscribe(sink);
        var stub = new RedirectingStub(uri =>
            uri.AbsolutePath == "/done" ? null : new Uri($"https://{host}/done"))
        {
            AllowAutoRedirect = false,
        };

        using var client = EgressHttp.CreateClient(stub, EgressFamilies.Http, site, Guard);
        using var response = await client.GetAsync(new Uri($"https://{host}/start"));

        response.StatusCode.Should().Be(HttpStatusCode.TemporaryRedirect);
        stub.Requested.Should().ContainSingle().Which.AbsolutePath.Should().Be("/start");
        sink.Seen.Should().ContainSingle();
    }

    [Fact]
    public async Task Differential_https_to_http_is_not_followed()
    {
        var server = Server.Value;
        server.Reset();
        using var ours = GuardedClient(server, allowRedirects: true, maxRedirects: 50);
        using var reference = ReferenceClient(server, maxRedirects: 50);

        using var ourResponse = await ours.SendAsync(Marked(HttpMethod.Get, server.DowngradeUri, "ours"));
        using var referenceResponse = await reference.SendAsync(Marked(HttpMethod.Get, server.DowngradeUri, "reference"));

        ourResponse.StatusCode.Should().Be(referenceResponse.StatusCode).And.Be(HttpStatusCode.Redirect);
        server.Hits.Should().NotContain(h => h.Path == "/downgrade-landed",
            "HTTPS to HTTP is returned to the caller");
    }

    [Fact]
    public async Task Differential_authorization_is_cleared_on_a_followed_redirect()
    {
        var server = Server.Value;
        server.Reset();
        using var ours = GuardedClient(server, allowRedirects: true, maxRedirects: 50);
        using var reference = ReferenceClient(server, maxRedirects: 50);

        using var ourResponse = await ours.SendAsync(Authorized(server.RedirectUri(302), "ours"));
        var ourLanding = server.Hits.Should().ContainSingle(h => h.Path == "/done" && h.Client == "ours").Which;
        server.Reset();
        using var referenceResponse = await reference.SendAsync(Authorized(server.RedirectUri(302), "reference"));
        var referenceLanding = server.Hits.Should().ContainSingle(h => h.Path == "/done" && h.Client == "reference").Which;

        ourResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        referenceResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        ourLanding.Authorization.Should().BeEmpty();
        referenceLanding.Authorization.Should().BeEmpty();
    }

    [Theory]
    [InlineData(301, "POST", "GET", false)]
    [InlineData(302, "POST", "GET", false)]
    [InlineData(303, "POST", "GET", false)]
    [InlineData(303, "HEAD", "HEAD", false)]
    [InlineData(307, "POST", "POST", true)]
    [InlineData(308, "POST", "POST", true)]
    public async Task Differential_status_method_and_body_match_SocketsHttpHandler(
        int status, string method, string followedMethod, bool keepsBody)
    {
        var server = Server.Value;
        server.Reset();
        using var ours = GuardedClient(server, allowRedirects: true, maxRedirects: 50);
        using var reference = ReferenceClient(server, maxRedirects: 50);
        var uri = server.RedirectUri(status);

        using var ourResponse = await ours.SendAsync(Request(method, uri, keepsBody || method == "POST", "ours"));
        var ourHit = server.Hits.Last(h => h.Path == "/done" && h.Client == "ours");
        server.Reset();
        using var referenceResponse = await reference.SendAsync(Request(method, uri, keepsBody || method == "POST", "reference"));
        var referenceHit = server.Hits.Last(h => h.Path == "/done" && h.Client == "reference");

        ourResponse.StatusCode.Should().Be(referenceResponse.StatusCode);
        ourHit.Method.Should().Be(referenceHit.Method).And.Be(followedMethod);
        ourHit.Body.Should().Be(referenceHit.Body);
        if (keepsBody)
            ourHit.Body.Should().Be("payload");
        else if (method == "POST")
            ourHit.Body.Should().BeEmpty();
    }

    [Fact]
    public async Task Differential_relative_and_missing_location_match_SocketsHttpHandler()
    {
        var server = Server.Value;
        server.Reset();
        using var ours = GuardedClient(server, allowRedirects: true, maxRedirects: 50);
        using var reference = ReferenceClient(server, maxRedirects: 50);

        using (var ourRelative = await ours.GetAsync(server.RelativeUri))
        using (var referenceRelative = await reference.GetAsync(server.RelativeUri))
        {
            ourRelative.StatusCode.Should().Be(referenceRelative.StatusCode).And.Be(HttpStatusCode.OK);
            ourRelative.RequestMessage!.RequestUri!.AbsolutePath.Should().Be("/done");
        }

        server.Reset();
        using var ourMissing = await ours.GetAsync(server.MissingLocationUri);
        using var referenceMissing = await reference.GetAsync(server.MissingLocationUri);
        ourMissing.StatusCode.Should().Be(referenceMissing.StatusCode).And.Be(HttpStatusCode.Redirect);
    }

    [Fact]
    public async Task Differential_fragment_and_redirect_limit_match_SocketsHttpHandler()
    {
        var server = Server.Value;
        server.Reset();
        using var ourHandler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 2,
            UseCookies = false,
            UseProxy = false,
        };
        using var ourInvoker = new HttpMessageInvoker(
            EgressHttp.Wrap(ourHandler, EgressFamilies.Http, NewSite(), Guard));
        using var referenceHandler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 2,
            UseCookies = false,
            UseProxy = false,
        };
        using var referenceInvoker = new HttpMessageInvoker(referenceHandler);

        using var ourFragment = ourInvoker.Send(new HttpRequestMessage(HttpMethod.Get, server.FragmentUri), CancellationToken.None);
        using var referenceFragment = referenceInvoker.Send(new HttpRequestMessage(HttpMethod.Get, server.FragmentUri), CancellationToken.None);
        referenceFragment.RequestMessage!.RequestUri!.Fragment.Should().Be("#section");
        ourFragment.RequestMessage!.RequestUri.Should().Be(referenceFragment.RequestMessage.RequestUri);

        server.Reset();
        using var ourLimit = ourInvoker.Send(new HttpRequestMessage(HttpMethod.Get, server.HopUri(5)), CancellationToken.None);
        using var referenceLimit = referenceInvoker.Send(new HttpRequestMessage(HttpMethod.Get, server.HopUri(5)), CancellationToken.None);
        ourLimit.StatusCode.Should().Be(referenceLimit.StatusCode).And.Be(HttpStatusCode.Redirect);
        ourLimit.RequestMessage!.RequestUri!.AbsolutePath.Should().Be(referenceLimit.RequestMessage!.RequestUri!.AbsolutePath);
    }

    private static HttpClient GuardedClient(LoopbackRedirectServer server, bool allowRedirects, int maxRedirects)
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = allowRedirects,
            MaxAutomaticRedirections = maxRedirects,
            UseCookies = false,
            UseProxy = false,
            ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
                certificate is not null && string.Equals(certificate.Thumbprint, server.Certificate.Thumbprint, StringComparison.OrdinalIgnoreCase),
        };
        return EgressHttp.CreateClient(handler, EgressFamilies.Http, NewSite(), Guard);
    }

    private static HttpClient ReferenceClient(LoopbackRedirectServer server, int maxRedirects)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = maxRedirects,
            UseCookies = false,
            UseProxy = false,
        };
        handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, _) =>
            certificate is not null && string.Equals(certificate.GetCertHashString(), server.Certificate.Thumbprint, StringComparison.OrdinalIgnoreCase);
        return new HttpClient(handler);
    }

    private static HttpRequestMessage Marked(HttpMethod method, Uri uri, string client)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.TryAddWithoutValidation("X-Ashlar-Twin", client);
        return request;
    }

    private static HttpRequestMessage Authorized(Uri uri, string client)
    {
        var request = Marked(HttpMethod.Get, uri, client);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "twin-secret");
        return request;
    }

    private static HttpRequestMessage Request(string method, Uri uri, bool withBody, string client)
    {
        var request = Marked(new HttpMethod(method), uri, client);
        if (withBody && method != "HEAD")
            request.Content = new StringContent("payload");
        return request;
    }

    private static string NewSite() => "twin:redirect:" + Guid.NewGuid().ToString("N");

    private static string UniqueHost() => "egress-redirect-" + Guid.NewGuid().ToString("N") + ".example";

    private static HttpMessageHandler HandlerOf(HttpMessageInvoker invoker)
    {
        var field = typeof(HttpMessageInvoker)
            .GetFields(BindingFlags.NonPublic | BindingFlags.Instance)
            .Single(f => typeof(HttpMessageHandler).IsAssignableFrom(f.FieldType));
        return (HttpMessageHandler)field.GetValue(invoker)!;
    }

    private static HttpMessageHandler PrimaryOf(HttpMessageInvoker invoker)
    {
        var handler = HandlerOf(invoker);
        while (handler is DelegatingHandler delegating && delegating.InnerHandler is not null)
            handler = delegating.InnerHandler;
        return handler;
    }

    private static int CountNamed(HttpMessageHandler handler, string name)
    {
        var count = 0;
        var seen = new HashSet<HttpMessageHandler>();
        while (seen.Add(handler))
        {
            if (handler.GetType().Name == name)
                count++;
            if (handler is not DelegatingHandler delegating || delegating.InnerHandler is null)
                break;
            handler = delegating.InnerHandler;
        }

        return count;
    }

    private sealed class SiteSink(string site) : IEgressDecisionSink
    {
        private readonly ConcurrentQueue<EgressDecision> _seen = new();

        public IReadOnlyList<EgressDecision> Seen => _seen.ToArray();

        public void Record(EgressDecision decision)
        {
            if (string.Equals(decision.Site, site, StringComparison.Ordinal))
                _seen.Enqueue(decision);
        }
    }

    /// <summary>
    /// A primary that follows one redirect itself while <see cref="HttpClientHandler.AllowAutoRedirect"/> is true,
    /// which is what the platform handler does before Ashlar turns it off.
    /// </summary>
    private sealed class RedirectingStub : HttpClientHandler
    {
        private readonly Func<Uri, Uri?> _locationFor;
        private readonly ConcurrentQueue<Uri> _requested = new();

        public RedirectingStub(Func<Uri, Uri?> locationFor) => _locationFor = locationFor;

        public IReadOnlyList<Uri> Requested => _requested.ToArray();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Answer(request));

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Answer(request);

        private HttpResponseMessage Answer(HttpRequestMessage request)
        {
            var uri = request.RequestUri ?? throw new InvalidOperationException("the stub requires a URI");
            _requested.Enqueue(uri);
            var location = _locationFor(uri);
            if (location is null)
                return new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request };

            var response = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect) { RequestMessage = request };
            response.Headers.Location = location;
            if (!AllowAutoRedirect)
                return response;

            response.Dispose();
            var next = location.IsAbsoluteUri ? location : new Uri(uri, location);
            using var followed = new HttpRequestMessage(HttpMethod.Get, next);
            var inner = Answer(followed);
            inner.RequestMessage = request;
            return inner;
        }
    }

    private sealed class TerminalHandler : HttpMessageHandler
    {
        public Uri? LastUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent) { RequestMessage = request });
        }
    }

    private sealed class RewritingHandler : DelegatingHandler
    {
        private readonly Uri _rewritten;

        public RewritingHandler(HttpMessageHandler inner, Uri rewritten)
            : base(inner) => _rewritten = rewritten;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.RequestUri = _rewritten;
            return base.SendAsync(request, cancellationToken);
        }

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.RequestUri = _rewritten;
            return base.Send(request, cancellationToken);
        }
    }

    private sealed class NoContentPrimary : HttpClientHandler
    {
        private int _sends;

        public int Sends => Volatile.Read(ref _sends);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _sends);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent) { RequestMessage = request });
        }
    }

    /// <summary>Not an <see cref="HttpClientHandler"/>. Follows by handing back a different request message.</summary>
    private sealed class FollowingUnknownPrimary : HttpMessageHandler
    {
        private readonly Uri _followed;

        public FollowingUnknownPrimary(Uri followed) => _followed = followed;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var followed = new HttpRequestMessage(HttpMethod.Get, _followed);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = followed });
        }
    }

    private sealed class WarningCapture : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _warnings = new();

        public IReadOnlyList<string> Warnings => _warnings.ToArray();

        public ILogger CreateLogger(string categoryName) => new Logger(_warnings);

        public void Dispose()
        {
        }

        private sealed class Logger(ConcurrentQueue<string> warnings) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (logLevel == LogLevel.Warning)
                    warnings.Enqueue(formatter(state, exception));
            }
        }
    }

    private sealed record Hit(string Path, string Method, string Authorization, string Body, string Client);

    private sealed class LoopbackRedirectServer : IDisposable
    {
        private readonly WebApplication _app;
        private readonly ConcurrentQueue<Hit> _hits = new();
        private Uri _http = new("http://127.0.0.1/");
        private Uri _https = new("https://127.0.0.1/");

        private LoopbackRedirectServer(WebApplication app, X509Certificate2 certificate)
        {
            _app = app;
            Certificate = certificate;
        }

        public Uri Http => _http;

        public Uri Https => _https;

        public X509Certificate2 Certificate { get; }

        public IReadOnlyList<Hit> Hits => _hits.ToArray();

        public Uri DowngradeUri => new($"https://127.0.0.1:{Https.Port}/downgrade");

        public Uri RelativeUri => new($"http://127.0.0.1:{Http.Port}/relative");

        public Uri MissingLocationUri => new($"http://127.0.0.1:{Http.Port}/missing");

        public Uri FragmentUri => new($"http://127.0.0.1:{Http.Port}/r/302#section");

        public Uri RedirectUri(int status) => new($"http://127.0.0.1:{Http.Port}/r/{status}");

        public Uri HopUri(int remaining) => new($"http://127.0.0.1:{Http.Port}/hop/{remaining}");

        public void Reset()
        {
            while (_hits.TryDequeue(out _))
            {
            }
        }

        public static LoopbackRedirectServer Start()
        {
            var certificate = CreateCertificate();
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
            builder.Logging.SetMinimumLevel(LogLevel.Warning);
            builder.WebHost.PreferHostingUrls(false);
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.Listen(IPAddress.Loopback, 0);
                options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate));
            });

            var app = builder.Build();
            var server = new LoopbackRedirectServer(app, certificate);

            app.Map("/downgrade", (HttpContext ctx) =>
            {
                ctx.Response.StatusCode = StatusCodes.Status302Found;
                ctx.Response.Headers.Location = $"http://127.0.0.1:{server.Http.Port}/downgrade-landed";
                return Task.CompletedTask;
            });
            app.Map("/downgrade-landed", (HttpContext ctx) => Record(server, ctx, ""));
            app.Map("/relative", (HttpContext ctx) =>
            {
                ctx.Response.StatusCode = StatusCodes.Status302Found;
                ctx.Response.Headers.Location = "/done";
                return Task.CompletedTask;
            });
            app.Map("/missing", (HttpContext ctx) =>
            {
                ctx.Response.StatusCode = StatusCodes.Status302Found;
                return Task.CompletedTask;
            });
            app.Map("/r/{code:int}", (int code, HttpContext ctx) =>
            {
                ctx.Response.StatusCode = code;
                ctx.Response.Headers.Location = "/done";
                return Task.CompletedTask;
            });
            app.Map("/hop/{remaining:int}", (int remaining, HttpContext ctx) =>
            {
                ctx.Response.StatusCode = StatusCodes.Status302Found;
                ctx.Response.Headers.Location = remaining <= 1 ? "/done" : $"/hop/{remaining - 1}";
                return Task.CompletedTask;
            });
            app.Map("/done", async (HttpContext ctx) =>
            {
                var body = ctx.Request.ContentLength is > 0
                    ? await new StreamReader(ctx.Request.Body).ReadToEndAsync()
                    : "";
                Record(server, ctx, body);
            });

            app.StartAsync().GetAwaiter().GetResult();
            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses
                .Select(address => new Uri(address))
                .ToArray();
            server._http = addresses.Single(address => address.Scheme == "http");
            server._https = addresses.Single(address => address.Scheme == "https");
            return server;

            static void Record(LoopbackRedirectServer owner, HttpContext ctx, string body)
            {
                owner._hits.Enqueue(new Hit(
                    ctx.Request.Path.Value ?? "",
                    ctx.Request.Method,
                    ctx.Request.Headers.Authorization.ToString(),
                    body,
                    ctx.Request.Headers["X-Ashlar-Twin"].ToString()));
            }
        }

        public void Dispose()
        {
            _app.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Certificate.Dispose();
        }

        private static X509Certificate2 CreateCertificate()
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=127.0.0.1", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var san = new SubjectAlternativeNameBuilder();
            san.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(san.Build());
            using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2));
            var pfx = created.Export(X509ContentType.Pfx);
#if NET9_0_OR_GREATER
            return X509CertificateLoader.LoadPkcs12(pfx, password: null, X509KeyStorageFlags.Exportable);
#else
            return new X509Certificate2(pfx, string.Empty, X509KeyStorageFlags.Exportable);
#endif
        }
    }
}
