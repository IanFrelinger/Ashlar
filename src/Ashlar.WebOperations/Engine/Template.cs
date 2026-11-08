using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using Ashlar.WebOperations.Specs;

namespace Ashlar.WebOperations;

/// <summary><c>{name}</c> placeholders, filled with values encoded for where they appear.</summary>
public static class Template
{
    private static readonly Regex Placeholder = new(@"\{([A-Za-z_][A-Za-z0-9_]*)\}", RegexOptions.None, SpecJson.RegexBudget);

    /// <summary>Placeholder names in a template.</summary>
    public static IEnumerable<string> Placeholders(string template) =>
        Placeholder.Matches(template).Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal);

    /// <summary>Fills a URL: in the path a value is one segment (slashes encoded); in the query it is a query value.</summary>
    public static string FillUrl(string template, IReadOnlyDictionary<string, string> args)
    {
        var q = template.IndexOf('?', StringComparison.Ordinal);
        var path = q < 0 ? template : template[..q];
        var query = q < 0 ? "" : template[q..];
        // A whole-URL parameter ("{url}") is passed through as given; the caller validated it is an https URL.
        if (path.Trim() is var p && Placeholder.Match(p) is { Success: true } whole && whole.Length == p.Length)
            return args[whole.Groups[1].Value] + Fill(query, args, Uri.EscapeDataString);
        return Fill(path, args, Uri.EscapeDataString) + Fill(query, args, Uri.EscapeDataString);
    }

    /// <summary>Fills a body: JSON-string-escaped for JSON bodies, form-encoded otherwise.</summary>
    public static string FillBody(string template, IReadOnlyDictionary<string, string> args, string contentType) =>
        contentType.Contains("json", StringComparison.OrdinalIgnoreCase)
            ? Fill(template, args, v => JsonEncodedText.Encode(v, JavaScriptEncoder.UnsafeRelaxedJsonEscaping).ToString())
            : Fill(template, args, Uri.EscapeDataString);

    /// <summary>Fills a plain value (for <c>where</c> filters).</summary>
    public static string FillPlain(string template, IReadOnlyDictionary<string, string> args) => Fill(template, args, v => v);

    private static string Fill(string template, IReadOnlyDictionary<string, string> args, Func<string, string> encode)
    {
        var sb = new StringBuilder();
        var last = 0;
        foreach (Match m in Placeholder.Matches(template))
        {
            sb.Append(template, last, m.Index - last);
            sb.Append(args.TryGetValue(m.Groups[1].Value, out var v) ? encode(v) : m.Value);
            last = m.Index + m.Length;
        }
        return sb.Append(template, last, template.Length - last).ToString();
    }
}

/// <summary>
/// A small path language over JSON: <c>$</c> is the root; <c>a.b</c> walks objects; <c>a[0]</c> indexes; <c>a[*]</c>
/// fans out over an array; <c>["odd key"]</c> quotes a key. A path can select several values.
/// </summary>
public static class JsonPath
{
    /// <summary>Every value the path selects (missing branches select nothing).</summary>
    public static IReadOnlyList<System.Text.Json.Nodes.JsonNode?> Select(System.Text.Json.Nodes.JsonNode? root, string path)
    {
        var current = new List<System.Text.Json.Nodes.JsonNode?> { root };
        foreach (var step in Steps(path))
        {
            var next = new List<System.Text.Json.Nodes.JsonNode?>();
            foreach (var node in current)
            {
                switch (step)
                {
                    case { Kind: StepKind.Key } when node is System.Text.Json.Nodes.JsonObject o && o.TryGetPropertyValue(step.Key!, out var v):
                        next.Add(v);
                        break;
                    case { Kind: StepKind.Key } when node is System.Text.Json.Nodes.JsonArray arr:
                        // Lenient: a key applied to an array reads it from each object element (schema.org data puts
                        // one object or an array of them in the same place).
                        foreach (var element in arr)
                            if (element is System.Text.Json.Nodes.JsonObject eo && eo.TryGetPropertyValue(step.Key!, out var ev))
                                next.Add(ev);
                        break;
                    case { Kind: StepKind.Index } when node is System.Text.Json.Nodes.JsonArray a && step.Index < a.Count:
                        next.Add(a[step.Index]);
                        break;
                    case { Kind: StepKind.All } when node is System.Text.Json.Nodes.JsonArray a:
                        next.AddRange(a);
                        break;
                }
            }
            current = next;
        }
        return current;
    }

    /// <summary>Whether the path selects anything.</summary>
    public static bool Exists(System.Text.Json.Nodes.JsonNode? root, string path) => Select(root, path).Count > 0;

    private enum StepKind { Key, Index, All }

    private sealed record Step(StepKind Kind, string? Key = null, int Index = 0);

    private static IEnumerable<Step> Steps(string path)
    {
        var p = path.Trim();
        if (p == "$" || p.Length == 0)
            yield break;
        if (p.StartsWith("$.", StringComparison.Ordinal)) p = p[2..];
        else if (p.StartsWith('$')) p = p[1..];
        var i = 0;
        while (i < p.Length)
        {
            if (p[i] == '.') { i++; continue; }
            if (p[i] == '[')
            {
                var end = p.IndexOf(']', i);
                if (end < 0) throw new FormatException($"unclosed [ in path '{path}'");
                var inner = p[(i + 1)..end].Trim();
                i = end + 1;
                if (inner == "*") yield return new Step(StepKind.All);
                else if (inner.Length >= 2 && inner[0] is '"' or '\'') yield return new Step(StepKind.Key, inner[1..^1]);
                else if (int.TryParse(inner, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var idx)) yield return new Step(StepKind.Index, Index: idx);
                else throw new FormatException($"bad index [{inner}] in path '{path}'");
                continue;
            }
            var start = i;
            while (i < p.Length && p[i] != '.' && p[i] != '[') i++;
            yield return new Step(StepKind.Key, p[start..i]);
        }
    }

    /// <summary>The path to every array of objects in a document, largest first (used by learning and repair).</summary>
    public static IEnumerable<(string Path, System.Text.Json.Nodes.JsonArray Array)> ObjectArrays(System.Text.Json.Nodes.JsonNode? root)
    {
        var found = new List<(string, System.Text.Json.Nodes.JsonArray)>();
        void Walk(System.Text.Json.Nodes.JsonNode? node, string path, int depth)
        {
            if (depth > 12) return;
            switch (node)
            {
                case System.Text.Json.Nodes.JsonArray a:
                    if (a.Count > 0 && a.All(x => x is System.Text.Json.Nodes.JsonObject)) found.Add((path, a));
                    if (a.Count > 0) Walk(a[0], path + "[0]", depth + 1);
                    break;
                case System.Text.Json.Nodes.JsonObject o:
                    foreach (var (k, v) in o)
                        Walk(v, path == "$" ? KeyPath(k) : path + "." + KeyPath(k), depth + 1);
                    break;
            }
        }
        Walk(root, "$", 0);
        return found.OrderByDescending(f => ((System.Text.Json.Nodes.JsonArray)f.Item2).Count).Select(f => (f.Item1, f.Item2));
    }

    private static string KeyPath(string key) =>
        Regex.IsMatch(key, "^[A-Za-z_$][A-Za-z0-9_$-]*$", RegexOptions.None, SpecJson.RegexBudget) ? key : $"[\"{key}\"]";
}
