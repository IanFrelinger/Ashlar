using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Ashlar.WebOperations.Specs;

namespace Ashlar.WebOperations;

/// <summary>Why an answer could not be read the way the spec says.</summary>
public sealed record ExtractionFailure(string Class, string Reason);

/// <summary>Turns a response body into the operation's data, or says why it could not (drift or blocked).</summary>
public static class Extraction
{
    private static readonly Regex LdScript = new(@"<script[^>]*type\s*=\s*[""']application/ld\+json[""'][^>]*>(.*?)</script>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline, SpecJson.RegexBudget);
    private static readonly Regex Challenge = new(@"cf-chl|challenge-platform|captcha|Just a moment\.\.\.|Access denied|unusual traffic",
        RegexOptions.IgnoreCase, SpecJson.RegexBudget);

    /// <summary>Whether a body looks like a bot wall rather than the page.</summary>
    public static bool LooksLikeChallenge(string body) => body.Length < 200_000 && Challenge.IsMatch(body) && !body.TrimStart().StartsWith('{') && !body.TrimStart().StartsWith('[');

    /// <summary>The parsed document for the spec's format, or a failure.</summary>
    public static (JsonNode? Document, ExtractionFailure? Failure) Parse(ResponseSpec r, string body)
    {
        try
        {
            switch (r.Format)
            {
                case "json":
                    return (JsonNode.Parse(body), null);
                case "embedded-json":
                    var m = Regex.Match(body, r.Marker!, RegexOptions.Singleline, SpecJson.RegexBudget);
                    if (!m.Success)
                        return (null, new ExtractionFailure(LooksLikeChallenge(body) ? "blocked" : "drift", "the page no longer carries the embedded data marker"));
                    return (ReadJsonValueAt(body, m.Index + m.Length), null);
                case "json-ld":
                    var items = new JsonArray();
                    foreach (Match s in LdScript.Matches(body))
                    {
                        JsonNode? node;
                        try { node = JsonNode.Parse(WebUtility.HtmlDecode(s.Groups[1].Value.Trim())); }
                        catch (JsonException) { continue; }
                        foreach (var candidate in Flatten(node))
                            if (r.LdType is null || TypeMatches(candidate, r.LdType))
                                items.Add(candidate?.DeepClone());
                    }
                    if (items.Count == 0)
                        return (null, new ExtractionFailure(LooksLikeChallenge(body) ? "blocked" : "input", $"no schema.org {r.LdType ?? "data"} on the page"));
                    return (items, null);
                default:
                    return (null, new ExtractionFailure("error", $"unknown format {r.Format}"));
            }
        }
        catch (JsonException ex)
        {
            return (null, new ExtractionFailure(LooksLikeChallenge(body) ? "blocked" : "drift", $"the answer is not the JSON the spec expects: {ex.Message}"));
        }
    }

    private static IEnumerable<JsonNode?> Flatten(JsonNode? node)
    {
        if (node is JsonArray a) { foreach (var x in a) foreach (var y in Flatten(x)) yield return y; }
        else if (node is JsonObject o && o["@graph"] is JsonArray g) { foreach (var x in g) yield return x; }
        else yield return node;
    }

    private static bool TypeMatches(JsonNode? node, string type) =>
        node is JsonObject o && o["@type"] switch
        {
            JsonValue v => string.Equals(v.ToString(), type, StringComparison.Ordinal),
            JsonArray arr => arr.Any(x => string.Equals(x?.ToString(), type, StringComparison.Ordinal)),
            _ => false,
        };

    /// <summary>Reads one JSON value starting at <paramref name="start"/> (skipping whitespace), honoring strings and nesting.</summary>
    internal static JsonNode? ReadJsonValueAt(string text, int start)
    {
        var i = start;
        while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
        if (i >= text.Length || text[i] is not ('{' or '['))
            throw new JsonException("no JSON object or array follows the marker");
        int depth = 0, begin = i;
        var inString = false;
        for (; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (c == '\\') i++;
                else if (c == '"') inString = false;
                continue;
            }
            if (c == '"') inString = true;
            else if (c is '{' or '[') depth++;
            else if (c is '}' or ']' && --depth == 0)
                return JsonNode.Parse(text.AsSpan(begin, i - begin + 1).ToString());
        }
        throw new JsonException("the embedded JSON value is not closed");
    }

    /// <summary>Selects, filters, checks shape and maps fields. The result is a JSON array (list) or object (single).</summary>
    public static (JsonNode? Data, ExtractionFailure? Failure) Select(ResponseSpec r, JsonNode? document, IReadOnlyDictionary<string, string> args)
    {
        var selected = JsonPath.Select(document, r.Extract);
        if (selected.Count == 0)
            return (null, new ExtractionFailure("drift", $"the answer no longer has '{r.Extract}'"));
        // A list (or a filtered single) reads the items of the selected array; otherwise the selection itself.
        var items = (r.List || r.Where.Count > 0) && selected.Count == 1 && selected[0] is JsonArray arr ? arr.ToList() : selected.ToList();
        foreach (var (field, valueTemplate) in r.Where)
        {
            var want = Template.FillPlain(valueTemplate, args);
            items = items.Where(i => JsonPath.Select(i, field).Any(v => string.Equals(Scalar(v), want, StringComparison.Ordinal))).ToList();
        }
        if (!r.List)
            items = items.Take(1).ToList();
        if (items.Count == 0)
            return r.EmptyIsInput ? (null, new ExtractionFailure("input", "nothing matches these arguments")) : (new JsonArray(), null);
        foreach (var item in items)
        {
            var missing = r.Shape.Where(k => !JsonPath.Exists(item, k)).ToList();
            if (missing.Count > 0)
                return (null, new ExtractionFailure("drift", $"items no longer carry: {string.Join(", ", missing)}"));
        }
        var mapped = items.Select(i => r.Fields.Count == 0 ? i?.DeepClone() : Map(i, r.Fields)).ToList();
        return r.List ? (new JsonArray(mapped.ToArray()), null) : (mapped[0], null);
    }

    private static JsonObject Map(JsonNode? item, Dictionary<string, JsonNode?> fields)
    {
        var o = new JsonObject();
        foreach (var (name, def) in fields)
        {
            string path;
            string? transform = null;
            if (def is JsonObject spec)
            {
                path = spec["path"]?.GetValue<string>() ?? throw new InvalidDataException($"field {name} has no path");
                transform = spec["as"]?.GetValue<string>();
            }
            else
            {
                path = def?.GetValue<string>() ?? throw new InvalidDataException($"field {name} has no path");
            }
            var values = JsonPath.Select(item, path).Where(v => v is not null).ToList();
            if (values.Count == 0)
                continue; // absent means "the source does not say"
            o[name] = Transform(values, transform);
        }
        return o;
    }

    private static JsonNode? Transform(List<JsonNode?> values, string? transform)
    {
        if (transform is null)
            return values.Count == 1 ? values[0]!.DeepClone() : JsonValue.Create(string.Join("; ", values.Select(Scalar).Where(s => s.Length > 0).Distinct(StringComparer.Ordinal)));
        var first = values[0]!;
        return transform switch
        {
            "date" => JsonValue.Create(Scalar(first) is { Length: >= 10 } d ? d[..10] : Scalar(first)),
            "epoch-ms-date" => first is JsonValue v && v.TryGetValue<long>(out var ms)
                ? JsonValue.Create(DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)) : null,
            "html-text" => JsonValue.Create(HtmlText(string.Join("\n", values.Select(Scalar)))),
            "cents" => first is JsonValue c && c.TryGetValue<double>(out var cents) ? JsonValue.Create(cents / 100) : null,
            "text" => JsonValue.Create(string.Join("; ", values.Select(Scalar).Where(s => s.Length > 0).Distinct(StringComparer.Ordinal))),
            _ => throw new InvalidDataException($"unknown transform '{transform}'"),
        };
    }

    /// <summary>A node as text: strings as-is, other values as JSON.</summary>
    public static string Scalar(JsonNode? v) => v switch
    {
        null => "",
        JsonValue jv when jv.TryGetValue<string>(out var s) => s,
        _ => v.ToJsonString(),
    };

    /// <summary>Plain text from HTML (entity-escaped HTML too): list items become "- " lines; tags dropped.</summary>
    public static string HtmlText(string html)
    {
        var t = WebUtility.HtmlDecode(html); // a second decode handles double-escaped markup
        if (t.Contains("&lt;", StringComparison.Ordinal)) t = WebUtility.HtmlDecode(t);
        t = Regex.Replace(t, @"<\s*(br|/p|/li|/h\d|/div|/tr)\s*/?>", "\n", RegexOptions.IgnoreCase, SpecJson.RegexBudget);
        t = Regex.Replace(t, @"<li[^>]*>", "\n- ", RegexOptions.IgnoreCase, SpecJson.RegexBudget);
        t = Regex.Replace(t, "<[^>]+>", "", RegexOptions.None, SpecJson.RegexBudget);
        t = WebUtility.HtmlDecode(t).Replace(' ', ' ');
        return Regex.Replace(Regex.Replace(t, @"[ \t]+\n", "\n", RegexOptions.None, SpecJson.RegexBudget), @"\n{3,}", "\n\n", RegexOptions.None, SpecJson.RegexBudget).Trim();
    }
}
