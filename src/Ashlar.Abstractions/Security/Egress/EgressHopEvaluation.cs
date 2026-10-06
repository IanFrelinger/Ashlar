namespace Ashlar.Abstractions.Security.Egress;

/// <summary>
/// The per-request note that lets every HTTP send be decided by the authority it goes to (SPEC-007 PR 4.3, design
/// §2.6 gap 2): which guard, family and site decide the request, and the last authority they decided.
/// </summary>
/// <remarks>
/// <para><b>Where it lives.</b> On the request itself, in <c>HttpRequestMessage.Options</c> (<c>Properties</c> on the
/// netstandard2.0 asset), under a key whose value type is internal, so code outside this assembly can neither forge
/// nor read it. <see cref="EgressGuardHandler"/> writes a fresh note at the start of every send, and decides the
/// request's URI; <see cref="EgressRedirectHandler"/>, directly above the primary handler, decides again before each
/// send whose authority differs from the last one decided (a redirect hop, or a handler between the two that rewrote
/// <c>RequestUri</c>); the guard handler decides once more after the send when the response's request names an
/// authority that was never decided (a primary handler that followed a redirect itself).</para>
/// <para><b>Authority.</b> The scheme, host and port: what a decision records. A change of path, query, fragment or
/// userinfo alone is the same destination and is not decided again. A request with no URI is the authority
/// <c>unknown</c>, as its record is; a relative URI, which no handler can send, is an authority of its own, decided
/// once (as a fault: it names no host).</para>
/// <para><b>Faults.</b> A guard that throws (a custom <see cref="IEgressGuard"/> may) is swallowed and counted, and
/// the authority still counts as decided, so a throwing guard is asked once per authority, as it was asked once per
/// send before redirects were followed here.</para>
/// </remarks>
internal sealed class EgressHopEvaluation
{
    private const string KeyName = "Ashlar.Egress.HopEvaluation";

    private const string NoUri = "\u0000unknown";

    private const string Relative = "\u0000relative";

#if NET5_0_OR_GREATER
    private static readonly HttpRequestOptionsKey<EgressHopEvaluation> Key = new(KeyName);
#endif

    private static long _guardFaults;

    private EgressHopEvaluation(IEgressGuard? guard, string family, string site)
    {
        Guard = guard;
        Family = family;
        Site = site;
    }

    /// <summary>How many times a guard threw while a request was being decided; each throw was swallowed.</summary>
    internal static long GuardFaults => Interlocked.Read(ref _guardFaults);

    /// <summary>The guard, or <see langword="null"/> for <see cref="EgressGuard.ProcessDefault"/>, read at each decision.</summary>
    internal IEgressGuard? Guard { get; }

    /// <summary>The family every decision of this request is made with.</summary>
    internal string Family { get; }

    /// <summary>The site every decision of this request is made with.</summary>
    internal string Site { get; }

    /// <summary>The authority last decided, or <see langword="null"/> before the first decision.</summary>
    internal string? Authority { get; private set; }

    /// <summary>
    /// Starts a fresh note on <paramref name="request"/> for this guard, family and site, replacing any earlier one,
    /// and decides the request's URI.
    /// </summary>
    internal static void Begin(HttpRequestMessage request, IEgressGuard? guard, string family, string site)
    {
        var note = new EgressHopEvaluation(guard, family, site);
        Store(request, note);
        note.Decide(request.RequestUri);
    }

    /// <summary>
    /// Decides <paramref name="uri"/> unless the note on <paramref name="request"/> last decided the same authority.
    /// With no note (no guard handler sent this request), a note is started with the fallback guard, family and site,
    /// so the send is still decided.
    /// </summary>
    /// <returns><see langword="true"/> when a decision was made.</returns>
    internal static bool EnsureDecided(HttpRequestMessage request, Uri? uri, IEgressGuard? fallbackGuard, string fallbackFamily, string fallbackSite)
    {
        var note = Find(request);
        if (note is null)
        {
            note = new EgressHopEvaluation(fallbackGuard, fallbackFamily, fallbackSite);
            Store(request, note);
        }

        if (note.Authority is not null && string.Equals(note.Authority, AuthorityOf(uri), StringComparison.Ordinal))
            return false;

        note.Decide(uri);
        return true;
    }

    /// <summary>
    /// After a send: decides the URI the response's request names when the note on <paramref name="request"/> never
    /// decided its authority. A response with no request message is not checked: there is nothing to decide.
    /// </summary>
    internal static void CheckAfterSend(HttpRequestMessage request, HttpResponseMessage? response)
    {
        var sent = response?.RequestMessage?.RequestUri;
        var note = Find(request);
        if (sent is null || note is null)
            return;

        if (!string.Equals(note.Authority, AuthorityOf(sent), StringComparison.Ordinal))
            note.Decide(sent);
    }

    /// <summary>The scheme, host and port of <paramref name="uri"/>, the part a decision records.</summary>
    internal static string AuthorityOf(Uri? uri)
    {
        if (uri is null)
            return NoUri;

        // A relative URI names no authority: no handler can send it, and its decision is a fault. It is one authority of
        // its own, so it is decided once per request, not once more by every handler that sees it.
        if (!uri.IsAbsoluteUri)
            return Relative;

        return uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.StrongPort, UriFormat.UriEscaped);
    }

    private static EgressHopEvaluation? Find(HttpRequestMessage request)
    {
#if NET5_0_OR_GREATER
        return request.Options.TryGetValue(Key, out var note) ? note : null;
#else
        return request.Properties.TryGetValue(KeyName, out var value) ? value as EgressHopEvaluation : null;
#endif
    }

    private static void Store(HttpRequestMessage request, EgressHopEvaluation note)
    {
#if NET5_0_OR_GREATER
        request.Options.Set(Key, note);
#else
        request.Properties[KeyName] = note;
#endif
    }

    // Reads the URI and nothing else from the request. The authority is noted first, so a guard that throws is still
    // asked once per authority.
    private void Decide(Uri? uri)
    {
        Authority = AuthorityOf(uri);
        try
        {
            var egress = uri is null
                ? new EgressRequest(Family, Site, EgressDestinations.UnknownDestination)
                : new EgressRequest(Family, Site, uri);
            _ = (Guard ?? EgressGuard.ProcessDefault).Evaluate(egress);
        }
#pragma warning disable CA1031 // A custom guard may throw; report-only means the send goes ahead unchanged, so the fault is counted.
        catch (Exception)
#pragma warning restore CA1031
        {
            Interlocked.Increment(ref _guardFaults);
        }
    }
}
