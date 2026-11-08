using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Ashlar.Bricks.ApiAnything.Jobs;

/// <summary>How to re-read one posting at its source: an api-anything <c>getJob</c> call.</summary>
/// <param name="Site">greenhouse, lever, ashby, workable or workday.</param>
/// <param name="Args">The <c>getJob</c> arguments.</param>
public sealed record PostingRef(string Site, IReadOnlyDictionary<string, string> Args)
{
    /// <summary>A stable text form, for storage and deduplication.</summary>
    /// <returns>The key.</returns>
    public string Key() => Site + ":" + string.Join("&", Args.OrderBy(a => a.Key, StringComparer.Ordinal).Select(a => $"{a.Key}={a.Value}"));
}

/// <summary>One posting, normalized across sites. Every field is what the source said; absent means not stated.</summary>
/// <param name="Employer">Employer display name (the board's label, or the slug title-cased).</param>
/// <param name="Title">Posting title.</param>
/// <param name="Location">Location text as the source displays it.</param>
/// <param name="Url">The posting's public URL.</param>
/// <param name="RequisitionId">A requisition number when the source states one.</param>
/// <param name="WorkArrangement">remote, hybrid or onsite when the source states it.</param>
/// <param name="WorkArrangementLabel">The source's own label when it is not one of those (Workday's <c>remoteType</c>).</param>
/// <param name="PayText">Pay as the source states it.</param>
/// <param name="PostedDate">Posting or creation date (yyyy-MM-dd) when the source states it.</param>
/// <param name="Description">Plain-text description; empty when the operation returns none.</param>
/// <param name="Source">The getJob call that re-reads it.</param>
/// <param name="ProvesOpen">Whether a successful read is evidence the posting is open (false for Workday, whose op notes say it is not).</param>
public sealed record JobPosting(
    string Employer,
    string Title,
    string? Location,
    string? Url,
    string? RequisitionId,
    string? WorkArrangement,
    string? WorkArrangementLabel,
    string? PayText,
    string? PostedDate,
    string Description,
    PostingRef Source,
    bool ProvesOpen);

/// <summary>A board entry from <c>listJobs</c>: enough to filter by title and to fetch details.</summary>
/// <param name="Employer">Employer display name.</param>
/// <param name="Title">Posting title.</param>
/// <param name="Location">Location text.</param>
/// <param name="Url">Posting URL.</param>
/// <param name="WorkArrangement">remote, hybrid or onsite when stated.</param>
/// <param name="PostedDate">yyyy-MM-dd when stated.</param>
/// <param name="PayText">Pay when the list states it (Ashby).</param>
/// <param name="Source">The getJob call for its details.</param>
public sealed record BoardEntry(
    string Employer, string Title, string? Location, string? Url, string? WorkArrangement, string? PostedDate, string? PayText, PostingRef Source);

/// <summary>An employer board: <c>greenhouse:acme</c>, or <c>lever:acme|Acme Robotics</c> with a display name.</summary>
public sealed record BoardRef(string Site, string Slug, string Employer)
{
    private static readonly Regex SlugRule = new("^[A-Za-z0-9._-]+$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>Parses a board spec.</summary>
    /// <param name="spec">The spec.</param>
    /// <param name="board">The board, when valid.</param>
    /// <param name="error">Why not, when invalid.</param>
    /// <returns>Whether it parsed.</returns>
    public static bool TryParse(string? spec, out BoardRef? board, out string error)
    {
        board = null;
        error = "";
        var text = spec?.Trim() ?? "";
        var bar = text.IndexOf('|', StringComparison.Ordinal);
        var head = bar >= 0 ? text[..bar] : text;
        var label = bar >= 0 ? text[(bar + 1)..].Trim() : "";
        var colon = head.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0)
        {
            error = "use greenhouse:<slug>, lever:<slug> or ashby:<slug>";
            return false;
        }
        var site = head[..colon].Trim().ToLowerInvariant();
        var slug = head[(colon + 1)..].Trim();
        if (site is not ("greenhouse" or "lever" or "ashby"))
        {
            error = $"'{site}' has no list operation; supported boards are greenhouse, lever and ashby (add single postings by URL instead)";
            return false;
        }
        if (!SlugRule.IsMatch(slug))
        {
            error = "the board slug may contain only letters, digits, '.', '_' and '-'";
            return false;
        }
        board = new BoardRef(site, slug, label.Length > 0 ? label : JobSources.Pretty(slug));
        return true;
    }
}

/// <summary>Turns api-anything results for the ATS sites into <see cref="JobPosting"/> and <see cref="BoardEntry"/>.</summary>
public static class JobSources
{
    private static readonly TimeSpan RegexBudget = TimeSpan.FromSeconds(1);

    private static readonly (string Site, Regex Pattern)[] UrlPatterns =
    [
        ("greenhouse", new Regex(@"^https://(?:job-boards|boards)\.greenhouse\.io/([A-Za-z0-9_-]+)/jobs/(\d+)(?:[/?#]|$)", RegexOptions.IgnoreCase, RegexBudget)),
        ("lever", new Regex(@"^https://jobs\.lever\.co/([A-Za-z0-9._-]+)/([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})(?:[/?#]|$)", RegexOptions.IgnoreCase, RegexBudget)),
        ("ashby", new Regex(@"^https://jobs\.ashbyhq\.com/([A-Za-z0-9._-]+)/([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})(?:[/?#]|$)", RegexOptions.IgnoreCase, RegexBudget)),
        ("workable", new Regex(@"^https://apply\.workable\.com/([A-Za-z0-9_-]+)/j/([A-Za-z0-9]+)(?:[/?#]|$)", RegexOptions.IgnoreCase, RegexBudget)),
        ("workday", new Regex(@"^https://workday\.wd5\.myworkdayjobs\.com/.+/job/.+/([A-Za-z0-9-]+_JR-\d+)(?:[/?#]|$)", RegexOptions.IgnoreCase, RegexBudget)),
    ];

    private static readonly Regex Requisition = new(
        // A label is required ("Req ID", "Req #", "Requisition", "Job ID") and the id must contain a digit, so the word
        // "Requirements" is never read as requisition "uirements".
        @"\b(?:req\.?\s*(?:id|#|no\.?|number)|requisition(?:\s*(?:id|#|no\.?|number))?|job\s*id)\s*[:#]?\s*([A-Z0-9][A-Z0-9-]*\d[A-Z0-9-]*)\b", RegexOptions.IgnoreCase, RegexBudget);

    /// <summary>The getJob call for a pasted posting URL, or <see langword="null"/> when the site is not supported.</summary>
    /// <param name="url">The URL.</param>
    /// <returns>The ref and the employer slug title-cased.</returns>
    public static (PostingRef Ref, string Employer)? FromUrl(string? url)
    {
        var text = url?.Trim() ?? "";
        foreach (var (site, pattern) in UrlPatterns)
        {
            var m = pattern.Match(text);
            if (!m.Success)
                continue;
            var a = m.Groups[1].Value;
            var b = m.Groups.Count > 2 ? m.Groups[2].Value : "";
            return site switch
            {
                "greenhouse" => (new PostingRef(site, Args(("company", a), ("jobId", b))), Pretty(a)),
                "lever" => (new PostingRef(site, Args(("company", a), ("postingId", b.ToLowerInvariant()))), Pretty(a)),
                "ashby" => (new PostingRef(site, Args(("company", a), ("jobId", b))), Pretty(a)),
                "workable" => (new PostingRef(site, Args(("company", a), ("shortcode", b))), Pretty(a)),
                _ => (new PostingRef(site, Args(("job", a))), "Workday"),
            };
        }
        return null;
    }

    internal static IReadOnlyDictionary<string, string> Args(params (string Key, string Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

    internal static string Pretty(string slug) =>
        CultureInfo.InvariantCulture.TextInfo.ToTitleCase(Regex.Replace(slug, "[-_.]+", " ", RegexOptions.None, RegexBudget).ToLowerInvariant());

    /// <summary>Plain text from posting HTML: list items become "- " lines, tags are dropped, entities decoded.</summary>
    /// <param name="html">HTML, or <see langword="null"/>.</param>
    /// <returns>The text.</returns>
    public static string StripHtml(string? html)
    {
        if (string.IsNullOrEmpty(html))
            return "";
        var text = Regex.Replace(html, @"<\s*(br|/p|/li|/h\d|/div)\s*/?>", "\n", RegexOptions.IgnoreCase, RegexBudget);
        text = Regex.Replace(text, @"<li[^>]*>", "\n- ", RegexOptions.IgnoreCase, RegexBudget);
        text = Regex.Replace(text, "<[^>]+>", "", RegexOptions.None, RegexBudget);
        text = WebUtility.HtmlDecode(text);
        return Regex.Replace(text, @"\n{3,}", "\n\n", RegexOptions.None, RegexBudget).Trim();
    }

    /// <summary>remote, hybrid or onsite from a site's workplace field.</summary>
    /// <param name="value">The field.</param>
    /// <returns>The arrangement, or <see langword="null"/> when unstated or unknown.</returns>
    public static string? Arrangement(string? value) =>
        (value ?? "").Trim().ToLowerInvariant().Replace("-", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal) switch
        {
            "remote" => "remote",
            "hybrid" => "hybrid",
            "onsite" or "inoffice" => "onsite",
            _ => null,
        };

    private static string? ArrangementFromText(string? location, string description)
    {
        var text = $"{location}\n{description}".ToLowerInvariant();
        if (text.Contains("hybrid", StringComparison.Ordinal))
            return "hybrid";
        if (Regex.IsMatch(text, @"\bremote\b", RegexOptions.None, RegexBudget))
            return "remote";
        if (Regex.IsMatch(text, @"\bon-?site\b|\bin[- ]office\b", RegexOptions.None, RegexBudget))
            return "onsite";
        return null;
    }

    private static string? RequisitionFrom(string text)
    {
        var m = Requisition.Match(text);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Number => v.GetRawText(),
                _ => null,
            }
            : null;

    private static string? EpochMsToDate(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? DateTimeOffset.FromUnixTimeMilliseconds(v.GetInt64()).UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : null;

    private static string? DatePrefix(string? value) => value is { Length: >= 10 } ? value[..10] : null;

    /// <summary>Board entries from a <c>listJobs</c> result's data.</summary>
    /// <param name="board">The board.</param>
    /// <param name="data">The result's <c>data</c>.</param>
    /// <returns>The entries.</returns>
    public static IReadOnlyList<BoardEntry> ParseBoard(BoardRef board, JsonElement? data)
    {
        ArgumentNullException.ThrowIfNull(board);
        var list = new List<BoardEntry>();
        if (data is not { ValueKind: JsonValueKind.Array } items)
            return list;
        foreach (var item in items.EnumerateArray())
        {
            var title = Str(item, "title") ?? "";
            switch (board.Site)
            {
                case "greenhouse":
                {
                    var id = Str(item, "jobId") ?? "";
                    list.Add(new BoardEntry(board.Employer, title, Str(item, "location"),
                        Str(item, "url") ?? $"https://job-boards.greenhouse.io/{board.Slug}/jobs/{id}", null,
                        DatePrefix(Str(item, "updatedAt")), null, new PostingRef("greenhouse", Args(("company", board.Slug), ("jobId", id)))));
                    break;
                }
                case "lever":
                {
                    var id = Str(item, "postingId") ?? "";
                    list.Add(new BoardEntry(board.Employer, title, Str(item, "location"),
                        Str(item, "url") ?? $"https://jobs.lever.co/{board.Slug}/{id}", Arrangement(Str(item, "workplaceType")),
                        EpochMsToDate(item, "createdAt"), null, new PostingRef("lever", Args(("company", board.Slug), ("postingId", id)))));
                    break;
                }
                default:
                {
                    var id = Str(item, "jobId") ?? "";
                    list.Add(new BoardEntry(board.Employer, title, Str(item, "location"), $"https://jobs.ashbyhq.com/{board.Slug}/{id}",
                        Arrangement(Str(item, "workplaceType")), null, Str(item, "compensation"),
                        new PostingRef("ashby", Args(("company", board.Slug), ("jobId", id)))));
                    break;
                }
            }
        }
        return list;
    }

    /// <summary>A posting from a <c>getJob</c> result's data (an object, or a one-element list for Greenhouse).</summary>
    /// <param name="source">The ref that was read.</param>
    /// <param name="employer">Employer display name, or <see langword="null"/> for the slug title-cased.</param>
    /// <param name="data">The result's <c>data</c>.</param>
    /// <returns>The posting, or <see langword="null"/> when the data holds none.</returns>
    public static JobPosting? ParsePosting(PostingRef source, string? employer, JsonElement? data)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (data is not { } d)
            return null;
        if (d.ValueKind == JsonValueKind.Array)
        {
            if (d.GetArrayLength() == 0)
                return null;
            d = d[0];
        }
        if (d.ValueKind != JsonValueKind.Object)
            return null;

        var args = source.Args;
        args.TryGetValue("company", out var company);
        employer ??= company is null ? "Workday" : Pretty(company);
        var title = Str(d, "title") ?? "";
        switch (source.Site)
        {
            case "greenhouse":
            {
                var desc = Str(d, "description") ?? "";
                var loc = Str(d, "location");
                return new JobPosting(employer, title, loc, $"https://job-boards.greenhouse.io/{company}/jobs/{args["jobId"]}",
                    RequisitionFrom(desc), ArrangementFromText(loc, desc), null, null, null, desc, source, ProvesOpen: true);
            }
            case "lever":
            {
                var sections = d.TryGetProperty("sections", out var secs) && secs.ValueKind == JsonValueKind.Array
                    ? string.Join("\n", secs.EnumerateArray().Select(s => $"{Str(s, "text")}\n{StripHtml(Str(s, "content"))}"))
                    : "";
                var desc = string.Join("\n", new[] { Str(d, "description"), sections, Str(d, "additional") }.Where(s => !string.IsNullOrWhiteSpace(s)));
                string? pay = null;
                if (d.TryGetProperty("salaryRange", out var sal) && sal.ValueKind == JsonValueKind.Object &&
                    sal.TryGetProperty("min", out var min) && min.ValueKind == JsonValueKind.Number)
                {
                    var max = sal.TryGetProperty("max", out var mx) && mx.ValueKind == JsonValueKind.Number ? mx.GetDecimal() : min.GetDecimal();
                    var interval = (Str(sal, "interval") ?? "").Replace("per-", "", StringComparison.Ordinal);
                    pay = string.Create(CultureInfo.InvariantCulture, $"{Str(sal, "currency")} {min.GetDecimal():N0}–{max:N0} per {interval}").Trim();
                }
                var loc = Str(d, "location");
                return new JobPosting(employer, title, loc, Str(d, "url") ?? $"https://jobs.lever.co/{company}/{args["postingId"]}",
                    RequisitionFrom(desc), Arrangement(Str(d, "workplaceType")) ?? ArrangementFromText(loc, desc), null, pay,
                    EpochMsToDate(d, "createdAt"), desc, source, ProvesOpen: true);
            }
            case "ashby":
            {
                var desc = StripHtml(Str(d, "descriptionHtml"));
                var loc = Str(d, "location");
                return new JobPosting(employer, title, loc, $"https://jobs.ashbyhq.com/{company}/{args["jobId"]}", RequisitionFrom(desc),
                    Arrangement(Str(d, "workplaceType")) ?? ArrangementFromText(loc, desc), null, Str(d, "compensation"), null, desc, source, ProvesOpen: true);
            }
            case "workable":
            {
                string? loc = null;
                if (d.TryGetProperty("location", out var l) && l.ValueKind == JsonValueKind.Object)
                    loc = string.Join(", ", new[] { "city", "region", "country" }.Select(k => Str(l, k)).Where(v => !string.IsNullOrWhiteSpace(v)));
                var desc = string.Join("\n", new[] { "description", "requirements", "benefits" }.Select(k => StripHtml(Str(d, k))).Where(s => s.Length > 0));
                var remote = d.TryGetProperty("remote", out var r) && r.ValueKind == JsonValueKind.True;
                return new JobPosting(employer, title, string.IsNullOrEmpty(loc) ? null : loc, $"https://apply.workable.com/{company}/j/{args["shortcode"]}/",
                    RequisitionFrom(desc), remote ? "remote" : Arrangement(Str(d, "workplace")), null, null, DatePrefix(Str(d, "published")), desc, source, ProvesOpen: true);
            }
            case "workday":
            {
                // The op's notes: the public listing does not prove the position remains open, and there is no
                // description. A read here never makes a listing open on its own.
                var locs = new List<string>();
                if (Str(d, "location") is { } primary)
                    locs.Add(primary);
                if (d.TryGetProperty("additionalLocations", out var extra) && extra.ValueKind == JsonValueKind.Array)
                    locs.AddRange(extra.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!));
                return new JobPosting(employer, title, locs.Count > 0 ? string.Join("; ", locs) : null, Str(d, "externalUrl"),
                    Str(d, "jobReqId"), null, Str(d, "remoteType"), null, null, "", source, ProvesOpen: false);
            }
            default:
                return null;
        }
    }
}
