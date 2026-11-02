using System.Text;
using System.Text.Json;

namespace Ashlar.Bricks.GitHub;

/// <summary>Version 1 of the open, read-only pull-request evidence snapshot. This is not a release authorization.</summary>
internal sealed record ReleaseEvidencePack(
    string SchemaVersion,
    string Repository,
    int PullRequest,
    string Url,
    string? HeadSha,
    string? BaseRef,
    string? BaseSha,
    DateTimeOffset CollectionStartedAt,
    DateTimeOffset CollectedAt,
    string AshlarVersion,
    string BrickVersion,
    string? AuditId,
    string Scope,
    EvidenceAuthorization Authorization,
    string Verdict,
    string Assurance,
    IReadOnlyList<EvidenceFinding> Findings,
    IReadOnlyList<string> Limitations,
    EvidenceFreshness Freshness);

internal sealed record EvidenceAuthorization(string Credential, string EffectivePermissions, string AuthenticatedActor);
internal sealed record EvidenceFreshness(string CollectionConsistency, string Revalidation, int CollectionAttempts = 1);
internal sealed record EvidenceFinding(string Id, string Status, bool Required, string Explanation, IReadOnlyList<string> Sources);

/// <summary>Produces a conservative assurance result without changing the existing GitHub-derived verdict.</summary>
internal static class ReleaseEvidence
{
    internal static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    internal static ReleaseEvidencePack Create(BlockerSnapshot snapshot, BlockerAnswer answer,
        DateTimeOffset started, DateTimeOffset completed, string version, bool credentialSupplied, string? collectionCheckName = null, string brickVersion = "unknown")
    {
        var findings = new List<EvidenceFinding>();
        var pr = snapshot.PullRequest;
        var target = snapshot.Target;
        var repository = target.Owner + "/" + target.Repo;
        var sources = new[] { target.WebUrl };
        var commit = $"https://github.com/{repository}/commit/{pr?.HeadSha ?? pr?.Ci?.Sha}";
        void Add(string id, string status, string explanation, params string[] links) =>
            findings.Add(new EvidenceFinding(id, status, true, explanation, links.Length == 0 ? sources : links.Select(l => BlockerAnalysis.Link(l) ?? target.WebUrl).ToArray()));

        for (var i = 0; i < answer.Blockers.Count; i++)
        {
            var blocker = answer.Blockers[i];
            var unsettled = IsUnsettledCiBlocker(pr, blocker);
            var provenUnmet = IsProvenUnmetCiBlocker(pr, blocker);
            // A caller may identify this collection job. A proven required pending job is currently
            // unmet; unreadable policy/provider/CI leaves the circular requirement unknown.
            if (!string.IsNullOrWhiteSpace(collectionCheckName) && blocker.Reason.Contains(BlockerAnalysis.Quote(collectionCheckName), StringComparison.Ordinal)
                && unsettled
                && pr?.Ci?.Checks?.Any(c => c.Name == collectionCheckName && c.Status != "completed") == true
                && (pr.Protection.RequiredChecks is null || pr.Protection.RequiredChecks.Contains(collectionCheckName, StringComparer.Ordinal)))
                Add("collection-check", provenUnmet ? "fail" : "unknown",
                    provenUnmet
                        ? "The required collection check is pending; its circular requirement is currently unmet."
                        : "The collection job is pending and its requirement is circular or could not be established.", blocker.Evidence);
            else if (IsUnverifiedOriginCiBlocker(pr, blocker))
                Add("blocker-" + (i + 1), "unknown", "The legacy verdict reports: " + blocker.Reason
                    + " Its app/provider identity was not verified against the required check; this does not establish a failed requirement.", blocker.Evidence);
            else if (MissingRequiredCheckName(pr, blocker) is { } missing && !IsCompleteCi(pr?.Ci))
                Add("blocker-" + (i + 1), "unknown", "The required check " + BlockerAnalysis.Quote(missing)
                    + " was not observed in incomplete CI results; this does not establish whether it reported.", blocker.Evidence);
            else
                Add("blocker-" + (i + 1), unsettled && !provenUnmet ? "unknown" : "fail",
                    blocker.Reason, blocker.Evidence);
        }
        for (var i = 0; i < answer.Unknowns.Count; i++)
            Add("unavailable-" + (i + 1), "unknown", answer.Unknowns[i].What + ": " + answer.Unknowns[i].Why);

        if (pr is null)
            Add("pull-request", "unknown", "The pull request could not be inspected.");
        else if (!pr.Closed && !pr.Merged)
        {
            Add("head-identity", IsCommitSha(pr.HeadSha) ? "pass" : "unknown",
                IsCommitSha(pr.HeadSha) ? "A 40-character hexadecimal head commit was observed for this snapshot's collection attempt."
                    : "The head commit is missing or is not a supported 40-character hexadecimal GitHub SHA.");
            Add("base-identity", IsCommitSha(pr.BaseSha) && !string.IsNullOrWhiteSpace(pr.BaseRef) ? "pass" : "unknown",
                IsCommitSha(pr.BaseSha) && !string.IsNullOrWhiteSpace(pr.BaseRef) ? "The relevant base reference and 40-character hexadecimal commit were observed."
                    : "The base reference or commit is missing or malformed; base-change detection cannot establish stable input identity.");
            Add("collection-consistency", snapshot.CollectionConsistency == "stable" ? "pass" : "unknown",
                snapshot.CollectionConsistency == "stable"
                    ? (snapshot.CollectionAttempts > 1 ? "Recollected once after an observed PR change. " : "")
                        + "The final PR read agreed with this attempt's head, base, draft, open state and mergeability. Checks, reviews and rules are not an atomic snapshot."
                    : "The final PR read changed or could not establish the collected head, base and mergeability facts; collect again.");
            var policySources = new[]
            {
                $"https://api.github.com/repos/{repository}/branches/{Uri.EscapeDataString(pr.BaseRef)}/protection/required_status_checks",
                $"https://api.github.com/repos/{repository}/branches/{Uri.EscapeDataString(pr.BaseRef)}/protection/required_pull_request_reviews",
                $"https://api.github.com/repos/{repository}/rules/branches/{Uri.EscapeDataString(pr.BaseRef)}",
                $"https://api.github.com/repos/{repository}/branches/{Uri.EscapeDataString(pr.BaseRef)}",
            };
            Add("required-check-policy", pr.Protection.RequiredChecks is null ? "unknown" : "pass",
                pr.Protection.RequiredChecks is null ? "The complete required-check list could not be read; GitHub mergeability alone does not establish assurance."
                    : "Required check names were read from GitHub policy sources.", policySources);
            Add("rulesets", pr.Protection.RulesRead ? "pass" : "unknown",
                pr.Protection.RulesRead ? "Active branch rules were read (this collector evaluates check and review requirements only)."
                    : "Active rules were unavailable, malformed or at the pagination limit; absence of rules was not established.", policySources[2]);
            if (pr.Protection.RequiredChecks is { Count: > 0 } && !pr.Protection.RequiredCheckOriginsKnown)
                Add("required-check-origin", "unknown", "Required check app/provider bindings could not be verified; a matching check name alone is insufficient.", policySources);
            foreach (var rule in pr.Protection.UnsupportedRules ?? [])
                Add("unsupported-rule-" + findings.Count, "unknown", "Detected mandatory rule " + BlockerAnalysis.Quote(rule) + " is outside this collector's supported policy evaluation.", policySources[2]);
            var approvalKnown = pr.Protection.ReviewDecision == "APPROVED"
                || (pr.Protection.ReviewDecision == "NONE" && (pr.Protection.RequiredApprovals is null or 0) && !pr.Protection.AdditionalReviewRules)
                || (pr.Protection.RequiredApprovals == 0 && !pr.Protection.ApprovalsPartial && !pr.Protection.AdditionalReviewRules);
            if (pr.Protection.ReviewDecision == "NONE" && pr.Protection.RequiredApprovals is > 0)
                Add("conflicting-review-policy", "unknown", "GitHub's review decision NONE conflicts with an observed positive approval requirement; collect again and review the source policy.", policySources);
            var approvalFailed = pr.Protection.ReviewDecision is "REVIEW_REQUIRED" or "CHANGES_REQUESTED";
            Add("approval-policy", approvalFailed ? "fail" : approvalKnown ? "pass" : "unknown",
                approvalFailed ? "GitHub reports required review approval as unsatisfied."
                    : approvalKnown ? "GitHub's review decision, or a fully read zero-approval rule, establishes the review requirement."
                    : "Approval count alone does not establish current code-owner, last-push or stale-review requirements.");
            if (pr.Protection.RequiredApprovals is > 0 && pr.Protection.ReviewDecision is null
                && pr.Reviews?.Any(r => string.Equals(r.State, "APPROVED", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(r.CommitSha, pr.HeadSha, StringComparison.OrdinalIgnoreCase)) == true)
                Add("stale-approvals", "unknown", "An approval is for another or unknown commit; current approval eligibility was not established.");

            if (pr.Ci?.Checks is { } observedChecks && pr.Ci.ChecksReported < observedChecks.Count
                || pr.Ci?.Statuses is { } observedStatuses && pr.Ci.StatusesReported < observedStatuses.Count)
                Add("inconsistent-ci-counts", "unknown", "GitHub returned more results than its reported total; collection completeness is inconsistent.", commit);
            if (pr.Protection.RequiredChecks is { } required)
            {
                foreach (var name in required.Distinct(StringComparer.Ordinal))
                {
                    var checks = pr.Ci?.Checks?.Where(c => string.Equals(c.Name, name, StringComparison.Ordinal)).ToList() ?? [];
                    var statuses = pr.Ci?.Statuses?.Where(s => string.Equals(s.Context, name, StringComparison.Ordinal)).ToList() ?? [];
                    var isCollection = !string.IsNullOrWhiteSpace(collectionCheckName) && name == collectionCheckName;
                    var allPassed = checks.All(c => c.Status == "completed" && c.Conclusion is "success" or "neutral" or "skipped")
                        && statuses.All(s => s.State == "success");
                    var absent = checks.Count + statuses.Count == 0;
                    var knownFailure = checks.Any(c => c.Status == "completed" && c.Conclusion is
                        "failure" or "timed_out" or "cancelled" or "action_required" or "startup_failure" or "stale")
                        || statuses.Any(s => s.State is "failure" or "error");
                    var originKnown = pr.Protection.RequiredCheckOriginsKnown;
                    var complete = IsCompleteCi(pr.Ci);
                    var pending = checks.Any(c => IsKnownPendingState(c.Status))
                        || statuses.Any(s => string.Equals(s.State, "pending", StringComparison.OrdinalIgnoreCase));
                    var status = !originKnown ? "unknown" : knownFailure ? "fail" : !complete ? "unknown"
                        : absent || pending ? "fail" : isCollection ? "unknown" : allPassed ? "pass" : "unknown";
                    var links = checks.Select(c => BlockerAnalysis.Link(c.Url) ?? commit)
                        .Concat(statuses.Select(s => BlockerAnalysis.Link(s.Url) ?? commit)).DefaultIfEmpty(commit).Distinct(StringComparer.Ordinal).ToArray();
                    Add("required-check-" + (findings.Count + 1), status,
                        !originKnown ? "Observations named " + BlockerAnalysis.Quote(name) + " could not be bound to the required app/provider; their conclusions do not establish satisfaction or failure of that requirement."
                            : absent && !complete ? "The required check " + BlockerAnalysis.Quote(name) + " was not observed in incomplete CI results; this does not establish whether it reported."
                            : !complete && !knownFailure ? "CI results for required check " + BlockerAnalysis.Quote(name) + " were incomplete; satisfaction could not be established."
                            : isCollection && !knownFailure ? "Required collection check " + BlockerAnalysis.Quote(name)
                                + (absent || pending ? " is circular and currently unmet; it must complete outside this snapshot." : " is circular; it must complete outside this snapshot.")
                            : absent ? "Required check " + BlockerAnalysis.Quote(name) + " has not reported on the observed head."
                            : pending && !knownFailure ? "Required check " + BlockerAnalysis.Quote(name) + " is pending; its requirement is currently unmet."
                            : "Observed required check " + BlockerAnalysis.Quote(name) + (allPassed ? " has a passing conclusion." : knownFailure ? " has a failed or conflicting conclusion." : " has no recognized passing conclusion."), links);
                }
            }
        }
        var assurance = pr?.Closed == true || pr?.Merged == true ? "not-applicable"
            : snapshot.CollectionAttempts > 1 && snapshot.CollectionConsistency != "stable" ? "unknown"
            : findings.Any(f => f.Required && f.Status == "fail") ? "fail"
            : findings.Any(f => f.Required && f.Status == "unknown") ? "unknown" : "pass";
        return new ReleaseEvidencePack("1.0", repository, target.Number, target.WebUrl, pr?.HeadSha ?? pr?.Ci?.Sha,
            pr?.BaseRef, pr?.BaseSha, started, completed, version, brickVersion, null, "github.com: one pull request; read-only checks, reviews and branch-rule observations",
            new EvidenceAuthorization(credentialSupplied ? "supplied-to-client" : "not-supplied-to-brick", "not-enumerated", "unknown"),
            answer.Verdict, assurance, findings,
            ["Human release decision required; this pack does not prove compliance or authorize a release.",
             "No repository code, PR body, diff, private file content, artifact payload or access token is included.",
             "GitHub rules are not fully reimplemented: deployment, merge-queue, app-bound check identity, signatures, bypass permissions and custom rules require separate review.",
             "API sources are collected sequentially; facts may change during collection or immediately afterward. A matching head does not prove freshness.",
             "No authenticated caller identity or host audit ID is established by this brick; the host may correlate its audit separately."],
            new EvidenceFreshness(snapshot.CollectionConsistency, "Recollect when head/base, checks, reviews, rules or authorization may have changed; no fixed validity period is asserted.", snapshot.CollectionAttempts));
    }

    private static bool IsCommitSha(string? value) => value is { Length: 40 }
        && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

    private static bool IsCompleteCi(CiFacts? ci) => ci is { Checks: { } checks, Statuses: { } statuses, ChecksReadComplete: true }
        && ci.ChecksReported == checks.Count && ci.StatusesReported == statuses.Count;

    private static bool IsKnownPendingState(string? state) => state?.ToLowerInvariant()
        is "queued" or "in_progress" or "waiting" or "pending" or "requested";

    private static string PendingCheckReason(CheckFact check)
    {
        var status = check.Status is "queued" or "in_progress" or "waiting" or "pending" or "requested"
            ? check.Status.Replace('_', ' ') : BlockerAnalysis.Quote(check.Status);
        return $"The check {BlockerAnalysis.Quote(check.Name)} is still {status}.";
    }

    private static string? MissingRequiredCheckName(PullRequestFacts? pr, Blocker blocker) =>
        pr is { Ci: { } ci } ? pr.Protection.RequiredChecks?.FirstOrDefault(name => blocker.Reason ==
            $"The required check {BlockerAnalysis.Quote(name)} has not reported on {ci.Sha[..Math.Min(ci.Sha.Length, 12)]}.") : null;

    private static bool IsProvenUnmetCiBlocker(PullRequestFacts? pr, Blocker blocker)
    {
        if (pr is null || !pr.Protection.RequiredCheckOriginsKnown || pr.Protection.RequiredChecks is not { } required
            || !IsCompleteCi(pr.Ci))
            return false;
        if (MissingRequiredCheckName(pr, blocker) is not null)
            return true;
        var ci = pr.Ci!;
        return ci.Checks!.Any(c => required.Contains(c.Name, StringComparer.Ordinal)
                && IsKnownPendingState(c.Status) && blocker.Reason == PendingCheckReason(c))
            || ci.Statuses!.Any(s => required.Contains(s.Context, StringComparer.Ordinal)
                && string.Equals(s.State, "pending", StringComparison.OrdinalIgnoreCase)
                && blocker.Reason == $"The status {BlockerAnalysis.Quote(s.Context)} is still pending.");
    }

    private static bool IsUnverifiedOriginCiBlocker(PullRequestFacts? pr, Blocker blocker)
    {
        if (pr is null || pr.Protection.RequiredCheckOriginsKnown
            || pr.Protection.RequiredChecks is not { } required || pr.Ci is not { } ci)
            return false;

        // The legacy verdict deliberately keeps its name-only check semantics. Only exact CI
        // reason shapes are downgraded here; unrelated blockers remain independently established.
        foreach (var check in ci.Checks ?? [])
        {
            if (required.Contains(check.Name, StringComparer.Ordinal)
                && string.Equals(check.Status, "completed", StringComparison.OrdinalIgnoreCase) && check.Conclusion is { } conclusion
                && blocker.Reason == $"The check {BlockerAnalysis.Quote(check.Name)} concluded {conclusion.Replace('_', ' ')}.")
                return true;
        }
        foreach (var status in ci.Statuses ?? [])
        {
            var state = status.State?.ToLowerInvariant();
            if (state is not ("failure" or "error"))
                continue;
            var reason = $"The status {BlockerAnalysis.Quote(status.Context)} reported {state}"
                + (string.IsNullOrWhiteSpace(status.Description) ? "." : $": {BlockerAnalysis.Quote(status.Description)}.");
            if (required.Contains(status.Context, StringComparer.Ordinal) && blocker.Reason == reason)
                return true;
        }
        return false;
    }

    private static bool IsUnsettledCiBlocker(PullRequestFacts? pr, Blocker blocker)
    {
        if (pr?.Ci is not { } ci)
            return false;
        foreach (var check in ci.Checks ?? [])
        {
            if (check.Status != "completed" && blocker.Reason == PendingCheckReason(check))
                return true;
        }
        if (ci.Statuses?.Any(s => s.State == "pending" && blocker.Reason == $"The status {BlockerAnalysis.Quote(s.Context)} is still pending.") == true)
            return true;
        return MissingRequiredCheckName(pr, blocker) is not null;
    }

    internal static string Render(ReleaseEvidencePack pack)
    {
        var output = new StringBuilder();
        output.AppendLine("## Pull-request evidence snapshot");
        output.AppendLine().AppendLine($"[{BlockerAnalysis.Quote(pack.Repository)} PR #{pack.PullRequest}]({pack.Url})");
        output.AppendLine().AppendLine($"GitHub verdict: **{pack.Verdict}**. Scoped assurance: **{pack.Assurance}**.");
        output.AppendLine().AppendLine($"Head: {BlockerAnalysis.Quote(pack.HeadSha ?? "unknown")}; base: {BlockerAnalysis.Quote(pack.BaseRef ?? "unknown")} ({BlockerAnalysis.Quote(pack.BaseSha ?? "unknown")}).");
        output.AppendLine().AppendLine($"Collected: {pack.CollectedAt:O}; schema {pack.SchemaVersion}; Ashlar {BlockerAnalysis.Quote(pack.AshlarVersion)}; brick {BlockerAnalysis.Quote(pack.BrickVersion)}.");
        output.AppendLine().AppendLine($"Collection attempts: {pack.Freshness.CollectionAttempts}; final PR comparison: {pack.Freshness.CollectionConsistency}.");
        foreach (var finding in pack.Findings)
        {
            output.AppendLine().Append("- **").Append(finding.Status).Append("**: ").Append(finding.Explanation);
            foreach (var source in finding.Sources)
                output.Append(" [source](").Append(source).Append(')');
            output.AppendLine();
        }
        output.AppendLine().AppendLine("Limitations:");
        foreach (var limitation in pack.Limitations)
            output.AppendLine().Append("- ").AppendLine(limitation);
        return output.ToString();
    }
}
