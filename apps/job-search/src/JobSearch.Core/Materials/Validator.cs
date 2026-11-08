using System.Globalization;
using System.Text.RegularExpressions;
using Ashlar.WebOperations.Jobs;
using JobSearch.Profile;
using JobSearch.Screening;

namespace JobSearch.Materials;

/// <summary>One claim in a draft and what supports it.</summary>
public sealed record ClaimCheck(string Claim, string Status, string? Evidence);

/// <summary>The record of how a draft was validated. Stored with the document and shown in the tracker.</summary>
public sealed record ValidationRecord
{
    /// <summary>When.</summary>
    public string ValidatedAt { get; init; } = "";
    /// <summary>No unsupported claim was found.</summary>
    public bool Passed { get; init; }
    /// <summary>Every checked claim with its evidence.</summary>
    public List<ClaimCheck> Claims { get; init; } = [];
    /// <summary>Claims with no support in the résumé or the user's confirmed profile.</summary>
    public List<ClaimCheck> Unsupported { get; init; } = [];
    /// <summary>Posting requirements the candidate's résumé does not show; the draft does not claim them.</summary>
    public List<string> MissingRequirements { get; init; } = [];
    /// <summary>What only the user can supply or confirm before using the draft.</summary>
    public List<string> NeedsConfirmation { get; init; } = [];
    /// <summary>Result of reading the PDF back, when one was rendered.</summary>
    public PdfCheck? Pdf { get; init; }
    /// <summary>Always present: no applicant-tracking outcome is predicted or guaranteed.</summary>
    public string AtsNote { get; init; } = "Keywords are the posting's own terms that the résumé already supports. No applicant tracking system outcome is predicted or guaranteed.";
}

/// <summary>The extractable-text check of a rendered PDF.</summary>
public sealed record PdfCheck(bool Passed, double Ratio, int Pages, int ExpectedWords, string? Error = null);

/// <summary>Checks a draft against the source résumé and the user's confirmed profile.</summary>
public static class Validator
{
    private static readonly TimeSpan B = TimeSpan.FromSeconds(1);

    /// <summary>Validates <paramref name="draft"/>.</summary>
    public static ValidationRecord Validate(Draft draft, CandidateProfile p, JobPosting posting, Assessment a, PdfCheck? pdf = null)
    {
        var f = p.Facts;
        var resumeLines = p.ResumeText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select(l => l.Trim()).ToList();
        var bullets = f.Employment.SelectMany(j => j.Bullets).ToList();
        var rlow = p.ResumeText.ToLowerInvariant();
        var claims = new List<ClaimCheck>();
        var unsupported = new List<ClaimCheck>();
        var needs = new List<string>();

        void Check(string claim, string? evidence)
        {
            var c = new ClaimCheck(claim, evidence is null ? "unsupported" : "supported", evidence);
            claims.Add(c);
            if (evidence is null) unsupported.Add(c);
        }

        string? LineOf(string text)
        {
            var i = resumeLines.FindIndex(l => l.Contains(text, StringComparison.Ordinal));
            return i < 0 ? null : $"résumé line {i + 1}";
        }

        foreach (var (style, text) in draft.Lines)
        {
            if (text.Length == 0)
                continue;
            if (text.Contains(Tailor.ConfirmMarker, StringComparison.Ordinal))
            {
                needs.Add($"Placeholder to complete: {text[text.IndexOf(Tailor.ConfirmMarker, StringComparison.Ordinal)..]}");
                continue;
            }
            if (style == "li")
            {
                // Bullets must be the résumé's own words (or a certification line from it).
                var b = bullets.FirstOrDefault(x => x.Value == text);
                Check(text, b is not null ? $"résumé line {b.Line}" : LineOf(text));
            }
            else if (style == "b" && draft.Kind == "resume")
            {
                var job = f.Employment.FirstOrDefault(j => j.Raw == text);
                Check(text, job is not null ? $"résumé line {job.Line}" : null);
                if (job is { Start: { } s, End: { } e } && e != "present" && string.CompareOrdinal(s, e) > 0)
                    unsupported.Add(new ClaimCheck(text, "inconsistent", $"start {s} is after end {e}"));
            }
        }

        // Every skill term the draft names must be on the résumé; this is what catches a gap skill slipping in. The
        // posting's own title and employer are references to the job, not claims, so they are left out of the scan.
        var draftLow = draft.Text.Replace(posting.Title, " ", StringComparison.Ordinal)
            .Replace(posting.Employer, " ", StringComparison.Ordinal).ToLowerInvariant();
        foreach (var term in Screener.Lexicon.Where(t => Screener.HasTerm(draftLow, t)))
            Check($"skill: {term}", Screener.HasTerm(rlow, term) ? "named in the résumé" : null);

        // Employers named in the draft: résumé employers, or the target employer.
        foreach (var job in f.Employment.Where(j => j.Employer is not null))
            if (draft.Text.Contains(job.Employer!, StringComparison.Ordinal))
                Check($"employer: {job.Employer}", $"résumé line {job.Line}");

        // A name line must be the résumé's.
        if (f.Name is not null && !draft.Text.Contains(f.Name.Value, StringComparison.Ordinal))
            unsupported.Add(new ClaimCheck("name", "missing", "the draft does not carry the résumé's name"));

        // A years claim must equal the computed figure.
        foreach (Match m in Tailor.YearsClaim.Matches(draft.Text))
        {
            var claimed = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            var actual = p.Number("years_experience");
            Check($"{claimed} years of experience", actual is { } y && Math.Abs(y - claimed) < 0.05 ? "computed from résumé employment dates" : null);
        }

        // Clearance appears only as the user confirmed it.
        if (Regex.IsMatch(draftLow, @"\b(ts/sci|top secret|secret|public trust)\b", RegexOptions.None, B))
            Check("clearance", p.ConfirmedText("clearance_status") is { } cs && draft.Text.Contains(cs, StringComparison.Ordinal) ? "user-confirmed clearance status" : null);

        needs.AddRange(a.Unknowns.Select(u => $"{u.Requirement}: {u.Detail}"));
        if (p.Field("name").Status != "confirmed")
            needs.Add("Confirm the name and contact details derived from the résumé.");

        return new ValidationRecord
        {
            ValidatedAt = Store.Clock.Iso(),
            Passed = unsupported.Count == 0,
            Claims = claims,
            Unsupported = unsupported,
            MissingRequirements = a.Gaps.Where(g => g.Kind == "requirement").Select(g => g.Requirement).ToList(),
            NeedsConfirmation = needs,
            Pdf = pdf,
        };
    }
}
