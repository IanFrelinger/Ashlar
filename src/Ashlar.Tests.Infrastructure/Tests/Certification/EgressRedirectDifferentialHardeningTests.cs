using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Ashlar.Abstractions.Security;
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
/// SPEC-007 PR 4.3, the differential twin of Ashlar's redirect follower: the same requests, over a real
/// <see cref="SocketsHttpHandler"/> to a loopback Kestrel, once with the runtime following redirects itself and once
/// through a factory client under the egress guard, where the egress redirect follower does (PR 4 design §2.6 gap 2,
/// R-b "runtime parity"; Appendix B: "the runtime's redirect rules, the differential twin settles them").
/// </summary>
/// <remarks>
/// <para><b>What is compared.</b> The final status, the final request URI (fragment included) and method, and every
/// request the server saw, in order: scheme, host, method, path and query, whether it carried <c>Authorization</c>,
/// and the body length. Cases: 300, 301, 302, 303, 307 and 308, with <c>GET</c> and <c>POST</c>, and 304 and 305 (not
/// followed); a relative, a scheme-relative, a dot-segment, a missing and an invalid <c>Location</c>; another host;
/// <c>https</c> to <c>http</c> (not followed) and <c>http</c> to <c>https</c>; the redirect limit reached and
/// exceeded; a fragment.</para>
/// <para><b>Also pinned, against the real handlers.</b> A factory client over the factory's own primary handler
/// decides each new loopback authority before the server sees the hop; under an enforcing guard the hop to a remote
/// host is decided, refused, before the server sees it (report-only until SPEC-007 PR 4.7, so it is still sent); a
/// client with <c>NeverFollowRedirects</c> returns the 3xx; a redirect to a
/// scheme other than <c>http</c> or <c>https</c> is returned, not followed; a primary that has already sent (its
/// <c>AllowAutoRedirect</c> setter throws) keeps following on its own, and the final authority is decided after the
/// send; a hop from a remote host into loopback Kestrel is returned, recorded, and never reaches the server (owner
/// decision 2026-10-06).</para>
/// <para><b>Hermetic.</b> Every connection goes to the loopback listener: the handlers' <c>ConnectCallback</c> connects
/// any host name to 127.0.0.1, so no name is resolved and nothing leaves the machine. The HTTPS listener uses a
/// self-signed certificate made here, which the client accepts. Each case has its own token in every path and host,
/// so the server's log is filtered per case; decisions are filtered by the token too.</para>
/// </remarks>
[Trait("Category", "Certification")]
public sealed class EgressRedirectDifferentialHardeningTests : IClassFixture<EgressRedirectDifferentialHardeningTests.LoopbackRedirectServer>
{
    private const string Payload = "payload";

    private static long _clock;

    private readonly LoopbackRedirectServer _server;

    public EgressRedirectDifferentialHardeningTests(LoopbackRedirectServer server) => _server = server;

    public static TheoryData<string> Cases => new()
    {
        "300-POST", "301-GET", "301-POST", "302-GET", "302-POST", "303-GET", "303-POST", "303-PUT", "307-POST", "308-POST",
        "304-GET", "305-GET",
        "relative-location", "scheme-relative-location", "dot-segment-location", "missing-location", "invalid-location",
        "cross-host", "https-to-http", "http-to-https",
        "limit-reached", "limit-exceeded", "fragment",
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task The_follower_does_what_the_runtime_does(string name)
    {
        var runtime = await RunAsync(name, factory: false);
        var ours = await RunAsync(name, factory: true);

        ours.Should().BeEquivalentTo(runtime, options => options.WithStrictOrdering(), "case {0}", name);
        runtime.Seen.Should().NotBeEmpty();
    }

    [Fact]
    public async Task A_factory_client_over_the_factorys_own_primary_decides_each_new_authority_before_the_server_sees_it()
    {
        var token = NewToken();
        var name = "own-primary-" + token;
        var recorder = new Recorder(d => d.Site == "factory:" + name);
        using var subscription = EgressDecisionLog.Subscribe(recorder);

        var services = new ServiceCollection();
        services.AddAshlarEgressGuard();
        services.AddHttpClient(name);
        using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(name);

        var target = $"http://localhost:{_server.HttpPort}/{token}/ok";
        using var response = await client.GetAsync(new Uri($"http://127.0.0.1:{_server.HttpPort}/{token}/r/307?to={Uri.EscapeDataString(target)}"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var seen = _server.For(token);
        seen.Select(s => s.PathAndQuery).Should().HaveCount(2).And.EndWith($"/{token}/ok");
        recorder.Decisions.Select(d => d.Decision.Destination).Should().Equal(
            $"http://127.0.0.1:{_server.HttpPort}", $"http://localhost:{_server.HttpPort}");
        recorder.Decisions[1].At.Should().BeLessThan(seen[1].At, "the new authority was decided before the server saw the hop");
    }

    [Fact]
    public async Task Under_an_enforcing_guard_the_hop_to_a_remote_host_is_decided_refused_before_the_server_sees_it()
    {
        var token = NewToken();
        var name = "enforce-" + token;
        var recorder = new Recorder(d => d.Site == "factory:" + name);
        using var subscription = EgressDecisionLog.Subscribe(recorder);

        var services = new ServiceCollection();
        services.AddSingleton<IEgressGuard>(new EgressGuard("full", "enforce"));
        services.AddAshlarEgressGuard();
        services.AddHttpClient(name).ConfigurePrimaryHttpMessageHandler(() => Primary(maxRedirects: 50));
        using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(name);

        var remote = $"http://remote-{token}.example:{_server.HttpPort}/{token}/b";
        using var response = await client.GetAsync(new Uri($"http://127.0.0.1:{_server.HttpPort}/{token}/r/307?to={Uri.EscapeDataString(remote)}"));

        var decisions = recorder.Decisions;
        decisions.Should().HaveCount(2);
        decisions[0].Decision.Refused.Should().BeFalse("hop 1 is loopback, inside the host boundary");
        var hop2 = decisions[1].Decision;
        hop2.Destination.Should().Be($"http://remote-{token}.example:{_server.HttpPort}");
        hop2.Mode.Should().Be("enforce");
        hop2.Refused.Should().BeTrue();
        hop2.Access.Reason.Should().Be(AccessDenialReason.SystemHighData);

        var seen = _server.For(token);
        seen.Should().HaveCount(2, "report-only until SPEC-007 PR 4.7: the refused hop is still sent");
        seen[1].Host.Should().StartWith($"remote-{token}.example");
        decisions[1].At.Should().BeLessThan(seen[1].At, "the refusal is decided before the hop reaches the server, which is where PR 4.7 stops it");
    }

    [Fact]
    public async Task A_client_that_never_follows_returns_the_redirect_and_the_server_never_sees_the_target()
    {
        var token = NewToken();
        var name = "never-" + token;

        var services = new ServiceCollection();
        services.AddAshlarEgressGuard();
        NeverFollowRedirects(services.AddHttpClient(name));
        using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(name);

        using var response = await client.GetAsync(new Uri($"http://127.0.0.1:{_server.HttpPort}/{token}/r/302?to={Uri.EscapeDataString($"/{token}/b")}"));

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        _server.For(token).Select(s => s.PathAndQuery).Should().ContainSingle().Which.Should().StartWith($"/{token}/r/302");
    }

    [Fact]
    public async Task A_redirect_to_a_scheme_other_than_http_or_https_is_returned_not_followed()
    {
        var token = NewToken();
        var name = "scheme-" + token;

        var services = new ServiceCollection();
        services.AddAshlarEgressGuard();
        services.AddHttpClient(name).ConfigurePrimaryHttpMessageHandler(() => Primary(maxRedirects: 50));
        using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(name);

        using var response = await client.GetAsync(new Uri($"http://a-{token}.test:{_server.HttpPort}/{token}/r/302?to={Uri.EscapeDataString("ftp://files.example/x")}"));

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        _server.For(token).Should().ContainSingle();
    }

    [Fact]
    public async Task A_primary_that_has_already_sent_keeps_following_on_its_own_and_the_final_authority_is_decided_after_the_send()
    {
        // The AllowAutoRedirect setter throws once the handler has sent. Such a primary is left as it is (fail closed
        // would be to refuse the client; the design keeps it building), so the runtime follows below the follower, and
        // the authority the response's request names is decided after the send. Known limit in docs/EgressInventory.md.
        var token = NewToken();
        var site = "twin:redirect:started-" + token;
        var recorder = new Recorder(d => d.Site == site);
        using var subscription = EgressDecisionLog.Subscribe(recorder);

        var primary = Primary(maxRedirects: 50);
        using (var raw = new HttpClient(primary, disposeHandler: false))
        using (var warm = await raw.GetAsync(new Uri($"http://127.0.0.1:{_server.HttpPort}/{token}/warm")))
        {
            warm.StatusCode.Should().Be(HttpStatusCode.OK, "the primary has sent, so its AllowAutoRedirect can no longer change");
        }

        using var client = EgressHttp.CreateClient(primary, EgressFamilies.Http, site, new EgressGuard("full"));
        var target = $"http://localhost:{_server.HttpPort}/{token}/ok";
        using var response = await client.GetAsync(new Uri($"http://127.0.0.1:{_server.HttpPort}/{token}/r/307?to={Uri.EscapeDataString(target)}"));

        primary.AllowAutoRedirect.Should().BeTrue("the setter throws on a started handler, so the primary is left as it is");
        FollowerFollows(client).Should().BeFalse("the follower never follows for a primary it could not turn off");
        response.StatusCode.Should().Be(HttpStatusCode.OK, "the primary followed on its own");
        var seen = _server.For(token);
        seen.Select(s => s.PathAndQuery).Should().HaveCount(3).And.EndWith($"/{token}/ok");
        recorder.Decisions.Select(d => d.Decision.Destination).Should().Equal(
            $"http://127.0.0.1:{_server.HttpPort}", $"http://localhost:{_server.HttpPort}");
        recorder.Decisions[1].At.Should().BeGreaterThan(seen[2].At, "the primary's own hop is decided only after the send");
    }

    [Fact]
    public async Task A_remote_hop_into_loopback_Kestrel_is_returned_and_the_server_never_sees_it()
    {
        // Owner decision 2026-10-06 (O2), against the real primary: the first hop is presented as a remote host by a
        // stub that answers 307 to loopback Kestrel; everything else goes through the real SocketsHttpHandler. The 3xx
        // comes back, the server sees nothing, and both hops are recorded.
        var token = NewToken();
        var name = "inward-" + token;
        var recorder = new Recorder(d => d.Site == "factory:" + name);
        using var subscription = EgressDecisionLog.Subscribe(recorder);
        var target = $"http://127.0.0.1:{_server.HttpPort}/{token}/b";

        var services = new ServiceCollection();
        services.AddAshlarEgressGuard();
        services.AddHttpClient(name).ConfigurePrimaryHttpMessageHandler(() => new RemoteFrontedPrimary(Primary(maxRedirects: 50), $"remote-{token}.example", target));
        using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(name);

        using var response = await client.PostAsync(new Uri($"http://remote-{token}.example/{token}/a"), new StringContent(Payload));

        response.StatusCode.Should().Be(HttpStatusCode.TemporaryRedirect);
        _server.For(token).Should().BeEmpty("the server inside the boundary never sees the bounced request");
        recorder.Decisions.Select(d => d.Decision.Destination).Should().Equal($"http://remote-{token}.example", $"http://127.0.0.1:{_server.HttpPort}");
        recorder.Decisions[1].Decision.DestinationClass.Should().Be(EgressDestinationClass.Host);
    }

    /// <summary>
    /// A known-type primary that answers one remote host itself (307 to a given Location) and sends everything else
    /// through a real handler to the loopback listener.
    /// </summary>
    private sealed class RemoteFrontedPrimary : HttpClientHandler
    {
        private readonly HttpMessageInvoker _real;
        private readonly string _remoteHost;
        private readonly string _location;

        public RemoteFrontedPrimary(SocketsHttpHandler real, string remoteHost, string location)
        {
            _real = new HttpMessageInvoker(real);
            _remoteHost = remoteHost;
            _location = location;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (string.Equals(request.RequestUri!.Host, _remoteHost, StringComparison.OrdinalIgnoreCase))
            {
                var response = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect) { RequestMessage = request };
                response.Headers.Location = new Uri(_location);
                return Task.FromResult(response);
            }

            return _real.SendAsync(request, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _real.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>The <c>Follows</c> of the redirect follower directly under the client's guard handler, by reflection (no InternalsVisibleTo).</summary>
    private static bool FollowerFollows(HttpMessageInvoker client)
    {
        var field = typeof(HttpMessageInvoker).GetFields(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            .Single(f => typeof(HttpMessageHandler).IsAssignableFrom(f.FieldType));
        var guardHandler = (DelegatingHandler)field.GetValue(client)!;
        var follower = guardHandler.InnerHandler!;
        follower.GetType().Name.Should().Be("EgressRedirectHandler");
        var settings = follower.GetType().GetField("_settings", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(follower)!;
        return (bool)settings.GetType().GetProperty("Follow", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(settings)!;
    }

    private static void NeverFollowRedirects(IHttpClientBuilder builder)
    {
        var extension = typeof(EgressServiceCollectionExtensions).Assembly.GetType("Ashlar.Infrastructure.Egress.EgressHttpClientBuilderExtensions");
        extension.Should().NotBeNull("the reusable SNS redirect configuration must exist");
        extension!.GetMethod("NeverFollowRedirects")!.Invoke(null, [builder]);
    }

    private static long Tick() => Interlocked.Increment(ref _clock);

    private static string NewToken() => Guid.NewGuid().ToString("N")[..12];

    /// <summary>A real handler whose every connection goes to the loopback listener on the URL's port.</summary>
    private static SocketsHttpHandler Primary(int maxRedirects) => new()
    {
        MaxAutomaticRedirections = maxRedirects,
        UseCookies = false,
        UseProxy = false,
        ConnectCallback = async (context, cancellationToken) =>
        {
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, context.DnsEndPoint.Port), cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
        SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = static (_, _, _, _) => true },
    };

    private async Task<Outcome> RunAsync(string name, bool factory)
    {
        var token = NewToken();
        var (start, method, maxRedirects) = Build(name, token);

        HttpClient client;
        ServiceProvider? provider = null;
        if (factory)
        {
            var services = new ServiceCollection();
            services.AddAshlarEgressGuard();
            services.AddHttpClient("diff").ConfigurePrimaryHttpMessageHandler(() => Primary(maxRedirects));
            provider = services.BuildServiceProvider();
            client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("diff");
        }
        else
        {
            client = new HttpClient(Primary(maxRedirects));
        }

        try
        {
            using var request = new HttpRequestMessage(method, start);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "secret");
            if (method != HttpMethod.Get)
                request.Content = new StringContent(Payload);

            using var response = await client.SendAsync(request);
            var final = response.RequestMessage!;
            return new Outcome(
                (int)response.StatusCode,
                Normalize(final.RequestUri!.ToString(), token),
                final.Method.Method,
                _server.For(token).Select(s => s with { At = 0, Host = Normalize(s.Host, token), PathAndQuery = Normalize(s.PathAndQuery, token) }).ToList());
        }
        finally
        {
            client.Dispose();
            if (provider is not null)
                await provider.DisposeAsync();
        }
    }

    private string Normalize(string text, string token) =>
        text.Replace(token, "T", StringComparison.Ordinal)
            .Replace(":" + _server.HttpsPort, ":HTTPS", StringComparison.Ordinal)
            .Replace(":" + _server.HttpPort, ":HTTP", StringComparison.Ordinal)
            .Replace("%3A" + _server.HttpsPort, "%3AHTTPS", StringComparison.Ordinal)
            .Replace("%3A" + _server.HttpPort, "%3AHTTP", StringComparison.Ordinal);

    private (Uri Start, HttpMethod Method, int MaxRedirects) Build(string name, string token)
    {
        var http = $"http://a-{token}.test:{_server.HttpPort}";
        var https = $"https://a-{token}.test:{_server.HttpsPort}";
        string To(string location) => "?to=" + Uri.EscapeDataString(location);

        var parts = name.Split('-');
        if (int.TryParse(parts[0], out var status))
            return (new Uri($"{http}/{token}/r/{status}{To($"/{token}/ok")}"), new HttpMethod(parts[1]), 50);

        return name switch
        {
            "relative-location" => (new Uri($"{http}/{token}/r/302{To("ok?q=1")}"), HttpMethod.Get, 50),
            "scheme-relative-location" => (new Uri($"{http}/{token}/r/302{To($"//b-{token}.test:{_server.HttpPort}/{token}/ok")}"), HttpMethod.Get, 50),
            "dot-segment-location" => (new Uri($"{http}/{token}/r/302{To($"../{token}/ok")}"), HttpMethod.Get, 50),
            "missing-location" => (new Uri($"{http}/{token}/r/302"), HttpMethod.Get, 50),
            "invalid-location" => (new Uri($"{http}/{token}/r/302{To("::not a uri::")}"), HttpMethod.Get, 50),
            "cross-host" => (new Uri($"{http}/{token}/r/307{To($"http://b-{token}.test:{_server.HttpPort}/{token}/ok")}"), HttpMethod.Post, 50),
            "https-to-http" => (new Uri($"{https}/{token}/r/302{To($"{http}/{token}/ok")}"), HttpMethod.Get, 50),
            "http-to-https" => (new Uri($"{http}/{token}/r/302{To($"{https}/{token}/ok")}"), HttpMethod.Get, 50),
            "limit-reached" => (new Uri($"{http}/{token}/chain/3"), HttpMethod.Get, 3),
            "limit-exceeded" => (new Uri($"{http}/{token}/chain/5"), HttpMethod.Get, 3),
            "fragment" => (new Uri($"{http}/{token}/r/301{To($"/{token}/ok")}#keep"), HttpMethod.Get, 50),
            _ => throw new ArgumentOutOfRangeException(nameof(name), name, "unknown case"),
        };
    }

    public sealed record Seen(long At, string Scheme, string Host, string Method, string PathAndQuery, bool HadAuthorization, int BodyLength);

    private sealed record Outcome(int Status, string FinalUri, string FinalMethod, List<Seen> Seen);

    private sealed record Stamped(long At, EgressDecision Decision);

    private sealed class Recorder(Func<EgressDecision, bool> keep) : IEgressDecisionSink
    {
        private readonly ConcurrentQueue<Stamped> _decisions = new();

        public IReadOnlyList<Stamped> Decisions => _decisions.ToArray();

        public void Record(EgressDecision decision)
        {
            if (keep(decision))
                _decisions.Enqueue(new Stamped(Tick(), decision));
        }
    }

    /// <summary>
    /// A loopback Kestrel with an HTTP and an HTTPS listener. <c>/{token}/r/{status}?to={location}</c> answers that
    /// status with that <c>Location</c> (none when <c>to</c> is absent); <c>/{token}/chain/{n}</c> redirects 302 to
    /// <c>/{token}/chain/{n-1}</c> until 0; anything else answers 200. Every request is logged with the tick it arrived at.
    /// </summary>
    public sealed class LoopbackRedirectServer : IAsyncLifetime
    {
        private readonly ConcurrentQueue<Seen> _seen = new();
        private WebApplication? _app;

        public int HttpPort { get; private set; }

        public int HttpsPort { get; private set; }

        public IReadOnlyList<Seen> For(string token) =>
            _seen.Where(s => s.PathAndQuery.Contains(token, StringComparison.Ordinal)).OrderBy(s => s.At).ToList();

        public async Task InitializeAsync()
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = "Production",
                ContentRootPath = AppContext.BaseDirectory,
            });
            builder.Logging.ClearProviders();
            var certificate = CreateCertificate();
            builder.WebHost.ConfigureKestrel(kestrel =>
            {
                kestrel.Listen(IPAddress.Loopback, 0);
                kestrel.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate));
            });

            _app = builder.Build();
            _app.Run(HandleAsync);
            await _app.StartAsync();

            foreach (var address in _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses)
            {
                var uri = new Uri(address);
                if (uri.Scheme == Uri.UriSchemeHttps)
                    HttpsPort = uri.Port;
                else
                    HttpPort = uri.Port;
            }

            HttpPort.Should().BePositive();
            HttpsPort.Should().BePositive();
        }

        public async Task DisposeAsync()
        {
            if (_app is not null)
            {
                await _app.StopAsync();
                await _app.DisposeAsync();
            }
        }

        private static X509Certificate2 CreateCertificate()
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, critical: false));
            return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        }

        private async Task HandleAsync(HttpContext context)
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync();
            var path = context.Request.Path.Value ?? string.Empty;
            _seen.Enqueue(new Seen(
                Tick(),
                context.Request.Scheme,
                context.Request.Host.Value ?? string.Empty,
                context.Request.Method,
                path + context.Request.QueryString.Value,
                context.Request.Headers.Authorization.Count > 0,
                body.Length));

            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length >= 3 && segments[1] == "r" && int.TryParse(segments[2], out var status))
            {
                context.Response.StatusCode = status;
                if (context.Request.Query.TryGetValue("to", out var to))
                    context.Response.Headers.Location = to.ToString();
                return;
            }

            if (segments.Length >= 3 && segments[1] == "chain" && int.TryParse(segments[2], out var left) && left > 0)
            {
                context.Response.StatusCode = StatusCodes.Status302Found;
                context.Response.Headers.Location = $"/{segments[0]}/chain/{left - 1}";
                return;
            }

            context.Response.StatusCode = StatusCodes.Status200OK;
            await context.Response.WriteAsync("ok");
        }
    }
}
