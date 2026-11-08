using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace JobSearch.Profile;

/// <summary>A fact and the 1-based résumé line it came from.</summary>
public sealed record Sourced(string Value, int Line);

/// <summary>One employment entry. Dates are yyyy-MM or "present"; null when the résumé's text could not be read.</summary>
public sealed record Employment(string? Title, string? Employer, string? Location, string? Start, string? End, int Line, string Raw, List<Sourced> Bullets);

/// <summary>Everything the parser read from a résumé. Nothing here is inferred.</summary>
public sealed record ResumeFacts
{
    /// <summary>Name line.</summary>
    public Sourced? Name { get; init; }
    /// <summary>Email.</summary>
    public Sourced? Email { get; init; }
    /// <summary>Phone.</summary>
    public Sourced? Phone { get; init; }
    /// <summary>City, ST.</summary>
    public Sourced? Location { get; init; }
    /// <summary>Summary lines.</summary>
    public List<Sourced> Summary { get; init; } = [];
    /// <summary>Employment entries.</summary>
    public List<Employment> Employment { get; init; } = [];
    /// <summary>Education lines.</summary>
    public List<Sourced> Education { get; init; } = [];
    /// <summary>Skills.</summary>
    public List<Sourced> Skills { get; init; } = [];
    /// <summary>Certifications.</summary>
    public List<Sourced> Certifications { get; init; } = [];
    /// <summary>A clearance statement, when the résumé makes one. A claim to confirm, not a status.</summary>
    public Sourced? ClearanceClaim { get; init; }
    /// <summary>Lines the parser could not place; kept so nothing silently disappears.</summary>
    public List<Sourced> Unparsed { get; init; } = [];
}

/// <summary>Reads plain-text résumés with conventional section headings.</summary>
public static class ResumeParser
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(1);
    private const string DateToken = @"(?:[A-Za-z]{3,9}\.?\s+\d{4}|\d{4}-\d{2}|\d{1,2}/\d{4}|\d{4})";
    private static readonly Regex DateRange = new($@"({DateToken})\s*(?:–|—|-|to)\s*({DateToken}|present|current|now)", RegexOptions.IgnoreCase, Budget);
    private static readonly Regex EmailRx = new(@"[\w.+-]+@[\w-]+\.[\w.-]+", RegexOptions.None, Budget);
    private static readonly Regex PhoneRx = new(@"\(?\d{3}\)?[\s.-]?\d{3}[\s.-]?\d{4}", RegexOptions.None, Budget);
    private static readonly Regex CityState = new(@"\b([A-Z][a-zA-Z .'-]+,\s*[A-Z]{2})\b", RegexOptions.None, Budget);
    private static readonly Regex ClearanceRx = new(@"\b(TS/SCI|Top Secret|Secret|Public Trust)\b", RegexOptions.IgnoreCase, Budget);
    private static readonly Regex Bullet = new(@"^[-*•]\s*(.+)$", RegexOptions.None, Budget);
    private static readonly Regex HeadSplit = new(@"\s+(?:—|–|-|at|@)\s+|\s*\|\s*", RegexOptions.None, Budget);
    private static readonly string[] Months = ["jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"];

    private static readonly Dictionary<string, string> Sections = new(StringComparer.Ordinal)
    {
        ["summary"] = "summary", ["profile"] = "summary", ["objective"] = "summary",
        ["experience"] = "experience", ["work experience"] = "experience", ["professional experience"] = "experience",
        ["employment"] = "experience", ["education"] = "education", ["skills"] = "skills", ["technical skills"] = "skills",
        ["certifications"] = "certifications", ["licenses"] = "certifications", ["clearance"] = "clearance",
        ["security clearance"] = "clearance",
    };

    /// <summary>SHA-256 of the text, lowercase hex.</summary>
    public static string Sha256(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>yyyy-MM, "present", or null.</summary>
    public static string? ParseMonth(string token, bool end = false)
    {
        var t = token.Trim().TrimEnd('.').ToLowerInvariant();
        if (t is "present" or "current" or "now")
            return "present";
        if (Regex.Match(t, @"^(\d{4})-(\d{2})$", RegexOptions.None, Budget) is { Success: true } a)
            return $"{a.Groups[1].Value}-{a.Groups[2].Value}";
        if (Regex.Match(t, @"^(\d{1,2})/(\d{4})$", RegexOptions.None, Budget) is { Success: true } b)
            return $"{b.Groups[2].Value}-{int.Parse(b.Groups[1].Value, CultureInfo.InvariantCulture):00}";
        if (Regex.Match(t, @"^([a-z]{3,9})\.?\s+(\d{4})$", RegexOptions.None, Budget) is { Success: true } c &&
            Array.IndexOf(Months, c.Groups[1].Value[..3]) is var m and >= 0)
            return $"{c.Groups[2].Value}-{m + 1:00}";
        if (Regex.IsMatch(t, @"^\d{4}$", RegexOptions.None, Budget))
            return $"{t}-{(end ? 12 : 1):00}";
        return null;
    }

    private static string? SectionOf(string raw)
    {
        var line = raw.Trim();
        var key = Regex.Replace(line.ToLowerInvariant(), "[^a-z ]", "", RegexOptions.None, Budget).Trim();
        var looksLikeHeading = line == line.ToUpperInvariant() || line.EndsWith(':') || line.StartsWith('#');
        return looksLikeHeading && Sections.TryGetValue(key, out var s) ? s : null;
    }

    /// <summary>Parses résumé text.</summary>
    public static ResumeFacts Parse(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        Sourced? name = null, email = null, phone = null, location = null, clearance = null;
        var summary = new List<Sourced>();
        var jobs = new List<Employment>();
        var education = new List<Sourced>();
        var skills = new List<Sourced>();
        var certs = new List<Sourced>();
        var unparsed = new List<Sourced>();
        var section = "header";
        Employment? current = null;

        for (var i = 0; i < lines.Length; i++)
        {
            var n = i + 1;
            var line = lines[i].Trim().TrimStart('#').Trim();
            if (line.Length == 0)
                continue;
            if (SectionOf(lines[i]) is { } sec)
            {
                section = sec;
                current = null;
                continue;
            }
            switch (section)
            {
                case "header":
                    if (name is null && !EmailRx.IsMatch(line) && line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 5)
                    {
                        name = new Sourced(line, n);
                        break;
                    }
                    if (email is null && EmailRx.Match(line) is { Success: true } em) email = new Sourced(em.Value, n);
                    if (phone is null && PhoneRx.Match(line) is { Success: true } ph) phone = new Sourced(ph.Value, n);
                    if (location is null && CityState.Match(line) is { Success: true } cs) location = new Sourced(cs.Groups[1].Value.Trim(), n);
                    break;
                case "summary":
                    summary.Add(new Sourced(line, n));
                    break;
                case "experience":
                    if (Bullet.Match(line) is { Success: true } b && current is not null)
                    {
                        current.Bullets.Add(new Sourced(b.Groups[1].Value.Trim(), n));
                        break;
                    }
                    if (DateRange.Match(line) is { Success: true } dr)
                    {
                        var head = line[..dr.Index].Trim(' ', '|', ',', '–', '—', '-');
                        var parts = HeadSplit.Split(head).Select(p => p.Trim()).Where(p => p.Length > 0).ToArray();
                        current = new Employment(parts.ElementAtOrDefault(0), parts.ElementAtOrDefault(1), parts.ElementAtOrDefault(2),
                            ParseMonth(dr.Groups[1].Value), ParseMonth(dr.Groups[2].Value, end: true), n, line, []);
                        jobs.Add(current);
                    }
                    else
                    {
                        unparsed.Add(new Sourced(line, n));
                    }
                    break;
                case "education":
                    education.Add(new Sourced(line, n));
                    break;
                case "skills":
                    var body = line.Contains(':', StringComparison.Ordinal) ? line[(line.IndexOf(':', StringComparison.Ordinal) + 1)..] : line;
                    foreach (var s in body.Split([',', ';', '•', '|'], StringSplitOptions.RemoveEmptyEntries))
                    {
                        var skill = s.Trim(' ', '-', '*');
                        if (skill.Length > 0)
                            skills.Add(new Sourced(skill, n));
                    }
                    break;
                case "certifications":
                    certs.Add(new Sourced(line.TrimStart('-', '*', '•', ' '), n));
                    break;
                case "clearance":
                    clearance ??= new Sourced(line.TrimStart('-', '*', '•', ' '), n);
                    break;
                default:
                    unparsed.Add(new Sourced(line, n));
                    break;
            }
        }

        if (clearance is null)
        {
            for (var i = 0; i < lines.Length; i++)
            {
                if (ClearanceRx.IsMatch(lines[i]) && lines[i].Contains("clearance", StringComparison.OrdinalIgnoreCase))
                {
                    clearance = new Sourced(lines[i].Trim(' ', '-', '*', '•'), i + 1);
                    break;
                }
            }
        }

        return new ResumeFacts
        {
            Name = name, Email = email, Phone = phone, Location = location, Summary = summary, Employment = jobs,
            Education = education, Skills = skills, Certifications = certs, ClearanceClaim = clearance, Unparsed = unparsed,
        };
    }

    /// <summary>Merged employment span in years (one decimal), or null when any entry's dates could not be read.</summary>
    public static double? TotalYears(IReadOnlyList<Employment> jobs, DateOnly today)
    {
        if (jobs.Count == 0)
            return null;
        var spans = new List<(int A, int B)>();
        foreach (var j in jobs)
        {
            if (j.Start is null || j.End is null)
                return null;
            var a = Months12(j.Start);
            var b = j.End == "present" ? today.Year * 12 + today.Month : Months12(j.End);
            if (b < a)
                return null;
            spans.Add((a, b));
        }
        spans.Sort();
        var merged = new List<(int A, int B)> { spans[0] };
        foreach (var (a, b) in spans.Skip(1))
        {
            var last = merged[^1];
            if (a <= last.B + 1)
                merged[^1] = (last.A, Math.Max(last.B, b));
            else
                merged.Add((a, b));
        }
        return Math.Round(merged.Sum(s => s.B - s.A + 1) / 12.0, 1);

        static int Months12(string ym) => int.Parse(ym[..4], CultureInfo.InvariantCulture) * 12 + int.Parse(ym[5..7], CultureInfo.InvariantCulture);
    }
}
