using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;

namespace Ashlar.Abstractions.Security.Egress;

/// <summary>
/// Follows redirects for a primary whose own <c>AllowAutoRedirect</c> Ashlar has turned off, and re-evaluates a hop
/// whose authority is not the one the guard handler already recorded on the request.
/// </summary>
/// <remarks>
/// <para>It sits directly above the primary. On net8.0 and net10.0 the chain is the guard handler, then this
/// handler, then the primary. On netstandard2.0 the synchronous-send hop stays between the guard handler and this
/// one, and a synchronous <c>Send</c> throws from that hop before this handler runs.</para>
/// <para>It follows only when the primary originally had <c>AllowAutoRedirect</c> true, at most that primary's
/// <c>MaxAutomaticRedirections</c>. An <see cref="EgressHttp"/> client follows only the same origin (scheme, host
/// and port; P2). A factory client follows across origins (P1). Neither route follows a redirect from outside the
/// host boundary into it (including link-local addresses); that attempted hop is recorded and the 3xx is returned
/// (owner decision 2026-10-06, O2). HTTPS to HTTP, and any scheme other than http or https, is returned to the caller.
/// Evaluation records decisions; until PR 4.7 the route does not act on <see cref="EgressDecision.Refused"/>.</para>
/// </remarks>
internal sealed class EgressRedirectHandler : DelegatingHandler
{
    private readonly string _family;
    private readonly string _site;
    private readonly IEgressGuard? _guard;
    private readonly RedirectSettings _settings;

    internal EgressRedirectHandler(
        HttpMessageHandler inner,
        string family,
        string site,
        IEgressGuard? guard,
        RedirectSettings settings)
        : base(inner)
    {
        SecurityGuard.ThrowIfNull(inner, nameof(inner));
        SecurityGuard.ThrowIfNull(family, nameof(family));
        SecurityGuard.ThrowIfNull(site, nameof(site));
        SecurityGuard.ThrowIfNull(settings, nameof(settings));
        _family = family;
        _site = site;
        _guard = guard;
        _settings = settings;
    }

    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!_settings.Follow)
        {
            EnsureEvaluated(request);
            return base.SendAsync(request, cancellationToken);
        }

        return FollowAsync(request, cancellationToken);
    }

#if NET5_0_OR_GREATER
    /// <inheritdoc />
    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!_settings.Follow)
        {
            EnsureEvaluated(request);
            return base.Send(request, cancellationToken);
        }

        return Follow(request, cancellationToken);
    }
#endif

    private async Task<HttpResponseMessage> FollowAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        EnsureEvaluated(request);
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var redirects = 0;
        while (TryGetRedirect(request.RequestUri, response, out var location))
        {
            redirects++;
            if (redirects > _settings.MaxAutomaticRedirections)
                break;

            if (cancellationToken.IsCancellationRequested)
            {
                response.Dispose();
                cancellationToken.ThrowIfCancellationRequested();
            }

            var status = response.StatusCode;
            response.Dispose();
            ApplyRedirect(request, status, location);
            EvaluateHop(request);
            response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        return response;
    }

#if NET5_0_OR_GREATER
    private HttpResponseMessage Follow(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        EnsureEvaluated(request);
        var response = base.Send(request, cancellationToken);
        var redirects = 0;
        while (TryGetRedirect(request.RequestUri, response, out var location))
        {
            redirects++;
            if (redirects > _settings.MaxAutomaticRedirections)
                break;

            if (cancellationToken.IsCancellationRequested)
            {
                response.Dispose();
                cancellationToken.ThrowIfCancellationRequested();
            }

            var status = response.StatusCode;
            response.Dispose();
            ApplyRedirect(request, status, location);
            EvaluateHop(request);
            response = base.Send(request, cancellationToken);
        }

        return response;
    }
#endif

    private void EnsureEvaluated(HttpRequestMessage request)
    {
        if (request is null)
            return;

        if (EgressEvaluatedAuthority.Matches(request))
            return;

        try
        {
            Evaluate(request.RequestUri);
        }
#pragma warning disable CA1031 // A custom guard may throw; report-only means the send goes ahead, so the fault is counted.
        catch (Exception)
#pragma warning restore CA1031
        {
            EgressGuardHandler.RecordGuardFault();
        }

        EgressEvaluatedAuthority.Stamp(request, request.RequestUri);
    }

    /// <summary>A followed hop is its own decision, including one that stays on the same authority.</summary>
    private void EvaluateHop(HttpRequestMessage request)
    {
        try
        {
            Evaluate(request.RequestUri);
        }
#pragma warning disable CA1031 // A custom guard may throw; report-only means the send goes ahead, so the fault is counted.
        catch (Exception)
#pragma warning restore CA1031
        {
            EgressGuardHandler.RecordGuardFault();
        }

        EgressEvaluatedAuthority.Stamp(request, request.RequestUri);
    }

    private void Evaluate(Uri? uri)
    {
        var egress = uri is null
            ? new EgressRequest(_family, _site, EgressDestinations.UnknownDestination)
            : new EgressRequest(_family, _site, uri);
        _ = (_guard ?? EgressGuard.ProcessDefault).Evaluate(egress);
    }

    private bool TryGetRedirect(Uri? requestUri, HttpResponseMessage response, out Uri location)
    {
        location = null!;
        if (requestUri is null || !requestUri.IsAbsoluteUri || !IsRedirect(response.StatusCode))
            return false;

        var header = response.Headers.Location;
        if (header is null)
            return false;

        if (!header.IsAbsoluteUri)
            header = new Uri(requestUri, header);

        var requestFragment = requestUri.Fragment;
        if (!string.IsNullOrEmpty(requestFragment) && string.IsNullOrEmpty(header.Fragment))
            header = new UriBuilder(header) { Fragment = requestFragment }.Uri;

        // O2 precedes the scheme/P2 checks: unix/npipe and an HTTPS-to-loopback HTTP attempt must be recorded too.
        if (!InsideHostBoundary(requestUri) && InsideHostBoundary(header))
        {
            RecordUnfollowed(header);
            return false;
        }

        if (!IsHttpOrHttps(header))
            return false;

        if (IsHttpsToHttp(requestUri, header))
            return false;

        if (!_settings.FollowCrossHost && !EgressEvaluatedAuthority.SameOrigin(requestUri, header))
            return false;

        location = header;
        return true;
    }

    private static void ApplyRedirect(HttpRequestMessage request, HttpStatusCode status, Uri location)
    {
        request.Headers.Authorization = null;
        request.RequestUri = location;
        if (!RequestRequiresForceGet(status, request.Method))
            return;

        request.Method = HttpMethod.Get;
        request.Content = null;
        if (request.Headers.TransferEncodingChunked == true)
            request.Headers.TransferEncodingChunked = false;
    }

    private const HttpStatusCode StatusPermanentRedirect = (HttpStatusCode)308;

    private static bool IsRedirect(HttpStatusCode status) => status is
        HttpStatusCode.MultipleChoices or
        HttpStatusCode.Moved or
        HttpStatusCode.Found or
        HttpStatusCode.SeeOther or
        HttpStatusCode.TemporaryRedirect or
        StatusPermanentRedirect;

    private static bool IsHttpOrHttps(Uri uri) =>
        string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
        || string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);

    private static bool IsHttpsToHttp(Uri from, Uri to) =>
        string.Equals(from.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
        && string.Equals(to.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase);

    private static bool InsideHostBoundary(Uri uri) =>
        EgressDestinations.IsInsideHost(uri) || EgressDestinations.IsLinkLocalHost(uri.Host);

    // An attempted inward hop gets a record without stamping the request: the response still belongs to the
    // original authority, and the post-send check must not mistake that original response for another redirect.
    private void RecordUnfollowed(Uri destination)
    {
        try
        {
            Evaluate(destination);
        }
#pragma warning disable CA1031 // The inward redirect remains unfollowed even if a custom reporting guard throws.
        catch (Exception)
#pragma warning restore CA1031
        {
            EgressGuardHandler.RecordGuardFault();
        }
    }

    private static bool RequestRequiresForceGet(HttpStatusCode status, HttpMethod method)
    {
        switch (status)
        {
            case HttpStatusCode.MultipleChoices:
            case HttpStatusCode.Moved:
            case HttpStatusCode.Found:
                return method == HttpMethod.Post;
            case HttpStatusCode.SeeOther:
                return method != HttpMethod.Get && method != HttpMethod.Head;
            default:
                return false;
        }
    }
}

/// <summary>What a primary's redirect settings were before Ashlar turned <c>AllowAutoRedirect</c> off.</summary>
internal sealed class RedirectSettings
{
    internal RedirectSettings(bool follow, int maxAutomaticRedirections, bool followCrossHost)
    {
        Follow = follow;
        MaxAutomaticRedirections = maxAutomaticRedirections;
        FollowCrossHost = followCrossHost;
    }

    internal bool Follow { get; }

    internal int MaxAutomaticRedirections { get; }

    internal bool FollowCrossHost { get; }
}

/// <summary>Remembers a primary's redirect settings and splices <see cref="EgressRedirectHandler"/> above it.</summary>
internal static class EgressRedirects
{
    private static readonly ConditionalWeakTable<HttpMessageHandler, RedirectSettings> Memory = new();
    private static readonly object Gate = new();

    internal static HttpMessageHandler InsertAbovePrimary(
        HttpMessageHandler inner,
        string family,
        string site,
        IEgressGuard? guard,
        bool followCrossHost)
    {
        DelegatingHandler? parent = null;
        var current = inner;
        var underHop = false;
        for (var depth = 0; depth < 64; depth++)
        {
            if (current is EgressRedirectHandler)
                return inner;

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

        // The hop owns an immutable invoker. A started parent cannot be spliced either. Keep such a chain intact;
        // the decider and the guard's post-send check record it without taking over the primary's redirects.
        if (underHop || (parent is not null && !CanReplaceInner(parent)))
            return Decider(inner, family, site, guard, followCrossHost);

        var settings = RememberAndDisable(current, followCrossHost);
#pragma warning disable CA2000 // Ownership passes to the caller or to the parent handler's InnerHandler.
        var redirect = new EgressRedirectHandler(current, family, site, guard, settings);
#pragma warning restore CA2000
        if (parent is null)
            return redirect;

        try
        {
            parent.InnerHandler = redirect;
            return inner;
        }
        catch (InvalidOperationException)
        {
            // The parent started between the probe and the splice. Do not re-enable a primary that another wrapper
            // may already be mediating; fail closed to returning its redirects rather than following outside it.
            return Decider(inner, family, site, guard, followCrossHost);
        }
    }

    private static HttpMessageHandler Decider(HttpMessageHandler inner, string family, string site, IEgressGuard? guard, bool followCrossHost) =>
        new EgressRedirectHandler(inner, family, site, guard, new RedirectSettings(false, 50, followCrossHost));

    private static bool CanReplaceInner(DelegatingHandler parent)
    {
        try
        {
            parent.InnerHandler = parent.InnerHandler!;
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    internal static RedirectSettings RememberAndDisable(HttpMessageHandler primary, bool followCrossHost)
    {
        SecurityGuard.ThrowIfNull(primary, nameof(primary));
        lock (Gate)
        {
            if (!Memory.TryGetValue(primary, out var original))
            {
                original = ReadAndDisable(primary);
                Memory.Add(primary, original);
            }

            // The primary's original redirect settings are shared; the P1/P2 policy belongs to this wrapper.
            return new RedirectSettings(original.Follow, original.MaxAutomaticRedirections, followCrossHost);
        }
    }

    private static RedirectSettings ReadAndDisable(HttpMessageHandler primary)
    {
        var allow = false;
        var max = 50;
        try
        {
            switch (primary)
            {
                case HttpClientHandler http when http.Credentials is null:
                    allow = http.AllowAutoRedirect;
                    max = http.MaxAutomaticRedirections;
                    if (allow)
                        http.AllowAutoRedirect = false;
                    break;
#if !NETSTANDARD2_0
                case SocketsHttpHandler sockets when sockets.Credentials is null:
                    allow = sockets.AllowAutoRedirect;
                    max = sockets.MaxAutomaticRedirections;
                    if (allow)
                        sockets.AllowAutoRedirect = false;
                    break;
#endif
            }
        }
#pragma warning disable CA1031 // A started, unsupported or unreadable primary keeps its own behaviour; we never follow for it.
        catch (Exception)
#pragma warning restore CA1031
        {
            allow = false;
        }

        return new RedirectSettings(allow, Math.Max(0, max), false);
    }
}

/// <summary>The authority the guard last evaluated, stored on the request so a later hop can see that it changed.</summary>
internal static class EgressEvaluatedAuthority
{
    internal static bool SameOrigin(Uri left, Uri right) => Authority.From(left).Equals(Authority.From(right));

    private readonly struct Authority : IEquatable<Authority>
    {
        private Authority(string? scheme, string? host, int port)
        {
            Scheme = scheme;
            Host = host;
            Port = port;
        }

        private string? Scheme { get; }

        private string? Host { get; }

        private int Port { get; }

        public static Authority From(Uri? uri) =>
            uri is null || !uri.IsAbsoluteUri
                ? new Authority(null, null, -1)
                : new Authority(uri.Scheme, uri.Host, uri.Port);

        public bool Equals(Authority other) =>
            string.Equals(Scheme, other.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(Host, other.Host, StringComparison.OrdinalIgnoreCase)
            && Port == other.Port;

        public override bool Equals(object? obj) => obj is Authority other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = 17;
                hash = (hash * 31) + (Scheme is null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(Scheme));
                hash = (hash * 31) + (Host is null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(Host));
                return (hash * 31) + Port;
            }
        }
    }

#if NET5_0_OR_GREATER
    private static readonly HttpRequestOptionsKey<Authority> Key = new("Ashlar.Egress.EvaluatedAuthority");

    internal static void Stamp(HttpRequestMessage request, Uri? uri) => request.Options.Set(Key, Authority.From(uri));

    internal static bool Matches(HttpRequestMessage request) =>
        request.Options.TryGetValue(Key, out var stamped) && stamped.Equals(Authority.From(request.RequestUri));

    internal static bool Matches(HttpRequestMessage request, Uri? uri) =>
        request.Options.TryGetValue(Key, out var stamped) && stamped.Equals(Authority.From(uri));
#else
    private const string Key = "Ashlar.Egress.EvaluatedAuthority";

    internal static void Stamp(HttpRequestMessage request, Uri? uri) => request.Properties[Key] = Authority.From(uri);

    internal static bool Matches(HttpRequestMessage request) =>
        request.Properties.TryGetValue(Key, out var value) && value is Authority stamped && stamped.Equals(Authority.From(request.RequestUri));

    internal static bool Matches(HttpRequestMessage request, Uri? uri) =>
        request.Properties.TryGetValue(Key, out var value) && value is Authority stamped && stamped.Equals(Authority.From(uri));
#endif

    internal static string Describe(Uri? uri)
    {
        if (uri is null || !uri.IsAbsoluteUri)
            return "unknown";

        var port = uri.IsDefaultPort ? string.Empty : ":" + uri.Port.ToString(CultureInfo.InvariantCulture);
        return uri.Scheme + "://" + uri.Host + port;
    }
}
