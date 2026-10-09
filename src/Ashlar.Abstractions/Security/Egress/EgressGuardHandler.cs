namespace Ashlar.Abstractions.Security.Egress;

/// <summary>
/// The HTTP adapter: a <see cref="DelegatingHandler"/> that evaluates the requests it sends with the egress guard,
/// then sends them unchanged only in report mode or when allowed. The remarks describe asset coverage.
/// </summary>
/// <remarks>
/// <para>Under enforcement a refusal faults <c>SendAsync</c> or throws from <c>Send</c> before the inner
/// handler runs. Host guard faults are recorded as NoDecision and use the process mode.</para>
/// <para><b>Report-only guarantees.</b> It evaluates every <c>SendAsync</c> (and, on net8.0 and later, every
/// <c>Send</c>) before handing the request on. A redirect hop, or a URI rewritten before the primary sends, is
/// evaluated again. It never reads or buffers
/// <see cref="HttpRequestMessage.Content"/>, never reads, adds or changes a header, and returns the inner handler's
/// response instance unchanged. A guard that throws (a custom <see cref="IEgressGuard"/> may) is swallowed and
/// counted, so it never reaches the caller; an exception from the inner handler is not caught. After the response,
/// if <see cref="HttpResponseMessage.RequestMessage"/> names a different authority than the one evaluated for this
/// send, that authority is evaluated too: a primary Ashlar could not stop from following has already sent, and the
/// warning says the body may already have gone. Report mode does not throw and does not set
/// <see cref="EgressDecision.Refused"/>.</para>
/// <para><b>Refused, not evaluated, on the netstandard2.0 asset.</b> That asset, which .NET 5-7 apps resolve, cannot
/// override the synchronous <c>HttpMessageHandler.Send</c>: netstandard2.0 has no such member. A synchronous
/// <c>HttpClient.Send</c> or <c>HttpMessageInvoker.Send</c> therefore reaches the inherited
/// <c>DelegatingHandler.Send</c>, which forwards to the inner handler's <c>Send</c> without calling
/// <c>SendAsync</c>. So <see cref="EgressHttp"/> puts <c>SynchronousSendRefusedOnNetstandard20Asset</c> under this
/// handler on that asset, and the runtime refuses that <c>Send</c> before anything is sent;
/// <see cref="EgressHttp.CreateDelegatingHandler"/>, whose inner handler a factory pipeline sets, is refused there
/// instead. Only <c>SendAsync</c> is evaluated on that asset.</para>
/// <para>The decision records the request URI's scheme, host and port only, or <c>unknown</c> when the request has
/// no URI. A <see langword="null"/> guard means <see cref="EgressGuard.ProcessDefault"/>, read at each send.</para>
/// </remarks>
internal sealed class EgressGuardHandler : DelegatingHandler
{
    private static long _guardFaults;

    private readonly string _family;
    private readonly string _site;
    private readonly IEgressGuard? _guard;

    /// <summary>A handler with no inner handler yet, for an <c>IHttpClientFactory</c> pipeline to complete.</summary>
    internal EgressGuardHandler(string family, string site, IEgressGuard? guard)
    {
        _family = family;
        _site = site;
        _guard = guard;
    }

    /// <summary>A handler that sends through <paramref name="innerHandler"/>, which it then owns.</summary>
    internal EgressGuardHandler(HttpMessageHandler innerHandler, string family, string site, IEgressGuard? guard)
        : base(innerHandler)
    {
        _family = family;
        _site = site;
        _guard = guard;
    }

    /// <summary>How many times a guard threw while a request was being evaluated; each throw was swallowed.</summary>
    internal static long GuardFaults => Interlocked.Read(ref _guardFaults);

    /// <summary>
    /// Called when a primary followed a URI the guard had not evaluated. Null on an <see cref="EgressHttp"/> client,
    /// which traces the warning; the factory filter sets it to the <c>Ashlar.Egress</c> logger.
    /// </summary>
    internal Action<string>? UnmediatedRedirectWarning { get; set; }

    /// <summary>Preserves P2 when this EgressHttp guard surrounds an existing factory P1 follower.</summary>
    internal bool RestrictRedirectsToSameOrigin { get; set; }

    /// <summary>Preserves this factory client's explicit no-follow policy through a shared redirect follower.</summary>
    internal bool NeverFollowRedirects { get; set; }

    /// <summary>Counts a guard fault swallowed by this handler or by the redirect handler under it.</summary>
    internal static void RecordGuardFault() => Interlocked.Increment(ref _guardFaults);

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Report(request);
        var send = base.SendAsync(request, cancellationToken);
        return await FinishAsync(request, send).ConfigureAwait(false);
    }

#if NET5_0_OR_GREATER
    /// <inheritdoc />
    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Report(request);
        var response = base.Send(request, cancellationToken);
        FinishSend(request, response);
        return response;
    }
#endif

    private async Task<HttpResponseMessage> FinishAsync(HttpRequestMessage request, Task<HttpResponseMessage> send)
    {
        var response = await send.ConfigureAwait(false);
        FinishSend(request, response);
        return response;
    }

    // Reads RequestUri and nothing else from the request. A null request is not a send: the base handler refuses it.
    private void Report(HttpRequestMessage? request)
    {
        if (request is null)
            return;

        if (RestrictRedirectsToSameOrigin)
            EgressEvaluatedAuthority.RequireSameOrigin(request);
        if (NeverFollowRedirects)
            EgressEvaluatedAuthority.RequireNoRedirects(request);

        Evaluate(request.RequestUri);

        EgressEvaluatedAuthority.Stamp(request, request.RequestUri);
    }

    private void FinishSend(HttpRequestMessage? request, HttpResponseMessage response) =>
        ReportIfResponseAuthorityDiffers(request, response);

    private void ReportIfResponseAuthorityDiffers(HttpRequestMessage? request, HttpResponseMessage response)
    {
        if (request is null || response.RequestMessage is null)
            return;

        var followed = response.RequestMessage.RequestUri;
        if (EgressEvaluatedAuthority.Matches(request, followed))
            return;

        try
        {
            Evaluate(followed);
        }
        catch (EgressRefusedException)
        {
            response.Dispose();
            throw;
        }
        finally
        {
            WarnUnmediatedRedirect(followed);
        }

        EgressEvaluatedAuthority.Stamp(request, followed);
    }

    private void WarnUnmediatedRedirect(Uri? followed)
    {
        var message = "The followed URI's authority " + EgressEvaluatedAuthority.Describe(followed)
            + " differs from the one evaluated before the send; the body may already have gone.";
        try
        {
            if (UnmediatedRedirectWarning is { } warn)
                warn(message);
            else
                System.Diagnostics.Trace.TraceWarning(message);
        }
#pragma warning disable CA1031 // A diagnostic callback cannot replace the policy refusal or change a response.
        catch (Exception)
#pragma warning restore CA1031
        {
            EgressDecisionLog.RecordSinkFault();
        }
    }

    private void Evaluate(Uri? uri)
    {
        var egress = uri is null
            ? new EgressRequest(_family, _site, EgressDestinations.UnknownDestination)
            : new EgressRequest(_family, _site, uri);
        EgressGuard.EvaluateForRoute(_guard ?? EgressGuard.ProcessDefault, egress).ThrowIfRefused();
    }
}
