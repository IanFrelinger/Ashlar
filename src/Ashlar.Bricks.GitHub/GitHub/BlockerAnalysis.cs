using System.Text;

namespace Ashlar.Bricks.GitHub;

internal sealed record Blocker(string Reason, string Evidence);

internal sealed record Note(string Text, string? Evidence);

internal sealed record BlockerAnswer(
    string Verdict,
    IReadOnlyList<Blocker> Blockers,
    IReadOnlyList<Unknown> Unknowns,
    IReadOnlyList<Note> Notes,
    IReadOnlyList<string> Evidence,
    string Summary,
    string Markdown);

/// <summary>
/// Turns what was read into an answer. Pure: no I/O, so every rule here is tested without GitHub.
/// </summary>
/// <remarks>
/// Verdicts: <c>blocked</c> (at least one blocker), <c>unknown</c> (no blocker found, but something that could hide one
/// was not read), <c>ready</c>, and for pull requests <c>merged</c> and <c>closed</c>. A failing check that is not
/// required is a note, never a blocker. Only when neither branch protection nor the public required-check lists could
/// be read does GitHub's own <c>mergeable_state</c> decide whether a failing check blocks: <c>clean</c>,
/// <c>unstable</c> and <c>has_hooks</c> mean GitHub would allow the merge, so failures are notes.
/// </remarks>
internal static class BlockerAnalysis
{
    public const string Blocked = "blocked";
    public const string Ready = "ready";
    public const string UnknownVerdict = "unknown";
    public const string Merged = "merged";
    public const string Closed = "closed";

    private static readonly HashSet<string> FailedConclusions = new(StringComparer.OrdinalIgnoreCase)
    {
        "failure", "timed_out", "cancelled", "action_required", "startup_failure", "stale",
    };

    private static readonly HashSet<string> MergeAllowedStates = new(StringComparer.OrdinalIgnoreCase)
    {
        "clean", "unstable", "has_hooks",
    };

    public static BlockerAnswer Analyze(BlockerSnapshot snapshot)
    {
        var blockers = new List<Blocker>();
        var unknowns = new List<Unknown>(snapshot.Unknowns);
        var notes = new List<Note>();
        string verdict;

        if (snapshot.PullRequest is { } pr)
            verdict = AnalyzePullRequest(snapshot.Target, pr, blockers, unknowns, notes);
        else if (snapshot.Release is { } release)
            verdict = AnalyzeRelease(snapshot.Target, release, blockers, unknowns, notes);
        else
            verdict = UnknownVerdict;

        var evidence = new List<string> { snapshot.Target.WebUrl };
        evidence.AddRange(blockers.Select(b => b.Evidence));
        evidence.AddRange(notes.Where(n => n.Evidence is not null).Select(n => n.Evidence!));
        evidence = evidence.Distinct(StringComparer.Ordinal).ToList();

        var summary = Summarize(snapshot.Target, verdict, blockers.Count, unknowns.Count);
        return new BlockerAnswer(verdict, blockers, unknowns, notes, evidence, summary,
            Render(snapshot.Target, summary, blockers, unknowns, notes));
    }

    private static string AnalyzePullRequest(
        GitHubTarget target, PullRequestFacts pr, List<Blocker> blockers, List<Unknown> unknowns, List<Note> notes)
    {
        if (pr.Merged)
            return Merged;
        if (pr.Closed)
        {
            blockers.Add(new Blocker("The pull request is closed without being merged.", pr.Url));
            return Closed;
        }

        if (pr.Draft)
            blockers.Add(new Blocker("The pull request is a draft.", pr.Url));

        var state = pr.MergeableState?.ToLowerInvariant();
        if (pr.Mergeable == false || state == "dirty")
            blockers.Add(new Blocker("The branch has merge conflicts with " + pr.BaseRef + ".", pr.Url));
        else if (state == "behind")
            blockers.Add(new Blocker("The branch is behind " + pr.BaseRef + ", and the base branch requires branches to be up to date.", pr.Url));
        else if (pr.Mergeable is null || state is null or "unknown")
            unknowns.Add(new Unknown("Mergeability",
                "GitHub had not computed it when asked (it does so in a background job, which can take a while); ask again later, and if it stays unknown, the pull request page shows GitHub's own view."));

        var mergeAllowed = state is not null && MergeAllowedStates.Contains(state);
        var protection = pr.Protection;

        AnalyzeReviews(pr, protection, mergeAllowed, blockers, unknowns);

        var checksAdvisory = protection.RequiredChecks is null && mergeAllowed;
        if (protection.RequiredChecks is null && !mergeAllowed)
        {
            unknowns.Add(new Unknown("Which checks are required",
                "Neither branch protection (it needs admin rights) nor the base branch's public required-check lists could be read, so every failing or pending check is listed as a blocker."));
        }

        AnalyzeCi(target, pr.Ci, protection.RequiredChecks, checksAdvisory, "head commit", blockers, unknowns, notes);

        if (state == "blocked" && blockers.Count == 0)
        {
            blockers.Add(new Blocker(
                "GitHub reports merging as blocked by a branch rule this read could not identify (branch protection or a repository ruleset).",
                pr.Url));
        }

        return Verdict(blockers, unknowns);
    }

    private static void AnalyzeReviews(
        PullRequestFacts pr, ProtectionFacts protection, bool mergeAllowed, List<Blocker> blockers, List<Unknown> unknowns)
    {
        if (pr.Reviews is null)
        {
            unknowns.Add(new Unknown("Reviews", "The reviews could not be read."));
            return;
        }

        var latest = pr.Reviews
            .Where(r => !string.Equals(r.State, "commented", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(r.State, "pending", StringComparison.OrdinalIgnoreCase))
            .GroupBy(r => r.User, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(r => r.SubmittedAt ?? DateTimeOffset.MinValue).First())
            .ToList();

        foreach (var review in latest.Where(r => string.Equals(r.State, "changes_requested", StringComparison.OrdinalIgnoreCase)))
            blockers.Add(new Blocker($"{review.User} requested changes.", review.Url ?? pr.Url));

        var approvals = latest.Count(r => string.Equals(r.State, "approved", StringComparison.OrdinalIgnoreCase));
        var tooFew = protection.RequiredApprovals is { } required && approvals < required
            ? new Blocker($"{approvals} of {required} required approving reviews.", pr.Url)
            : null;

        // GitHub's review decision, when read, is the authority: it also covers code-owner and last-push approval rules.
        if (protection.ReviewDecision is { } decision)
        {
            if (string.Equals(decision, "REVIEW_REQUIRED", StringComparison.OrdinalIgnoreCase))
            {
                blockers.Add(tooFew ?? new Blocker(
                    $"GitHub reports that an approving review is required (review decision REVIEW_REQUIRED); the pull request has {approvals} approving review(s).", pr.Url));
            }
        }
        else if (tooFew is not null)
        {
            blockers.Add(tooFew);
        }
        else if ((protection.RequiredApprovals is null || protection.ApprovalsPartial) && !mergeAllowed)
        {
            unknowns.Add(new Unknown("Required approvals",
                $"Branch protection is not readable with this token (it needs admin rights), and GitHub's review decision could not be read; the pull request has {approvals} approving review(s)."));
        }
    }

    private static string AnalyzeRelease(
        GitHubTarget target, ReleaseFacts release, List<Blocker> blockers, List<Unknown> unknowns, List<Note> notes)
    {
        var repoUrl = $"https://github.com/{target.Owner}/{target.Repo}";
        if (!release.TagExists)
        {
            blockers.Add(new Blocker($"The tag {release.Tag} does not exist.", repoUrl + "/tags"));
            return Verdict(blockers, unknowns);
        }

        if (release.ReleaseUrl is null)
            blockers.Add(new Blocker($"No GitHub release exists for the tag {release.Tag}.", repoUrl + "/releases"));
        else if (release.Draft == true)
            blockers.Add(new Blocker("The release is still a draft.", release.ReleaseUrl));

        if (release.Prerelease == true)
            notes.Add(new Note("The release is marked as a pre-release.", release.ReleaseUrl));
        if (release.AssetCount == 0)
            notes.Add(new Note("The release has no assets attached.", release.ReleaseUrl));

        AnalyzeCi(target, release.Ci, requiredChecks: null, advisory: false, "tagged commit", blockers, unknowns, notes);
        return Verdict(blockers, unknowns);
    }

    private static void AnalyzeCi(
        GitHubTarget target,
        CiFacts? ci,
        IReadOnlyList<string>? requiredChecks,
        bool advisory,
        string commitLabel,
        List<Blocker> blockers,
        List<Unknown> unknowns,
        List<Note> notes)
    {
        if (ci is null)
        {
            unknowns.Add(new Unknown("CI", $"The checks on the {commitLabel} could not be read."));
            return;
        }

        var commitUrl = $"https://github.com/{target.Owner}/{target.Repo}/commit/{ci.Sha}";
        var reported = new HashSet<string>(StringComparer.Ordinal);
        var problems = new List<(string Name, string Reason, string Evidence)>();

        if (ci.Checks is null)
            unknowns.Add(new Unknown("Check runs", $"The check runs on the {commitLabel} could not be read."));
        else
        {
            if (ci.ChecksReported > ci.Checks.Count)
                unknowns.Add(new Unknown("Check runs", $"{ci.ChecksReported - ci.Checks.Count} of {ci.ChecksReported} check runs were not read."));
            foreach (var check in ci.Checks)
            {
                reported.Add(check.Name);
                if (!string.Equals(check.Status, "completed", StringComparison.OrdinalIgnoreCase))
                    problems.Add((check.Name, $"The check {check.Name} is still {check.Status.Replace('_', ' ')}.", check.Url ?? commitUrl));
                else if (check.Conclusion is { } conclusion && FailedConclusions.Contains(conclusion))
                    problems.Add((check.Name, $"The check {check.Name} concluded {conclusion.Replace('_', ' ')}.", check.Url ?? commitUrl));
            }
        }

        if (ci.Statuses is null)
            unknowns.Add(new Unknown("Commit statuses", $"The commit statuses on the {commitLabel} could not be read."));
        else
        {
            if (ci.StatusesReported > ci.Statuses.Count)
                unknowns.Add(new Unknown("Commit statuses", $"{ci.StatusesReported - ci.Statuses.Count} of {ci.StatusesReported} commit statuses were not read."));
            foreach (var status in ci.Statuses)
            {
                reported.Add(status.Context);
                var state = status.State.ToLowerInvariant();
                if (state == "pending")
                    problems.Add((status.Context, $"The status {status.Context} is still pending.", status.Url ?? commitUrl));
                else if (state is "failure" or "error")
                    problems.Add((status.Context, $"The status {status.Context} reported {state}" + (string.IsNullOrWhiteSpace(status.Description) ? "." : $": {status.Description}"), status.Url ?? commitUrl));
            }
        }

        if (problems.Count == 0 && ci.Checks is not null && ci.Statuses is not null)
        {
            var sha = ci.Sha[..Math.Min(ci.Sha.Length, 12)];
            notes.Add(ci.Checks.Count + ci.Statuses.Count == 0
                ? new Note($"No check run or commit status has reported on the {commitLabel} {sha}.", commitUrl)
                : new Note($"Read {ci.Checks.Count} check run(s) and {ci.Statuses.Count} commit status(es) on the {commitLabel} {sha}; none is failing or pending.", commitUrl));
        }

        var required = requiredChecks is null ? null : new HashSet<string>(requiredChecks, StringComparer.Ordinal);
        foreach (var (name, reason, evidence) in problems)
        {
            if (advisory || (required is not null && !required.Contains(name)))
                notes.Add(new Note(reason + " It is not required, so it does not block the merge.", evidence));
            else
                blockers.Add(new Blocker(reason, evidence));
        }

        if (required is not null && ci.Checks is not null && ci.Statuses is not null)
        {
            foreach (var name in required.Where(n => !reported.Contains(n)).OrderBy(n => n, StringComparer.Ordinal))
                blockers.Add(new Blocker($"The required check {name} has not reported on {ci.Sha[..Math.Min(ci.Sha.Length, 12)]}.", commitUrl));
        }
    }

    private static string Verdict(List<Blocker> blockers, List<Unknown> unknowns) =>
        blockers.Count > 0 ? Blocked : unknowns.Count > 0 ? UnknownVerdict : Ready;

    private static string Summarize(GitHubTarget target, string verdict, int blockers, int unknowns)
    {
        var head = verdict switch
        {
            Blocked => $"{target.Label} is BLOCKED by {blockers} blocker(s)",
            Ready => $"{target.Label} is READY: nothing blocking was found",
            Merged => $"{target.Label} is already merged",
            Closed => $"{target.Label} is closed without being merged",
            _ => $"{target.Label}: no blocker found, but it is UNKNOWN whether anything blocks it",
        };
        head = char.ToUpperInvariant(head[0]) + head[1..];
        return unknowns > 0 ? $"{head}; {unknowns} unknown(s)." : head + ".";
    }

    private static string Render(GitHubTarget target, string summary, List<Blocker> blockers, List<Unknown> unknowns, List<Note> notes)
    {
        var md = new StringBuilder();
        md.Append("**What is blocking ").Append(target.Label).Append("?** ").AppendLine(summary);
        Section(md, "Blockers", blockers.Select(b => $"{b.Reason} ([evidence]({b.Evidence}))"));
        Section(md, "Unknowns", unknowns.Select(u => $"{u.What}: {u.Why}"));
        Section(md, "Notes", notes.Select(n => n.Evidence is null ? n.Text : $"{n.Text} ([evidence]({n.Evidence}))"));
        md.AppendLine().Append("Read from ").AppendLine(target.WebUrl);
        return md.ToString();
    }

    private static void Section(StringBuilder md, string title, IEnumerable<string> lines)
    {
        var list = lines.ToList();
        if (list.Count == 0)
            return;
        md.AppendLine().Append(title).AppendLine(":");
        foreach (var line in list)
            md.Append("- ").AppendLine(line);
    }
}
