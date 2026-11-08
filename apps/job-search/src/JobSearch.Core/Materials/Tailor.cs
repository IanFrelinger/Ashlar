using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Ashlar.WebOperations.Jobs;
using JobSearch.Profile;
using JobSearch.Screening;

namespace JobSearch.Materials;

/// <summary>A drafted document as styled lines: h = heading, b = bold line, p = paragraph, li = bullet.</summary>
public sealed record Draft(string Kind, IReadOnlyList<(string Style, string Text)> Lines)
{
    /// <summary>template (assembled from résumé facts) or model (worded by a model, then checked).</summary>
    public string Origin { get; init; } = "template";

    /// <summary>Plain text, the form validated, hashed and compared for staleness.</summary>
    public string Text => string.Join("\n", Lines.Select(l => l.Style == "li" ? "- " + l.Text : l.Text));

    /// <summary>HTML for upload as an editable Google Doc (Drive converts it).</summary>
    public string Html()
    {
        var sb = new StringBuilder("<html><body style=\"font-family:Arial,sans-serif;font-size:11pt\">");
        var inList = false;
        foreach (var (style, text) in Lines)
        {
            var t = WebUtility.HtmlEncode(text);
            if (style == "li" && !inList) { sb.Append("<ul>"); inList = true; }
            if (style != "li" && inList) { sb.Append("</ul>"); inList = false; }
            sb.Append(style switch
            {
                "h" => $"<h2>{t}</h2>",
                "b" => $"<p><b>{t}</b></p>",
                "li" => $"<li>{t}</li>",
                _ => text.Length == 0 ? "<p>&nbsp;</p>" : $"<p>{t}</p>",
            });
        }
        if (inList) sb.Append("</ul>");
        return sb.Append("</body></html>").ToString();
    }
}

/// <summary>
/// Drafts a job-specific résumé and cover letter from verified facts only. Résumé bullets are the résumé's own bullets,
/// verbatim, reordered by relevance; nothing is reworded, so nothing new is claimed. The cover letter states only facts
/// with résumé evidence and leaves bracketed [CONFIRM: …] placeholders where only the user can speak.
/// </summary>
public static class Tailor
{
    /// <summary>Placeholder marker the validator reports as needing the user.</summary>
    public const string ConfirmMarker = "[CONFIRM:";

    /// <summary>The tailored résumé.</summary>
    public static Draft Resume(CandidateProfile p, Assessment a)
    {
        var f = p.Facts;
        var keywords = a.MatchedSkills.Concat(a.PreferredMatched).Distinct(StringComparer.Ordinal).ToList();
        var lines = new List<(string, string)>();
        if (f.Name is not null) lines.Add(("h", f.Name.Value));
        var contact = string.Join(" | ", new[] { f.Email?.Value, f.Phone?.Value, f.Location?.Value }.Where(x => x is not null));
        if (contact.Length > 0) lines.Add(("p", contact));

        if (f.Summary.Count > 0)
        {
            lines.Add(("h", "Summary"));
            lines.AddRange(f.Summary.Select(s => ("p", s.Value)));
        }

        if (f.Employment.Count > 0)
        {
            lines.Add(("h", "Experience"));
            foreach (var job in f.Employment)
            {
                lines.Add(("b", job.Raw));
                var ordered = job.Bullets
                    .Select((b, i) => (b, i, hits: keywords.Count(k => Screener.HasTerm(b.Value.ToLowerInvariant(), k))))
                    .OrderByDescending(x => x.hits).ThenBy(x => x.i);
                lines.AddRange(ordered.Select(x => ("li", x.b.Value)));
            }
        }

        if (f.Skills.Count > 0)
        {
            lines.Add(("h", "Skills"));
            var first = f.Skills.Where(s => keywords.Any(k => Screener.HasTerm(s.Value.ToLowerInvariant(), k))).Select(s => s.Value);
            var rest = f.Skills.Select(s => s.Value).Where(s => !first.Contains(s));
            lines.Add(("p", string.Join(", ", first.Concat(rest).Distinct(StringComparer.Ordinal))));
        }
        if (f.Education.Count > 0)
        {
            lines.Add(("h", "Education"));
            lines.AddRange(f.Education.Select(e => ("p", e.Value)));
        }
        if (f.Certifications.Count > 0)
        {
            lines.Add(("h", "Certifications"));
            lines.AddRange(f.Certifications.Select(c => ("li", c.Value)));
        }
        // Clearance appears only as the user confirmed it, in the user's words.
        if (p.ConfirmedText("clearance_status") is { } clearance && !clearance.StartsWith("none", StringComparison.OrdinalIgnoreCase))
        {
            lines.Add(("h", "Clearance"));
            lines.Add(("p", clearance));
        }
        return new Draft("resume", lines);
    }

    /// <summary>The cover letter.</summary>
    public static Draft CoverLetter(CandidateProfile p, JobPosting posting, Assessment a, DateOnly today) =>
        CoverLetterWith(p, posting, today, CoverLetterBody(p, posting, a));

    /// <summary>
    /// A cover letter around <paramref name="body"/>: the header (name, contact, date, addressee, salutation) and the
    /// sign-off are assembled here from the profile and the posting, whoever wrote the body.
    /// </summary>
    public static Draft CoverLetterWith(CandidateProfile p, JobPosting posting, DateOnly today, IEnumerable<(string Style, string Text)> body, string origin = "template")
    {
        var f = p.Facts;
        var lines = new List<(string, string)>();
        var name = f.Name?.Value ?? $"{ConfirmMarker} your name]";
        lines.Add(("b", name));
        var contact = string.Join(" | ", new[] { f.Email?.Value, f.Phone?.Value }.Where(x => x is not null));
        if (contact.Length > 0) lines.Add(("p", contact));
        lines.Add(("p", today.ToString("MMMM d, yyyy", CultureInfo.InvariantCulture)));
        lines.Add(("p", ""));
        lines.Add(("p", $"Hiring Team, {posting.Employer}"));
        lines.Add(("p", $"Re: {posting.Title}{(posting.RequisitionId is { } r ? $" (Req. {r})" : "")}"));
        lines.Add(("p", ""));
        lines.Add(("p", "Dear Hiring Team,"));
        lines.AddRange(body);
        lines.Add(("p", ""));
        lines.Add(("p", "Sincerely,"));
        lines.Add(("p", name));
        return new Draft("cover_letter", lines) { Origin = origin };
    }

    /// <summary>The template body: the résumé's facts, in fixed sentences.</summary>
    public static List<(string Style, string Text)> CoverLetterBody(CandidateProfile p, JobPosting posting, Assessment a)
    {
        var f = p.Facts;
        var lines = new List<(string, string)>
        {
            ("p", $"I am applying for the {posting.Title} position at {posting.Employer}. {ConfirmMarker} one or two sentences, in your own words, on why this role and team interest you.]"),
        };

        // The most relevant employment entry, quoted through its own top bullets.
        var keywords = a.MatchedSkills;
        var best = f.Employment
            .Select((j, i) => (j, i, hits: j.Bullets.Sum(b => keywords.Count(k => Screener.HasTerm(b.Value.ToLowerInvariant(), k)))))
            .OrderByDescending(x => x.hits).ThenBy(x => x.i).FirstOrDefault();
        if (best.j is { Title: not null, Employer: not null } job)
        {
            var bullets = job.Bullets
                .OrderByDescending(b => keywords.Count(k => Screener.HasTerm(b.Value.ToLowerInvariant(), k)))
                .Take(2).Select(b => b.Value).ToList();
            if (bullets.Count > 0)
            {
                lines.Add(("p", $"As {job.Title} at {job.Employer}, my work included:"));
                lines.AddRange(bullets.Select(b => ("li", b)));
            }
        }
        if (a.MatchedSkills.Count > 0)
            lines.Add(("p", $"My résumé shows experience with {Join(a.MatchedSkills.Select(t => AsWritten(p.ResumeText, t)).ToList())}, which this posting lists among its requirements."));
        if (p.Number("years_experience") is { } years && years > 0)
            lines.Add(("p", $"My résumé covers {years:0.#} years of professional experience."));
        lines.Add(("p", "I would welcome the chance to discuss the role. Thank you for your consideration."));
        return lines;
    }

    /// <summary>The term as the résumé spells it ("Epic", "EHR"), not the lexicon's lowercase.</summary>
    internal static string AsWritten(string resume, string term)
    {
        var m = Regex.Match(resume, $@"(?<![A-Za-z0-9+#]){Regex.Escape(term)}(?![A-Za-z0-9+#])", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
        return m.Success ? m.Value : term;
    }

    private static string Join(IReadOnlyList<string> items) => items.Count switch
    {
        1 => items[0],
        2 => $"{items[0]} and {items[1]}",
        _ => string.Join(", ", items.Take(items.Count - 1)) + ", and " + items[^1],
    };

    internal static readonly Regex YearsClaim = new(@"(\d+(?:\.\d)?) years of professional experience", RegexOptions.None, TimeSpan.FromSeconds(1));
}
