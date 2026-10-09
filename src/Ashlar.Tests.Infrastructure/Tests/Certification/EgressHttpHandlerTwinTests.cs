using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using Ashlar.Abstractions.Security.Egress;
using FluentAssertions;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>
/// SPEC-007 PR 3a, behavioural twin of the egress guard's HTTP adapter: the handler that
/// <see cref="EgressHttp.Wrap"/>, <see cref="EgressHttp.CreateClient(HttpMessageHandler, string, string, IEgressGuard?)"/>
/// and <see cref="EgressHttp.CreateDelegatingHandler"/> put in front of a send.
/// </summary>
/// <remarks>
/// <para><b>What is pinned: report-only.</b> Exactly one decision per send, on the async and the sync path, made
/// before the inner handler runs; the response is the inner handler's own instance; the request reaches the inner
/// handler as the same instance with the same content instance and the same headers, and its content is never read,
/// buffered or measured; a guard that throws (or returns nothing) changes nothing the caller sees; an exception from
/// the inner handler reaches the caller as the same instance; the decision records only the scheme, host and port.
/// Also pinned: a <see langword="null"/> guard means <see cref="EgressGuard.ProcessDefault"/>, a request without a
/// URI is recorded as <c>unknown</c>, a <see langword="null"/> request is not a send, the client and wrapped handler
/// own the inner handler, the factory handler waits for its pipeline to set the inner handler, and the plain client
/// is built over <see cref="HttpClientHandler"/>, the handler <c>new HttpClient()</c> uses.</para>
/// <para><b>Process-global state.</b> The decision log is process-wide and other classes make decisions in
/// parallel, so each test subscribes a sink that keeps only its own unique site (<see cref="NewSite"/>) and disposes
/// it. No test touches the network: the inner handler is a stub, and the one client built over a real
/// <see cref="HttpClientHandler"/> is inspected and disposed without sending. No environment variable is read or
/// written: guards get their profile through the constructor, and the one test of the <see langword="null"/> guard
/// asserts nothing about the profile.</para>
/// </remarks>
[Trait("Category", "Certification")]
public sealed class EgressHttpHandlerTwinTests
{
    private const string GuardHandlerTypeName = "EgressGuardHandler";

    private static readonly string[] SecretMarkers = ["twin-user", "pa55word", "PATHMARK", "QUERYTOKEN", "FRAGMARK"];

    /// <summary>A guard with an explicit profile, so no decision here reads the environment.</summary>
    private static readonly EgressGuard Guard = new("full");

    // ---------------------------------------------------------------------------------------------------------
    // One decision per send
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Wrap_records_exactly_one_decision_per_send()
    {
        var site = NewSite();
        var sink = new SiteSink(site);
        using var subscription = EgressDecisionLog.Subscribe(sink);
        var stub = new StubHandler();
        using var invoker = new HttpMessageInvoker(EgressHttp.Wrap(stub, EgressFamilies.Http, site, Guard));

        for (var i = 0; i < 3; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://twin{i}.example/v1/PATHMARK");
            using var response = await invoker.SendAsync(request, CancellationToken.None);

            sink.Seen.Should().HaveCount(i + 1, "send {0} adds exactly one decision", i + 1);
        }

        stub.AsyncSends.Should().Be(3);
        sink.Seen.Select(d => d.Destination).Should().Equal("https://twin0.example", "https://twin1.example", "https://twin2.example");
        sink.Seen.Should().OnlyContain(d => d.Family == EgressFamilies.Http && d.Mode == "report" && d.Fault == null);
        sink.Seen.Select(d => d.Sequence).Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task Concurrent_sends_each_record_exactly_one_decision()
    {
        const int sends = 64;
        var site = NewSite();
        var sink = new SiteSink(site);
        using var subscription = EgressDecisionLog.Subscribe(sink);
        var stub = new StubHandler();
        using var invoker = new HttpMessageInvoker(EgressHttp.Wrap(stub, EgressFamilies.Grpc, site, Guard));

        await Task.WhenAll(Enumerable.Range(0, sends).Select(i => Task.Run(async () =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"http://peer{i}.example:5000/agent.Transport/Send");
            using var response = await invoker.SendAsync(request, CancellationToken.None);
        })));

        stub.AsyncSends.Should().Be(sends);
        sink.Seen.Should().HaveCount(sends);
        sink.Seen.Select(d => d.Sequence).Should().OnlyHaveUniqueItems();
        sink.Seen.Select(d => d.Destination).Should().BeEquivalentTo(
            Enumerable.Range(0, sends).Select(i => $"http://peer{i}.example:5000"));
    }

    [Fact]
    public void The_sync_Send_path_records_one_decision_and_returns_the_inner_response()
    {
        var site = NewSite();
        var sink = new SiteSink(site);
        using var subscription = EgressDecisionLog.Subscribe(sink);
        var stub = new StubHandler();
        using var invoker = new HttpMessageInvoker(EgressHttp.Wrap(stub, EgressFamilies.Http, site, Guard));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://twin-user:pa55word@sync.example:8443/PATHMARK?q=QUERYTOKEN");

        using var response = invoker.Send(request, CancellationToken.None);

        response.Should().BeSameAs(stub.LastResponse);
        stub.SyncSends.Should().Be(1);
        stub.AsyncSends.Should().Be(0);
        sink.Seen.Should().ContainSingle().Which.Destination.Should().Be("https://sync.example:8443");
    }

    [Fact]
    public async Task Through_HttpClient_one_send_is_one_decision_and_the_response_is_the_inner_handlers()
    {
        var site = NewSite();
        var sink = new SiteSink(site);
        using var subscription = EgressDecisionLog.Subscribe(sink);
        var stub = new StubHandler();
        using var client = EgressHttp.CreateClient(stub, EgressFamilies.ModelLegacy, site, Guard);

        using var response = await client.GetAsync(new Uri("https://twin-user:pa55word@api.example.com/v1/PATHMARK?key=QUERYTOKEN"));

        response.Should().BeSameAs(stub.LastResponse);
        stub.AsyncSends.Should().Be(1);
        var decision = sink.Seen.Should().ContainSingle().Which;
        decision.Destination.Should().Be("https://api.example.com");
        decision.Family.Should().Be(EgressFamilies.ModelLegacy);
        decision.DestinationClass.Should().Be(EgressDestinationClass.ExternalModel);
        decision.Destination.Should().NotContainAny(SecretMarkers);
    }

    // ---------------------------------------------------------------------------------------------------------
    // The request and the response pass through unchanged
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_request_reaches_the_inner_handler_unchanged_and_its_content_is_never_read()
    {
        var site = NewSite();
        var sink = new SiteSink(site);
        using var subscription = EgressDecisionLog.Subscribe(sink);
        var stub = new StubHandler();
        using var invoker = new HttpMessageInvoker(EgressHttp.Wrap(stub, EgressFamilies.Mcp, site, Guard));
        var content = new CountingContent();
        using var request = new HttpRequestMessage(
            HttpMethod.Post, "https://twin-user:pa55word@content.example:8443/v1/PATHMARK?token=QUERYTOKEN#FRAGMARK")
        {
            Content = content,
        };
        request.Headers.Add("X-Twin", "1");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "QUERYTOKEN");
        var headersBefore = Snapshot(request.Headers);
        using var cancellation = new CancellationTokenSource();

        using var response = await invoker.SendAsync(request, cancellation.Token);

        response.Should().BeSameAs(stub.LastResponse);
        stub.LastRequest.Should().BeSameAs(request);
        stub.LastContent.Should().BeSameAs(content);
        stub.LastToken.Should().Be(cancellation.Token, "the caller's token reaches the inner handler");
        stub.HeadersAtSend.Should().Equal(headersBefore, "the guard neither adds nor changes a header");
        content.Reads.Should().Be(0, "the guard never reads or buffers the request body");
        content.LengthQueries.Should().Be(0, "the guard never asks the body for its length");

        var decision = sink.Seen.Should().ContainSingle().Which;
        decision.Destination.Should().Be("https://content.example:8443");
        decision.Destination.Should().NotContainAny(SecretMarkers);
    }

    [Fact]
    public async Task A_send_without_a_uri_is_recorded_as_unknown()
    {
        var site = NewSite();
        var sink = new SiteSink(site);
        using var subscription = EgressDecisionLog.Subscribe(sink);
        var stub = new StubHandler();
        using var invoker = new HttpMessageInvoker(EgressHttp.Wrap(stub, EgressFamilies.Http, site, Guard));
        using var request = new HttpRequestMessage();

        using var response = await invoker.SendAsync(request, CancellationToken.None);

        response.Should().BeSameAs(stub.LastResponse);
        var decision = sink.Seen.Should().ContainSingle().Which;
        decision.Destination.Should().Be("unknown");
        decision.Fault.Should().BeNull();
    }

    [Fact]
    public async Task A_null_request_is_not_a_send_and_is_refused_as_before()
    {
        var site = NewSite();
        var sink = new SiteSink(site);
        using var subscription = EgressDecisionLog.Subscribe(sink);
        using var handler = EgressHttp.Wrap(new StubHandler(), EgressFamilies.Http, site, Guard);

        // HttpMessageInvoker refuses null before any handler runs, so reach the protected SendAsync directly.
        var sendAsync = typeof(HttpMessageHandler).GetMethod(
            "SendAsync",
            BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null,
            [typeof(HttpRequestMessage), typeof(CancellationToken)],
            modifiers: null);
        sendAsync.Should().NotBeNull();

        var task = (Task<HttpResponseMessage>)sendAsync!.Invoke(handler, [null, CancellationToken.None])!;
        var act = async () => await task;

        await act.Should().ThrowAsync<ArgumentNullException>();
        sink.Seen.Should().BeEmpty("no request, no decision");
    }

    // ---------------------------------------------------------------------------------------------------------
    // Faults never change what the caller sees
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_guard_that_throws_does_not_change_the_response()
    {
        var stub = new StubHandler();
        var guard = new ThrowingGuard();
        using var invoker = new HttpMessageInvoker(EgressHttp.Wrap(stub, EgressFamilies.Http, NewSite(), guard));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://fault.example/");
        var faultsBefore = GuardFaults();

        HttpResponseMessage? response = null;
        var act = async () => response = await invoker.SendAsync(request, CancellationToken.None);

        await act.Should().NotThrowAsync("report-only: a guard fault never reaches the caller");
        using (response)
        {
            guard.Calls.Should().Be(1);
            response.Should().BeSameAs(stub.LastResponse);
            response!.StatusCode.Should().Be(StubHandler.Status);
            stub.AsyncSends.Should().Be(1);
            stub.LastRequest.Should().BeSameAs(request);
            GuardFaults().Should().BeGreaterThan(faultsBefore, "a swallowed guard fault is counted");
        }
    }

    [Fact]
    public async Task A_guard_that_throws_does_not_change_the_response_through_HttpClient_or_the_sync_path()
    {
        var stub = new StubHandler();
        var guard = new ThrowingGuard();
        using var client = EgressHttp.CreateClient(stub, EgressFamilies.Http, NewSite(), guard);

        using (var response = await client.GetAsync(new Uri("https://fault.example/")))
        {
            response.Should().BeSameAs(stub.LastResponse);
            response.StatusCode.Should().Be(StubHandler.Status);
        }

        using var invoker = new HttpMessageInvoker(EgressHttp.Wrap(stub, EgressFamilies.Http, NewSite(), guard), disposeHandler: false);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://fault.example/sync");
        using (var response = invoker.Send(request, CancellationToken.None))
        {
            response.Should().BeSameAs(stub.LastResponse);
        }

        guard.Calls.Should().Be(2);
        stub.AsyncSends.Should().Be(1);
        stub.SyncSends.Should().Be(1);
    }

    [Fact]
    public async Task A_guard_that_returns_nothing_does_not_change_the_response()
    {
        var stub = new StubHandler();
        var guard = new NullGuard();
        using var invoker = new HttpMessageInvoker(EgressHttp.Wrap(stub, EgressFamilies.Http, NewSite(), guard));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://null.example/");

        using var response = await invoker.SendAsync(request, CancellationToken.None);

        guard.Calls.Should().Be(1);
        response.Should().BeSameAs(stub.LastResponse);
    }

    [Fact]
    public async Task An_exception_from_the_inner_handler_reaches_the_caller_unchanged()
    {
        var site = NewSite();
        var sink = new SiteSink(site);
        using var subscription = EgressDecisionLog.Subscribe(sink);
        var failure = new HttpRequestException("the inner handler failed");
        using var invoker = new HttpMessageInvoker(EgressHttp.Wrap(new FailingHandler(failure), EgressFamilies.A2A, site, Guard));

        using (var request = new HttpRequestMessage(HttpMethod.Post, "https://agent.example/a2a"))
        {
            var act = async () => await invoker.SendAsync(request, CancellationToken.None);
            (await act.Should().ThrowAsync<HttpRequestException>()).Which.Should().BeSameAs(failure);
        }

        using (var request = new HttpRequestMessage(HttpMethod.Post, "https://agent.example/a2a-sync"))
        {
            var act = () => invoker.Send(request, CancellationToken.None);
            act.Should().Throw<HttpRequestException>().Which.Should().BeSameAs(failure);
        }

        sink.Seen.Should().HaveCount(2, "each decision is made before the send, so a failed send still has its record");
    }

    // ---------------------------------------------------------------------------------------------------------
    // Which guard, and the shapes of the adapter
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_custom_guard_is_asked_once_per_send_with_the_family_site_and_request_uri()
    {
        var site = NewSite();
        var guard = new RecordingGuard(Guard);
        var stub = new StubHandler();
        using var invoker = new HttpMessageInvoker(EgressHttp.Wrap(stub, EgressFamilies.WebSearch, site, guard));
        var uri = new Uri("https://search.example/bing?q=QUERYTOKEN");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);

        using var response = await invoker.SendAsync(request, CancellationToken.None);

        var asked = guard.Requests.Should().ContainSingle().Which;
        asked.Family.Should().Be(EgressFamilies.WebSearch);
        asked.Site.Should().Be(site);
        asked.Destination.Should().Be(uri, "the guard is asked about the request's own URI");
        asked.DestinationName.Should().BeNull();
        response.Should().BeSameAs(stub.LastResponse);
    }

    [Fact]
    public async Task A_null_guard_means_the_process_default()
    {
        var site = NewSite();
        var sink = new SiteSink(site);
        using var subscription = EgressDecisionLog.Subscribe(sink);
        var stub = new StubHandler();
        using var invoker = new HttpMessageInvoker(EgressHttp.Wrap(stub, EgressFamilies.Telemetry, site));
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://otlp.example:4318/v1/traces");

        using var response = await invoker.SendAsync(request, CancellationToken.None);

        response.Should().BeSameAs(stub.LastResponse);
        var decision = sink.Seen.Should().ContainSingle("the process default guard publishes like any other").Which;
        decision.Family.Should().Be(EgressFamilies.Telemetry);
        decision.Destination.Should().Be("https://otlp.example:4318");
        decision.Fault.Should().BeNull();
    }

    [Fact]
    public async Task The_factory_handler_waits_for_its_pipeline_to_set_the_inner_handler()
    {
        var site = "factory:" + NewSite();
        var sink = new SiteSink(site);
        using var subscription = EgressDecisionLog.Subscribe(sink);
        var stub = new StubHandler();

        var handler = EgressHttp.CreateDelegatingHandler(EgressFamilies.HttpFactory, site, Guard);
        handler.InnerHandler.Should().BeNull("an IHttpClientFactory pipeline sets the inner handler");
        handler.InnerHandler = stub;
        using var client = new HttpClient(handler);

        using var response = await client.GetAsync(new Uri("https://factory.example/PATHMARK?x=QUERYTOKEN"));

        response.Should().BeSameAs(stub.LastResponse);
        var decision = sink.Seen.Should().ContainSingle().Which;
        decision.Family.Should().Be(EgressFamilies.HttpFactory);
        decision.Site.Should().Be(site);
        decision.Destination.Should().Be("https://factory.example");
    }

    [Fact]
    public void The_plain_client_is_the_guard_over_an_HttpClientHandler_and_every_call_shape_builds()
    {
        var site = NewSite();

        using var plain = EgressHttp.CreateClient(EgressFamilies.Mcp, site);
        using var withGuard = EgressHttp.CreateClient(EgressFamilies.Mcp, site, Guard);
        using var namedGuard = EgressHttp.CreateClient(EgressFamilies.Mcp, site, guard: Guard);
        using var overInner = EgressHttp.CreateClient(new StubHandler(), EgressFamilies.Mcp, site);

        foreach (var client in new[] { plain, withGuard, namedGuard })
        {
            var outer = HandlerOf(client);
            outer.GetType().Name.Should().Be(GuardHandlerTypeName);
            var redirect = outer.Should().BeAssignableTo<DelegatingHandler>().Which.InnerHandler;
            redirect.Should().NotBeNull();
            redirect!.GetType().Name.Should().Be("EgressRedirectHandler");
            redirect.Should().BeAssignableTo<DelegatingHandler>().Which.InnerHandler
                .Should().BeOfType<HttpClientHandler>("the handler new HttpClient() uses")
                .Which.AllowAutoRedirect.Should().BeFalse();
        }

        var overRedirect = HandlerOf(overInner).Should().BeAssignableTo<DelegatingHandler>().Which.InnerHandler;
        overRedirect.Should().NotBeNull();
        overRedirect!.GetType().Name.Should().Be("EgressRedirectHandler");
        overRedirect.Should().BeAssignableTo<DelegatingHandler>().Which.InnerHandler.Should().BeOfType<StubHandler>();
    }

    [Fact]
    public void The_client_and_the_wrapped_handler_own_the_inner_handler()
    {
        var wrappedInner = new StubHandler();
        EgressHttp.Wrap(wrappedInner, EgressFamilies.Grpc, NewSite(), Guard).Dispose();
        wrappedInner.Disposed.Should().BeTrue("disposing the wrapped handler disposes the inner one");

        var clientInner = new StubHandler();
        EgressHttp.CreateClient(clientInner, EgressFamilies.MeshPull, NewSite(), Guard).Dispose();
        clientInner.Disposed.Should().BeTrue("disposing the client disposes the inner handler, as new HttpClient(inner) does");
    }

    [Fact]
    public void Missing_arguments_fail_when_the_client_is_built_never_at_a_send()
    {
        var stub = new StubHandler();

        ((Action)(() => EgressHttp.Wrap(null!, EgressFamilies.Http, "s"))).Should().Throw<ArgumentNullException>().WithParameterName("inner");
        ((Action)(() => EgressHttp.Wrap(stub, null!, "s"))).Should().Throw<ArgumentNullException>().WithParameterName("family");
        ((Action)(() => EgressHttp.Wrap(stub, EgressFamilies.Http, null!))).Should().Throw<ArgumentNullException>().WithParameterName("site");
        ((Action)(() => EgressHttp.CreateClient(null!, "s"))).Should().Throw<ArgumentNullException>().WithParameterName("family");
        ((Action)(() => EgressHttp.CreateClient(EgressFamilies.Http, null!))).Should().Throw<ArgumentNullException>().WithParameterName("site");
        ((Action)(() => EgressHttp.CreateClient((HttpMessageHandler)null!, EgressFamilies.Http, "s"))).Should().Throw<ArgumentNullException>().WithParameterName("inner");
        ((Action)(() => EgressHttp.CreateDelegatingHandler(null!, "s"))).Should().Throw<ArgumentNullException>().WithParameterName("family");
        ((Action)(() => EgressHttp.CreateDelegatingHandler(EgressFamilies.HttpFactory, null!))).Should().Throw<ArgumentNullException>().WithParameterName("site");

        stub.Dispose();
    }

    // ---------------------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>A site string no other test uses, so records published process-wide can be told apart.</summary>
    private static string NewSite() => "twin:http:" + Guid.NewGuid().ToString("N");

    private static string[] Snapshot(HttpHeaders headers) =>
        headers.Select(header => header.Key + ": " + string.Join(", ", header.Value)).ToArray();

    /// <summary>The handler an <see cref="HttpMessageInvoker"/> sends through, which it keeps in a private field.</summary>
    private static HttpMessageHandler HandlerOf(HttpMessageInvoker invoker)
    {
        var field = typeof(HttpMessageInvoker)
            .GetFields(BindingFlags.NonPublic | BindingFlags.Instance)
            .SingleOrDefault(f => typeof(HttpMessageHandler).IsAssignableFrom(f.FieldType));
        field.Should().NotBeNull("HttpMessageInvoker keeps its handler in one private field");
        return (HttpMessageHandler)field!.GetValue(invoker)!;
    }

    /// <summary>
    /// The handler's internal guard-fault counter, by reflection: this assembly is not in Ashlar.Abstractions'
    /// InternalsVisibleTo. It only rises, and other classes run in parallel, so callers compare "rose".
    /// </summary>
    private static long GuardFaults()
    {
        var type = typeof(EgressHttp).Assembly.GetType("Ashlar.Abstractions.Security.Egress." + GuardHandlerTypeName);
        type.Should().NotBeNull();
        var property = type!.GetProperty("GuardFaults", BindingFlags.NonPublic | BindingFlags.Static);
        property.Should().NotBeNull("the handler counts the guard faults it swallows");
        return Convert.ToInt64(property!.GetValue(null), CultureInfo.InvariantCulture);
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

    /// <summary>Answers every send with a fresh response and records what it was handed.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        public const HttpStatusCode Status = HttpStatusCode.Accepted;

        private int _asyncSends;
        private int _syncSends;

        public int AsyncSends => Volatile.Read(ref _asyncSends);

        public int SyncSends => Volatile.Read(ref _syncSends);

        public HttpResponseMessage? LastResponse { get; private set; }

        public HttpRequestMessage? LastRequest { get; private set; }

        public HttpContent? LastContent { get; private set; }

        public CancellationToken LastToken { get; private set; }

        public string[] HeadersAtSend { get; private set; } = [];

        public bool Disposed { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _asyncSends);
            return Task.FromResult(Answer(request, cancellationToken));
        }

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _syncSends);
            return Answer(request, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }

        private HttpResponseMessage Answer(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(Status) { RequestMessage = request };
            LastRequest = request;
            LastContent = request.Content;
            LastToken = cancellationToken;
            HeadersAtSend = Snapshot(request.Headers);
            LastResponse = response;
            return response;
        }
    }

    private sealed class FailingHandler(Exception failure) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(failure);

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw failure;
    }

    /// <summary>A body that counts every attempt to read or measure it, and fails the read.</summary>
    private sealed class CountingContent : HttpContent
    {
        private int _reads;
        private int _lengthQueries;

        public int Reads => Volatile.Read(ref _reads);

        public int LengthQueries => Volatile.Read(ref _lengthQueries);

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => Read();

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken) =>
            Read();

        protected override void SerializeToStream(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _reads);
            throw new InvalidOperationException("the request body was read");
        }

        protected override Task<Stream> CreateContentReadStreamAsync()
        {
            Interlocked.Increment(ref _reads);
            return Task.FromException<Stream>(new InvalidOperationException("the request body was read"));
        }

        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) =>
            CreateContentReadStreamAsync();

        protected override Stream CreateContentReadStream(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _reads);
            throw new InvalidOperationException("the request body was read");
        }

        protected override bool TryComputeLength(out long length)
        {
            Interlocked.Increment(ref _lengthQueries);
            length = 0;
            return false;
        }

        private Task Read()
        {
            Interlocked.Increment(ref _reads);
            return Task.FromException(new InvalidOperationException("the request body was read"));
        }
    }

    private sealed class ThrowingGuard : IEgressGuard
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public EgressDecision Evaluate(EgressRequest request)
        {
            Interlocked.Increment(ref _calls);
            throw new InvalidOperationException("a custom guard failed");
        }
    }

    private sealed class NullGuard : IEgressGuard
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public EgressDecision Evaluate(EgressRequest request)
        {
            Interlocked.Increment(ref _calls);
            return null!;
        }
    }

    private sealed class RecordingGuard(IEgressGuard inner) : IEgressGuard
    {
        private readonly ConcurrentQueue<EgressRequest> _requests = new();

        public IReadOnlyList<EgressRequest> Requests => _requests.ToArray();

        public EgressDecision Evaluate(EgressRequest request)
        {
            _requests.Enqueue(request);
            return inner.Evaluate(request);
        }
    }
}
