using System.Collections.Concurrent;
using System.Net;
using Ashlar.Abstractions.Security.Egress;
using Ashlar.Infrastructure.Egress;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>
/// SPEC-007 PR 3a, the behavioural twin of the factory-defaults binding: <c>AddAshlarEgressGuard</c> puts the
/// report-only guard handler on every <see cref="IHttpClientFactory"/> client in the container, once, and writes
/// each decision to the <c>Ashlar.Egress</c> logger at Debug.
/// </summary>
/// <remarks>
/// <para><b>What is pinned.</b> Calling <c>AddAshlarEgressGuard</c> twice still gives exactly one decision per send.
/// A named client's site is <c>factory:</c> plus its name, and the unnamed default client's site is
/// <c>factory:</c>. A client the host registers itself, before or after the call, is guarded too (owner decision Q1,
/// option A), and the guard runs outside the host's own handlers. A host-registered <see cref="IEgressGuard"/> is the
/// one the handler uses. The logger entry is Debug, event 7300 <c>EgressDecision</c>, category
/// <c>Ashlar.Egress</c>, with the exact template, and carries site, family and destination but never the path,
/// query, userinfo, fragment, a header or the body. The subscription is activated by handler construction and by
/// the hosted activator, and disposing the provider unsubscribes it.</para>
/// <para><b>Why the static facts are not enough.</b> The convention test can see that the binding call exists; only
/// a send through a real factory shows that the handler is on the client, once, with the right site.</para>
/// <para><b>Isolation.</b> The decision log is process-wide and other classes make decisions in parallel, so every
/// assertion filters by a destination host unique to the test. Hermetic: a stub primary handler answers every send,
/// so nothing leaves the process.</para>
/// <para>The twin that an <c>AddAshlar</c> kernel provider records a decision is
/// <see cref="EgressKernelFactoryTwinTests"/>: SPEC-007 PR 3b wired <c>AddAshlarEgressGuard</c> into <c>AddAshlar</c>
/// and into every kernel member that registers a factory client.</para>
/// </remarks>
[Trait("Category", "Certification")]
public sealed class EgressFactoryDefaultsTwinTests
{
    /// <summary>The template from the SPEC-007 PR 3 design, written out so a change to it is a deliberate edit here.</summary>
    private const string ExpectedTemplate =
        "Egress {Outcome} site={Site} family={Family} dest={Destination} class={DestinationClass} "
        + "destLabel={DestinationLabel} current={Current} ({CurrentBasis}) reason={Reason} detail={Detail} "
        + "profile={Profile} enforcesByDefault={ProfileEnforcesByDefault} fault={Fault} seq={Sequence} "
        + "mode={Mode} modeBasis={ModeBasis} ref={Ref} at={At} destBasis={DestinationBasis} "
        + "allowed={Allowed} refused={Refused}";

    private const string Category = "Ashlar.Egress";

    [Fact]
    public async Task AddedTwice_ANamedClient_RecordsExactlyOneDecisionPerSend_WithSiteFactoryX()
    {
        var host = UniqueHost();
        var recorder = new DecisionRecorder(host);
        using var subscription = EgressDecisionLog.Subscribe(recorder);
        var sends = new SendCounter();

        var services = new ServiceCollection();
        services.AddAshlarEgressGuard();
        services.AddAshlarEgressGuard();
        services.AddHttpClient("x").ConfigurePrimaryHttpMessageHandler(() => new StubPrimaryHandler(sends));

        using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("x");

        using (var response = await client.GetAsync(new Uri($"https://{host}/a/b?c=d")))
            response.StatusCode.Should().Be(HttpStatusCode.NoContent, "the stub primary handler answers every send");

        sends.Count.Should().Be(1);
        var decision = recorder.Decisions.Should().ContainSingle(
            "AddAshlarEgressGuard ran twice, and a second binding would put a second guard handler on the client").Which;
        decision.Site.Should().Be("factory:x");
        decision.Family.Should().Be(EgressFamilies.HttpFactory);
        decision.Destination.Should().Be($"https://{host}", "the record keeps scheme and host, never the path or query");
        decision.DestinationClass.Should().Be(EgressDestinationClass.NetworkExport);
        decision.Fault.Should().BeNull();

        using (await client.GetAsync(new Uri($"https://{host}/again")))
        using (await client.GetAsync(new Uri($"https://{host}/and-again")))
        {
        }

        sends.Count.Should().Be(3);
        recorder.Decisions.Should().HaveCount(3, "one send is one decision");
        recorder.Decisions.Should().OnlyContain(d => d.Site == "factory:x");
    }

    [Fact]
    public void AddedTwice_RegistersEachServiceOnce()
    {
        var services = new ServiceCollection();
        services.AddAshlarEgressGuard();
        services.AddAshlarEgressGuard();

        services.Where(d => d.ServiceType == typeof(IEgressGuard)).Should().ContainSingle()
            .Which.ImplementationInstance.Should().BeSameAs(EgressGuard.ProcessDefault);
        services.Where(d => d.ServiceType == typeof(EgressDecisionLoggerSubscription)).Should().ContainSingle()
            .Which.Lifetime.Should().Be(ServiceLifetime.Singleton);
        services.Where(d => d.ServiceType == typeof(IHostedService)
                && d.ImplementationType == typeof(EgressDecisionLoggerActivator))
            .Should().ContainSingle();
    }

    [Fact]
    public async Task TheUnnamedDefaultClient_IsGuarded_WithSiteFactoryColon()
    {
        var host = UniqueHost();
        var recorder = new DecisionRecorder(host);
        using var subscription = EgressDecisionLog.Subscribe(recorder);
        var sends = new SendCounter();

        var services = new ServiceCollection();
        services.AddAshlarEgressGuard();
        services.ConfigureHttpClientDefaults(b => b.ConfigurePrimaryHttpMessageHandler(() => new StubPrimaryHandler(sends)));

        using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient();

        using (await client.GetAsync(new Uri($"https://{host}/")))
        {
        }

        sends.Count.Should().Be(1);
        var decision = recorder.Decisions.Should().ContainSingle().Which;
        decision.Site.Should().Be("factory:", "the unnamed default client's name is empty");
        decision.Family.Should().Be(EgressFamilies.HttpFactory);
    }

    [Fact]
    public async Task ClientsTheHostRegistersItself_BeforeAndAfter_AreGuarded_OutsideTheHostsOwnHandlers()
    {
        var host = UniqueHost();
        var recorder = new DecisionRecorder(host);
        using var subscription = EgressDecisionLog.Subscribe(recorder);
        var sends = new SendCounter();
        var seenByHostHandler = new ConcurrentQueue<int>();

        var services = new ServiceCollection();

        // Registered before the guard, with a handler of the host's own.
        services.AddHttpClient("host-own")
            .AddHttpMessageHandler(() => new HostOwnHandler(recorder, seenByHostHandler))
            .ConfigurePrimaryHttpMessageHandler(() => new StubPrimaryHandler(sends));

        services.AddAshlarEgressGuard();

        // Registered after the guard, as a typed client.
        var typed = services.AddHttpClient<HostTypedClient>()
            .ConfigurePrimaryHttpMessageHandler(() => new StubPrimaryHandler(sends));

        using var provider = services.BuildServiceProvider();

        using (var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("host-own"))
        using (await client.GetAsync(new Uri($"https://{host}/own")))
        {
        }

        recorder.Decisions.Should().ContainSingle().Which.Site.Should().Be("factory:host-own");
        seenByHostHandler.Should().Equal(new[] { 1 },
            "the guard handler is the outermost additional handler, so its decision is recorded before the host's "
            + "own handler runs");

        var typedClient = provider.GetRequiredService<HostTypedClient>();
        using (await typedClient.Http.GetAsync(new Uri($"https://{host}/typed")))
        {
        }

        sends.Count.Should().Be(2);
        recorder.Decisions.Should().HaveCount(2);
        recorder.Decisions[1].Site.Should().Be("factory:" + typed.Name);
        typed.Name.Should().Contain(nameof(HostTypedClient));
    }

    [Fact]
    public async Task AHostWideHandler_RegisteredBeforeTheGuard_RunsInsideTheGuard()
    {
        var host = UniqueHost();
        var recorder = new DecisionRecorder(host);
        using var subscription = EgressDecisionLog.Subscribe(recorder);
        var seenByHostHandler = new ConcurrentQueue<int>();

        var services = new ServiceCollection();

        // A host default that adds a handler to every client, registered first: defaults run in registration order,
        // so only an insert at the front, not an append, puts the guard outside it.
        services.ConfigureHttpClientDefaults(b => b
            .AddHttpMessageHandler(() => new HostOwnHandler(recorder, seenByHostHandler))
            .ConfigurePrimaryHttpMessageHandler(() => new StubPrimaryHandler(new SendCounter())));
        services.AddAshlarEgressGuard();

        using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("x");
        using (await client.GetAsync(new Uri($"https://{host}/")))
        {
        }

        recorder.Decisions.Should().ContainSingle().Which.Site.Should().Be("factory:x");
        seenByHostHandler.Should().Equal(new[] { 1 },
            "the guard handler is inserted first, so a retry or resilience handler the host adds runs inside it and one "
            + "send is one decision");
    }

    [Fact]
    public async Task AGuardTheHostRegistered_IsTheOneTheHandlerUses()
    {
        var host = UniqueHost();
        var hostGuard = new CountingGuard(host);

        var services = new ServiceCollection();
        services.AddSingleton<IEgressGuard>(hostGuard);
        services.AddAshlarEgressGuard();
        services.AddHttpClient("x").ConfigurePrimaryHttpMessageHandler(() => new StubPrimaryHandler(new SendCounter()));

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IEgressGuard>().Should().BeSameAs(hostGuard, "TryAdd keeps the host's guard");

        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("x");
        using (await client.GetAsync(new Uri($"https://{host}/")))
        {
        }

        hostGuard.Requests.Should().ContainSingle().Which.Site.Should().Be("factory:x");
    }

    [Fact]
    public async Task TheLoggerEntry_IsDebug_Event7300_WithSiteFamilyAndDestination_AndNoPathQueryHeaderOrBody()
    {
        var host = UniqueHost();
        var capture = new CapturingLoggerProvider();

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Debug).AddProvider(capture));
        services.AddAshlarEgressGuard();
        services.AddHttpClient("x").ConfigurePrimaryHttpMessageHandler(() => new StubPrimaryHandler(new SendCounter()));

        // No hosted service is started: building the client's handlers is what activates the subscription.
        using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("x");

        using (var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri($"https://usersecret:pwsecret@{host}:8443/pathsecret/x?querysecret=1#fragsecret")))
        {
            request.Headers.Add("X-Egress-Twin", "headersecret");
            request.Content = new StringContent("bodysecret");
            using var response = await client.SendAsync(request);
        }

        var entry = capture.For(host).Should().ContainSingle("one send through a guarded client is one log entry").Which;
        entry.Level.Should().Be(LogLevel.Debug, "Information would change the stdout of CLI verbs that log to the console");
        entry.EventId.Id.Should().Be(7300);
        entry.EventId.Name.Should().Be("EgressDecision");
        entry.Property("{OriginalFormat}").Should().Be(ExpectedTemplate);

        entry.Message.Should().StartWith("Egress would-refuse ", "with no subject the current label is SystemHigh");
        entry.Message.Should().Contain(" site=factory:x ");
        entry.Message.Should().Contain(" family=http.factory ");
        entry.Message.Should().Contain($" dest=https://{host}:8443 ");
        entry.Message.Should().Contain(" class=NetworkExport ");
        entry.Message.Should().Contain(" current=SystemHigh (no-subject) ");
        entry.Message.Should().Contain(" reason=SystemHighData ");
        entry.Message.Should().Contain(" fault=none ");
        entry.Property("Site").Should().Be("factory:x");
        entry.Property("Family").Should().Be("http.factory");
        entry.Property("Destination").Should().Be($"https://{host}:8443");

        var propertyTexts = entry.State.Select(p => p.Value?.ToString() ?? string.Empty).ToList();
        foreach (var secret in new[] { "usersecret", "pwsecret", "pathsecret", "querysecret", "fragsecret", "headersecret", "bodysecret", "X-Egress-Twin" })
        {
            entry.Message.Should().NotContain(secret);
            propertyTexts.Should().NotContain(text => text.Contains(secret, StringComparison.Ordinal),
                "no structured property may carry {0}", secret);
        }
    }

    [Fact]
    public async Task AtTheDefaultInformationLevel_NothingIsLogged()
    {
        var host = UniqueHost();
        var capture = new CapturingLoggerProvider();
        var recorder = new DecisionRecorder(host);
        using var subscription = EgressDecisionLog.Subscribe(recorder);

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(capture));
        services.AddAshlarEgressGuard();
        services.AddHttpClient("x").ConfigurePrimaryHttpMessageHandler(() => new StubPrimaryHandler(new SendCounter()));

        using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("x");
        using (await client.GetAsync(new Uri($"https://{host}/")))
        {
        }

        recorder.Decisions.Should().ContainSingle("the decision is made and recorded either way");
        capture.For(host).Should().BeEmpty("decisions are Debug, which is off unless an operator turns Ashlar.Egress on");
    }

    [Fact]
    public void TheHostedActivator_SubscribesTheLogger_WithoutAnyHttpClient()
    {
        var capture = new CapturingLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Debug).AddProvider(capture));
        services.AddAshlarEgressGuard();

        using var provider = services.BuildServiceProvider();

        var before = UniqueHost();
        Decide(before);
        capture.For(before).Should().BeEmpty("nothing has built the subscription yet");

        provider.GetServices<IHostedService>().OfType<EgressDecisionLoggerActivator>().Should().ContainSingle();

        var after = UniqueHost();
        Decide(after);
        capture.For(after).Should().ContainSingle("the hosted activator built the subscription")
            .Which.Message.Should().Contain(" site=EG-TWIN-01 ");
    }

    [Fact]
    public void DisposingTheProvider_Unsubscribes()
    {
        var capture = new CapturingLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Debug).AddProvider(capture));
        services.AddAshlarEgressGuard();

        // Disposed explicitly below; the using also disposes it if an assertion fails first, so a failed run
        // cannot leave a subscription on the process-wide log. A second Dispose does nothing.
        using var provider = services.BuildServiceProvider();
        _ = provider.GetRequiredService<EgressDecisionLoggerSubscription>();

        var whileLive = UniqueHost();
        Decide(whileLive);
        capture.For(whileLive).Should().ContainSingle("positive control: the subscription is live");

        provider.Dispose();

        var afterDispose = UniqueHost();
        Decide(afterDispose);
        capture.For(afterDispose).Should().BeEmpty("the provider disposed the subscription, which unsubscribed its sink");
    }

    private static string UniqueHost() => $"egress-twin-{Guid.NewGuid():N}.example";

    private static void Decide(string host) =>
        EgressGuard.ProcessDefault.Evaluate(new EgressRequest(EgressFamilies.Http, "EG-TWIN-01", new Uri($"https://{host}/")));

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

    private sealed class SendCounter
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public void Increment() => Interlocked.Increment(ref _count);
    }

    /// <summary>The primary handler: answers 204 to every send, so nothing leaves the process.</summary>
    private sealed class StubPrimaryHandler : HttpMessageHandler
    {
        private readonly SendCounter _sends;

        public StubPrimaryHandler(SendCounter sends) => _sends = sends;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _sends.Increment();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent) { RequestMessage = request });
        }
    }

    /// <summary>A handler of the host's own: notes how many decisions were already recorded when it runs.</summary>
    private sealed class HostOwnHandler : DelegatingHandler
    {
        private readonly DecisionRecorder _recorder;
        private readonly ConcurrentQueue<int> _seen;

        public HostOwnHandler(DecisionRecorder recorder, ConcurrentQueue<int> seen)
        {
            _recorder = recorder;
            _seen = seen;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _seen.Enqueue(_recorder.Decisions.Count);
            return base.SendAsync(request, cancellationToken);
        }
    }

    /// <summary>A typed client of the host's own.</summary>
    internal sealed class HostTypedClient
    {
        public HostTypedClient(HttpClient http) => Http = http;

        public HttpClient Http { get; }
    }

    /// <summary>A guard the host registered: keeps the requests for this test's host, then decides as the default does.</summary>
    private sealed class CountingGuard : IEgressGuard
    {
        private readonly string _host;
        private readonly ConcurrentQueue<EgressRequest> _requests = new();

        public CountingGuard(string host) => _host = host;

        public IReadOnlyList<EgressRequest> Requests => _requests.ToArray();

        public EgressDecision Evaluate(EgressRequest request)
        {
            if (request.Destination?.Host.Contains(_host, StringComparison.Ordinal) == true)
                _requests.Enqueue(request);
            return EgressGuard.ProcessDefault.Evaluate(request);
        }
    }

    private sealed record LogEntry(
        string Category,
        LogLevel Level,
        EventId EventId,
        string Message,
        IReadOnlyList<KeyValuePair<string, object?>> State)
    {
        public object? Property(string name) => State.FirstOrDefault(p => p.Key == name).Value;
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<LogEntry> _entries = new();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _entries);

        /// <summary>The <c>Ashlar.Egress</c> entries whose message names <paramref name="host"/>.</summary>
        public IReadOnlyList<LogEntry> For(string host) => _entries
            .Where(e => e.Category == Category && e.Message.Contains(host, StringComparison.Ordinal))
            .ToList();

        public void Dispose()
        {
        }

        private sealed class CapturingLogger : ILogger
        {
            private readonly string _category;
            private readonly ConcurrentQueue<LogEntry> _entries;

            public CapturingLogger(string category, ConcurrentQueue<LogEntry> entries)
            {
                _category = category;
                _entries = entries;
            }

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                var properties = state as IReadOnlyList<KeyValuePair<string, object?>>;
                _entries.Enqueue(new LogEntry(
                    _category,
                    logLevel,
                    eventId,
                    formatter(state, exception),
                    properties is null ? Array.Empty<KeyValuePair<string, object?>>() : properties.ToArray()));
            }
        }
    }
}
