namespace Ashlar.Abstractions.Security.Egress;

/// <summary>
/// The HTTP adapter: a <see cref="DelegatingHandler"/> that evaluates the requests it sends with the egress guard,
/// then sends them unchanged. The remarks say which sends are covered on which asset.
/// </summary>
/// <remarks>
/// <para><b>Report-only guarantees.</b> It evaluates every <c>SendAsync</c> (and, on net8.0 and later, every
/// <c>Send</c>) once for the request's URI, before handing the request on, and once more for each other authority
/// (scheme, host and port) the request is sent to on its way: a redirect hop that <see cref="EgressRedirectHandler"/>
/// follows, or a handler between the two that rewrites <c>RequestUri</c>, is decided by the redirect handler before
/// it is sent, with this handler's guard, family and site (SPEC-007 PR 4.3). After the send, if the response's request
/// names an authority nobody decided (a primary handler that followed a redirect itself), that one is decided too,
/// late: the request has already gone. Retries of the same authority are not decided again. It never reads or buffers
/// <see cref="HttpRequestMessage.Content"/>, never reads, adds or changes a header, and returns the inner handler's
/// response instance unchanged; it notes what it decided in the request's options (<see cref="EgressHopEvaluation"/>).
/// A guard that throws (a custom <see cref="IEgressGuard"/> may) is swallowed and counted, so it never reaches the
/// caller; an exception from the inner handler is not caught.</para>
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
    internal static long GuardFaults => EgressHopEvaluation.GuardFaults;

    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // A null request is not a send: the base handler refuses it, synchronously, as before.
        if (request is null)
            return base.SendAsync(request!, cancellationToken);

        EgressHopEvaluation.Begin(request, _guard, _family, _site);
        return SendAndCheckAsync(request, cancellationToken);
    }

#if NET5_0_OR_GREATER
    /// <inheritdoc />
    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request is null)
            return base.Send(request!, cancellationToken);

        EgressHopEvaluation.Begin(request, _guard, _family, _site);
        var response = base.Send(request, cancellationToken);
        EgressHopEvaluation.CheckAfterSend(request, response);
        return response;
    }
#endif

    private async Task<HttpResponseMessage> SendAndCheckAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        EgressHopEvaluation.CheckAfterSend(request, response);
        return response;
    }
}
