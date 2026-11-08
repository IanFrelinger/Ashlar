using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Ashlar.Abstractions.Security.Egress;

#pragma warning disable ASHLAREXP002 // IEgressGuard, EgressGuard, EgressRequest, EgressRefusedException, EgressHttp and EgressFamilies are the experimental egress surface; this client is the inventoried egress site EG-HTTP-09 (docs/EgressInventory.md).

namespace Ashlar.Bricks.ApiAnything;

/// <summary>
/// One answer from <c>api-anything serve</c>. A call that reached the site is a result whatever happened there;
/// <see cref="Class"/> says what (<c>ok</c>, <c>input</c>, <c>drift</c>, <c>auth</c>, <c>rate</c>, <c>blocked</c>,
/// <c>error</c>, <c>refused</c>, <c>pending</c>).
/// </summary>
/// <param name="Ok">The server's <c>ok</c>.</param>
/// <param name="Class">The server's <c>class</c>.</param>
/// <param name="Tier">The transport tier that answered, when it ran.</param>
/// <param name="Data">The operation's data, when there is any.</param>
/// <param name="Reason">Why it failed, in the server's words.</param>
/// <param name="Next">What the server says to do next.</param>
/// <param name="Retry">Whether to send it again: <c>safe</c>, <c>after_wait</c>, <c>after_fix</c>, <c>no</c>.</param>
/// <param name="RetryAfter">How long to wait before retrying, when the server said.</param>
/// <param name="Upstream">The website host the call was evaluated against by the egress guard.</param>
public sealed record ApiAnythingResult(
    bool Ok,
    string Class,
    int? Tier,
    JsonElement? Data,
    string? Reason,
    string? Next,
    string? Retry,
    TimeSpan? RetryAfter,
    string Upstream);

/// <summary>A call the client refused or could not complete (not a site-side failure, which is a result).</summary>
public sealed class ApiAnythingException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">What went wrong.</param>
    public ApiAnythingException(string message) : base(message) { }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="inner">The cause.</param>
    public ApiAnythingException(string message, Exception inner) : base(message, inner) { }

    /// <summary>Creates the exception.</summary>
    public ApiAnythingException() { }
}

/// <summary>
/// Calls <c>api-anything serve</c> (https://github.com/goodnight000/api-anything) over its documented REST contract.
/// </summary>
/// <remarks>
/// <para>
/// The request this client sends goes to a loopback server, which the egress guard classes as inside the host. The
/// server then fetches the website from its own process, outside the guard. So before every call the client asks the
/// guard about the <em>website</em> (<see cref="ResolveUpstreamAsync"/>), under the same site id: a deployment
/// profile that refuses external egress refuses the call before the server is asked to make it.
/// </para>
/// <para>
/// The client never sends a website credential. Operations that need a login use the server's own stored session; the
/// job bricks in this package call only logged-out public reads.
/// </para>
/// </remarks>
public sealed class ApiAnythingClient : IDisposable
{
    /// <summary>The egress inventory id of this client (docs/EgressInventory.md).</summary>
    public const string EgressSite = "EG-HTTP-09";

    /// <summary>The website each bundled site the job bricks use reads from, per its spec in api-anything's <c>sites/</c>.</summary>
    public static readonly IReadOnlyDictionary<string, string> KnownUpstreams = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["greenhouse"] = "job-boards.greenhouse.io",
        ["lever"] = "api.lever.co",
        ["ashby"] = "jobs.ashbyhq.com",
        ["workable"] = "apply.workable.com",
        ["workday"] = "workday.wd5.myworkdayjobs.com",
    };

    private readonly ApiAnythingOptions _options;
    private readonly IEgressGuard _guard;
    private readonly HttpClient _http;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    /// <summary>Creates a client.</summary>
    /// <param name="options">Where the server is. Its base URL must be loopback.</param>
    /// <param name="guard">The egress guard; <see langword="null"/> for <see cref="EgressGuard.ProcessDefault"/>.</param>
    /// <param name="transport">The handler that sends; <see langword="null"/> for a new <see cref="HttpClientHandler"/>. Tests pass a stub.</param>
    /// <param name="delay">How to wait between polls; tests pass a no-op.</param>
    /// <exception cref="ArgumentException">The base URL is not loopback.</exception>
    public ApiAnythingClient(
        ApiAnythingOptions options,
        IEgressGuard? guard = null,
        HttpMessageHandler? transport = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!ApiAnythingOptions.IsLoopback(options.BaseUrl))
            throw new ArgumentException("api-anything serve must be reached on a loopback address (it holds the operator's sessions and token).", nameof(options));
        if (options.MaxTier is < 1 or > 3)
            throw new ArgumentException("MaxTier must be 1, 2 or 3.", nameof(options));
        _options = options;
        _guard = guard ?? EgressGuard.ProcessDefault;
        _delay = delay ?? ((d, ct) => Task.Delay(d, ct));
#pragma warning disable CA2000 // Ownership passes to the client, which disposes the guard handler and the transport.
        _http = EgressHttp.CreateClient(transport ?? new HttpClientHandler(), EgressFamilies.Http, EgressSite, _guard);
#pragma warning restore CA2000
        _http.BaseAddress = options.BaseUrl;
        _http.Timeout = options.Timeout + TimeSpan.FromSeconds(5);
        if (!string.IsNullOrEmpty(options.Token))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.Token);
    }

    /// <summary>
    /// The website host a site reads from: the server's own description of the site (its <c>baseUrl</c>) when it gives
    /// one, else <see cref="KnownUpstreams"/>, else <see langword="null"/> (and the call is refused).
    /// </summary>
    /// <param name="site">The site name.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The host, or <see langword="null"/>.</returns>
    public async Task<string?> ResolveUpstreamAsync(string site, CancellationToken cancellationToken = default)
    {
        if (KnownUpstreams.TryGetValue(site, out var known))
            return known;
        try
        {
            using var response = await _http.GetAsync(new Uri($"v1/sites/{Uri.EscapeDataString(site)}", UriKind.Relative), cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            foreach (var candidate in new[] { doc.RootElement, doc.RootElement.TryGetProperty("site", out var s) ? s : default })
            {
                if (candidate.ValueKind == JsonValueKind.Object && candidate.TryGetProperty("baseUrl", out var b) &&
                    b.ValueKind == JsonValueKind.String && Uri.TryCreate(b.GetString(), UriKind.Absolute, out var baseUrl))
                    return baseUrl.Host;
            }
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
        return null;
    }

    /// <summary>Runs one read operation.</summary>
    /// <param name="site">The site, for example <c>lever</c>.</param>
    /// <param name="op">The operation, for example <c>listJobs</c>.</param>
    /// <param name="args">Its arguments; values are sent as strings, as the server's schema expects.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The result, whatever happened at the site.</returns>
    /// <exception cref="EgressRefusedException">The deployment profile refuses egress to the site's host.</exception>
    /// <exception cref="ApiAnythingException">The site's host is unknown, the token was refused, the server is
    /// unreachable, or a pending job outlived <see cref="ApiAnythingOptions.Timeout"/>.</exception>
    public async Task<ApiAnythingResult> CallAsync(string site, string op, IReadOnlyDictionary<string, string> args, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(site);
        ArgumentException.ThrowIfNullOrEmpty(op);
        ArgumentNullException.ThrowIfNull(args);

        var upstream = await ResolveUpstreamAsync(site, cancellationToken).ConfigureAwait(false)
            ?? throw new ApiAnythingException($"Cannot tell which website '{site}' reads from, so the egress guard cannot decide; the call was not sent.");
        _guard.Evaluate(new EgressRequest(EgressFamilies.Http, EgressSite, new Uri($"https://{upstream}/"))).ThrowIfRefused();

        var body = new Dictionary<string, object> { ["args"] = args, ["maxTier"] = _options.MaxTier };
        if (_options.MaxAge is { } maxAge)
            body["maxAgeMs"] = (long)maxAge.TotalMilliseconds;
        var path = $"v1/call/{Uri.EscapeDataString(site)}/{Uri.EscapeDataString(op)}";

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.Timeout);
        try
        {
            var result = await SendAsync(HttpMethod.Post, path, JsonSerializer.Serialize(body), upstream, timeout.Token).ConfigureAwait(false);
            // A long call answers `pending` with a job. Never resend the call: poll the job (each poll waits up to ~25 s server-side).
            while (result.Class == "pending" && result.Job is { } job)
            {
                result = await SendAsync(HttpMethod.Get, $"v1/jobs/{Uri.EscapeDataString(job)}", null, upstream, timeout.Token).ConfigureAwait(false);
                if (result.Class == "pending")
                    await _delay(TimeSpan.FromSeconds(1), timeout.Token).ConfigureAwait(false);
            }
            return result.Result;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new ApiAnythingException($"{site}.{op} did not finish within {_options.Timeout.TotalSeconds:0} s.");
        }
    }

    private async Task<(ApiAnythingResult Result, string Class, string? Job)> SendAsync(HttpMethod method, string path, string? json, string upstream, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        if (json is not null)
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ApiAnythingException($"api-anything serve is not answering at {_options.BaseUrl}: {ex.Message}", ex);
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                throw new ApiAnythingException("api-anything serve refused the token (set API_ANYTHING_TOKEN or API_ANYTHING_HOME).");
            // 200 is a call that ran; 429 is "server busy", a call result that never reached the site.
            if (response.StatusCode != HttpStatusCode.OK && response.StatusCode != HttpStatusCode.TooManyRequests)
                throw new ApiAnythingException($"api-anything serve answered HTTP {(int)response.StatusCode} for {path}.");
            return Parse(text, upstream);
        }
    }

    internal static (ApiAnythingResult Result, string Class, string? Job) Parse(string text, string upstream)
    {
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        string? Str(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var ok = root.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.True;
        var cls = Str("class") ?? (ok ? "ok" : "error");
        int? tier = root.TryGetProperty("tier", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt32() : null;
        JsonElement? data = root.TryGetProperty("data", out var d) ? d.Clone() : null;
        TimeSpan? retryAfter = root.TryGetProperty("retryAfterMs", out var ra) && ra.ValueKind == JsonValueKind.Number
            ? TimeSpan.FromMilliseconds(ra.GetDouble()) : null;
        var result = new ApiAnythingResult(ok, cls, tier, data, Str("reason"), Str("next"), Str("retry"), retryAfter, upstream);
        return (result, cls, Str("job"));
    }

    /// <inheritdoc />
    public void Dispose() => _http.Dispose();
}
