namespace Ashlar.Abstractions.Security.Egress;

/// <summary>
/// The HTTP adapter: a <see cref="DelegatingHandler"/> that evaluates every request it sends with the egress guard,
/// then sends it unchanged.
/// </summary>
/// <remarks>
/// <para><b>Report-only guarantees.</b> It evaluates exactly once per send, before handing the request on. It never
/// reads or buffers <see cref="HttpRequestMessage.Content"/>, never reads, adds or changes a header, and returns the
/// inner handler's response instance unchanged. A guard that throws (a custom <see cref="IEgressGuard"/> may) is
/// swallowed and counted, so it never reaches the caller; an exception from the inner handler is not caught.</para>
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
