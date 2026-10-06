using System.Net;
using System.Runtime.CompilerServices;

namespace Ashlar.Abstractions.Security.Egress;

/// <summary>
/// The redirect follower Ashlar puts directly above every primary handler it can reach (SPEC-007 PR 4.3, design §2.6
/// gap 2, R-b): it decides each authority a request is sent to before that send, and follows redirects itself, so
/// every hop of a redirect is decided before it is sent.
/// </summary>
/// <remarks>
/// <para><b>Why it exists.</b> A primary handler that follows redirects itself (<see cref="HttpClientHandler"/> and
/// <c>SocketsHttpHandler</c> do by default) sends every hop after the first without the guard handler above it ever
/// seeing it. <see cref="Install"/> turns the primary's own following off and puts this handler directly above it,
/// remembering what the primary would have done.</para>
/// <para><b>Per-authority decisions.</b> Before every send it hands on, including the first, it compares the
/// request's authority (scheme, host and port) with the last one decided for the request
/// (<see cref="EgressHopEvaluation"/>) and, on a difference, decides it with the guard handler's guard, family and
/// site. That covers redirect hops, and also a handler between the guard handler and this one that rewrites
/// <c>RequestUri</c> (service discovery, hedging, base-address rewriters). A request that no guard handler sent is
/// decided with this handler's own guard, family and site.</para>
/// <para><b>Following, at parity with the runtime.</b> Only when the primary would have followed, and up to its own
/// <c>MaxAutomaticRedirections</c>. The status codes are 300, 301, 302, 303, 307 and 308 with a <c>Location</c>; a
/// relative <c>Location</c> is resolved against the request URI, and the request's fragment is kept when the
/// <c>Location</c> has none. A redirect from <c>https</c> to anything else, or to a scheme other than <c>http</c> or
/// <c>https</c>, is not followed: the 3xx response is returned. On 301 and 302 a <c>POST</c>, and on 303 anything but
/// <c>GET</c> and <c>HEAD</c>, becomes a <c>GET</c> with no content. Each hop clears <c>Authorization</c>, disposes
/// the response it does not return, and sends the same <see cref="HttpRequestMessage"/>, whose <c>RequestUri</c> and
/// method it changes, as the runtime's own follower does. Past the limit, the last 3xx response is returned.</para>
/// <para><b>Across origins</b> (default D33). A client built by <see cref="EgressHttp"/> does not follow a redirect to
/// another origin (scheme, host or port): the 3xx response is returned to the caller, so the API-key headers those
/// clients set never follow it. A factory client follows it, and the new origin is decided before it is sent.</para>
/// <para><b>Report-only.</b> Each decision is recorded, and the guard refuses nothing yet: until SPEC-007 PR 4.7 no
/// route acts on <see cref="EgressDecision.Refused"/>, so a refused hop is still sent.</para>
/// <para>It overrides <c>Send</c> as well on net8.0 and later. On the netstandard2.0 asset it sits under the
/// synchronous-send hop, which refuses a synchronous <c>Send</c> before it gets here.</para>
/// </remarks>
internal sealed class EgressRedirectHandler : DelegatingHandler
{
    /// <summary>What the runtime's handlers default <c>MaxAutomaticRedirections</c> to.</summary>
    internal const int RuntimeDefaultMaxRedirects = 50;

    private static readonly ConditionalWeakTable<HttpMessageHandler, Original> Originals = new();

    private static readonly object Gate = new();

    private readonly string _family;
    private readonly string _site;
    private readonly IEgressGuard? _guard;

    private EgressRedirectHandler(
        HttpMessageHandler primary,
        bool follows,
        int maxRedirects,
        bool followsAcrossOrigins,
        string family,
        string site,
        IEgressGuard? guard)
        : base(primary)
    {
        Follows = follows;
        MaxRedirects = maxRedirects;
        FollowsAcrossOrigins = followsAcrossOrigins;
        _family = family;
        _site = site;
        _guard = guard;
    }

    /// <summary><see langword="true"/> when this handler follows redirects, because the primary would have.</summary>
    internal bool Follows { get; }

    /// <summary>The primary's own <c>MaxAutomaticRedirections</c>.</summary>
    internal int MaxRedirects { get; }

    /// <summary><see langword="true"/> for a factory client (P1); <see langword="false"/> for an <see cref="EgressHttp"/> client (P2).</summary>
    internal bool FollowsAcrossOrigins { get; }

    /// <summary>
    /// Puts the follower directly above the primary handler at the end of <paramref name="handler"/>'s chain, and
    /// returns the handler to send through in place of <paramref name="handler"/>.
    /// </summary>
    /// <remarks>
    /// <para>The chain is walked through <see cref="DelegatingHandler.InnerHandler"/> and, on the netstandard2.0 asset,
    /// through the synchronous-send hop's <c>Inner</c>. A chain that already holds a follower (a handler
    /// <see cref="EgressHttp.Wrap"/> built, wrapped again) is returned unchanged.</para>
    /// <para>A primary that is an <see cref="HttpClientHandler"/> or a <c>SocketsHttpHandler</c> has its
    /// <c>AllowAutoRedirect</c> turned off; its original setting and limit are kept per instance, so a primary shared
    /// by several clients is followed as it originally was every time. Any other primary (a test stub, an in-memory
    /// handler), a primary whose setting can no longer change because it has sent, and a primary with
    /// <c>Credentials</c> (the runtime does not send those to a redirect target, and a follower above the primary
    /// cannot stop it) is left as it is and not followed here: its own following, if any, is checked after the send
    /// by the guard handler.</para>
    /// <para>When the follower cannot go directly above the primary (the handler above it has already sent, so its
    /// <c>InnerHandler</c> is fixed, or the primary is under the netstandard2.0 hop), it goes above
    /// <paramref name="handler"/> instead and only decides; the primary is not changed.</para>
    /// </remarks>
    internal static HttpMessageHandler Install(
        HttpMessageHandler handler,
        bool followsAcrossOrigins,
        string family,
        string site,
        IEgressGuard? guard)
    {
        DelegatingHandler? parent = null;
        var current = handler;
        var underHop = false;
        for (var depth = 0; depth < 64; depth++)
        {
            if (current is EgressRedirectHandler)
                return handler;

            if (current is DelegatingHandler delegating && delegating.InnerHandler is { } next)
            {
                parent = delegating;
                current = next;
                continue;
            }

#if NETSTANDARD2_0
            if (current is SynchronousSendRefusedOnNetstandard20Asset hop)
            {
                underHop = true;
                current = hop.Inner;
                continue;
            }
#endif
            break;
        }

        if (underHop || (parent is not null && !CanReplaceInner(parent)))
            return Decider(handler, family, site, guard);

        var primary = current;
        var (follows, maxRedirects) = TurnOffFollowing(primary);
#pragma warning disable CA2000 // Ownership passes inward: the follower owns the primary, and the chain above (or the caller) owns the follower.
        var follower = new EgressRedirectHandler(primary, follows, maxRedirects, followsAcrossOrigins, family, site, guard);
#pragma warning restore CA2000
        if (parent is null)
            return follower;

        try
        {
            parent.InnerHandler = follower;
            return handler;
        }
        catch (InvalidOperationException)
        {
            // The handler above started between the check and the splice: put the primary back as it was.
            RestoreFollowing(primary);
            return Decider(handler, family, site, guard);
        }
    }

    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request is null)
            return base.SendAsync(request!, cancellationToken);

        Decide(request);
        return Follows ? FollowAsync(request, cancellationToken) : base.SendAsync(request, cancellationToken);
    }

#if NET5_0_OR_GREATER
    /// <inheritdoc />
    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request is null)
            return base.Send(request!, cancellationToken);

        Decide(request);
        var response = base.Send(request, cancellationToken);
        if (!Follows)
            return response;

        var hops = 0;
        while (NextHop(request, response, ref hops))
        {
            response = base.Send(request, cancellationToken);
        }

        return response;
    }
#endif

    private static EgressRedirectHandler Decider(HttpMessageHandler handler, string family, string site, IEgressGuard? guard) =>
        new(handler, follows: false, RuntimeDefaultMaxRedirects, followsAcrossOrigins: false, family, site, guard);

    // The setter throws once the handler has sent (or been disposed); assigning the same value is otherwise a no-op.
    private static bool CanReplaceInner(DelegatingHandler parent)
    {
        try
        {
            parent.InnerHandler = parent.InnerHandler!;
            return true;
        }
        catch (InvalidOperationException)
        {
            // Started, or disposed (ObjectDisposedException derives from it).
            return false;
        }
    }

    private static (bool Follows, int MaxRedirects) TurnOffFollowing(HttpMessageHandler primary)
    {
        lock (Gate)
        {
            if (Originals.TryGetValue(primary, out var known))
                return (known.Follows, known.MaxRedirects);

            bool follows;
            int maxRedirects;
            try
            {
                switch (primary)
                {
                    case HttpClientHandler client when client.Credentials is null || client.Credentials is CredentialCache:
                        follows = client.AllowAutoRedirect;
                        maxRedirects = client.MaxAutomaticRedirections;
                        if (follows)
                            client.AllowAutoRedirect = false;
                        break;
#if NET5_0_OR_GREATER
                    case SocketsHttpHandler sockets when sockets.Credentials is null || sockets.Credentials is CredentialCache:
                        follows = sockets.AllowAutoRedirect;
                        maxRedirects = sockets.MaxAutomaticRedirections;
                        if (follows)
                            sockets.AllowAutoRedirect = false;
                        break;
#endif
                    default:
                        return (false, RuntimeDefaultMaxRedirects);
                }
            }
            catch (InvalidOperationException)
            {
                // It has sent, or been disposed, so its setting is fixed: it keeps following on its own.
                return (false, RuntimeDefaultMaxRedirects);
            }
            catch (PlatformNotSupportedException)
            {
                return (false, RuntimeDefaultMaxRedirects);
            }

            Originals.Add(primary, new Original(follows, maxRedirects));
            return (follows, maxRedirects);
        }
    }

    private static void RestoreFollowing(HttpMessageHandler primary)
    {
        lock (Gate)
        {
            if (!Originals.TryGetValue(primary, out var original))
                return;

            Originals.Remove(primary);
            if (!original.Follows)
                return;

            try
            {
                switch (primary)
                {
                    case HttpClientHandler client:
                        client.AllowAutoRedirect = true;
                        break;
#if NET5_0_OR_GREATER
                    case SocketsHttpHandler sockets:
                        sockets.AllowAutoRedirect = true;
                        break;
#endif
                }
            }
            catch (InvalidOperationException)
            {
                // It has sent since: it stays as it is, and its sends are checked after they return.
            }
        }
    }

    private static Uri? RedirectTarget(Uri requestUri, HttpResponseMessage response)
    {
        switch ((int)response.StatusCode)
        {
            case 300:
            case 301:
            case 302:
            case 303:
            case 307:
            case 308:
                break;
            default:
                return null;
        }

        var location = response.Headers.Location;
        if (location is null)
            return null;

        if (!location.IsAbsoluteUri)
            location = new Uri(requestUri, location);

        // A Location without a fragment keeps the request's (RFC 7231 §7.1.2).
        var requestFragment = requestUri.Fragment;
        if (requestFragment.Length > 1 && string.IsNullOrEmpty(location.Fragment))
            location = new UriBuilder(location) { Fragment = requestFragment.Substring(1) }.Uri;

        var toHttps = string.Equals(location.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        var toHttp = string.Equals(location.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase);
        if (!toHttps && !toHttp)
            return null;

        // Never from a secure scheme to a non-secure one.
        if (!toHttps && IsSecure(requestUri.Scheme))
            return null;

        return location;
    }

    private static bool IsSecure(string scheme) =>
        string.Equals(scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
        || string.Equals(scheme, "wss", StringComparison.OrdinalIgnoreCase);

    private static bool RequiresGet(int status, HttpMethod method) => status switch
    {
        300 or 301 or 302 => method == HttpMethod.Post,
        303 => method != HttpMethod.Get && method != HttpMethod.Head,
        _ => false,
    };

    private static bool SameOrigin(Uri a, Uri b) =>
        string.Equals(EgressHopEvaluation.AuthorityOf(a), EgressHopEvaluation.AuthorityOf(b), StringComparison.Ordinal);

    private void Decide(HttpRequestMessage request) =>
        _ = EgressHopEvaluation.EnsureDecided(request, request.RequestUri, _guard, _family, _site);

    private async Task<HttpResponseMessage> FollowAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var hops = 0;
        while (NextHop(request, response, ref hops))
        {
            response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        return response;
    }

    // When the response is a redirect this handler follows, readies the request for the next hop (and decides it, if
    // its authority is new) and disposes the response; otherwise leaves both alone and returns false.
    private bool NextHop(HttpRequestMessage request, HttpResponseMessage response, ref int hops)
    {
        var requestUri = request.RequestUri;
        if (requestUri is null || !requestUri.IsAbsoluteUri)
            return false;

        var location = RedirectTarget(requestUri, response);
        if (location is null)
            return false;

        if (!FollowsAcrossOrigins && !SameOrigin(requestUri, location))
            return false;

        if (++hops > MaxRedirects)
            return false;

        var status = (int)response.StatusCode;
        response.Dispose();

        request.Headers.Authorization = null;
        request.RequestUri = location;
        if (RequiresGet(status, request.Method))
        {
            request.Method = HttpMethod.Get;
            request.Content = null;
            if (request.Headers.TransferEncodingChunked == true)
                request.Headers.TransferEncodingChunked = false;
        }

        Decide(request);
        return true;
    }

    private sealed class Original
    {
        public Original(bool follows, int maxRedirects)
        {
            Follows = follows;
            MaxRedirects = maxRedirects;
        }

        public bool Follows { get; }

        public int MaxRedirects { get; }
    }
}
