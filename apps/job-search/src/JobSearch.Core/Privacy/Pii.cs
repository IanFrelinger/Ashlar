using System.Globalization;
using System.Text.RegularExpressions;
using Ashlar.Abstractions.Security;
using Ashlar.Abstractions.Security.Egress;
using JobSearch.Profile;

#pragma warning disable ASHLAREXP002 // SecurityLabel, SecurityLevel, HighWaterMark and EgressSubject are the experimental egress surface; PII routing is enforced through them by design.

namespace JobSearch.Privacy;

/// <summary>
/// Personal data (PII) and where it may go. Reading a user's data observes <see cref="Label"/> in the current egress
/// subject frame. Every model client goes through Ashlar's egress guard in enforce mode, and only loopback counts as
/// inside the host, so a frame that has read PII can reach the private model on this server and nothing else.
/// </summary>
public static class Pii
{
    /// <summary>The label personal data carries. SPEC-010 §3.5 maps Confidential to no-external-model, no-network-export.</summary>
    public static SecurityLabel Label { get; } = new(SecurityLevel.Confidential);

    /// <summary>Records that the current flow has read personal data. Inside no frame, the flow already counts as SystemHigh.</summary>
    public static void Observe() => EgressSubject.Observe(Label);

    /// <summary>
    /// Enters a frame for work that must not touch personal data, such as asking Claude to write a step from a generic
    /// design. Anything that reads PII inside it raises the frame, and the guard then refuses any call off this host.
    /// </summary>
    public static IDisposable EnterPiiFreeFrame(string purpose) => EgressSubject.Enter(purpose, new HighWaterMark(SecurityLabel.Public));

    /// <summary>Enters a frame for work on one user's personal data (it can reach only this host).</summary>
    public static IDisposable EnterPersonalFrame(string purpose)
    {
        var frame = EgressSubject.Enter(purpose, new HighWaterMark(SecurityLabel.Public));
        Observe();
        return frame;
    }
}

/// <summary>One thing the PII gate found, without the matched text (findings are logged; the text is the PII).</summary>
public sealed record PiiFinding(string Kind, string Where, int Length);

/// <summary>
/// The detector content must pass before it leaves this host for a model that is not on it. It looks for identifier
/// shapes (emails, phone numbers, ID-like and card-like numbers, profile links, street addresses) and for the user's own
/// strings (their name, contact details, résumé lines and employers, and the values of their settings). It is a
/// detector, not a proof; free text a user types never goes off-host at all (STUDIO.md I13).
/// </summary>
public sealed class PiiGate
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(1);

    private static readonly (string Kind, Regex Pattern)[] Shapes =
    [
        ("email", new Regex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}", RegexOptions.CultureInvariant, Budget)),
        ("phone", new Regex(@"(?<!\d)(?:\+?1[\s.-]?)?\(?\d{3}\)?[\s.-]\d{3}[\s.-]\d{4}(?!\d)", RegexOptions.CultureInvariant, Budget)),
        ("phone", new Regex(@"(?<![\w+])\+\d{1,3}(?:[\s.-]?\d{2,4}){3,5}(?!\d)", RegexOptions.CultureInvariant, Budget)),
        ("national-id", new Regex(@"(?<!\d)\d{3}-\d{2}-\d{4}(?!\d)", RegexOptions.CultureInvariant, Budget)),
        ("profile-link", new Regex(@"(?:linkedin\.com/in|github\.com|facebook\.com|instagram\.com|twitter\.com|x\.com|t\.me)/[A-Za-z0-9_.%-]{2,}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Budget)),
        ("street-address", new Regex(@"\b\d{1,5}\s+(?:[A-Z][a-z]+\s){1,3}(?:Street|St|Avenue|Ave|Road|Rd|Boulevard|Blvd|Lane|Ln|Drive|Dr|Court|Ct|Way|Place|Pl)\b\.?", RegexOptions.CultureInvariant, Budget)),
    ];

    private static readonly Regex CardLike = new(@"(?<!\d)(?:\d[ -]?){12,18}\d(?!\d)", RegexOptions.CultureInvariant, Budget);

    /// <summary>Words too common to say anything about a person, ignored when they are a user's setting value.</summary>
    private static readonly HashSet<string> Common = new(StringComparer.OrdinalIgnoreCase)
    {
        "remote", "hybrid", "onsite", "on-site", "full-time", "part-time", "contract", "senior", "junior", "lead", "staff",
        "engineer", "developer", "manager", "analyst", "data", "python", "sql", "java", "the", "and", "for", "with", "yes", "no",
        "true", "false", "none", "any", "all",
    };

    private readonly List<(string Kind, string Text)> _personal = [];

    /// <summary>A gate with no user strings: identifier shapes only.</summary>
    public PiiGate() { }

    /// <summary>A gate that also refuses the user's own strings.</summary>
    public PiiGate(CandidateProfile? profile, IEnumerable<string>? settingValues = null)
    {
        if (profile is not null)
        {
            var f = profile.Facts;
            if (f.Name?.Value is { } name)
            {
                AddPersonal("name", name);
                foreach (var part in name.Split([' ', '-', '.'], StringSplitOptions.RemoveEmptyEntries))
                    AddPersonal("name", part);
            }
            AddPersonal("email", f.Email?.Value);
            AddPersonal("phone", f.Phone?.Value);
            AddPersonal("location", f.Location?.Value);
            foreach (var job in f.Employment)
                AddPersonal("employer", job.Employer);
            foreach (var e in f.Education)
                AddPersonal("education", e.Value);
            foreach (var line in profile.ResumeText.Split('\n'))
            {
                var t = line.Trim().TrimStart('-', '*', '•', ' ');
                if (t.Length >= 25)
                    AddPersonal("résumé line", t);
            }
            foreach (var (key, field) in profile.Fields)
                if (field.Value is { ValueKind: System.Text.Json.JsonValueKind.String } v && v.GetString() is { Length: >= 3 } s && key is not "years_experience")
                    AddPersonal($"profile:{key}", s);
        }
        foreach (var v in settingValues ?? [])
        {
            foreach (var piece in (v ?? "").Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries))
                AddPersonal("setting", piece.Trim());
        }
    }

    private void AddPersonal(string kind, string? text)
    {
        var t = text?.Trim();
        if (t is null || t.Length < 3 || Common.Contains(t))
            return;
        // A short number (a year, a count) says nothing about a person; a long one (pay, an ID) may.
        if (t.All(char.IsDigit) && t.Length < 5)
            return;
        _personal.Add((kind, t));
    }

    /// <summary>What the gate finds in the named parts of some content; empty means it may leave the host.</summary>
    public IReadOnlyList<PiiFinding> Inspect(IEnumerable<(string Where, string Text)> parts)
    {
        var findings = new List<PiiFinding>();
        foreach (var (where, text) in parts)
        {
            if (string.IsNullOrEmpty(text))
                continue;
            foreach (var (kind, pattern) in Shapes)
                foreach (Match m in pattern.Matches(text))
                    findings.Add(new PiiFinding(kind, where, m.Length));
            foreach (Match m in CardLike.Matches(text))
            {
                var digits = new string(m.Value.Where(char.IsDigit).ToArray());
                if (digits.Length is >= 13 and <= 19 && Luhn(digits))
                    findings.Add(new PiiFinding("card-number", where, m.Length));
            }
            foreach (var (kind, value) in _personal)
                if (ContainsWord(text, value))
                    findings.Add(new PiiFinding($"your {kind}", where, value.Length));
        }
        return findings;
    }

    /// <summary>Convenience for one piece of text.</summary>
    public IReadOnlyList<PiiFinding> Inspect(string where, string text) => Inspect([(where, text)]);

    private static bool ContainsWord(string text, string value)
    {
        var from = 0;
        while (true)
        {
            var at = text.IndexOf(value, from, StringComparison.OrdinalIgnoreCase);
            if (at < 0)
                return false;
            var end = at + value.Length;
            var before = at == 0 || !char.IsLetterOrDigit(text[at - 1]);
            var after = end >= text.Length || !char.IsLetterOrDigit(text[end]);
            if (before && after)
                return true;
            from = at + 1;
        }
    }

    private static bool Luhn(string digits)
    {
        var sum = 0;
        var doubleIt = false;
        for (var i = digits.Length - 1; i >= 0; i--)
        {
            var d = digits[i] - '0';
            if (doubleIt)
            {
                d *= 2;
                if (d > 9) d -= 9;
            }
            sum += d;
            doubleIt = !doubleIt;
        }
        return sum % 10 == 0;
    }

    /// <summary>A short description of findings for a user or a log, without the matched text.</summary>
    public static string Describe(IReadOnlyList<PiiFinding> findings) =>
        string.Join("; ", findings.GroupBy(f => (f.Kind, f.Where)).Select(g => $"{g.Key.Kind} in {g.Key.Where}" + (g.Count() > 1 ? $" (×{g.Count().ToString(CultureInfo.InvariantCulture)})" : "")));
}

/// <summary>A model reply, with the tokens it cost.</summary>
public sealed record ModelReply(string Text, string Model, long InputTokens, long OutputTokens);

/// <summary>
/// The server-hosted model: the only model that may receive personal data. It runs on this host (loopback), so the
/// egress guard admits it from a frame that has read PII.
/// </summary>
public interface IPrivateModel
{
    /// <summary>Asks for a JSON object that matches <paramref name="jsonSchema"/>.</summary>
    Task<ModelReply> CompleteJsonAsync(string system, string user, string jsonSchema, CancellationToken ct);

    /// <summary>Asks for plain text.</summary>
    Task<ModelReply> CompleteTextAsync(string system, string user, CancellationToken ct);

    /// <summary>What the model is, for records and the designer.</summary>
    string Describe();

    /// <summary>Whether the model answers and has its model ready (the operator's <c>/health/model</c> probe); sends no personal data.</summary>
    Task<bool> ReadyAsync(CancellationToken ct) => Task.FromResult(true);
}

/// <summary>
/// The external step writer (Claude). It receives only generic designs that passed the PII gate, inside a PII-free
/// frame; the guard refuses it from any frame that has read personal data.
/// </summary>
public interface IStepWriter
{
    /// <summary>Asks for a JSON object that matches <paramref name="jsonSchema"/>.</summary>
    Task<ModelReply> WriteJsonAsync(string system, string user, string jsonSchema, CancellationToken ct);

    /// <summary>What the writer is.</summary>
    string Describe();
}
