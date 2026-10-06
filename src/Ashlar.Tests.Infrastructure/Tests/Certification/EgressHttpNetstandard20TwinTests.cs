using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using System.Net;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.Loader;
using System.Runtime.Versioning;
using Ashlar.Abstractions.Security.Egress;
using FluentAssertions;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>
/// SPEC-007 PR 4.2, behavioural twin of the synchronous-<c>Send</c> gap on the netstandard2.0 asset of
/// Ashlar.Abstractions (design §2.6, gap 1, option C). Run on this runtime, that asset refuses a synchronous
/// <c>Send</c> through an <see cref="EgressHttp"/> client or handler before anything is sent, still evaluates every
/// <c>SendAsync</c> exactly once, publishes no record when a client or handler is built (no egress has happened
/// then), and refuses to build the factory handler, whose synchronous <c>Send</c> it cannot cover.
/// </summary>
/// <remarks>
/// <para><b>Why a second load context.</b> This project binds the net8.0 or net10.0 build of Ashlar.Abstractions,
/// where the guard handler overrides <c>Send</c>, so <see cref="EgressHttpHandlerTwinTests"/> never runs the
/// netstandard2.0 asset that .NET 5-7 apps resolve. The csproj builds that asset (a <c>ProjectReference</c> with
/// <c>SetTargetFramework</c> netstandard2.0 and <c>ReferenceOutputAssembly</c> false) and copies it to
/// <c>abstractions-netstandard2.0/</c> in the output. <see cref="Netstandard20"/> loads it into an
/// <see cref="AssemblyLoadContext"/> of its own, together with a second copy of this test assembly, so that
/// <see cref="Netstandard20Driver"/>'s calls to <see cref="EgressHttp"/> bind to the netstandard2.0 build. Running
/// netstandard2.0 IL on this runtime reproduces the .NET 5-7 behaviour, which depends on which overrides were
/// compiled, not on the runtime's version. Everything else resolves from the default context, so
/// <see cref="HttpClient"/>, <see cref="HttpMessageHandler"/> and the exceptions are the same types on both sides.
/// The first test proves the harness is not vacuous: the driver's <see cref="EgressHttp"/> is the netstandard2.0
/// build, and this runtime has the synchronous <c>Send</c> the gap is about.</para>
/// <para><b>Process-global state.</b> The isolated copy has statics of its own (the decision log, the sequence, the
/// fault counters), and only this class reaches them; xUnit runs one class's tests one at a time. Its event source is
/// the one exception: it is a second <c>Ashlar-Egress</c> source in the process, so the harness initializes the guard's
/// own source first (<c>InitializeTheGuardsEventSource</c> says why). No test sends to
/// the network: every send goes to a stub, and the clients built over a real <see cref="HttpClientHandler"/> are
/// inspected and disposed without sending. No test reads or writes an environment variable: every guard gets its
/// profile through the constructor, and the clients built with the process-default guard assert nothing about the
/// profile.</para>
/// </remarks>
[Trait("Category", "Certification")]
public sealed class EgressHttpNetstandard20TwinTests
{
    private const string Netstandard20Framework = ".NETStandard,Version=v2.0";
    private const string GuardHandlerTypeName = "EgressGuardHandler";
    private const string HopTypeName = "SynchronousSendRefusedOnNetstandard20Asset";
    private const string FollowerTypeName = "EgressRedirectHandler";
    private const string EventSourceName = "Ashlar-Egress";

    // ---------------------------------------------------------------------------------------------------------
    // The harness runs the netstandard2.0 build
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_harness_runs_the_netstandard20_build_of_Abstractions_in_a_load_context_of_its_own()
    {
        var isolated = Netstandard20.Abstractions;

        isolated.Should().NotBeSameAs(typeof(EgressHttp).Assembly);
        AssemblyLoadContext.GetLoadContext(isolated).Should().NotBeSameAs(AssemblyLoadContext.Default);
        FrameworkOf(isolated).Should().Be(Netstandard20Framework);
        FrameworkOf(typeof(EgressHttp).Assembly).Should().StartWith(".NETCoreApp", "this project binds the net8.0 or later asset");
        Netstandard20.Run<string>(nameof(Netstandard20Driver.BoundFramework)).Should().Be(
            Netstandard20Framework, "the driver's calls to EgressHttp must bind to the netstandard2.0 build, or nothing here tests it");
        RuntimeHasSynchronousSend().Should().BeTrue(
            "the gap exists only on a runtime whose HttpMessageHandler has a synchronous Send, which netstandard2.0 cannot override");
    }

    [Fact]
    public void With_the_isolated_copy_loaded_the_guards_own_event_source_still_writes_every_decision()
    {
        // The isolated copy publishes, so its own Ashlar-Egress source exists beside the guard's.
        Netstandard20.Run<string[]>(nameof(Netstandard20Driver.PublishOneDecision), NewSite()).Should().ContainSingle(
            "the isolated copy must publish, or its event source never exists and this proves nothing");
        var site = NewSite();
        using var listener = new GuardEventListener();

        _ = new EgressGuard("full").Evaluate(new EgressRequest(EgressFamilies.Http, site, new Uri("https://es.example/")));

        listener.Sites.Should().ContainSingle(s => s == site,
            "two event sources named {0} may not both be enabled, so the harness must keep the guard's own working: "
            + "EgressGuardDecisionTests and every operator listener read it",
            EventSourceName);
    }

    // ---------------------------------------------------------------------------------------------------------
    // A synchronous Send is refused before it is sent; SendAsync is still evaluated exactly once
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_synchronous_Send_through_CreateClient_is_refused_before_anything_is_sent_and_SendAsync_is_still_evaluated_once()
    {
        var observed = await Netstandard20.RunAsync(nameof(Netstandard20Driver.SendThroughClientAsync), NewSite());

        AssertSynchronousSendRefused(observed);
        AssertAsynchronousSendEvaluatedOnce(observed, callersToken: false);
    }

    [Fact]
    public async Task A_synchronous_Send_through_a_Wrap_handler_is_refused_before_anything_is_sent_and_SendAsync_is_still_evaluated_once()
    {
        var observed = await Netstandard20.RunAsync(nameof(Netstandard20Driver.SendThroughWrapAsync), NewSite());

        AssertSynchronousSendRefused(observed);
        AssertAsynchronousSendEvaluatedOnce(observed, callersToken: true);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Building a client records nothing: no egress has happened yet
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void Building_a_client_or_handler_publishes_no_record()
    {
        var observed = Netstandard20.Run<IReadOnlyDictionary<string, object?>>(nameof(Netstandard20Driver.BuildEveryShape), NewSite());

        const string because = "a record published when a client is built would tell an operator that an egress was refused "
            + "when none was attempted, and a client that only calls SendAsync would leave one too; the refusal is the "
            + "runtime's NotSupportedException, to the caller";
        ((string[])observed["records.client-process-default"]!).Should().BeEmpty(because);
        ((string[])observed["records.client-with-guard"]!).Should().BeEmpty(because);
        ((string[])observed["records.client-over-inner"]!).Should().BeEmpty(because);
        ((string[])observed["records.wrap"]!).Should().BeEmpty(because);
    }

    // ---------------------------------------------------------------------------------------------------------
    // The shapes: every EgressHttp client and Wrap handler has the hop under the guard; the factory handler is refused
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void Every_client_and_Wrap_handler_puts_the_hop_between_the_guard_and_the_inner_handler()
    {
        var observed = Netstandard20.Run<IReadOnlyDictionary<string, object?>>(nameof(Netstandard20Driver.BuildEveryShape), NewSite());

        // The chain is walked through DelegatingHandler.InnerHandler and, at the hop, which is not a DelegatingHandler,
        // through its internal Inner: the step the redirect follower's walker (SPEC-007 PR 4.3) takes there.
        // SPEC-007 PR 4.3 puts the redirect follower directly above the primary handler, under the hop.
        var overHttpClientHandler = GuardHandlerTypeName + " > " + HopTypeName + " > " + FollowerTypeName + " > " + nameof(HttpClientHandler);
        var overStub = GuardHandlerTypeName + " > " + HopTypeName + " > " + FollowerTypeName + " > StubHandler";
        observed["shape.client-process-default"].Should().Be(overHttpClientHandler, "CreateClient(family, site) is the guard over the hop over an HttpClientHandler");
        observed["shape.client-with-guard"].Should().Be(overHttpClientHandler);
        observed["shape.client-over-inner"].Should().Be(overStub);
        observed["shape.wrap"].Should().Be(overStub);
        observed["inner.client-over-inner"].Should().Be(true, "the chain ends at the caller's own handler instance");
        observed["inner.wrap"].Should().Be(true);
    }

    [Fact]
    public async Task On_the_netstandard20_asset_the_follower_is_found_through_the_hop_and_follows_a_same_origin_redirect()
    {
        var observed = await Netstandard20.RunAsync(nameof(Netstandard20Driver.FollowThroughTheHopAsync), NewSite());

        // SPEC-007 PR 4.3: a handler Wrap built, wrapped again, already has a follower under its hop. The walker that
        // places the follower steps through the hop's Inner, finds it, and adds no second one.
        observed["nested.followers"].Should().Be(1, "the walker steps through the hop (observed: {0})", observed["nested.shape"]);
        observed["follow.status"].Should().Be(200, "the follower under the hop follows a same-origin redirect");
        ((string[])observed["follow.paths"]!).Should().Equal("/a", "/a2");
        ((string[])observed["follow.records"]!).Should().Equal(
            ["fault=- reason=SystemHighData allowed=False destination=https://async.example mode=report profile=full family=http"],
            "one origin, one decision");
    }

    [Fact]
    public void The_client_and_the_Wrap_handler_still_own_the_inner_handler_through_the_hop()
    {
        var observed = Netstandard20.Run<IReadOnlyDictionary<string, object?>>(nameof(Netstandard20Driver.DisposeEveryOwner), NewSite());

        observed["client.innerDisposed"].Should().Be(true, "disposing the client disposes the inner handler, as new HttpClient(inner) does");
        observed["wrap.innerDisposed"].Should().Be(true, "disposing the wrapped handler disposes the inner one");
    }

    [Fact]
    public void CreateDelegatingHandler_is_refused_on_a_runtime_with_a_synchronous_Send_after_its_arguments_are_checked()
    {
        var observed = Netstandard20.Run<IReadOnlyDictionary<string, object?>>(nameof(Netstandard20Driver.BuildFactoryHandler), "factory:" + NewSite());

        observed["exception"].Should().Be(
            typeof(PlatformNotSupportedException).FullName,
            "a factory pipeline sets the inner handler itself, so the hop cannot be put under the guard and a synchronous Send would go out unevaluated");
        ((string)observed["message"]!).Should().Contain("net8.0", "the refusal says which asset covers synchronous sends");
        observed["nullFamily"].Should().Be(typeof(ArgumentNullException).FullName + ":family", "a missing argument is still reported first");
        observed["nullSite"].Should().Be(typeof(ArgumentNullException).FullName + ":site");
    }

    // ---------------------------------------------------------------------------------------------------------
    // Assertions shared by the client and the Wrap handler
    // ---------------------------------------------------------------------------------------------------------

    private static void AssertSynchronousSendRefused(IReadOnlyDictionary<string, object?> observed)
    {
        // What the synchronous Send did, in one line, so a failure shows it (before the fix: "returned 202, 1 inner
        // send(s), 0 decision(s)", the gap itself).
        var syncSend = $"{observed["sync.message"]}, {observed["sync.innerSends"]} inner send(s), "
            + $"{((string[])observed["sync.records"]!).Length} decision(s)";

        observed["sync.exception"].Should().Be(
            typeof(NotSupportedException).FullName,
            "on the netstandard2.0 asset a synchronous Send reaches the hop, which has no Send override, so the runtime "
            + "refuses it (observed: {0})",
            syncSend);
        observed["sync.innerSends"].Should().Be(0, "nothing reaches the inner handler: the refusal is before anything is sent");
        ((string[])observed["sync.records"]!).Should().BeEmpty("the refused send is not evaluated: no Ashlar code is on that path");
        ((string)observed["sync.message"]!).Should().Contain(HopTypeName, "the runtime's message names the hop, so the refusal explains itself");
        ((string[])observed["build.records"]!).Should().BeEmpty("building the client is not an egress, so it publishes nothing");
    }

    /// <param name="observed">The driver's observations.</param>
    /// <param name="callersToken">Whether the inner handler must get the caller's own token: through an
    /// <see cref="HttpMessageInvoker"/> it does; <see cref="HttpClient"/> links it with its timeout first.</param>
    private static void AssertAsynchronousSendEvaluatedOnce(IReadOnlyDictionary<string, object?> observed, bool callersToken)
    {
        ((string[])observed["async.records"]!).Should().Equal(
            ["fault=- reason=SystemHighData allowed=False destination=https://async.example mode=report profile=full family=http"],
            "SendAsync is still evaluated exactly once, before the send, and its request reaches the inner handler");
        observed["async.innerSends"].Should().Be(1);
        observed["async.sameResponse"].Should().Be(true, "the response is the inner handler's own instance");
        observed["async.sameRequest"].Should().Be(true, "the inner handler gets the caller's request instance");
        if (callersToken)
            observed["async.sameToken"].Should().Be(true, "the caller's token reaches the inner handler");
    }

    // ---------------------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------------------

    private static string NewSite() => "twin:ns20:" + Guid.NewGuid().ToString("N");

    private static string? FrameworkOf(Assembly assembly) => assembly.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName;

    private static bool RuntimeHasSynchronousSend() =>
        typeof(HttpMessageHandler).GetMethod(
            "Send",
            BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null,
            [typeof(HttpRequestMessage), typeof(CancellationToken)],
            modifiers: null) is not null;

    /// <summary>
    /// Listens to the guard's own <c>Ashlar-Egress</c> source, never the isolated copy's, and keeps the site of every
    /// <c>Decision</c> event it writes.
    /// </summary>
    private sealed class GuardEventListener : EventListener
    {
        // A field initializer runs before the base constructor, which may already call OnEventSourceCreated.
        private readonly ConcurrentQueue<string> _sites = new();

        public IReadOnlyList<string> Sites => _sites.ToArray();

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (IsTheGuardsOwn(eventSource))
                EnableEvents(eventSource, EventLevel.Informational);
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (eventData.EventSource is not { } source || !IsTheGuardsOwn(source) || eventData.EventId != 1)
                return;

            var index = eventData.PayloadNames?.IndexOf("site") ?? -1;
            if (index >= 0 && eventData.Payload is { } payload && index < payload.Count && payload[index] is string site)
                _sites.Enqueue(site);
        }

        private static bool IsTheGuardsOwn(EventSource eventSource) =>
            string.Equals(eventSource.Name, EventSourceName, StringComparison.Ordinal)
            && ReferenceEquals(eventSource.GetType().Assembly, typeof(EgressHttp).Assembly);
    }

    /// <summary>
    /// The netstandard2.0 build of Ashlar.Abstractions and a second copy of this test assembly, in one
    /// <see cref="AssemblyLoadContext"/>. Only the name <c>Ashlar.Abstractions</c> is resolved there; every other
    /// reference falls through to the default context.
    /// </summary>
    internal static class Netstandard20
    {
        internal const string Folder = "abstractions-netstandard2.0";

        private static readonly Lazy<IsolatedContext> Loaded = new(() =>
        {
            InitializeTheGuardsEventSource();
            return new IsolatedContext(
                Path.Combine(AppContext.BaseDirectory, Folder, "Ashlar.Abstractions.dll"),
                typeof(Netstandard20).Assembly.Location);
        });

        internal static Assembly Abstractions => Loaded.Value.Abstractions;

        internal static T Run<T>(string method, params object?[] arguments)
        {
            var driver = Loaded.Value.TestCopy.GetType(typeof(Netstandard20Driver).FullName!, throwOnError: true)!;
            var target = driver.GetMethod(method, BindingFlags.Public | BindingFlags.Static);
            target.Should().NotBeNull("the driver has a public static method {0}", method);
            try
            {
                return (T)target!.Invoke(null, arguments)!;
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
        }

        internal static Task<IReadOnlyDictionary<string, object?>> RunAsync(string method, params object?[] arguments) =>
            Run<Task<IReadOnlyDictionary<string, object?>>>(method, arguments);

        /// <summary>
        /// Enables the guard's own <c>Ashlar-Egress</c> event source once, before the isolated copy, which has a source of
        /// the same name, is loaded.
        /// </summary>
        /// <remarks>
        /// .NET builds a source's event descriptors when it is first enabled, and refuses to while another live source
        /// has the same name and GUID (<c>EventSource_EventSourceGuidInUse</c>, unless the
        /// <c>System.Diagnostics.Tracing.EventSource.AllowDuplicateSourceNames</c> switch is set). The refusal is only
        /// reported out of band: the source simply stays disabled. So if the guard's source had not been enabled before
        /// the isolated copy published its first record, no listener could enable it afterwards, and
        /// <see cref="EgressGuardDecisionTests"/>' event-source tests failed (18 of 20 runs beside this class, 0 of 20
        /// without it). Built here first, its descriptors stay; the isolated copy's source is the one that cannot be
        /// enabled, which nothing needs: this class reads that copy's records through a sink.
        /// </remarks>
        private static void InitializeTheGuardsEventSource()
        {
            var log = typeof(EgressHttp).Assembly
                .GetType("Ashlar.Abstractions.Security.Egress.EgressEventSource", throwOnError: true)!
                .GetField("Log", BindingFlags.NonPublic | BindingFlags.Static)?
                .GetValue(null) as EventSource;
            log.Should().NotBeNull("the guard publishes every decision to its Ashlar-Egress event source");
            log!.Name.Should().Be(EventSourceName);

            using var enabler = new GuardEventListener();
            log.IsEnabled().Should().BeTrue(
                "the guard's own event source must be initialized before a second source named {0} exists", EventSourceName);
        }

        private sealed class IsolatedContext : AssemblyLoadContext
        {
            internal IsolatedContext(string abstractionsPath, string testAssemblyPath)
                : base("ashlar-abstractions-netstandard2.0", isCollectible: false)
            {
                File.Exists(abstractionsPath).Should().BeTrue(
                    "the csproj copies the netstandard2.0 build of Ashlar.Abstractions to {0}", abstractionsPath);
                Abstractions = LoadFromAssemblyPath(abstractionsPath);
                TestCopy = LoadFromAssemblyPath(testAssemblyPath);
            }

            internal Assembly Abstractions { get; }

            internal Assembly TestCopy { get; }

            protected override Assembly? Load(AssemblyName assemblyName) =>
                string.Equals(assemblyName.Name, "Ashlar.Abstractions", StringComparison.Ordinal) ? Abstractions : null;
        }
    }
}

/// <summary>
/// Runs only inside <see cref="EgressHttpNetstandard20TwinTests.Netstandard20"/>'s load context, where its
/// references to Ashlar.Abstractions bind to the netstandard2.0 build. It returns values whose types come from the
/// default context (strings, ints, bools, string arrays and dictionaries of them), so they mean the same on both sides
/// of the boundary; it asserts nothing itself.
/// </summary>
public static class Netstandard20Driver
{
    /// <summary>A guard with an explicit profile, so no decision here reads the environment.</summary>
    private static readonly EgressGuard Guard = new("full");

    public static string BoundFramework() =>
        typeof(EgressHttp).Assembly.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName ?? "(none)";

    public static async Task<IReadOnlyDictionary<string, object?>> SendThroughClientAsync(string site)
    {
        var observed = new Dictionary<string, object?>(StringComparer.Ordinal);
        var sink = new RecordSink(site);
        using var subscription = EgressDecisionLog.Subscribe(sink);
        var inner = new StubHandler();
        using var client = EgressHttp.CreateClient(inner, EgressFamilies.Http, site, Guard);
        observed["build.records"] = sink.Take();

        using (var request = new HttpRequestMessage(HttpMethod.Get, "https://sync.example/PATHMARK"))
            Capture(observed, () => client.Send(request), inner, sink);

        await SendAsyncOnce(observed, (request, token) => client.SendAsync(request, token), inner, sink);
        return observed;
    }

    public static async Task<IReadOnlyDictionary<string, object?>> SendThroughWrapAsync(string site)
    {
        var observed = new Dictionary<string, object?>(StringComparer.Ordinal);
        var sink = new RecordSink(site);
        using var subscription = EgressDecisionLog.Subscribe(sink);
        var inner = new StubHandler();
        using var invoker = new HttpMessageInvoker(EgressHttp.Wrap(inner, EgressFamilies.Http, site, Guard));
        observed["build.records"] = sink.Take();

        using (var request = new HttpRequestMessage(HttpMethod.Get, "https://sync.example/PATHMARK"))
            Capture(observed, () => invoker.Send(request, CancellationToken.None), inner, sink);

        await SendAsyncOnce(observed, (request, token) => invoker.SendAsync(request, token), inner, sink);
        return observed;
    }

    public static string[] PublishOneDecision(string site)
    {
        var sink = new RecordSink(site);
        using var subscription = EgressDecisionLog.Subscribe(sink);
        _ = Guard.Evaluate(new EgressRequest(EgressFamilies.Http, site, new Uri("https://es.example/")));
        return sink.Take();
    }

    public static IReadOnlyDictionary<string, object?> BuildEveryShape(string site)
    {
        var observed = new Dictionary<string, object?>(StringComparer.Ordinal);
        var sink = new RecordSink(site);
        using var subscription = EgressDecisionLog.Subscribe(sink);

        using (var client = EgressHttp.CreateClient(EgressFamilies.Mcp, site))
        {
            observed["records.client-process-default"] = sink.Take();
            observed["shape.client-process-default"] = Shape(HandlerOf(client));
        }

        using (var client = EgressHttp.CreateClient(EgressFamilies.Mcp, site, Guard))
        {
            observed["records.client-with-guard"] = sink.Take();
            observed["shape.client-with-guard"] = Shape(HandlerOf(client));
        }

        var clientInner = new StubHandler();
        using (var client = EgressHttp.CreateClient(clientInner, EgressFamilies.Mcp, site, Guard))
        {
            observed["records.client-over-inner"] = sink.Take();
            observed["shape.client-over-inner"] = Shape(HandlerOf(client));
            observed["inner.client-over-inner"] = ReferenceEquals(Chain(HandlerOf(client)).Last(), clientInner);
        }

        var wrapInner = new StubHandler();
        using (var handler = EgressHttp.Wrap(wrapInner, EgressFamilies.Grpc, site, Guard))
        {
            observed["records.wrap"] = sink.Take();
            observed["shape.wrap"] = Shape(handler);
            observed["inner.wrap"] = ReferenceEquals(Chain(handler).Last(), wrapInner);
        }

        return observed;
    }

    public static async Task<IReadOnlyDictionary<string, object?>> FollowThroughTheHopAsync(string site)
    {
        var observed = new Dictionary<string, object?>(StringComparer.Ordinal);
        var sink = new RecordSink(site);
        using var subscription = EgressDecisionLog.Subscribe(sink);

        using (var nested = EgressHttp.Wrap(EgressHttp.Wrap(new StubHandler(), EgressFamilies.Grpc, site, Guard), EgressFamilies.Grpc, site, Guard))
        {
            observed["nested.shape"] = Shape(nested);
            observed["nested.followers"] = Chain(nested).Count(h => h.GetType().Name == "EgressRedirectHandler");
        }

        var stub = new SameOriginRedirectingHandler();
        using (var client = EgressHttp.CreateClient(stub, EgressFamilies.Http, site, Guard))
        using (var response = await client.GetAsync(new Uri("https://async.example/a")))
        {
            observed["follow.status"] = (int)response.StatusCode;
            observed["follow.paths"] = stub.Paths;
        }

        observed["follow.records"] = sink.Take();
        return observed;
    }

    public static IReadOnlyDictionary<string, object?> DisposeEveryOwner(string site)
    {
        var clientInner = new StubHandler();
        EgressHttp.CreateClient(clientInner, EgressFamilies.MeshPull, site, Guard).Dispose();
        var wrapInner = new StubHandler();
        EgressHttp.Wrap(wrapInner, EgressFamilies.Grpc, site, Guard).Dispose();

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["client.innerDisposed"] = clientInner.Disposed,
            ["wrap.innerDisposed"] = wrapInner.Disposed,
        };
    }

    public static IReadOnlyDictionary<string, object?> BuildFactoryHandler(string site)
    {
        var observed = new Dictionary<string, object?>(StringComparer.Ordinal);
        try
        {
            using var handler = EgressHttp.CreateDelegatingHandler(EgressFamilies.HttpFactory, site, Guard);
            observed["exception"] = null;
            observed["message"] = string.Empty;
        }
        catch (Exception ex)
        {
            observed["exception"] = ex.GetType().FullName;
            observed["message"] = ex.Message;
        }

        observed["nullFamily"] = ArgumentFailure(() => EgressHttp.CreateDelegatingHandler(null!, site, Guard));
        observed["nullSite"] = ArgumentFailure(() => EgressHttp.CreateDelegatingHandler(EgressFamilies.HttpFactory, null!, Guard));
        return observed;
    }

    private static void Capture(Dictionary<string, object?> observed, Func<HttpResponseMessage> send, StubHandler inner, RecordSink sink)
    {
        var sendsBefore = inner.Sends;
        try
        {
            using var response = send();
            observed["sync.exception"] = null;
            observed["sync.message"] = "returned " + (int)response.StatusCode;
        }
        catch (Exception ex)
        {
            observed["sync.exception"] = ex.GetType().FullName;
            observed["sync.message"] = ex.Message;
        }

        observed["sync.innerSends"] = inner.Sends - sendsBefore;
        observed["sync.records"] = sink.Take();
    }

    private static async Task SendAsyncOnce(
        Dictionary<string, object?> observed,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> sendAsync,
        StubHandler inner,
        RecordSink sink)
    {
        var sendsBefore = inner.Sends;
        using var cancellation = new CancellationTokenSource();
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://async.example/PATHMARK");
        using var response = await sendAsync(request, cancellation.Token);

        observed["async.innerSends"] = inner.Sends - sendsBefore;
        observed["async.sameResponse"] = ReferenceEquals(response, inner.LastResponse);
        observed["async.sameRequest"] = ReferenceEquals(request, inner.LastRequest);
        observed["async.sameToken"] = inner.LastToken == cancellation.Token;
        observed["async.records"] = sink.Take();
    }

    private static string? ArgumentFailure(Func<DelegatingHandler> build)
    {
        try
        {
            build().Dispose();
            return null;
        }
        catch (ArgumentNullException ex)
        {
            return ex.GetType().FullName + ":" + ex.ParamName;
        }
    }

    private static string Shape(HttpMessageHandler outer) => string.Join(" > ", Chain(outer).Select(h => h.GetType().Name));

    /// <summary>
    /// The handlers from <paramref name="outer"/> inward: through <see cref="DelegatingHandler.InnerHandler"/>, and
    /// through the hop's internal <c>Inner</c>, which a walker of the chain needs because the hop is not a
    /// <see cref="DelegatingHandler"/>. At most ten, so a chain that loops still ends.
    /// </summary>
    private static List<HttpMessageHandler> Chain(HttpMessageHandler outer)
    {
        var chain = new List<HttpMessageHandler>();
        for (HttpMessageHandler? handler = outer; handler is not null && chain.Count < 10; handler = Next(handler))
            chain.Add(handler);
        return chain;
    }

    private static HttpMessageHandler? Next(HttpMessageHandler handler) =>
        handler switch
        {
            DelegatingHandler delegating => delegating.InnerHandler,
            _ when handler.GetType().Name == "SynchronousSendRefusedOnNetstandard20Asset" =>
                handler.GetType().GetProperty("Inner", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(handler) as HttpMessageHandler,
            _ => null,
        };

    /// <summary>The handler an <see cref="HttpMessageInvoker"/> sends through, which it keeps in a private field.</summary>
    private static HttpMessageHandler HandlerOf(HttpMessageInvoker invoker)
    {
        var field = typeof(HttpMessageInvoker)
            .GetFields(BindingFlags.NonPublic | BindingFlags.Instance)
            .Single(f => typeof(HttpMessageHandler).IsAssignableFrom(f.FieldType));
        return (HttpMessageHandler)field.GetValue(invoker)!;
    }

    /// <summary>Keeps the records published for one site, each as one line of text.</summary>
    private sealed class RecordSink(string site) : IEgressDecisionSink
    {
        private readonly ConcurrentQueue<string> _seen = new();

        public void Record(EgressDecision decision)
        {
            if (!string.Equals(decision.Site, site, StringComparison.Ordinal))
                return;

            _seen.Enqueue(
                $"fault={decision.Fault ?? "-"} reason={decision.Access.Reason} allowed={decision.Access.Allowed} "
                + $"destination={decision.Destination} mode={decision.Mode} profile={decision.Profile} family={decision.Family}");
        }

        /// <summary>The records seen since the last call.</summary>
        public string[] Take()
        {
            var taken = new List<string>();
            while (_seen.TryDequeue(out var line))
                taken.Add(line);
            return taken.ToArray();
        }
    }

    /// <summary>
    /// A primary of a type the follower knows (it derives from <see cref="HttpClientHandler"/>): <c>/a</c> answers 302 to
    /// <c>/a2</c> on the same origin, anything else 200. It never sends anything and never follows on its own.
    /// </summary>
    private sealed class SameOriginRedirectingHandler : HttpClientHandler
    {
        private readonly ConcurrentQueue<string> _paths = new();

        public string[] Paths => _paths.ToArray();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _paths.Enqueue(request.RequestUri!.AbsolutePath);
            var response = request.RequestUri.AbsolutePath == "/a"
                ? new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("/a2", UriKind.Relative) } }
                : new HttpResponseMessage(HttpStatusCode.OK);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }

    /// <summary>Answers every send, synchronous or not, with a fresh response and remembers what it was handed.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private int _sends;

        public int Sends => Volatile.Read(ref _sends);

        public HttpRequestMessage? LastRequest { get; private set; }

        public HttpResponseMessage? LastResponse { get; private set; }

        public CancellationToken LastToken { get; private set; }

        public bool Disposed { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Answer(request, cancellationToken));

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Answer(request, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }

        private HttpResponseMessage Answer(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _sends);
            LastRequest = request;
            LastToken = cancellationToken;
            LastResponse = new HttpResponseMessage(HttpStatusCode.Accepted) { RequestMessage = request };
            return LastResponse;
        }
    }
}
