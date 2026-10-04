namespace Ashlar.Abstractions.Security.Egress;

/// <summary>
/// The HTTP adapter: a <see cref="DelegatingHandler"/> that evaluates the requests it sends with the egress guard,
/// then sends them unchanged. The remarks say which sends are covered on which asset.
/// </summary>
/// <remarks>
/// <para><b>Report-only guarantees.</b> It evaluates every <c>SendAsync</c> (and, on net8.0 and later, every
/// <c>Send</c>) exactly once, before handing the request on. It never reads or buffers
/// <see cref="HttpRequestMessage.Content"/>, never reads, adds or changes a header, and returns the inner handler's
/// response instance unchanged. A guard that throws (a custom <see cref="IEgressGuard"/> may) is swallowed and
/// counted, so it never reaches the caller; an exception from the inner handler is not caught.</para>
/// <para><b>Not covered on the netstandard2.0 asset.</b> That asset, which .NET 5-7 apps resolve, cannot override the
/// synchronous <c>HttpMessageHandler.Send</c>: netstandard2.0 has no such member. A synchronous
/// <c>HttpClient.Send</c> or <c>HttpMessageInvoker.Send</c> therefore reaches the inherited
/// <c>DelegatingHandler.Send</c>, which forwards to the inner handler without calling <c>SendAsync</c>, and is not
/// evaluated there. Only <c>SendAsync</c> is covered on that asset. This gap must be closed before SPEC-007 PR 4
/// enforces.</para>
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

    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Report(request);
        return base.SendAsync(request, cancellationToken);
    }

#if NET5_0_OR_GREATER
    /// <inheritdoc />
    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Report(request);
        return base.Send(request, cancellationToken);
    }
#endif

    // Reads RequestUri and nothing else from the request. A null request is not a send: the base handler refuses it.
    private void Report(HttpRequestMessage? request)
    {
        if (request is null)
            return;

        try
        {
            var uri = request.RequestUri;
            var egress = uri is null
                ? new EgressRequest(_family, _site, EgressDestinations.UnknownDestination)
                : new EgressRequest(_family, _site, uri);
            _ = (_guard ?? EgressGuard.ProcessDefault).Evaluate(egress);
        }
#pragma warning disable CA1031 // A custom guard may throw; report-only means the send goes ahead unchanged, so the fault is counted.
        catch (Exception)
#pragma warning restore CA1031
        {
            Interlocked.Increment(ref _guardFaults);
        }
    }
}
