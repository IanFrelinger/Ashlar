using System.Globalization;
using System.Text.RegularExpressions;
using Ashlar.WebOperations.Jobs;
using JobSearch.Accounts;
using JobSearch.Profile;

namespace JobSearch.Screening;

/// <summary>A requirement the posting states, and the quote it came from.</summary>
public sealed record Finding(string Requirement, string Detail, string? SourceQuote = null, string? CandidateEvidence = null, string Kind = "requirement");

/// <summary>What the posting states that screening uses. Null means the posting does not say.</summary>
public sealed record Requirements
{
    /// <summary>Skills in required context.</summary>
    public List<string> SkillsRequired { get; init; } = [];
    /// <summary>Skills in preferred context.</summary>
    public List<string> SkillsPreferred { get; init; } = [];
    /// <summary>Minimum years of experience.</summary>
    public int? YearsMin { get; init; }
    /// <summary>associate, bachelor, master, doctorate.</summary>
    public string? Degree { get; init; }
    /// <summary>Whether "or equivalent" follows the degree.</summary>
    public bool DegreeOrEquivalent { get; init; }
    /// <summary>Clearance level named.</summary>
    public string? Clearance { get; init; }
    /// <summary>Active clearance required.</summary>
    public bool ClearanceMustBeActive { get; init; }
    /// <summary>The posting says the employer sponsors the clearance.</summary>
    public bool ClearanceSponsorshipStated { get; init; }
    /// <summary>U.S. citizenship required.</summary>
    public bool CitizenshipRequired { get; init; }
    /// <summary>offered | not_offered | null.</summary>
    public string? VisaSponsorship { get; init; }
    /// <summary>Travel percentage.</summary>
    public int? TravelPct { get; init; }
    /// <summary>Lowest stated pay figure.</summary>
    public decimal? PayMin { get; init; }
    /// <summary>Highest stated pay figure.</summary>
    public decimal? PayMax { get; init; }
    /// <summary>yyyy-MM-dd.</summary>
    public string? Deadline { get; init; }
    /// <summary>Remote eligibility restrictions quoted from the source.</summary>
    public List<string> RemoteRestrictions { get; init; } = [];
    /// <summary>Seniority read from the title.</summary>
    public string? Level { get; init; }
    /// <summary>Quote per requirement key.</summary>
    public Dictionary<string, string> Quotes { get; init; } = new(StringComparer.Ordinal);
}

/// <summary>The fit assessment for one posting and one candidate.</summary>
public sealed record Assessment
{
    /// <summary>strong_fit | conditional_fit | stretch | not_currently_eligible; empty until the certified rule decides.</summary>
    public string Recommendation { get; init; } = "";
    /// <summary>The certified rule's reason for <see cref="Recommendation"/>.</summary>
    public string RecommendationReason { get; init; } = "";
    /// <summary>Which rule decided: the certified brick's id and content hash.</summary>
    public string DecidedBy { get; init; } = "";
    /// <summary>The counts the certified rule decides from.</summary>
    public DecisionInputs Decision { get; init; } = new();
    /// <summary>What the user's own workflow steps said about the posting, in workflow order.</summary>
    public List<Studio.StepResult> Steps { get; init; } = [];
    /// <summary>The name of the user's step that excluded the posting; null when none did.</summary>
    public string? ExcludedBy { get; init; }
    /// <summary>For ordering; not a probability.</summary>
    public double Score { get; init; }
    /// <summary>Share of required skills with résumé evidence; null when none could be extracted.</summary>
    public double? Coverage { get; init; }
    /// <summary>What the posting states.</summary>
    public Requirements Requirements { get; init; } = new();
    /// <summary>Requirements met, each with the candidate's evidence.</summary>
    public List<Finding> Evidence { get; init; } = [];
    /// <summary>Requirements not shown, and preference mismatches (Kind = preference).</summary>
    public List<Finding> Gaps { get; init; } = [];
    /// <summary>Things that need the user's answer.</summary>
    public List<Finding> Unknowns { get; init; } = [];
    /// <summary>User-confirmed facts that rule the job out.</summary>
    public List<Finding> Blockers { get; init; } = [];
    /// <summary>Plain statements about the source (pay not stated, remote eligibility not stated, deadline passed).</summary>
    public List<string> Notes { get; init; } = [];
    /// <summary>Required skills matched.</summary>
    public List<string> MatchedSkills { get; init; } = [];
    /// <summary>Preferred skills matched.</summary>
    public List<string> PreferredMatched { get; init; } = [];
}

/// <summary>
/// The inputs of the certified brick jobsearch.recommendation, named as its input ports. Percentages and years are
/// integers (years in tenths) so the certified rule compares exactly; -1 means the posting or résumé does not say.
/// </summary>
public sealed record DecisionInputs
{
    /// <summary>Skills the posting requires.</summary>
    public int RequiredSkills { get; init; }
    /// <summary>Required skills the résumé shows.</summary>
    public int MatchedSkills { get; init; }
    /// <summary>Minimum years; -1 when none stated.</summary>
    public int YearsRequired { get; init; } = -1;
    /// <summary>Years x 10 from résumé dates; -1 when unknown.</summary>
    public int YearsHaveTenths { get; init; } = -1;
    /// <summary>User-confirmed facts that rule the job out.</summary>
    public int Blockers { get; init; }
    /// <summary>Requirements awaiting the user.</summary>
    public int Unknowns { get; init; }
    /// <summary>Conflicts with stated preferences.</summary>
    public int PreferenceMismatches { get; init; }
    /// <summary>The stated deadline has passed.</summary>
    public bool DeadlinePassed { get; init; }
    /// <summary>Coverage % for a strong fit.</summary>
    public int StrongPercent { get; init; } = 70;
    /// <summary>Coverage % for a conditional fit.</summary>
    public int ConditionalPercent { get; init; } = 40;
    /// <summary>Years short (x 10) still conditional.</summary>
    public int MaxYearsShortTenths { get; init; } = 20;

    /// <summary>The values keyed by the certified brick's input names.</summary>
    public Dictionary<string, object> ToPorts() => new(StringComparer.Ordinal)
    {
        ["requiredSkills"] = RequiredSkills, ["matchedSkills"] = MatchedSkills, ["yearsRequired"] = YearsRequired,
        ["yearsHaveTenths"] = YearsHaveTenths, ["blockers"] = Blockers, ["unknowns"] = Unknowns,
        ["preferenceMismatches"] = PreferenceMismatches, ["deadlinePassed"] = DeadlinePassed, ["strongPercent"] = StrongPercent,
        ["conditionalPercent"] = ConditionalPercent, ["maxYearsShortTenths"] = MaxYearsShortTenths,
    };
}

/// <summary>Requirement extraction and screening. Deterministic: same posting, profile and rules, same answer.</summary>
public static class Screener
{
    private static readonly TimeSpan B = TimeSpan.FromSeconds(1);

    /// <summary>The skills vocabulary searched for in postings (plus every skill on the résumé).</summary>
    public static readonly string[] Lexicon =
    [
        "python", "java", "javascript", "typescript", "golang", "rust", "c++", "c#", "scala", "kotlin", "ruby", "php",
        "sql", "postgresql", "mysql", "snowflake", "bigquery", "redshift", "dbt", "airflow", "spark", "kafka", "hadoop",
        "aws", "azure", "gcp", "kubernetes", "docker", "terraform", "ansible", "linux", "git", "ci/cd", "jenkins",
        "react", "node.js", "django", "flask", "fastapi", "graphql", "microservices",
        "machine learning", "pytorch", "tensorflow", "pandas", "tableau", "power bi", "looker", "excel", "statistics",
        "data modeling", "etl", "data pipelines", "data warehousing",
        "salesforce", "jira", "agile", "scrum", "project management", "stakeholder management", "budgeting",
        "product management", "roadmapping", "user research", "a/b testing", "figma",
        "nursing", "patient care", "epic", "hipaa", "cpr", "bls", "acls", "ehr",
        "accounting", "gaap", "financial modeling", "quickbooks", "auditing", "forecasting",
        "customer service", "crm", "negotiation", "recruiting", "onboarding",
        "network security", "siem", "incident response", "nist", "risk management", "penetration testing",
        "cad", "solidworks", "autocad", "matlab", "six sigma", "lean", "supply chain", "logistics",
        "pmp", "cissp", "security+", "cpa",
    ];

    private static readonly Regex PreferredHead = new(@"\b(preferred|nice to have|bonus|pluses?|desired)\b", RegexOptions.IgnoreCase, B);
    private static readonly Regex RequiredHead = new(@"\b(requirements|required|qualifications|must have|what you.ll need|you have)\b", RegexOptions.IgnoreCase, B);
    private static readonly Dictionary<string, int> DegreeRank = new(StringComparer.Ordinal) { ["associate"] = 1, ["bachelor"] = 2, ["master"] = 3, ["doctorate"] = 4 };
    private static readonly Dictionary<string, int> ClearanceRank = new(StringComparer.Ordinal) { ["public trust"] = 1, ["secret"] = 2, ["top secret"] = 3, ["ts/sci"] = 4 };
    private static readonly (string Word, string Level)[] Levels =
    [
        ("intern", "intern"), ("junior", "entry"), ("entry", "entry"), ("associate", "entry"), ("senior", "senior"), ("sr.", "senior"),
        ("staff", "staff"), ("principal", "principal"), ("lead", "lead"), ("manager", "manager"), ("director", "director"),
    ];

    /// <summary>Whole-term match that treats + and # as part of a word (c++, c#).</summary>
    public static bool HasTerm(string lowerText, string term) =>
        Regex.IsMatch(lowerText, $@"(?<![a-z0-9+#]){Regex.Escape(term)}(?![a-z0-9+#])", RegexOptions.None, B);

    private static string Quote(string text, int start, int end, int pad = 60)
    {
        int s = Math.Max(0, start - pad), e = Math.Min(text.Length, end + pad);
        return (s > 0 ? "…" : "") + text[s..e].Replace('\n', ' ').Trim() + (e < text.Length ? "…" : "");
    }

    /// <summary>Whether a title fits the targets (every significant word of some role) and avoids the exclusions.</summary>
    public static bool TitleRelevant(string title, IReadOnlyList<string> targetRoles, IReadOnlyList<string> exclude)
    {
        var t = title.ToLowerInvariant();
        if (exclude.Any(k => HasTerm(t, k.ToLowerInvariant())))
            return false;
        if (targetRoles.Count == 0)
            return true;
        string[] stop = ["of", "and", "the", "a", "an", "for", "i", "ii", "iii"];
        return targetRoles.Any(role =>
        {
            var words = Regex.Split(role.ToLowerInvariant(), "[^a-z0-9+#]+", RegexOptions.None, B).Where(w => w.Length > 0 && !stop.Contains(w)).ToList();
            return words.Count > 0 && words.All(w => HasTerm(t, w));
        });
    }

    /// <summary>What the posting states.</summary>
    public static Requirements Extract(JobPosting posting)
    {
        var desc = posting.Description ?? "";
        var low = desc.ToLowerInvariant();
        var quotes = new Dictionary<string, string>(StringComparer.Ordinal);
        var required = new List<string>();
        var preferred = new List<string>();

        var mode = "required";
        foreach (var raw in desc.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
                continue;
            var heading = line.Length < 60 && !line.StartsWith('-') && !line.StartsWith('*') && !line.StartsWith('•');
            if (heading && PreferredHead.IsMatch(line)) mode = "preferred";
            else if (heading && RequiredHead.IsMatch(line)) mode = "required";
            var lineMode = PreferredHead.IsMatch(line) ? "preferred" : mode;
            var ll = line.ToLowerInvariant();
            foreach (var term in Lexicon)
            {
                if (!HasTerm(ll, term) || required.Contains(term))
                    continue;
                var bucket = lineMode == "preferred" ? preferred : required;
                if (bucket.Contains(term))
                    continue;
                if (lineMode != "preferred")
                    preferred.Remove(term);
                bucket.Add(term);
                quotes[$"skill:{term}"] = line.Length > 200 ? line[..200] : line;
            }
        }

        int? yearsMin = null;
        foreach (Match m in Regex.Matches(low, @"(\d{1,2})\s*\+?\s*(?:-\s*\d{1,2}\s*)?years?(?:\s+of)?[^.\n]{0,60}experience", RegexOptions.None, B))
        {
            var y = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            if (yearsMin is null || y > yearsMin)
            {
                yearsMin = y;
                quotes["years"] = Quote(desc, m.Index, m.Index + m.Length);
            }
        }

        string? degree = null;
        var degreeOrEq = false;
        var dm = Regex.Match(low, @"\b(ph\.?d|doctorate|master'?s|bachelor'?s|associate'?s|b\.s\.|b\.a\.|m\.s\.)[^.\n]{0,40}\bdegree\b|\bdegree in\b", RegexOptions.None, B);
        if (dm.Success)
        {
            var w = dm.Groups[1].Value;
            degree = w.StartsWith("ph", StringComparison.Ordinal) || w.StartsWith("doc", StringComparison.Ordinal) ? "doctorate"
                : w.StartsWith("master", StringComparison.Ordinal) || w.StartsWith("m.", StringComparison.Ordinal) ? "master"
                : w.StartsWith("assoc", StringComparison.Ordinal) ? "associate" : "bachelor";
            degreeOrEq = low.AsSpan(dm.Index, Math.Min(low.Length - dm.Index, dm.Length + 80)).Contains("or equivalent", StringComparison.Ordinal);
            quotes["degree"] = Quote(desc, dm.Index, dm.Index + dm.Length);
        }

        string? clearance = null;
        bool active = false, sponsorStated = false;
        var cm = Regex.Match(low, @"(active|current)?\s*(ts/sci|top secret|secret|public trust)\b[^.\n]{0,30}clearance|(ability to obtain|able to obtain|eligible (?:for|to obtain))\s+(?:an?\s+)?(ts/sci|top secret|secret|public trust)", RegexOptions.None, B);
        if (cm.Success)
        {
            clearance = (cm.Groups[2].Success ? cm.Groups[2].Value : cm.Groups[4].Value).Trim();
            active = cm.Groups[1].Success && cm.Groups[1].Value.Length > 0;
            sponsorStated = Regex.IsMatch(low, @"sponsor[^.\n]{0,40}clearance|clearance[^.\n]{0,40}sponsor", RegexOptions.None, B);
            quotes["clearance"] = Quote(desc, cm.Index, cm.Index + cm.Length);
        }

        var citizen = Regex.Match(low, @"u\.?s\.? citizen(?:ship)?(?: is)? required|must be (?:a )?u\.?s\.? citizen|requires? u\.?s\.? citizenship", RegexOptions.None, B);
        if (citizen.Success) quotes["citizenship"] = Quote(desc, citizen.Index, citizen.Index + citizen.Length);

        string? visa = null;
        var noSponsor = Regex.Match(low, @"unable to sponsor|will not sponsor|cannot sponsor|not able to sponsor|no (?:visa )?sponsorship|without (?:visa )?sponsorship", RegexOptions.None, B);
        var yesSponsor = Regex.Match(low, @"visa sponsorship (?:is )?available|will sponsor|sponsorship (?:is )?(?:available|offered)", RegexOptions.None, B);
        if (noSponsor.Success) { visa = "not_offered"; quotes["sponsorship"] = Quote(desc, noSponsor.Index, noSponsor.Index + noSponsor.Length); }
        else if (yesSponsor.Success) { visa = "offered"; quotes["sponsorship"] = Quote(desc, yesSponsor.Index, yesSponsor.Index + yesSponsor.Length); }

        int? travel = null;
        var tm = Regex.Match(low, @"travel[^.\n%]{0,30}?(\d{1,3})\s*%|(\d{1,3})\s*%[^.\n]{0,15}travel", RegexOptions.None, B);
        if (tm.Success)
        {
            travel = int.Parse(tm.Groups[1].Success ? tm.Groups[1].Value : tm.Groups[2].Value, CultureInfo.InvariantCulture);
            quotes["travel"] = Quote(desc, tm.Index, tm.Index + tm.Length);
        }
        else if (Regex.IsMatch(low, @"\bno travel\b", RegexOptions.None, B))
        {
            travel = 0;
        }

        var pay = Money(posting.PayText ?? "");
        if (pay.Count == 0)
        {
            pay = Money(desc);
            if (pay.Count > 0 && Regex.Match(desc, @"\$\s?\d", RegexOptions.None, B) is { Success: true } pm)
                quotes["pay"] = Quote(desc, pm.Index, pm.Index + 40);
        }

        string? deadline = null;
        var ddm = Regex.Match(low, @"(?:apply by|deadline|applications? (?:close|due)|closing date)\s*(?:is|on|:)?\s*(\d{4}-\d{2}-\d{2}|(?:january|february|march|april|may|june|july|august|september|october|november|december)\s+\d{1,2},?\s+\d{4}|\d{1,2}/\d{1,2}/\d{4})", RegexOptions.None, B);
        if (ddm.Success && ParseDate(ddm.Groups[1].Value) is { } dd)
        {
            deadline = dd.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            quotes["deadline"] = Quote(desc, ddm.Index, ddm.Index + ddm.Length);
        }

        var restrictions = Regex.Matches(low, @"must (?:reside|live|be located) in[^.\n]{0,80}|remote[^.\n]{0,20}\((?:us|u\.s\.|usa)[^)]*\)|(?:not|cannot be) (?:available|hired|considered) in[^.\n]{0,80}|eligible states?[^.\n]{0,100}", RegexOptions.None, B)
            .Select(m => Quote(desc, m.Index, m.Index + m.Length, pad: 10)).ToList();

        var title = posting.Title.ToLowerInvariant();
        var level = Levels.FirstOrDefault(l => Regex.IsMatch(title, $@"\b{Regex.Escape(l.Word)}", RegexOptions.None, B)).Level;

        return new Requirements
        {
            SkillsRequired = required, SkillsPreferred = preferred, YearsMin = yearsMin, Degree = degree, DegreeOrEquivalent = degreeOrEq,
            Clearance = clearance, ClearanceMustBeActive = active, ClearanceSponsorshipStated = sponsorStated, CitizenshipRequired = citizen.Success,
            VisaSponsorship = visa, TravelPct = travel, PayMin = pay.Count > 0 ? pay.Min() : null, PayMax = pay.Count > 0 ? pay.Max() : null,
            Deadline = deadline, RemoteRestrictions = restrictions, Level = level, Quotes = quotes,
        };
    }

    private static List<decimal> Money(string text)
    {
        var list = new List<decimal>();
        foreach (Match m in Regex.Matches(text, @"\$\s?(\d{1,3}(?:,\d{3})+|\d+(?:\.\d+)?)\s*([kK])?", RegexOptions.None, B))
        {
            var v = decimal.Parse(m.Groups[1].Value.Replace(",", "", StringComparison.Ordinal), CultureInfo.InvariantCulture);
            if (m.Groups[2].Success) v *= 1000;
            if (v >= 1000) list.Add(v);
        }
        foreach (Match m in Regex.Matches(text, @"\b(?:USD|EUR|GBP|CAD)\s+(\d{1,3}(?:,\d{3})+)(?:\s*[–-]\s*(\d{1,3}(?:,\d{3})+))?", RegexOptions.None, B))
        {
            list.Add(decimal.Parse(m.Groups[1].Value.Replace(",", "", StringComparison.Ordinal), CultureInfo.InvariantCulture));
            if (m.Groups[2].Success)
                list.Add(decimal.Parse(m.Groups[2].Value.Replace(",", "", StringComparison.Ordinal), CultureInfo.InvariantCulture));
        }
        return list;
    }

    private static DateOnly? ParseDate(string text)
    {
        string[] formats = ["yyyy-MM-dd", "MMMM d, yyyy", "MMMM d yyyy", "MMMM dd, yyyy", "M/d/yyyy", "MM/dd/yyyy"];
        var t = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(text.Trim());
        return DateOnly.TryParseExact(t, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
    }

    private static string? ResumeEvidence(string resume, string term)
    {
        var lines = resume.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (HasTerm(lines[i].ToLowerInvariant(), term))
            {
                var l = lines[i].Trim();
                return $"résumé line {i + 1}: {(l.Length > 160 ? l[..160] : l)}";
            }
        }
        return null;
    }

    private static int CandidateDegreeRank(CandidateProfile p)
    {
        var best = 0;
        foreach (var e in p.Facts.Education.Select(x => x.Value.ToLowerInvariant()))
        {
            if (Regex.IsMatch(e, @"ph\.?d|doctor", RegexOptions.None, B)) best = Math.Max(best, 4);
            else if (Regex.IsMatch(e, @"master|m\.s\.|\bms\b|\bma\b|mba", RegexOptions.None, B)) best = Math.Max(best, 3);
            else if (Regex.IsMatch(e, @"bachelor|b\.s\.|b\.a\.|\bbs\b|\bba\b", RegexOptions.None, B)) best = Math.Max(best, 2);
            else if (Regex.IsMatch(e, "associate", RegexOptions.None, B)) best = Math.Max(best, 1);
        }
        return best;
    }

    private static bool IsYes(string? v) => v is not null && (v.Equals("true", StringComparison.OrdinalIgnoreCase) || v.Equals("yes", StringComparison.OrdinalIgnoreCase));

    /// <summary>Screens one posting for one candidate.</summary>
    public static Assessment Assess(JobPosting posting, CandidateProfile profile, Preferences prefs, DateOnly today)
    {
        var rules = prefs.Screening;
        var req = Extract(posting);
        var evidence = new List<Finding>();
        var gaps = new List<Finding>();
        var unknowns = new List<Finding>();
        var blockers = new List<Finding>();
        var mismatch = new List<Finding>();
        var notes = new List<string>();
        var resume = profile.ResumeText;
        var rlow = resume.ToLowerInvariant();

        var matched = new List<string>();
        foreach (var term in req.SkillsRequired)
        {
            req.Quotes.TryGetValue($"skill:{term}", out var q);
            if (HasTerm(rlow, term) && ResumeEvidence(resume, term) is { } ev)
            {
                matched.Add(term);
                evidence.Add(new Finding(term, "required skill shown in the résumé", q, ev));
            }
            else
            {
                gaps.Add(new Finding(term, "required in the posting; not found in the résumé", q));
            }
        }
        var preferredMatched = req.SkillsPreferred.Where(t => HasTerm(rlow, t)).ToList();
        double? coverage = req.SkillsRequired.Count > 0 ? (double)matched.Count / req.SkillsRequired.Count : null;

        double yearsShort = 0;
        if (req.YearsMin is { } need)
        {
            req.Quotes.TryGetValue("years", out var q);
            var years = profile.Number("years_experience");
            if (years is null)
                unknowns.Add(new Finding($"{need}+ years", "total experience could not be computed from the résumé dates", q));
            else if (years >= need)
                evidence.Add(new Finding($"{need}+ years", "experience", q, $"{years:0.#} years computed from résumé employment dates"));
            else
            {
                yearsShort = need - years.Value;
                gaps.Add(new Finding($"{need}+ years", $"résumé dates show {years:0.#} years", q));
            }
        }

        if (req.Degree is { } deg)
        {
            req.Quotes.TryGetValue("degree", out var q);
            if (CandidateDegreeRank(profile) >= DegreeRank[deg])
                evidence.Add(new Finding($"{deg} degree", "education", q, "résumé education section"));
            else
                gaps.Add(new Finding($"{deg} degree{(req.DegreeOrEquivalent ? " or equivalent" : "")}", "not shown in the résumé's education section", q));
        }

        if (req.Clearance is { } level)
        {
            req.Quotes.TryGetValue("clearance", out var q);
            var label = $"{level} clearance{(req.ClearanceMustBeActive ? " (active)" : "")}";
            var sponsor = req.ClearanceSponsorshipStated ? "" : " The posting does not say the employer sponsors the clearance.";
            var status = profile.ConfirmedText("clearance_status");
            if (status is null)
            {
                var claim = profile.Text("clearance_claim");
                unknowns.Add(new Finding(label, "clearance status not confirmed by the user" + (claim is null ? "" : $"; the résumé says: \"{claim}\"") + "." + sponsor, q));
            }
            else
            {
                var s = status.ToLowerInvariant();
                var have = ClearanceRank.Where(kv => s.Contains(kv.Key, StringComparison.Ordinal)).Select(kv => kv.Value).DefaultIfEmpty(0).Max();
                var needRank = ClearanceRank.GetValueOrDefault(level);
                var activeEnough = s.Contains("active", StringComparison.Ordinal) && !s.StartsWith("none", StringComparison.Ordinal) && have >= needRank;
                if (req.ClearanceMustBeActive && !activeEnough)
                    blockers.Add(new Finding(label, $"user states clearance status: \"{status}\"", q));
                else if (!req.ClearanceMustBeActive && !activeEnough)
                    unknowns.Add(new Finding(label, $"the posting asks for the ability to obtain it; the user's status is \"{status}\", and eligibility to obtain one is not established by the profile.{sponsor}", q));
                else
                    evidence.Add(new Finding(label, "clearance", q, $"user-confirmed clearance status: \"{status}\""));
            }
        }

        var auth = profile.ConfirmedText("work_authorization");
        if (req.CitizenshipRequired)
        {
            req.Quotes.TryGetValue("citizenship", out var q);
            if (auth is null)
                unknowns.Add(new Finding("U.S. citizenship", "work authorization not provided by the user", q));
            else if (auth.Contains("citizen", StringComparison.OrdinalIgnoreCase) && !auth.Contains("non", StringComparison.OrdinalIgnoreCase))
                evidence.Add(new Finding("U.S. citizenship", "work authorization", q, $"user-stated: \"{auth}\""));
            else
                blockers.Add(new Finding("U.S. citizenship", $"user-stated work authorization: \"{auth}\"", q));
        }
        if (req.VisaSponsorship == "not_offered")
        {
            req.Quotes.TryGetValue("sponsorship", out var q);
            var needs = profile.ConfirmedText("requires_sponsorship");
            if (needs is null)
                unknowns.Add(new Finding("no visa sponsorship", "whether the user needs sponsorship is unknown", q));
            else if (IsYes(needs))
                blockers.Add(new Finding("no visa sponsorship", "user states they require sponsorship", q));
        }

        if (req.TravelPct is > 0 and var travel)
        {
            req.Quotes.TryGetValue("travel", out var q);
            var limitText = rules.MaxTravelPct?.ToString(CultureInfo.InvariantCulture) ?? profile.ConfirmedText("max_travel_pct");
            if (limitText is null || !double.TryParse(limitText, NumberStyles.Float, CultureInfo.InvariantCulture, out var limit))
                unknowns.Add(new Finding($"{travel}% travel", "user's travel limit not stated", q));
            else if (travel > limit)
                mismatch.Add(new Finding($"{travel}% travel", $"above the user's limit of {limit}%", q, Kind: "preference"));
        }

        if (req.PayMax is null)
            notes.Add("Pay is not stated in the source.");
        else if (prefs.SalaryMin is { } floor && req.PayMax < floor)
        {
            var item = new Finding($"salary ≥ {floor:N0}", $"posted range tops out at {req.PayMax:N0}", req.Quotes.GetValueOrDefault("pay"), Kind: "preference");
            (rules.SalaryFloorIsHard ? blockers : mismatch).Add(item);
        }

        var arrangement = posting.WorkArrangement;
        if (arrangement is null)
            notes.Add(posting.WorkArrangementLabel is { } label ? $"The source labels the arrangement \"{label}\", which does not by itself mean fully remote." : "Work arrangement is not stated in the source.");
        else if (prefs.WorkArrangements.Count > 0 && !prefs.WorkArrangements.Contains(arrangement))
            mismatch.Add(new Finding($"work arrangement: {arrangement}", $"user prefers {string.Join(", ", prefs.WorkArrangements)}", Kind: "preference"));
        if (arrangement == "remote")
            notes.Add(req.RemoteRestrictions.Count > 0
                ? "Remote restrictions stated in the source: " + string.Join(" | ", req.RemoteRestrictions)
                : "The source does not say which states or countries are eligible for remote work; do not assume all are.");

        if (arrangement is "onsite" or "hybrid" && prefs.Locations.Count > 0)
        {
            var jobLoc = (posting.Location ?? "").ToLowerInvariant();
            if (!prefs.Locations.Any(l => jobLoc.Contains(l.Split(',')[0].Trim().ToLowerInvariant(), StringComparison.Ordinal)))
            {
                var relocate = profile.ConfirmedText("willing_to_relocate");
                if (IsYes(relocate))
                    notes.Add($"Location \"{posting.Location}\" is outside the user's locations; the user is willing to relocate.");
                else if (relocate is null)
                    unknowns.Add(new Finding($"location: {posting.Location}", "outside the user's locations; relocation willingness unknown"));
                else
                    mismatch.Add(new Finding($"location: {posting.Location}", "outside the user's locations; relocation not wanted", Kind: "preference"));
            }
        }

        var pastDeadline = req.Deadline is not null && DateOnly.ParseExact(req.Deadline, "yyyy-MM-dd", CultureInfo.InvariantCulture) < today;
        if (pastDeadline)
            notes.Add($"The stated application deadline {req.Deadline} has passed.");
        notes.AddRange(prefs.OtherRequirements.Select(r => $"User requirement to check by hand: {r}"));

        // The recommendation itself is not decided here. Screening produces the counts; the certified brick
        // jobsearch.recommendation (apps/job-search/certified/recommendation) turns them into the recommendation, so the
        // rule that decides is the exact source Ashlar's certification gate admitted. See DecisionInputs.
        if (coverage is null)
            notes.Add("No skill requirements could be extracted from the posting; review it by hand.");
        var yearsHave = profile.Number("years_experience");
        var decision = new DecisionInputs
        {
            RequiredSkills = req.SkillsRequired.Count,
            MatchedSkills = matched.Count,
            YearsRequired = req.YearsMin ?? -1,
            YearsHaveTenths = yearsHave is { } y ? (int)Math.Round(y * 10, MidpointRounding.AwayFromZero) : -1,
            Blockers = blockers.Count,
            Unknowns = unknowns.Count,
            PreferenceMismatches = mismatch.Count,
            DeadlinePassed = pastDeadline,
            StrongPercent = Percent(rules.StrongThreshold),
            ConditionalPercent = Percent(rules.ConditionalThreshold),
            MaxYearsShortTenths = (int)Math.Round(rules.MaxYearsShort * 10, MidpointRounding.AwayFromZero),
        };

        var score = Math.Round((coverage ?? 0.5) - 0.1 * unknowns.Count - 0.15 * mismatch.Count - 0.05 * yearsShort + 0.02 * preferredMatched.Count, 3);
        return new Assessment
        {
            Score = score, Coverage = coverage, Requirements = req, Evidence = evidence, Decision = decision,
            Gaps = [.. gaps, .. mismatch], Unknowns = unknowns, Blockers = blockers, Notes = notes,
            MatchedSkills = matched, PreferredMatched = preferredMatched,
        };
    }

    private static int Percent(double share) => (int)Math.Round(share * 100, MidpointRounding.AwayFromZero);

    /// <summary>The assessment with the certified rule's recommendation and reason applied.</summary>
    public static Assessment WithDecision(Assessment a, string recommendation, string reason, string decidedBy) =>
        a with { Recommendation = recommendation, RecommendationReason = reason, DecidedBy = decidedBy };
}
