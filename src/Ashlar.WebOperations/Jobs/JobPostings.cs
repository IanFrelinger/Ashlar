using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Ashlar.WebOperations.Specs;

namespace Ashlar.WebOperations.Jobs;

/// <summary>How to re-read one posting: a site operation and its arguments.</summary>
public sealed record PostingRef(string Site, string Operation, IReadOnlyDictionary<string, string> Args)
{
    /// <summary>A stable text form, for storage and deduplication.</summary>
    public string Key() => $"{Site}.{Operation}:" + string.Join("&", Args.OrderBy(a => a.Key, StringComparer.Ordinal).Select(a => $"{a.Key}={Uri.EscapeDataString(a.Value)}"));

    /// <summary>Parses <see cref="Key"/>.</summary>
    public static PostingRef? TryParse(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return null;
        var colon = key.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0)
            return null;
        var head = key[..colon];
        var dot = head.IndexOf('.', StringComparison.Ordinal);
        if (dot <= 0)
            return null;
        var args = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in key[(colon + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0) return null;
            args[pair[..eq]] = Uri.UnescapeDataString(pair[(eq + 1)..]);
        }
        return new PostingRef(head[..dot], head[(dot + 1)..], args);
    }
}

/// <summary>One posting, normalized across sites. Every field is what the source said; null means it does not say.</summary>
public sealed record JobPosting(
    string Employer, string Title, string? Location, string? Url, string? RequisitionId, string? WorkArrangement, string? WorkArrangementLabel,
    string? PayText, string? PostedDate, string? Deadline, string Description, string? RemoteEligibility, PostingRef Source, bool ProvesOpen);

/// <summary>A board entry from a list operation: enough to filter by title and to fetch details.</summary>
public sealed record BoardEntry(string Employer, string Title, string? Location, string? Url, string? WorkArrangement, string? PostedDate, string? PayText, PostingRef Source);

/// <summary>An employer board: <c>greenhouse:acme</c>, or <c>lever:acme|Acme Robotics</c> with a display name.</summary>
public sealed record BoardRef(string Site, string Slug, string Employer)
{
    /// <summary>Sites with a list operation.</summary>
    public static readonly string[] Sites = ["greenhouse", "lever", "ashby"];

    /// <summary>Parses a board spec.</summary>
    public static bool TryParse(string? spec, out BoardRef? board, out string error)
    {
        board = null;
        error = "";
        var text = spec?.Trim() ?? "";
        var bar = text.IndexOf('|', StringComparison.Ordinal);
        var head = bar >= 0 ? text[..bar] : text;
        var label = bar >= 0 ? text[(bar + 1)..].Trim() : "";
        var colon = head.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0) { error = "use greenhouse:<slug>, lever:<slug> or ashby:<slug>"; return false; }
        var site = head[..colon].Trim().ToLowerInvariant();
        var slug = head[(colon + 1)..].Trim();
        if (!Sites.Contains(site)) { error = $"'{site}' has no board listing; supported boards are {string.Join(", ", Sites)} (add single postings by URL instead)"; return false; }
        if (!Regex.IsMatch(slug, "^[A-Za-z0-9._-]+$", RegexOptions.None, SpecJson.RegexBudget) || slug.Contains("..", StringComparison.Ordinal))
        { error = "the board slug may contain only letters, digits, '.', '_' and '-'"; return false; }
        board = new BoardRef(site, slug, label.Length > 0 ? label : JobPostings.Pretty(slug));
        return true;
    }
}

/// <summary>Turns the canonical fields the bundled job specs emit into postings and board entries.</summary>
public static class JobPostings
{
    private static readonly (string Site, Regex Pattern, string IdArg)[] AtsUrls =
    [
        ("greenhouse", new Regex(@"^https://(?:job-boards|boards)\.greenhouse\.io/([A-Za-z0-9_-]+)/jobs/(\d+)(?:[/?#]|$)", RegexOptions.IgnoreCase, SpecJson.RegexBudget), "jobId"),
        ("lever", new Regex(@"^https://jobs\.lever\.co/([A-Za-z0-9._-]+)/([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})(?:[/?#]|$)", RegexOptions.IgnoreCase, SpecJson.RegexBudget), "postingId"),
        ("ashby", new Regex(@"^https://jobs\.ashbyhq\.com/([A-Za-z0-9._-]+)/([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})(?:[/?#]|$)", RegexOptions.IgnoreCase, SpecJson.RegexBudget), "jobId"),
    ];

    private static readonly Regex Requisition = new(
        @"\b(?:req\.?\s*(?:id|#|no\.?|number)|requisition(?:\s*(?:id|#|no\.?|number))?|job\s*id)\s*[:#]?\s*([A-Z0-9][A-Z0-9-]*\d[A-Z0-9-]*)\b",
        RegexOptions.IgnoreCase, SpecJson.RegexBudget);

    internal static string Pretty(string slug) =>
        CultureInfo.InvariantCulture.TextInfo.ToTitleCase(Regex.Replace(slug, "[-_.]+", " ", RegexOptions.None, SpecJson.RegexBudget).ToLowerInvariant());

    /// <summary>
    /// The read for a pasted posting URL: the employer's applicant-tracking API when the URL is one of theirs, otherwise
    /// the page's own schema.org JobPosting. Null for anything that is not an https URL.
    /// </summary>
    public static (PostingRef Ref, string? Employer)? FromUrl(string? url)
    {
        var text = url?.Trim() ?? "";
        foreach (var (site, pattern, idArg) in AtsUrls)
            if (pattern.Match(text) is { Success: true } m)
                return (new PostingRef(site, "getJob", new Dictionary<string, string>(StringComparer.Ordinal) { ["company"] = m.Groups[1].Value, [idArg] = site == "lever" ? m.Groups[2].Value.ToLowerInvariant() : m.Groups[2].Value }), Pretty(m.Groups[1].Value));
        if (Uri.TryCreate(text, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(u.UserInfo))
            return (new PostingRef("schema-org", "getJobPosting", new Dictionary<string, string>(StringComparer.Ordinal) { ["url"] = u.AbsoluteUri }), null);
        return null;
    }

    private static string? S(JsonObject o, string key) => o[key] is { } v ? Extraction.Scalar(v) is { Length: > 0 } s ? s : null : null;

    private static decimal? D(JsonObject o, string key) =>
        o[key] is JsonValue v && decimal.TryParse(Extraction.Scalar(v), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    /// <summary>remote, hybrid or onsite from a source's own label; null when unstated or not one of these.</summary>
    public static string? Arrangement(string? value) =>
        (value ?? "").Trim().ToLowerInvariant().Replace("-", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal).Replace("_", "", StringComparison.Ordinal) switch
        {
            "remote" or "telecommute" => "remote",
            "hybrid" => "hybrid",
            "onsite" or "inoffice" => "onsite",
            _ => null,
        };

    private static string? ArrangementFromText(string? location, string description)
    {
        var text = $"{location}\n{description}".ToLowerInvariant();
        if (text.Contains("hybrid", StringComparison.Ordinal)) return "hybrid";
        if (Regex.IsMatch(text, @"\bremote\b", RegexOptions.None, SpecJson.RegexBudget)) return "remote";
        if (Regex.IsMatch(text, @"\bon-?site\b|\bin[- ]office\b", RegexOptions.None, SpecJson.RegexBudget)) return "onsite";
        return null;
    }

    /// <summary>A requisition id stated in text ("Req ID: AA-2210"): a label is required and the id must contain a digit.</summary>
    public static string? RequisitionFromText(string text) => Requisition.Match(text) is { Success: true } m ? m.Groups[1].Value : null;

    private static string? Pay(JsonObject o)
    {
        if (S(o, "payText") is { } text) return text;
        if (D(o, "payMin") is not { } min) return null;
        var max = D(o, "payMax") ?? min;
        var interval = (S(o, "payInterval") ?? "").Replace("per-", "", StringComparison.Ordinal).ToLowerInvariant();
        return string.Create(CultureInfo.InvariantCulture, $"{S(o, "payCurrency")} {min:N0}–{max:N0}{(interval.Length > 0 ? $" per {interval}" : "")}").Trim();
    }

    /// <summary>A board entry from one item of a list operation.</summary>
    public static BoardEntry Entry(BoardRef board, JsonObject item)
    {
        var id = S(item, "id") ?? "";
        var idArg = board.Site switch { "lever" => "postingId", _ => "jobId" };
        var desc = S(item, "description") ?? "";
        return new BoardEntry(board.Employer, S(item, "title") ?? "", S(item, "location"), S(item, "url"),
            Arrangement(S(item, "workplaceType")) ?? (S(item, "isRemote") == "true" ? "remote" : null) ?? (desc.Length > 0 ? ArrangementFromText(S(item, "location"), desc) : null),
            S(item, "postedAt") ?? S(item, "updatedAt"), Pay(item),
            new PostingRef(board.Site, "getJob", new Dictionary<string, string>(StringComparer.Ordinal) { ["company"] = board.Slug, [idArg] = id }));
    }

    /// <summary>A posting from the data of a get operation.</summary>
    /// <param name="source">What was read.</param>
    /// <param name="employer">Display name, or null for the source's own (or the slug title-cased).</param>
    /// <param name="data">The operation's data.</param>
    /// <param name="today">For a declared expiry date.</param>
    public static JobPosting? Posting(PostingRef source, string? employer, JsonNode? data, DateOnly today)
    {
        if (data is not JsonObject o)
            return null;
        source.Args.TryGetValue("company", out var company);
        var desc = string.Join("\n", new[] { S(o, "description"), S(o, "sections"), S(o, "additional") }.Where(x => !string.IsNullOrWhiteSpace(x)));
        var location = S(o, "location") ?? (string.Join(", ", new[] { S(o, "city"), S(o, "region"), S(o, "country") }.Where(x => x is not null)) is { Length: > 0 } l ? l : null);
        var label = S(o, "workplaceType");
        var arrangement = Arrangement(label) ?? (S(o, "isRemote") == "true" ? "remote" : null) ?? ArrangementFromText(location, desc);
        var deadline = S(o, "deadline");
        var expired = deadline is not null && DateOnly.TryParseExact(deadline, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) && d < today;
        return new JobPosting(
            employer ?? S(o, "employer") ?? (company is null ? "Unknown employer" : Pretty(company)),
            S(o, "title") ?? "", location, S(o, "url") ?? source.Args.GetValueOrDefault("url"),
            S(o, "requisitionId") ?? RequisitionFromText(desc), arrangement, arrangement is null ? label : null,
            Pay(o), S(o, "postedAt") ?? S(o, "updatedAt"), deadline, desc, S(o, "remoteEligibility"), source,
            ProvesOpen: !expired);
    }
}
