using System.Globalization;
using Ashlar.Core.Domain.Bricks;
using Ashlar.Core.Domain.Execution;
using Ashlar.WebOperations.Jobs;
using JobSearch.Materials;
using JobSearch.Profile;
using JobSearch.Screening;

namespace JobSearch.Certified;

/// <summary>
/// The certified bricks the app decides with. The host loads each one from the gate-emitted assembly that its
/// certification record covers, after verifying the record's Ed25519 signature against the pinned signer key, and
/// refuses to start when any of them fails to verify (see JobSearch.Host.CertifiedBricks). Nothing in this app compiles
/// or constructs these rules from source.
/// </summary>
public interface ICertifiedRules
{
    /// <summary>The verified brick instance.</summary>
    Brick Get(string brickId);

    /// <summary>The content hash of the certification record that admitted the brick (shown with every decision).</summary>
    string ContentHash(string brickId);
}

/// <summary>The certified brick ids.</summary>
public static class CertifiedIds
{
    /// <summary>Counts to recommendation.</summary>
    public const string Recommendation = "jobsearch.recommendation";
    /// <summary>Draft claims against the résumé.</summary>
    public const string ClaimsCheck = "jobsearch.claims-check";

    /// <summary>Every certified brick the app requires.</summary>
    public static readonly string[] All = [Recommendation, ClaimsCheck];
}

/// <summary>The execution context the app runs certified bricks under: deterministic, no provider.</summary>
public sealed class AppExecutionContext : IExecutionContext
{
    /// <summary>Shared instance.</summary>
    public static AppExecutionContext Instance { get; } = new();
    /// <inheritdoc />
    public string AgentId => "jobsearch";
    /// <inheritdoc />
    public string BehaviorId => "jobsearch";
    /// <inheritdoc />
    public bool IsAirGapped => true;
    /// <inheritdoc />
    public bool AuditMode => false;
    /// <inheritdoc />
    public string Provider => "deterministic";
    /// <inheritdoc />
    public IReadOnlyDictionary<string, object> Variables { get; } = new Dictionary<string, object>();
}

/// <summary>The certified claims check's verdict on one draft, stored in its validation record.</summary>
public sealed record CertifiedCheck(string BrickId, string ContentHash, bool Passed, List<string> Unsupported);

/// <summary>What a draft's claims are checked against.</summary>
public sealed record ClaimsContext(string Resume, string ConfirmedClearance, string AllowedText, string SkillTerms);

/// <summary>Runs the certified claims check on a draft.</summary>
public static class CertifiedClaims
{
    /// <summary>The context for one candidate and posting.</summary>
    public static ClaimsContext Context(CandidateProfile p, JobPosting posting)
    {
        var allowed = new List<string> { posting.Title, posting.Employer };
        if (posting.RequisitionId is { } req)
            allowed.Add(req);
        if (p.Number("years_experience") is { } years)
            allowed.Add(years.ToString("0.#", CultureInfo.InvariantCulture));
        return new ClaimsContext(p.ResumeText, p.ConfirmedText("clearance_status") ?? "", string.Join("\n", allowed), string.Join("\n", Screener.Lexicon));
    }

    /// <summary>
    /// The part of a draft that makes claims. A cover letter's header (name, contact, date, addressee) and sign-off are
    /// assembled by the app from the profile and the posting, so only the lines between the salutation and the closing are
    /// claims; a résumé is all claims. Text from a [CONFIRM: …] placeholder on is the user's to write and is left out.
    /// </summary>
    public static string ClaimText(Draft draft)
    {
        IEnumerable<(string Style, string Text)> lines = draft.Lines;
        if (draft.Kind == "cover_letter")
        {
            var list = draft.Lines.ToList();
            var start = list.FindIndex(l => l.Text.StartsWith("Dear ", StringComparison.Ordinal));
            var end = list.FindLastIndex(l => l.Text.StartsWith("Sincerely", StringComparison.Ordinal));
            if (start >= 0)
                lines = list.Skip(start + 1).Take((end > start ? end : list.Count) - start - 1);
        }
        return string.Join("\n", lines.Select(l =>
        {
            var text = l.Text;
            var at = text.IndexOf(Tailor.ConfirmMarker, StringComparison.Ordinal);
            if (at >= 0)
                text = text[..at].TrimEnd();
            return l.Style == "li" ? "- " + text : text;
        }));
    }

    /// <summary>Runs jobsearch.claims-check on <paramref name="claimText"/>.</summary>
    public static async Task<CertifiedCheck> CheckAsync(ICertifiedRules rules, string claimText, ClaimsContext context, CancellationToken ct)
    {
        var input = new BrickInput(new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["resume"] = context.Resume, ["draft"] = claimText, ["confirmedClearance"] = context.ConfirmedClearance,
            ["allowedText"] = context.AllowedText, ["skillTerms"] = context.SkillTerms,
        });
        var output = await rules.Get(CertifiedIds.ClaimsCheck)
            .ExecuteAsync(input, ImplementationType.Deterministic, AppExecutionContext.Instance, ct).ConfigureAwait(false);
        var unsupported = output.Get<string>("unsupported");
        return new CertifiedCheck(CertifiedIds.ClaimsCheck, rules.ContentHash(CertifiedIds.ClaimsCheck), output.Get<bool>("passed"),
            unsupported.Length == 0 ? [] : [.. unsupported.Split('\n')]);
    }

    /// <summary>Runs jobsearch.claims-check on a draft.</summary>
    public static Task<CertifiedCheck> CheckAsync(ICertifiedRules rules, Draft draft, CandidateProfile p, JobPosting posting, CancellationToken ct) =>
        CheckAsync(rules, ClaimText(draft), Context(p, posting), ct);
}
