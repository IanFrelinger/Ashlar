using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Ashlar.WebOperations.Specs;

/// <summary>
/// One website, declared: which hosts it may reach and which operations it offers. A spec is data, never code, and it
/// never holds a credential: a header, cookie or query value that is a secret appears only as a reference
/// (<c>cookie:NAME</c>, <c>header:NAME</c>, <c>query:NAME</c>) that the caller resolves per call.
/// </summary>
public sealed record SiteSpec
{
    /// <summary>The site's name, e.g. <c>lever</c>.</summary>
    public string Site { get; init; } = "";
    /// <summary>Human title.</summary>
    public string Title { get; init; } = "";
    /// <summary>Version, incremented by every learn or repair.</summary>
    public int Version { get; init; } = 1;
    /// <summary>Hosts requests may go to. <c>*</c> allows any https host (read operations only; the egress guard still decides).</summary>
    public List<string> Hosts { get; init; } = [];
    /// <summary>Where the operations come from (documentation, a learned capture, a repair).</summary>
    public string Source { get; init; } = "";
    /// <summary>Caveats a caller should read.</summary>
    public string Notes { get; init; } = "";
    /// <summary>Operations by name.</summary>
    public Dictionary<string, OperationSpec> Operations { get; init; } = new(StringComparer.Ordinal);

    /// <summary>The operation, or an exception naming what exists.</summary>
    public OperationSpec Operation(string name) =>
        Operations.TryGetValue(name, out var op) ? op : throw new KeyNotFoundException($"{Site} has no operation '{name}' (has: {string.Join(", ", Operations.Keys)})");

    /// <summary>Whether <paramref name="host"/> is one this site may reach.</summary>
    public bool AllowsHost(string host) =>
        Hosts.Any(h => h == "*" || string.Equals(h, host, StringComparison.OrdinalIgnoreCase));

    /// <summary>Parses and validates a spec.</summary>
    public static SiteSpec Parse(string json)
    {
        var spec = JsonSerializer.Deserialize<SiteSpec>(json, SpecJson.Options) ?? throw new InvalidDataException("empty spec");
        spec.Validate();
        return spec;
    }

    /// <summary>Serializes.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, SpecJson.Indented);

    /// <summary>Throws <see cref="InvalidDataException"/> on a malformed spec.</summary>
    public void Validate()
    {
        if (!Regex.IsMatch(Site, "^[a-z0-9][a-z0-9.-]{0,62}$", RegexOptions.None, SpecJson.RegexBudget))
            throw new InvalidDataException($"site name '{Site}' must be lowercase letters, digits, '.' or '-'");
        if (Hosts.Count == 0)
            throw new InvalidDataException($"{Site}: no hosts declared");
        foreach (var (name, op) in Operations)
        {
            if (op.Kind is not ("read" or "write"))
                throw new InvalidDataException($"{Site}.{name}: kind must be read or write");
            if (op.Kind == "write" && Hosts.Contains("*"))
                throw new InvalidDataException($"{Site}.{name}: a write may not target any host (*)");
            var placeholders = Template.Placeholders(op.Request.Url).Concat(op.Request.Body is null ? [] : Template.Placeholders(op.Request.Body));
            foreach (var p in placeholders)
                if (!op.Params.ContainsKey(p))
                    throw new InvalidDataException($"{Site}.{name}: template uses {{{p}}} but no such param is declared");
            foreach (var (header, value) in op.Request.Headers)
                if (CredentialRules.LooksLikeCredential(header, value))
                    throw new InvalidDataException($"{Site}.{name}: header '{header}' holds what looks like a credential; use a reference in 'credentials'");
            foreach (var reference in op.Request.Credentials)
                if (!Regex.IsMatch(reference, "^(cookie|header|query):[A-Za-z0-9_.-]+$", RegexOptions.None, SpecJson.RegexBudget))
                    throw new InvalidDataException($"{Site}.{name}: credential reference '{reference}' must be cookie:NAME, header:NAME or query:NAME");
            if (op.Response.Format is not ("json" or "embedded-json" or "json-ld"))
                throw new InvalidDataException($"{Site}.{name}: response format must be json, embedded-json or json-ld");
            if (op.Response.Format == "embedded-json" && string.IsNullOrEmpty(op.Response.Marker))
                throw new InvalidDataException($"{Site}.{name}: embedded-json needs a marker");
        }
    }
}

/// <summary>One operation: a templated request and how to read its answer.</summary>
public sealed record OperationSpec
{
    /// <summary>read or write. Writes are refused unless the caller allows them, and are never retried.</summary>
    public string Kind { get; init; } = "read";
    /// <summary>What it returns.</summary>
    public string Description { get; init; } = "";
    /// <summary>Parameters by name.</summary>
    public Dictionary<string, ParamSpec> Params { get; init; } = new(StringComparer.Ordinal);
    /// <summary>The request.</summary>
    public RequestSpec Request { get; init; } = new();
    /// <summary>The answer.</summary>
    public ResponseSpec Response { get; init; } = new();
    /// <summary>Statuses that mean "nothing here for these arguments" (closed, unknown): class <c>input</c>.</summary>
    public List<int> InputStatuses { get; init; } = [404, 410];
    /// <summary>Pagination, when the operation is paged.</summary>
    public PagingSpec? Paging { get; init; }
    /// <summary>Arguments that answered when the operation was learned or written; used to verify a repair.</summary>
    public Dictionary<string, string> Example { get; init; } = new(StringComparer.Ordinal);
}

/// <summary>A parameter.</summary>
public sealed record ParamSpec
{
    /// <summary>Whether it must be given.</summary>
    public bool Required { get; init; } = true;
    /// <summary>A regex the value must match in full.</summary>
    public string? Pattern { get; init; }
    /// <summary>Default when absent.</summary>
    public string? Default { get; init; }
    /// <summary>What it is.</summary>
    public string Description { get; init; } = "";
}

/// <summary>The request template. <c>{param}</c> is replaced, encoded for where it appears.</summary>
public sealed record RequestSpec
{
    /// <summary>GET, POST, ...</summary>
    public string Method { get; init; } = "GET";
    /// <summary>Absolute https URL template.</summary>
    public string Url { get; init; } = "";
    /// <summary>Constant headers (never credentials).</summary>
    public Dictionary<string, string> Headers { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Body template, or null.</summary>
    public string? Body { get; init; }
    /// <summary>Body content type when there is a body.</summary>
    public string BodyType { get; init; } = "application/json";
    /// <summary>Credential references resolved per call (cookie:NAME, header:NAME, query:NAME).</summary>
    public List<string> Credentials { get; init; } = [];
}

/// <summary>How to read the answer.</summary>
public sealed record ResponseSpec
{
    /// <summary>json (the body), embedded-json (a JSON value inside an HTML page, after <see cref="Marker"/>), json-ld (schema.org script blocks).</summary>
    public string Format { get; init; } = "json";
    /// <summary>For embedded-json: a regex whose match ends where the JSON value begins.</summary>
    public string? Marker { get; init; }
    /// <summary>For json-ld: the @type to keep, e.g. JobPosting.</summary>
    public string? LdType { get; init; }
    /// <summary>Path to the data inside the parsed document (<c>$</c> for the whole).</summary>
    public string Extract { get; init; } = "$";
    /// <summary>Whether the data is a list of items.</summary>
    public bool List { get; init; }
    /// <summary>Keep only items whose field equals the value (templated), e.g. {"id": "{jobId}"}.</summary>
    public Dictionary<string, string> Where { get; init; } = new(StringComparer.Ordinal);
    /// <summary>input: an empty result means "nothing here for these arguments" rather than an empty answer.</summary>
    public bool EmptyIsInput { get; init; }
    /// <summary>Output fields: name → path, or {"path": ..., "as": transform}. Empty keeps each item whole.</summary>
    public Dictionary<string, JsonNode?> Fields { get; init; } = new(StringComparer.Ordinal);
    /// <summary>Raw keys every item must carry; a missing one is drift, not an empty value.</summary>
    public List<string> Shape { get; init; } = [];
}

/// <summary>Page-number pagination.</summary>
public sealed record PagingSpec
{
    /// <summary>The parameter carrying the page.</summary>
    public string Param { get; init; } = "page";
    /// <summary>First page number.</summary>
    public int Start { get; init; } = 1;
}

/// <summary>Shared JSON settings for specs.</summary>
public static class SpecJson
{
    /// <summary>Regex time budget used throughout.</summary>
    public static readonly TimeSpan RegexBudget = TimeSpan.FromSeconds(1);
    /// <summary>Compact.</summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
    /// <summary>Indented.</summary>
    public static readonly JsonSerializerOptions Indented = new(Options) { WriteIndented = true, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
}

/// <summary>What looks like a secret. Used to refuse specs and captures that would store one.</summary>
public static class CredentialRules
{
    private static readonly Regex SecretName = new(@"(^|[-_.])(auth|authorization|cookie|token|secret|password|passwd|session|sid|api[-_]?key|apikey|access[-_]?key|signature|sig|csrf|xsrf|bearer|jwt|key)([-_.]|$)",
        RegexOptions.IgnoreCase, SpecJson.RegexBudget);
    private static readonly Regex HighEntropy = new(@"^[A-Za-z0-9+/=_\-.]{24,}$", RegexOptions.None, SpecJson.RegexBudget);

    /// <summary>A name that carries secrets (Authorization, X-Api-Key, session_id, csrf_token...).</summary>
    public static bool IsSecretName(string name) => SecretName.IsMatch(name) || name.Equals("cookie", StringComparison.OrdinalIgnoreCase);

    /// <summary>A value that looks like a token: long, no spaces, mixed letters and digits.</summary>
    public static bool LooksLikeToken(string value) =>
        HighEntropy.IsMatch(value) && value.Any(char.IsDigit) && value.Any(char.IsLetter) && !value.Contains("://", StringComparison.Ordinal);

    /// <summary>A name/value pair that should never be stored literally.</summary>
    public static bool LooksLikeCredential(string name, string value) => IsSecretName(name) || LooksLikeToken(value);
}
