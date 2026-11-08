using System.Text;
using Ashlar.Core.Domain.Bricks;
using Ashlar.Core.Domain.Execution;

namespace JobSearch.Certified;

/// <summary>
/// jobsearch.claims-check: whether a drafted résumé or cover letter claims anything the candidate's own record does not
/// support. Certified: this exact source is what the job-search app runs, admitted by Ashlar's certification gate. It
/// judges drafts whoever wrote them, a template or a model.
/// Rules: a bullet must be a line of the résumé; a skill term may appear only if the résumé names it; every number must
/// be a whole number of the résumé or of the allowed context (dates, the job's title, computed years); a clearance may be mentioned
/// only as the user confirmed it.
/// </summary>
public sealed class ClaimsCheckBrick : DomainBrick
{
    public ClaimsCheckBrick()
    {
        Id = "jobsearch.claims-check";
        Name = "Draft claims check";
        Version = "1.0.0";
        Category = BrickCategory.Validation;
        Description = "Refuses a draft that claims what the résumé and the user's confirmed profile do not support.";
        Implementations = new BrickImplementations
        {
            Deterministic = new DeterministicImplementation
            {
                Id = "rules", Name = "Rules", Executor = "code", Description = "Line, term and number matching; no model.",
            },
        };
        Interface = new BrickInterface
        {
            Inputs =
            [
                new BrickInputDefinition("resume", "string", "The candidate's résumé text"),
                new BrickInputDefinition("draft", "string", "The draft; bullet lines start with \"- \""),
                new BrickInputDefinition("confirmedClearance", "string", "The clearance status the user confirmed; empty when none"),
                new BrickInputDefinition("allowedText", "string", "Context a draft may cite: the job's title and employer, dates, computed years"),
                new BrickInputDefinition("skillTerms", "string", "Skill vocabulary, one lowercase term per line"),
            ],
            Outputs =
            [
                new BrickOutputDefinition("passed", "bool", "No unsupported claim"),
                new BrickOutputDefinition("unsupportedCount", "int", "How many claims lack support"),
                new BrickOutputDefinition("unsupported", "string", "Each unsupported claim, one per line"),
            ],
        };
    }

    public override Task<BrickOutput> ExecuteAsync(BrickInput input, ImplementationType implementation, IExecutionContext context, CancellationToken cancellationToken = default)
    {
        var resume = input.Get<string>("resume");
        var draft = input.Get<string>("draft");
        var clearance = input.Get<string>("confirmedClearance").Trim();
        var allowed = input.Get<string>("allowedText");
        var terms = input.Get<string>("skillTerms");

        var problems = new List<string>();
        var resumeLines = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in resume.Split('\n'))
            resumeLines.Add(StripBullet(raw.Trim()));
        // A draft's number must be a whole number token of the résumé or the allowed context: "5" is not supported by
        // "9.5", nor "17" by "2017".
        var sourceNumbers = new HashSet<string>(StringComparer.Ordinal);
        var resumeNumbers = Numbers(resume);
        for (var n = 0; n < resumeNumbers.Count; n++)
            sourceNumbers.Add(resumeNumbers[n]);
        var allowedNumbers = Numbers(allowed);
        for (var n = 0; n < allowedNumbers.Count; n++)
            sourceNumbers.Add(allowedNumbers[n]);
        var resumeLower = resume.ToLowerInvariant();
        var allowedLower = allowed.ToLowerInvariant();
        var draftLower = draft.ToLowerInvariant();

        foreach (var raw in draft.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("- ", StringComparison.Ordinal))
            {
                var bullet = line.Substring(2).Trim();
                if (!resumeLines.Contains(bullet))
                    problems.Add("bullet not in the résumé: " + bullet);
                continue;
            }
            var numbers = Numbers(line);
            for (var n = 0; n < numbers.Count; n++) // indexed: List<T>.Enumerator is outside the IL-import allowlist
            {
                var number = numbers[n];
                if (!sourceNumbers.Contains(number))
                    problems.Add("number without a source: " + number);
            }
        }

        foreach (var raw in terms.Split('\n'))
        {
            var term = raw.Trim().ToLowerInvariant();
            if (term.Length == 0)
                continue;
            if (HasTerm(draftLower, term) && !HasTerm(resumeLower, term) && !HasTerm(allowedLower, term))
                problems.Add("skill not in the résumé: " + term);
        }

        var mentionsClearance = HasTerm(draftLower, "secret") || HasTerm(draftLower, "top secret") || HasTerm(draftLower, "ts/sci") || HasTerm(draftLower, "public trust");
        if (mentionsClearance && (clearance.Length == 0 || !draftLower.Contains(clearance.ToLowerInvariant(), StringComparison.Ordinal)))
            problems.Add("clearance mentioned but not as the user confirmed it");

        var output = new BrickOutput { Summary = problems.Count + " unsupported claim(s)" };
        output.Set("passed", problems.Count == 0);
        output.Set("unsupportedCount", problems.Count);
        output.Set("unsupported", string.Join("\n", problems));
        return Task.FromResult(output);
    }

    private static string StripBullet(string line)
    {
        var i = 0;
        while (i < line.Length && (line[i] == '-' || line[i] == '*' || line[i] == '•' || line[i] == ' '))
            i++;
        return line.Substring(i);
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '+' || c == '#';

    /// <summary>Whole-term match: the characters around the term are not letters, digits, '+' or '#'.</summary>
    private static bool HasTerm(string text, string term)
    {
        var from = 0;
        while (true)
        {
            var at = text.IndexOf(term, from, StringComparison.Ordinal);
            if (at < 0)
                return false;
            var end = at + term.Length;
            var beforeOk = at == 0 || !IsWordChar(text[at - 1]);
            var afterOk = end >= text.Length || !IsWordChar(text[end]);
            if (beforeOk && afterOk)
                return true;
            from = at + 1;
        }
    }

    /// <summary>Digit runs, keeping inner commas and points ("2,000", "9.4").</summary>
    private static List<string> Numbers(string line)
    {
        var found = new List<string>();
        var i = 0;
        while (i < line.Length)
        {
            if (!char.IsDigit(line[i]))
            {
                i++;
                continue;
            }
            var sb = new StringBuilder();
            while (i < line.Length && (char.IsDigit(line[i]) || ((line[i] == ',' || line[i] == '.') && i + 1 < line.Length && char.IsDigit(line[i + 1]))))
            {
                sb.Append(line[i]);
                i++;
            }
            found.Add(sb.ToString());
        }
        return found;
    }
}
