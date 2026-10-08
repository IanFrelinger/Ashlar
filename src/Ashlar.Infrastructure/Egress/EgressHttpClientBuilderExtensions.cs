using Microsoft.Extensions.DependencyInjection;

namespace Ashlar.Infrastructure.Egress;

/// <summary>
/// Per-client redirect settings for <see cref="IHttpClientFactory"/> clients under the egress guard (SPEC-007 PR 4.3).
/// </summary>
public static class EgressHttpClientBuilderExtensions
{
    /// <summary>
    /// A client over a known factory primary never follows a redirect: a 3xx response is returned to the caller.
    /// Unknown primary types keep their own behaviour. For a client whose destination
    /// was checked before the send (the SNS signing-certificate fetch checks the certificate URL's host), where any
    /// redirect would reach a host that was never checked.
    /// </summary>
    /// <param name="builder">The client's builder.</param>
    /// <returns><paramref name="builder"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// <para>It uses the configure-existing <c>ConfigurePrimaryHttpMessageHandler</c> overload, so the primary handler
    /// stays the one the factory and the client's other configuration chose; it only turns that handler's
    /// <c>AllowAutoRedirect</c> off. The egress redirect filter, which runs after every configuration action, reads that
    /// setting and so does not follow either. A named-client marker also makes Ashlar's follower return redirects
    /// when another client already mediated the shared primary, or when this client wraps an existing follower.
    /// The marker applies only to this client's requests; other clients keep their own redirect policy. A primary
    /// that is neither an <see cref="HttpClientHandler"/> nor a <see cref="SocketsHttpHandler"/> is left as it is.</para>
    /// </remarks>
    public static IHttpClientBuilder NeverFollowRedirects(this IHttpClientBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddSingleton(new EgressNoFollowClient(builder.Name));
        return builder.ConfigurePrimaryHttpMessageHandler(static (primary, _) =>
        {
            if (primary is HttpClientHandler handler)
                handler.AllowAutoRedirect = false;
            else if (primary is SocketsHttpHandler sockets)
                sockets.AllowAutoRedirect = false;
        });
    }
}

/// <summary>Names a factory client whose requests must never be followed by Ashlar's redirect mediator.</summary>
internal sealed record EgressNoFollowClient(string Name);
