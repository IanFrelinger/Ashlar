namespace Ashlar.Abstractions.Security.Egress;

/// <summary>
/// Builds HTTP clients and handlers whose requests are evaluated by the egress guard.
/// </summary>
/// <remarks>
/// <para>Every <c>SendAsync</c> through these (and, on net8.0 and later, every <c>Send</c>) is evaluated before the
/// request is sent, once for each authority (scheme, host and port) it is sent to, and the request is then sent
/// unchanged: the guard handler never reads the request content, never touches a header, and returns the inner
/// response instance. The decision is recorded and the guard refuses nothing (SPEC-007 PR 3); the one refusal, below,
/// is the netstandard2.0 asset's, not the guard's.</para>
/// <para><b>Redirects</b> (SPEC-007 PR 4.3). The redirect follower goes directly above the primary handler at the end
/// of the inner handler's chain. A primary that is an <see cref="HttpClientHandler"/> (or a <c>SocketsHttpHandler</c>)
/// has its own <c>AllowAutoRedirect</c> turned off, and the follower follows as it would have, deciding each new
/// authority before the hop is sent, except that a redirect to another origin is returned to the caller, not
/// followed: these clients often carry an API key in a header, which a cross-origin hop would carry along. Each hop
/// clears <c>Authorization</c>. A primary of another type, or one with <c>Credentials</c>, is not changed, and a
/// redirect it follows on its own is decided after the send. A handler between the guard handler and the primary that
/// rewrites the request URI is decided again before the send. <b>Behaviour change:</b> the primary handler's
/// <c>AllowAutoRedirect</c> reads <see langword="false"/> after the client or handler is built.</para>
/// <para><b>Refused on the netstandard2.0 asset.</b> That asset, which .NET 5-7 apps resolve, cannot override the
/// synchronous <c>HttpMessageHandler.Send</c>, so it cannot evaluate a synchronous send. On a runtime that has one
/// (.NET 5 and later), a synchronous <c>HttpClient.Send</c> or <c>HttpMessageInvoker.Send</c> through a client or
/// handler built here is refused with <see cref="NotSupportedException"/> before anything is sent. No Ashlar code runs
/// on that path, so the caller's exception is the refusal's only trace: no decision is published.
/// <see cref="CreateDelegatingHandler"/> throws <see cref="PlatformNotSupportedException"/> there. On .NET Framework,
/// classic Mono and Unity, which have no synchronous <c>Send</c>, nothing is refused. The hop is there on every
/// runtime that binds this asset, though, so on all of them it is the one change: each <c>SendAsync</c> takes one
/// extra in-process step, and the <see cref="DelegatingHandler.InnerHandler"/> of a <see cref="Wrap"/> handler is the
/// hop, not the inner handler. The hop is not a <see cref="DelegatingHandler"/>, so code outside Ashlar that walks the
/// chain through <c>InnerHandler</c> (to find the primary handler's type, for example) stops at it; the redirect
/// follower's own walker steps through it. Synchronous sends are evaluated on the net8.0 and later assets
/// (<c>docs/SdkCompatibilityPolicy.md</c>).</para>
/// <para>A <see langword="null"/> guard means <see cref="EgressGuard.ProcessDefault"/>, resolved at each send.</para>
/// <para>The family and site are checked here, when the client is built, so a send never fails on them.</para>
/// <para>Of the <c>CreateClient</c> overloads only the one with the most parameters has an optional parameter, as
/// the public-API analyzer requires; <c>CreateClient(family, site)</c> and <c>CreateClient(family, site, guard)</c>
/// cover the shorter calls.</para>
/// </remarks>
public static class EgressHttp
{
    /// <summary>
    /// A client over a new <see cref="HttpClientHandler"/> (the handler <c>new HttpClient()</c> uses), with
    /// <see cref="EgressGuard.ProcessDefault"/> in front of it. Disposing the client disposes both handlers.
    /// </summary>
    /// <param name="family">The path family, one of <see cref="EgressFamilies"/>.</param>
    /// <param name="site">The inventoried site id, for example <c>EG-MDL-03</c>.</param>
    /// <returns>The client.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="family"/> or <paramref name="site"/> is
    /// <see langword="null"/>.</exception>
    public static HttpClient CreateClient(string family, string site) => CreateClient(family, site, guard: null);

    /// <summary>
    /// A client over a new <see cref="HttpClientHandler"/> (the handler <c>new HttpClient()</c> uses), with the
    /// guard in front of it. Disposing the client disposes both handlers.
    /// </summary>
    /// <param name="family">The path family, one of <see cref="EgressFamilies"/>.</param>
    /// <param name="site">The inventoried site id, for example <c>EG-MDL-03</c>.</param>
    /// <param name="guard">The guard, or <see langword="null"/> for <see cref="EgressGuard.ProcessDefault"/>.</param>
    /// <returns>The client.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="family"/> or <paramref name="site"/> is
    /// <see langword="null"/>.</exception>
    public static HttpClient CreateClient(string family, string site, IEgressGuard? guard)
    {
        SecurityGuard.ThrowIfNull(family, nameof(family));
        SecurityGuard.ThrowIfNull(site, nameof(site));
#pragma warning disable CA2000 // Ownership passes inward: the client disposes the guard handler, which disposes the HttpClientHandler; the outer constructors throw only on a null argument.
        return new HttpClient(Guarded(new HttpClientHandler(), family, site, guard));
#pragma warning restore CA2000
    }

    /// <summary>
    /// A client over <paramref name="inner"/>, with the guard in front of it. The client owns
    /// <paramref name="inner"/>, as <c>new HttpClient(inner)</c> does: disposing the client disposes it.
    /// </summary>
    /// <param name="inner">The handler that sends the request.</param>
    /// <param name="family">The path family, one of <see cref="EgressFamilies"/>.</param>
    /// <param name="site">The inventoried site id.</param>
    /// <param name="guard">The guard, or <see langword="null"/> for <see cref="EgressGuard.ProcessDefault"/>.</param>
    /// <returns>The client.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="inner"/>, <paramref name="family"/> or
    /// <paramref name="site"/> is <see langword="null"/>.</exception>
    public static HttpClient CreateClient(HttpMessageHandler inner, string family, string site, IEgressGuard? guard = null)
    {
        SecurityGuard.ThrowIfNull(inner, nameof(inner));
        SecurityGuard.ThrowIfNull(family, nameof(family));
        SecurityGuard.ThrowIfNull(site, nameof(site));
#pragma warning disable CA2000 // Ownership passes inward: the client disposes the guard handler, which disposes inner; both constructors throw only on a null argument.
        return new HttpClient(Guarded(inner, family, site, guard));
#pragma warning restore CA2000
    }

    /// <summary>
    /// <paramref name="inner"/> with the guard in front of it, for code that builds its own client or channel. The
    /// returned handler owns <paramref name="inner"/>: disposing it disposes <paramref name="inner"/>.
    /// </summary>
    /// <param name="inner">The handler that sends the request.</param>
    /// <param name="family">The path family, one of <see cref="EgressFamilies"/>.</param>
    /// <param name="site">The inventoried site id.</param>
    /// <param name="guard">The guard, or <see langword="null"/> for <see cref="EgressGuard.ProcessDefault"/>.</param>
    /// <returns>The guarded handler.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="inner"/>, <paramref name="family"/> or
    /// <paramref name="site"/> is <see langword="null"/>.</exception>
    /// <remarks>The returned handler is a <see cref="DelegatingHandler"/>. Its <see cref="DelegatingHandler.InnerHandler"/>
    /// is <paramref name="inner"/> when <paramref name="inner"/> is itself a chain of delegating handlers (the redirect
    /// follower is then put directly above the chain's primary handler), and otherwise the redirect follower over
    /// <paramref name="inner"/>. On the netstandard2.0 asset, on every runtime, it is the hop that refuses a synchronous
    /// <c>Send</c>, and the hop is not a <see cref="DelegatingHandler"/>: code outside Ashlar that walks the chain
    /// through <c>InnerHandler</c> stops at it and does not see <paramref name="inner"/>.
    /// <c>InnerHandler</c> can be set until the first send. Do not replace it: that voids what this method guarantees.
    /// After a replacement on the netstandard2.0 asset a synchronous <c>Send</c> goes out unevaluated; on every asset
    /// redirects and rewritten URIs below the replacement are no longer decided before they are sent, and the handler
    /// neither sends through nor owns <paramref name="inner"/>.
    /// </remarks>
    public static HttpMessageHandler Wrap(HttpMessageHandler inner, string family, string site, IEgressGuard? guard = null)
    {
        SecurityGuard.ThrowIfNull(inner, nameof(inner));
        SecurityGuard.ThrowIfNull(family, nameof(family));
        SecurityGuard.ThrowIfNull(site, nameof(site));
        return Guarded(inner, family, site, guard);
    }

    /// <summary>
    /// A guard handler with no inner handler, for an <c>IHttpClientFactory</c> pipeline
    /// (<c>AddHttpMessageHandler</c>), which sets the inner handler.
    /// </summary>
    /// <param name="family">The path family, one of <see cref="EgressFamilies"/>.</param>
    /// <param name="site">The site id, for example <c>factory:&lt;client name&gt;</c>.</param>
    /// <param name="guard">The guard, or <see langword="null"/> for <see cref="EgressGuard.ProcessDefault"/>.</param>
    /// <returns>The guarded delegating handler.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="family"/> or <paramref name="site"/> is
    /// <see langword="null"/>.</exception>
    /// <exception cref="PlatformNotSupportedException">On the netstandard2.0 asset, on a runtime that has a synchronous
    /// <c>HttpMessageHandler.Send</c> (.NET 5 to 7 bind this asset): the pipeline sets this handler's inner handler
    /// itself, so a synchronous send through it could not be refused and would go out unevaluated.</exception>
    public static DelegatingHandler CreateDelegatingHandler(string family, string site, IEgressGuard? guard = null)
    {
        SecurityGuard.ThrowIfNull(family, nameof(family));
        SecurityGuard.ThrowIfNull(site, nameof(site));
#if NETSTANDARD2_0
        if (SynchronousSendRefusedOnNetstandard20Asset.RuntimeHasSynchronousSend)
            throw new PlatformNotSupportedException(SynchronousSendRefusedOnNetstandard20Asset.FactoryHandlerRefusal);
#endif
        return new EgressGuardHandler(family, site, guard);
    }

    /// <summary>
    /// The guard handler over <paramref name="inner"/>, which it then owns. On the netstandard2.0 asset the
    /// synchronous-send hop goes between them, so a synchronous <c>Send</c> is refused instead of going out
    /// unevaluated (see <c>SynchronousSendRefusedOnNetstandard20Asset</c>).
    /// </summary>
    private static EgressGuardHandler Guarded(HttpMessageHandler inner, string family, string site, IEgressGuard? guard)
    {
        // The redirect follower goes directly above the primary handler at the end of inner's chain (SPEC-007 PR 4.3),
        // and does not follow a redirect to another origin (P2, default D33).
#pragma warning disable CA2000 // Ownership passes inward: the guard handler (or the hop) owns what Install returns, which owns inner.
        var followed = EgressRedirectHandler.Install(inner, followsAcrossOrigins: false, family, site, guard);
#pragma warning restore CA2000
#if NETSTANDARD2_0
#pragma warning disable CA2000 // Ownership passes inward: the guard handler owns the hop, which owns inner; the guard handler's constructor throws only on a null argument.
        return new EgressGuardHandler(SynchronousSendRefusedOnNetstandard20Asset.Over(followed), family, site, guard);
#pragma warning restore CA2000
#else
        return new EgressGuardHandler(followed, family, site, guard);
#endif
    }
}
