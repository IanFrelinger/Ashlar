using System.Text.Json;
using System.Text.Json.Nodes;
using Ashlar.WebOperations.Specs;

namespace Ashlar.WebOperations.Learning;

/// <summary>One request/response pair as a browser or proxy saw it, with the argument values the user typed to cause it.</summary>
public sealed record CapturedExchange
{
    /// <summary>GET, POST...</summary>
    public string Method { get; init; } = "GET";
    /// <summary>Full URL.</summary>
    public string Url { get; init; } = "";
    /// <summary>Request headers as captured (credentials are removed by learning, never stored).</summary>
    public Dictionary<string, string> Headers { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Request body, if any.</summary>
    public string? Body { get; init; }
    /// <summary>Response status.</summary>
    public int Status { get; init; } = 200;
    /// <summary>Response body.</summary>
    public string ResponseBody { get; init; } = "";
    /// <summary>The argument values behind this request, e.g. {"company": "acme"}.</summary>
    public Dictionary<string, string> Args { get; init; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Reads the entries of a HAR file (the export every browser's developer tools offer). <paramref name="args"/>
    /// gives, per entry in order, the argument values used; entries are matched by index.
    /// </summary>
    public static IReadOnlyList<CapturedExchange> FromHar(string harJson, IReadOnlyList<Dictionary<string, string>> args)
    {
        var entries = JsonNode.Parse(harJson)?["log"]?["entries"]?.AsArray() ?? throw new InvalidDataException("not a HAR document (log.entries missing)");
        if (entries.Count != args.Count)
            throw new InvalidDataException($"the HAR holds {entries.Count} entries but {args.Count} argument sets were given; export only the requests to learn from");
        return entries.Select((e, i) => new CapturedExchange
        {
            Method = e!["request"]!["method"]!.GetValue<string>(),
            Url = e["request"]!["url"]!.GetValue<string>(),
            Headers = e["request"]!["headers"]!.AsArray().GroupBy(h => h!["name"]!.GetValue<string>(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => string.Join("; ", g.Select(h => h!["value"]!.GetValue<string>())), StringComparer.OrdinalIgnoreCase),
            Body = e["request"]!["postData"]?["text"]?.GetValue<string>(),
            Status = e["response"]!["status"]!.GetValue<int>(),
            ResponseBody = e["response"]!["content"]?["text"]?.GetValue<string>() ?? "",
            Args = new Dictionary<string, string>(args[i], StringComparer.Ordinal),
        }).ToList();
    }
}

/// <summary>Optional guidance when the answer's shape is ambiguous.</summary>
public sealed record LearnHints
{
    /// <summary>Where the data is, when not the largest list in the answer.</summary>
    public string? Extract { get; init; }
    /// <summary>Whether the data is a list (default: decided from the answer).</summary>
    public bool? List { get; init; }
    /// <summary>Fields to keep (name → path); default: every scalar key of the items.</summary>
    public Dictionary<string, string>? Fields { get; init; }
    /// <summary>A regex per parameter that the learned spec should enforce.</summary>
    public Dictionary<string, string>? Patterns { get; init; }
}

/// <summary>What learning produced.</summary>
public sealed record LearnOutcome(OperationSpec? Operation, IReadOnlyList<string> CredentialReferences, IReadOnlyList<string> Notes, string? Refusal)
{
    /// <summary>Whether an operation was produced.</summary>
    public bool Learned => Operation is not null;
}

/// <summary>
/// Builds an operation from captured exchanges. Each request part is compared across the samples: a part that is the
/// same everywhere is kept as-is; a part that changes exactly as one argument changes becomes that argument's slot; a
/// part that changes with no argument to explain it is refused (it is a nonce, a timestamp or a session value, and a
/// replay would fail or leak it). Credentials never enter the spec: cookies, authorization-like headers and secret
/// query values become references the caller resolves per call. The response's shape is what both samples share.
/// </summary>
public static class OperationLearner
{
    private static readonly HashSet<string> DroppedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "host", "content-length", "connection", "accept-encoding", "user-agent", "referer", "origin", "pragma", "cache-control",
        "if-none-match", "if-modified-since", "priority", "te", "upgrade-insecure-requests", "dnt",
    };
    private static readonly HashSet<string> CacheBusters = new(StringComparer.OrdinalIgnoreCase) { "_", "_t", "ts", "t", "cb", "cachebust", "nocache", "rand", "r" };

    /// <summary>Learns an operation from at least two exchanges whose arguments differ.</summary>
    public static LearnOutcome Learn(IReadOnlyList<CapturedExchange> samples, LearnHints? hints = null)
    {
        hints ??= new LearnHints();
        var notes = new List<string>();
        LearnOutcome Refuse(string why) => new(null, [], notes, why);

        if (samples.Count < 2)
            return Refuse("learning needs at least two captured requests made with different argument values");
        var names = samples[0].Args.Keys.ToList();
        if (names.Count == 0)
            return Refuse("say which argument values produced each request (e.g. company=acme)");
        foreach (var name in names)
        {
            if (samples.Any(s => !s.Args.ContainsKey(name)))
                return Refuse($"every sample needs a value for '{name}'");
            if (samples.Select(s => s.Args[name]).Distinct(StringComparer.Ordinal).Count() < 2)
                return Refuse($"'{name}' has the same value in every sample, so its place in the request cannot be found; capture with different values");
        }
        if (samples.Any(s => s.Status is < 200 or >= 300))
            return Refuse("learn only from successful (2xx) responses");
        if (samples.Select(s => s.Method.ToUpperInvariant()).Distinct().Count() > 1)
            return Refuse("the samples use different HTTP methods; they are not the same operation");

        var uris = samples.Select(s => new Uri(s.Url)).ToList();
        if (uris.Any(u => u.Scheme != Uri.UriSchemeHttps))
            return Refuse("learn only from https requests");
        if (uris.Select(u => u.Host.ToLowerInvariant()).Distinct().Count() > 1)
            return Refuse("the samples go to different hosts");

        // Path segments.
        var segments = uris.Select(u => u.AbsolutePath.Split('/')).ToList();
        if (segments.Select(s => s.Length).Distinct().Count() > 1)
            return Refuse("the request paths have different shapes");
        var pathParts = new List<string>();
        for (var i = 0; i < segments[0].Length; i++)
        {
            var slot = Slot(samples, segments.Select(s => Uri.UnescapeDataString(s[i])).ToList(), names);
            if (slot.Refusal is { } r) return Refuse($"path segment {i}: {r}");
            pathParts.Add(slot.Template is { } t && t != segments[0][i] && slot.IsSlot ? t : segments[0][i]);
        }

        // Query.
        var credentials = new List<string>();
        var queries = uris.Select(ParseQuery).ToList();
        var queryKeys = queries.SelectMany(q => q.Keys).Distinct(StringComparer.Ordinal).ToList();
        var queryParts = new List<string>();
        foreach (var key in queryKeys)
        {
            if (queries.Any(q => !q.ContainsKey(key)))
                return Refuse($"query '{key}' is present in some samples only");
            var values = queries.Select(q => q[key]).ToList();
            if (CredentialRules.IsSecretName(key))
            {
                credentials.Add($"query:{key}");
                notes.Add($"query '{key}' looks like a credential; it is a reference the caller supplies, not a stored value");
                continue;
            }
            var slot = Slot(samples, values, names);
            if (slot.Refusal is not null)
            {
                if (CacheBusters.Contains(key))
                {
                    notes.Add($"dropped query '{key}' (changes on every request; a cache-buster)");
                    continue;
                }
                return Refuse($"query '{key}': {slot.Refusal}");
            }
            if (!slot.IsSlot && CredentialRules.LooksLikeToken(values[0]))
                return Refuse($"query '{key}' carries a token-like constant; capture without signing in, or mark it as a credential");
            queryParts.Add($"{Uri.EscapeDataString(key)}={(slot.IsSlot ? slot.Template : Uri.EscapeDataString(values[0]))}");
        }

        // Headers.
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in samples[0].Headers)
        {
            if (DroppedHeaders.Contains(name) || name.StartsWith(':') || name.StartsWith("sec-", StringComparison.OrdinalIgnoreCase))
                continue;
            if (name.Equals("cookie", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var cookie in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    if (cookie.IndexOf('=') is var eq and > 0)
                        credentials.Add($"cookie:{cookie[..eq]}");
                notes.Add("cookies became references; the operation needs the caller's own signed-in session");
                continue;
            }
            if (CredentialRules.LooksLikeCredential(name, value))
            {
                credentials.Add($"header:{name}");
                notes.Add($"header '{name}' became a credential reference");
                continue;
            }
            if (samples.Any(s => !s.Headers.TryGetValue(name, out var v) || v != value))
                return Refuse($"header '{name}' changes between samples with no argument to explain it");
            headers[name] = value;
        }

        // Body.
        string? body = null;
        var bodyType = samples[0].Headers.TryGetValue("content-type", out var ctype) ? ctype.Split(';')[0].Trim() : "application/json";
        if (samples[0].Body is { Length: > 0 })
        {
            var learnedBody = LearnBody(samples, names, bodyType);
            if (learnedBody.Refusal is { } r) return Refuse($"body: {r}");
            body = learnedBody.Template;
            headers.Remove("content-type");
        }

        // Response.
        JsonNode?[] docs;
        try
        {
            docs = samples.Select(s => JsonNode.Parse(s.ResponseBody)).ToArray();
        }
        catch (JsonException)
        {
            return Refuse("the responses are not JSON; learn from the page's data request (XHR/fetch) rather than the page itself");
        }
        var extract = hints.Extract ?? JsonPath.ObjectArrays(docs[0]).Select(a => a.Path).FirstOrDefault() ?? "$";
        var list = hints.List ?? (JsonPath.Select(docs[0], extract) is [JsonArray]);
        List<JsonObject> Items(JsonNode? d) =>
            JsonPath.Select(d, extract).SelectMany(n => n is JsonArray a && list ? a.OfType<JsonObject>() : n is JsonObject o ? [o] : []).ToList();
        var allItems = docs.SelectMany(Items).ToList();
        if (allItems.Count == 0)
            return Refuse($"no data found at '{extract}' in every sample; give hints.extract");
        var shape = allItems.Select(i => i.Where(kv => kv.Value is JsonValue).Select(kv => kv.Key).ToHashSet(StringComparer.Ordinal))
            .Aggregate((a, b) => { a.IntersectWith(b); return a; }).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var fields = (hints.Fields ?? shape.ToDictionary(k => k, k => k, StringComparer.Ordinal))
            .ToDictionary(kv => kv.Key, kv => (JsonNode?)JsonValue.Create(kv.Value), StringComparer.Ordinal);

        var url = $"https://{uris[0].Host}{string.Join("/", pathParts)}{(queryParts.Count > 0 ? "?" + string.Join("&", queryParts) : "")}";
        var op = new OperationSpec
        {
            Kind = samples[0].Method.Equals("GET", StringComparison.OrdinalIgnoreCase) ? "read" : "write",
            Description = $"Learned from {samples.Count} captured requests.",
            Params = names.ToDictionary(n => n, n => new ParamSpec { Pattern = hints.Patterns?.GetValueOrDefault(n) }, StringComparer.Ordinal),
            Request = new RequestSpec { Method = samples[0].Method.ToUpperInvariant(), Url = url, Headers = headers, Body = body, BodyType = bodyType, Credentials = credentials.Distinct(StringComparer.Ordinal).ToList() },
            Response = new ResponseSpec { Format = "json", Extract = extract, List = list, Fields = fields, Shape = shape.Take(5).ToList() },
            Example = new Dictionary<string, string>(samples[^1].Args, StringComparer.Ordinal),
        };
        if (op.Kind == "write")
            notes.Add("a non-GET request was learned as a write: it will be refused unless a caller allows writes with an idempotency key");
        return new LearnOutcome(op, op.Request.Credentials, notes, null);
    }

    private sealed record SlotResult(string? Template, bool IsSlot, string? Refusal);

    /// <summary>Same everywhere → literal; follows exactly one argument (whole value or embedded) → that slot; else refused.</summary>
    private static SlotResult Slot(IReadOnlyList<CapturedExchange> samples, IReadOnlyList<string> values, IReadOnlyList<string> names)
    {
        if (values.Distinct(StringComparer.Ordinal).Count() == 1)
            return new SlotResult(values[0], false, null);
        foreach (var name in names)
        {
            if (Enumerable.Range(0, samples.Count).All(i => values[i] == samples[i].Args[name]))
                return new SlotResult($"{{{name}}}", true, null);
        }
        // An argument embedded in a larger value, with the same text around it in every sample (e.g. "Engineer_JR-{id}").
        foreach (var name in names)
        {
            var parts = Enumerable.Range(0, samples.Count).Select(i =>
            {
                var at = values[i].IndexOf(samples[i].Args[name], StringComparison.Ordinal);
                return at < 0 ? ((string Prefix, string Suffix)?)null : (values[i][..at], values[i][(at + samples[i].Args[name].Length)..]);
            }).ToList();
            if (parts.All(p => p is not null) && parts.Select(p => p!.Value.Prefix).Distinct().Count() == 1 && parts.Select(p => p!.Value.Suffix).Distinct().Count() == 1)
                return new SlotResult($"{parts[0]!.Value.Prefix}{{{name}}}{parts[0]!.Value.Suffix}", true, null);
        }
        return new SlotResult(null, false, "changes between samples, but not as any argument changes");
    }

    private static Dictionary<string, string> ParseQuery(Uri u) =>
        u.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .GroupBy(p => Uri.UnescapeDataString(p[0]), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => Uri.UnescapeDataString((g.First().ElementAtOrDefault(1) ?? "").Replace('+', ' ')), StringComparer.Ordinal);

    private static SlotResult LearnBody(IReadOnlyList<CapturedExchange> samples, IReadOnlyList<string> names, string bodyType)
    {
        if (!bodyType.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            var forms = samples.Select(s => ParseQuery(new Uri("https://x/?" + s.Body))).ToList();
            var parts = new List<string>();
            foreach (var key in forms[0].Keys)
            {
                var slot = Slot(samples, forms.Select(f => f.GetValueOrDefault(key, "")).ToList(), names);
                if (slot.Refusal is { } r) return new SlotResult(null, false, $"field '{key}' {r}");
                if (CredentialRules.LooksLikeCredential(key, forms[0][key])) return new SlotResult(null, false, $"field '{key}' carries a credential");
                parts.Add($"{Uri.EscapeDataString(key)}={(slot.IsSlot ? slot.Template : Uri.EscapeDataString(forms[0][key]))}");
            }
            return new SlotResult(string.Join("&", parts), true, null);
        }
        JsonNode?[] bodies;
        try { bodies = samples.Select(s => JsonNode.Parse(s.Body!)).ToArray(); }
        catch (JsonException) { return new SlotResult(null, false, "is not valid JSON"); }
        var template = bodies[0]!.DeepClone();
        string? refusal = null;
        void Walk(JsonNode? node, string path)
        {
            switch (node)
            {
                case JsonObject o:
                    foreach (var (k, v) in o.ToList())
                    {
                        if (CredentialRules.IsSecretName(k) && v is JsonValue) { refusal ??= $"key '{k}' carries a credential"; continue; }
                        Walk(v, path == "$" ? k : $"{path}.{k}");
                    }
                    break;
                case JsonArray a:
                    for (var i = 0; i < a.Count; i++) Walk(a[i], $"{path}[{i}]");
                    break;
                case JsonValue v when v.TryGetValue<string>(out _):
                    var values = bodies.Select(b => JsonPath.Select(b, path).FirstOrDefault()?.ToString() ?? "").ToList();
                    var slot = Slot(samples, values, names);
                    if (slot.Refusal is { } r) refusal ??= $"'{path}' {r}";
                    else if (slot.IsSlot) ReplaceAt(template, path, slot.Template!);
                    break;
                case JsonValue v:
                    var raw = bodies.Select(b => JsonPath.Select(b, path).FirstOrDefault()?.ToJsonString() ?? "").ToList();
                    if (raw.Distinct(StringComparer.Ordinal).Count() > 1) refusal ??= $"'{path}' changes between samples, but not as any argument changes";
                    break;
            }
        }
        Walk(bodies[0], "$");
        return refusal is null ? new SlotResult(template.ToJsonString(), true, null) : new SlotResult(null, false, refusal);
    }

    private static void ReplaceAt(JsonNode root, string path, string value)
    {
        var parentPath = path.Contains('.') || path.EndsWith(']') ? path[..Math.Max(path.LastIndexOf('.'), path.LastIndexOf('['))] : "$";
        var parent = JsonPath.Select(root, parentPath.Length == 0 ? "$" : parentPath).FirstOrDefault();
        var last = path[(Math.Max(path.LastIndexOf('.'), path.LastIndexOf('[')) + 1)..].TrimEnd(']');
        if (parent is JsonObject o) o[last] = value;
        else if (parent is JsonArray a && int.TryParse(last, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var i)) a[i] = value;
    }

    /// <summary>
    /// Adds a learned operation to a site and verifies it with a live call using arguments the samples did not use.
    /// It is saved only when that call answers <c>ok</c> with data of the learned shape.
    /// </summary>
    public static async Task<(SiteSpec? Saved, WebResult Verification)> VerifyAndSaveAsync(
        WebOperationClient client, string site, string operation, OperationSpec learned, IReadOnlyDictionary<string, string> verifyArgs,
        CallOptions? options = null, CancellationToken ct = default)
    {
        var host = new Uri(learned.Request.Url.Replace("{", "", StringComparison.Ordinal).Replace("}", "", StringComparison.Ordinal)).Host;
        var current = client.Specs.Get(site) ?? new SiteSpec { Site = site, Title = site, Version = 0, Hosts = [host], Source = "learned" };
        if (!current.AllowsHost(host))
            current = current with { Hosts = [.. current.Hosts, host] };
        var candidate = current with
        {
            Operations = new Dictionary<string, OperationSpec>(current.Operations, StringComparer.Ordinal) { [operation] = learned with { Example = new(verifyArgs, StringComparer.Ordinal) } },
            Source = current.Version == 0 ? "learned from captured requests" : $"{current.Source}; learned {operation}",
        };
        candidate.Validate();
        var trial = new SpecStore(includeBundled: false);
        trial.Save(candidate);
        using var trialClient = client.WithSpecs(trial);
        var result = await trialClient.CallAsync(site, operation, verifyArgs, (options ?? new CallOptions()) with { AutoHeal = false, MaxAge = null }, ct).ConfigureAwait(false);
        if (!result.Ok || (learned.Response.List && result.Data is JsonArray { Count: 0 }))
            return (null, result);
        return (client.Specs.Save(candidate), result);
    }
}
