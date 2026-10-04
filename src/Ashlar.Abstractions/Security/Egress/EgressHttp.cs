namespace Ashlar.Abstractions.Security.Egress;

/// <summary>
/// Builds HTTP clients and handlers whose every request is evaluated by the egress guard.
/// </summary>
/// <remarks>
/// <para>Each request through these is evaluated exactly once, before it is sent, and then sent unchanged: the
/// guard handler never reads the request content, never touches a header, and returns the inner response
/// instance. In SPEC-007 PR 3 the decision is recorded and nothing is refused.</para>
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
        return new HttpClient(new EgressGuardHandler(new HttpClientHandler(), family, site, guard));
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
        return new HttpClient(new EgressGuardHandler(inner, family, site, guard));
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
    public static HttpMessageHandler Wrap(HttpMessageHandler inner, string family, string site, IEgressGuard? guard = null)
    {
        SecurityGuard.ThrowIfNull(inner, nameof(inner));
        SecurityGuard.ThrowIfNull(family, nameof(family));
        SecurityGuard.ThrowIfNull(site, nameof(site));
        return new EgressGuardHandler(inner, family, site, guard);
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
    public static DelegatingHandler CreateDelegatingHandler(string family, string site, IEgressGuard? guard = null)
    {
        SecurityGuard.ThrowIfNull(family, nameof(family));
        SecurityGuard.ThrowIfNull(site, nameof(site));
        return new EgressGuardHandler(family, site, guard);
    }
}
