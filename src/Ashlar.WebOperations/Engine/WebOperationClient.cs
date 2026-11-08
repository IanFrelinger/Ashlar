using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Ashlar.Abstractions.Security.Egress;
using Ashlar.WebOperations.Specs;

#pragma warning disable ASHLAREXP002 // IEgressGuard, EgressGuard, EgressHttp and EgressFamilies are the experimental egress surface; this client is the inventoried egress site EG-HTTP-09 (docs/EgressInventory.md).

namespace Ashlar.WebOperations;

/// <summary>
/// The outcome of one operation call. A call that reached the site is a result whatever happened there.
/// <list type="table">
/// <item><term>ok</term><description>data as the spec reads it</description></item>
/// <item><term>input</term><description>nothing for these arguments (an input status, an empty filtered result, a bad argument)</description></item>
/// <item><term>drift</term><description>the answer no longer matches the spec; repair it</description></item>
/// <item><term>auth</term><description>a credential is missing or was refused</description></item>
/// <item><term>rate</term><description>the site or the pacing budget said wait</description></item>
/// <item><term>blocked</term><description>a bot wall or challenge page came back</description></item>
/// <item><term>refused</term><description>the egress guard, the host list or the write guard said no; nothing was sent</description></item>
/// <item><term>duplicate</term><description>a write with this idempotency key was already sent</description></item>
/// <item><term>error</term><description>the site failed or could not be reached</description></item>
/// </list>
/// </summary>
public sealed record WebResult
{
    /// <summary>Whether <see cref="Class"/> is ok.</summary>
    public bool Ok => Class == "ok";
    /// <summary>See the type remarks.</summary>
    public string Class { get; init; } = "error";
    /// <summary>HTTP status, when a response came back.</summary>
    public int? Status { get; init; }
    /// <summary>The data: a JSON array for list operations, an object otherwise.</summary>
    public JsonNode? Data { get; init; }
    /// <summary>Why it is not ok.</summary>
    public string? Reason { get; init; }
    /// <summary>The one step to take next.</summary>
    public string? Next { get; init; }
    /// <summary>safe, after_wait, after_fix or no.</summary>
    public string Retry { get; init; } = "no";
    /// <summary>When retrying makes sense.</summary>
    public TimeSpan? RetryAfter { get; init; }
    /// <summary>For writes: not_sent, sent or may_have_sent.</summary>
    public string? Write { get; init; }
    /// <summary>The host the request went to (or would have).</summary>
    public string Host { get; init; } = "";
    /// <summary>The spec version that answered.</summary>
    public int SpecVersion { get; init; }
    /// <summary>Served from the cache.</summary>
    public bool Cached { get; init; }
    /// <summary>The spec was repaired during this call; what changed.</summary>
    public string? Healed { get; init; }
}

/// <summary>Resolves credential references (<c>cookie:NAME</c>, <c>header:NAME</c>, <c>query:NAME</c>) for one caller.</summary>
public interface ICredentialSource
{
    /// <summary>Identifies whose credentials these are; cached answers are never shared across scopes.</summary>
    string Scope { get; }

    /// <summary>The value, or null when the caller has none.</summary>
    string? Resolve(string reference);
}

/// <summary>Remembers idempotency keys of writes already sent.</summary>
public interface IWriteLedger
{
    /// <summary>Records the key; false when it was already recorded.</summary>
    bool TryRecord(string site, string operation, string key);
}

/// <summary>In-process ledger.</summary>
public sealed class InMemoryWriteLedger : IWriteLedger
{
    private readonly ConcurrentDictionary<string, byte> _keys = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public bool TryRecord(string site, string operation, string key) => _keys.TryAdd($"{site}\u0000{operation}\u0000{key}", 0);
}

/// <summary>Per-call options.</summary>
public sealed record CallOptions
{
    /// <summary>The caller's credentials, for operations that declare credential references.</summary>
    public ICredentialSource? Credentials { get; init; }
    /// <summary>Answer from a result this recent instead of asking the site again (reads only).</summary>
    public TimeSpan? MaxAge { get; init; }
    /// <summary>Writes are refused unless this is true.</summary>
    public bool AllowWrites { get; init; }
    /// <summary>Required for writes; a repeated key is refused as duplicate.</summary>
    public string? IdempotencyKey { get; init; }
    /// <summary>On drift, try to repair the spec from the same response (no second request) and save it.</summary>
    public bool AutoHeal { get; init; } = true;
}

/// <summary>Client settings.</summary>
public sealed record WebOperationsOptions
{
    /// <summary>Minimum gap between two requests to one host, so automated traffic stays at human volume.</summary>
    public TimeSpan PerHostInterval { get; init; } = TimeSpan.FromSeconds(1);
    /// <summary>Upper bound per request.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Largest body read.</summary>
    public int MaxBodyBytes { get; init; } = 8 * 1024 * 1024;
    /// <summary>User-Agent sent with every request.</summary>
    public string UserAgent { get; init; } = "Ashlar-WebOperations/1.0 (+https://github.com/IanFrelinger/Ashlar)";
}

/// <summary>
/// Runs declared operations over plain HTTP. Every request goes through the Ashlar egress guard (inventory row
/// EG-HTTP-09) to the host the spec declares, so a deployment profile that refuses external egress refuses these calls.
/// </summary>
public sealed class WebOperationClient : IDisposable
{
    /// <summary>The egress inventory id of this client.</summary>
    public const string EgressSite = "EG-HTTP-09";

    private readonly SpecStore _specs;
    private readonly IEgressGuard _guard;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp = true;
    private readonly WebOperationsOptions _options;
    private readonly IWriteLedger _ledger;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<DateTimeOffset> _now;
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, WebResult Result)> _cache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _hostGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _hostLast = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates a client.</summary>
    /// <param name="specs">Where operations come from.</param>
    /// <param name="guard">The egress guard; null for <see cref="EgressGuard.ProcessDefault"/>.</param>
    /// <param name="transport">The handler that sends; null for a new <see cref="HttpClientHandler"/>. Tests pass a stub.</param>
    /// <param name="options">Settings.</param>
    /// <param name="ledger">Write ledger; null for an in-memory one.</param>
    /// <param name="delay">Waits (pacing); tests pass a recorder.</param>
    /// <param name="now">Clock; tests pin it.</param>
    public WebOperationClient(SpecStore specs, IEgressGuard? guard = null, HttpMessageHandler? transport = null, WebOperationsOptions? options = null,
        IWriteLedger? ledger = null, Func<TimeSpan, CancellationToken, Task>? delay = null, Func<DateTimeOffset>? now = null)
    {
        _specs = specs ?? throw new ArgumentNullException(nameof(specs));
        _guard = guard ?? EgressGuard.ProcessDefault;
        _options = options ?? new WebOperationsOptions();
        _ledger = ledger ?? new InMemoryWriteLedger();
        _delay = delay ?? ((d, ct) => Task.Delay(d, ct));
        _now = now ?? (() => DateTimeOffset.UtcNow);
#pragma warning disable CA2000 // The client owns the guard handler and the transport and disposes both.
        _http = EgressHttp.CreateClient(transport ?? new HttpClientHandler(), EgressFamilies.Http, EgressSite, _guard);
#pragma warning restore CA2000
        _http.Timeout = _options.Timeout;
    }

    private WebOperationClient(WebOperationClient parent, SpecStore specs)
    {
        _specs = specs;
        _guard = parent._guard;
        _options = parent._options;
        _ledger = parent._ledger;
        _delay = parent._delay;
        _now = parent._now;
        _http = parent._http;
        _ownsHttp = false;
    }

    /// <summary>A client over other specs that shares this one's transport, guard, ledger and settings (used to trial a learned spec).</summary>
    public WebOperationClient WithSpecs(SpecStore specs) => new(this, specs ?? throw new ArgumentNullException(nameof(specs)));

    /// <summary>The spec store this client reads.</summary>
    public SpecStore Specs => _specs;

    /// <summary>Runs one operation.</summary>
    public async Task<WebResult> CallAsync(string site, string operation, IReadOnlyDictionary<string, string> args, CallOptions? options = null, CancellationToken ct = default)
    {
        options ??= new CallOptions();
        var spec = _specs.Get(site) ?? throw new KeyNotFoundException($"no spec for site '{site}'");
        var op = spec.Operation(operation);

        // Arguments: required, defaults, patterns. A bad argument is the caller's to fix; nothing is sent.
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, p) in op.Params)
        {
            if (args.TryGetValue(name, out var v) && v.Length > 0) values[name] = v;
            else if (p.Default is not null) values[name] = p.Default;
            else if (p.Required) return Input(spec, $"missing argument '{name}'");
            if (values.TryGetValue(name, out var given) && p.Pattern is not null &&
                !Regex.IsMatch(given, $"^(?:{p.Pattern})$", RegexOptions.None, SpecJson.RegexBudget))
                return Input(spec, $"argument '{name}' does not match {p.Pattern}");
        }
        foreach (var extra in args.Keys.Where(k => !op.Params.ContainsKey(k)))
            return Input(spec, $"unknown argument '{extra}'");

        var url = Template.FillUrl(op.Request.Url, values);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo))
            return Input(spec, "the request URL must be an absolute https URL without credentials");
        if (!spec.AllowsHost(uri.Host))
            return Refused(spec, uri.Host, $"{uri.Host} is not a host the {site} spec declares");
        if (op.Kind == "read" && spec.Hosts.Contains("*") && (uri.IsLoopback || IPAddress.TryParse(uri.Host, out _)))
            return Refused(spec, uri.Host, "an open-host read may not target an IP address or loopback");

        // Egress decision before anything else happens.
        var decision = _guard.Evaluate(new EgressRequest(EgressFamilies.Http, EgressSite, new Uri($"https://{uri.Host}/")));
        if (decision.Refuses)
            return Refused(spec, uri.Host, $"the egress guard refused {uri.Host} under profile {decision.Profile}");

        // Credentials: resolved per call, never stored, never shared through the cache.
        var credentialValues = new List<(string Kind, string Name, string Value)>();
        foreach (var reference in op.Request.Credentials)
        {
            var value = options.Credentials?.Resolve(reference);
            if (string.IsNullOrEmpty(value))
                return new WebResult { Class = "auth", Host = uri.Host, SpecVersion = spec.Version, Reason = $"no credential for {reference}", Next = "sign in to the site and provide the credential", Retry = "after_fix" };
            var colon = reference.IndexOf(':', StringComparison.Ordinal);
            credentialValues.Add((reference[..colon], reference[(colon + 1)..], value));
        }
        var cacheScope = op.Request.Credentials.Count == 0 ? "public" : options.Credentials!.Scope;

        // Writes: explicit permission, an idempotency key, never twice, never from the cache, never retried.
        if (op.Kind == "write")
        {
            if (!options.AllowWrites)
                return Refused(spec, uri.Host, "writes are off; the caller must allow this write explicitly") with { Write = "not_sent" };
            if (string.IsNullOrWhiteSpace(options.IdempotencyKey))
                return Refused(spec, uri.Host, "a write needs an idempotency key") with { Write = "not_sent" };
            if (!_ledger.TryRecord(site, operation, options.IdempotencyKey))
                return new WebResult { Class = "duplicate", Write = "not_sent", Host = uri.Host, SpecVersion = spec.Version, Reason = "a write with this idempotency key was already sent" };
        }

        var cacheKey = $"{site}|{spec.Version}|{operation}|{cacheScope}|{string.Join("&", values.OrderBy(v => v.Key, StringComparer.Ordinal).Select(v => $"{v.Key}={v.Value}"))}";
        if (op.Kind == "read" && options.MaxAge is { } maxAge && _cache.TryGetValue(cacheKey, out var hit) && _now() - hit.At <= maxAge)
            return hit.Result with { Cached = true };

        var result = await SendAsync(spec, op, operation, uri, values, credentialValues, options, ct).ConfigureAwait(false);
        if (op.Kind == "read" && result.Ok)
            _cache[cacheKey] = (_now(), result);
        return result;
    }

    /// <summary>Every page of a paged list operation, until an empty page or <paramref name="maxPages"/>.</summary>
    public async Task<(JsonArray Items, WebResult Last)> CallAllPagesAsync(string site, string operation, IReadOnlyDictionary<string, string> args, int maxPages,
        CallOptions? options = null, CancellationToken ct = default)
    {
        var op = (_specs.Get(site) ?? throw new KeyNotFoundException(site)).Operation(operation);
        var all = new JsonArray();
        if (op.Paging is not { } paging)
        {
            var single = await CallAsync(site, operation, args, options, ct).ConfigureAwait(false);
            if (single.Ok && single.Data is JsonArray only) foreach (var i in only) all.Add(i?.DeepClone());
            return (all, single);
        }
        WebResult last = new();
        for (var page = paging.Start; page < paging.Start + maxPages; page++)
        {
            var pageArgs = new Dictionary<string, string>(args, StringComparer.Ordinal) { [paging.Param] = page.ToString(System.Globalization.CultureInfo.InvariantCulture) };
            last = await CallAsync(site, operation, pageArgs, options, ct).ConfigureAwait(false);
            if (!last.Ok || last.Data is not JsonArray items || items.Count == 0) break;
            foreach (var i in items) all.Add(i?.DeepClone());
        }
        return (all, last);
    }

    private async Task<WebResult> SendAsync(SiteSpec spec, OperationSpec op, string operation, Uri uri, Dictionary<string, string> values,
        List<(string Kind, string Name, string Value)> credentials, CallOptions options, CancellationToken ct)
    {
        foreach (var q in credentials.Where(c => c.Kind == "query"))
            uri = new Uri(uri + (uri.Query.Length == 0 ? "?" : "&") + $"{Uri.EscapeDataString(q.Name)}={Uri.EscapeDataString(q.Value)}");
        using var request = new HttpRequestMessage(new HttpMethod(op.Request.Method), uri);
        request.Headers.UserAgent.ParseAdd(_options.UserAgent);
        foreach (var (name, value) in op.Request.Headers)
            request.Headers.TryAddWithoutValidation(name, value);
        foreach (var h in credentials.Where(c => c.Kind == "header"))
            request.Headers.TryAddWithoutValidation(h.Name, h.Value);
        var cookies = credentials.Where(c => c.Kind == "cookie").Select(c => $"{c.Name}={c.Value}").ToList();
        if (cookies.Count > 0)
            request.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", cookies));
        if (op.Request.Body is { } body)
            request.Content = new StringContent(Template.FillBody(body, values, op.Request.BodyType), Encoding.UTF8, op.Request.BodyType);

        await PaceAsync(uri.Host, ct).ConfigureAwait(false);
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (EgressRefusedException ex)
        {
            return Refused(spec, uri.Host, $"the egress guard refused the request: {ex.Message}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return new WebResult
            {
                Class = "error", Host = uri.Host, SpecVersion = spec.Version, Reason = $"{uri.Host} could not be reached: {ex.Message}",
                Retry = op.Kind == "write" ? "no" : "after_wait", RetryAfter = TimeSpan.FromSeconds(30), Write = op.Kind == "write" ? "may_have_sent" : null,
            };
        }

        using (response)
        {
            var status = (int)response.StatusCode;
            var text = await ReadBodyAsync(response, ct).ConfigureAwait(false);
            var write = op.Kind == "write" ? "sent" : null;
            WebResult Make(string cls, string? reason, string? next, string retry, TimeSpan? after = null) => new()
            {
                Class = cls, Status = status, Host = uri.Host, SpecVersion = spec.Version, Reason = reason, Next = next, Retry = op.Kind == "write" ? "no" : retry, RetryAfter = after, Write = write,
            };

            if (op.InputStatuses.Contains(status))
                return Make("input", $"{uri.Host} answered {status}: nothing for these arguments", "check the arguments; a closed or unknown item answers this way", "after_fix");
            if (status == 429)
                return Make("rate", $"{uri.Host} asked to slow down", "wait, then retry", "after_wait", response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(60));
            if (status is 401 or 403 && credentials.Count > 0)
                return Make("auth", $"{uri.Host} refused the credential ({status})", "sign in again and refresh the credential", "after_fix");
            if (status is 401 or 403 or 503 && Extraction.LooksLikeChallenge(text))
                return Make("blocked", $"{uri.Host} served a challenge page ({status})", "the site is blocking automated requests; open it in a browser", "no");
            if (status >= 500)
                return Make("error", $"{uri.Host} failed ({status})", "retry later", "after_wait", TimeSpan.FromMinutes(1));
            if (status >= 400)
                return Make("error", $"{uri.Host} answered {status}", "check the spec for this site", "no");
            if (op.Kind == "write")
                return Make("ok", null, null, "no") with { Data = TryJson(text) };

            var (doc, parseFailure) = Extraction.Parse(op.Response, text);
            var (data, failure) = parseFailure is null ? Extraction.Select(op.Response, doc, values) : (null, parseFailure);
            if (failure is { Class: "drift" } && doc is not null && options.AutoHeal && Healer.TryRepairExtract(op, doc, values) is { } repaired)
            {
                var (healedData, healedFailure) = Extraction.Select(repaired.Response, doc, values);
                if (healedFailure is null)
                {
                    var saved = _specs.SaveRepair(spec, operation, repaired, $"extract moved from '{op.Response.Extract}' to '{repaired.Response.Extract}'");
                    return Make("ok", null, null, "safe") with { Data = healedData, SpecVersion = saved.Version, Healed = $"extract moved from '{op.Response.Extract}' to '{repaired.Response.Extract}'" };
                }
            }
            if (failure is not null)
            {
                return failure.Class switch
                {
                    "drift" => Make("drift", failure.Reason, "the site changed: repair the operation (relearn from a fresh capture)", "after_fix"),
                    "blocked" => Make("blocked", failure.Reason, "the site is blocking automated requests", "no"),
                    "input" => Make("input", failure.Reason, "check the arguments", "after_fix"),
                    _ => Make(failure.Class, failure.Reason, null, "no"),
                };
            }
            return Make("ok", null, null, "safe") with { Data = data };
        }
    }

    private async Task<string> ReadBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > _options.MaxBodyBytes)
                break;
        }
        var charset = response.Content.Headers.ContentType?.CharSet;
        Encoding encoding;
        try { encoding = charset is null ? Encoding.UTF8 : Encoding.GetEncoding(charset.Trim('"')); }
        catch (ArgumentException) { encoding = Encoding.UTF8; }
        return encoding.GetString(buffer.GetBuffer(), 0, (int)Math.Min(buffer.Length, _options.MaxBodyBytes));
    }

    private async Task PaceAsync(string host, CancellationToken ct)
    {
        var gate = _hostGates.GetOrAdd(host, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_hostLast.TryGetValue(host, out var last) && _now() - last < _options.PerHostInterval)
                await _delay(_options.PerHostInterval - (_now() - last), ct).ConfigureAwait(false);
            _hostLast[host] = _now();
        }
        finally
        {
            gate.Release();
        }
    }

    private static JsonNode? TryJson(string text)
    {
        try { return text.Length == 0 ? null : JsonNode.Parse(text); }
        catch (System.Text.Json.JsonException) { return null; }
    }

    private static WebResult Input(SiteSpec spec, string reason) =>
        new() { Class = "input", SpecVersion = spec.Version, Reason = reason, Next = "fix the arguments", Retry = "after_fix" };

    private static WebResult Refused(SiteSpec spec, string host, string reason) =>
        new() { Class = "refused", Host = host, SpecVersion = spec.Version, Reason = reason, Retry = "no" };

    /// <inheritdoc />
    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
        foreach (var g in _hostGates.Values) g.Dispose();
    }
}
