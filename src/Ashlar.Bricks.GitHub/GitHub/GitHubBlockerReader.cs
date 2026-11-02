using Octokit;

namespace Ashlar.Bricks.GitHub;

/// <summary>
/// Reads what <see cref="BlockerAnalysis"/> needs from the GitHub REST API. A read that fails becomes an
/// <see cref="Unknown"/> with GitHub's answer; it never becomes "passed" or "none". An egress refusal is not caught:
/// it is a policy decision, not a missing fact.
/// </summary>
internal sealed class GitHubBlockerReader
{
    /// <summary>
    /// Waits before each re-read of an open pull request whose mergeability GitHub had not computed. GitHub computes
    /// it in a background job and answers <c>null</c> until that finishes; every push to the base branch restarts it.
    /// </summary>
    internal static readonly IReadOnlyList<TimeSpan> MergeableRereadDelays = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2)];

    private readonly IGitHubClient _client;
    private readonly Func<TimeSpan, Task> _delay;

    public GitHubBlockerReader(IGitHubClient client, Func<TimeSpan, Task>? delay = null)
    {
        _client = client;
        _delay = delay ?? (d => Task.Delay(d));
    }

    public async Task<BlockerSnapshot> ReadAsync(GitHubTarget target)
    {
        var unknowns = new List<Unknown>();
        if (target.Kind != GitHubTargetKind.PullRequest)
            return new BlockerSnapshot(target, null, await ReadReleaseAsync(target, unknowns).ConfigureAwait(false), unknowns);

        PullRequest? anchor = null;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            // Each attempt has its own evidence and read failures. Earlier-head CI, reviews,
            // policy and unknowns must not be combined with the replacement snapshot.
            unknowns = [];
            var facts = await ReadPullRequestAsync(target, unknowns, anchor).ConfigureAwait(false);
            if (facts is null || facts.Closed || facts.Merged)
                return new BlockerSnapshot(target, facts, null, unknowns, CollectionAttempts: attempt);
            var final = await RereadAsync(target).ConfigureAwait(false);
            var consistency = CompareCollection(facts, final, target);
            if (attempt == 1 && consistency == "changed" && final is not null
                && IsCommitSha(final.Head?.Sha) && IsCommitSha(final.Base?.Sha))
            {
                // Reuse the valid final read as the new anchor. A failed or malformed read
                // is not retried, and an unstable second attempt never starts a third.
                anchor = final;
                continue;
            }
            return new BlockerSnapshot(target, facts, null, unknowns, consistency, attempt);
        }
        throw new InvalidOperationException("The bounded pull-request collection did not return a snapshot.");
    }

    private static string CompareCollection(PullRequestFacts facts, PullRequest? final, GitHubTarget target) =>
        final is null || !ValidPullMetadata(final, target) ? "unknown"
            : !string.Equals(final.Head?.Sha, facts.HeadSha, StringComparison.OrdinalIgnoreCase)
              || !string.Equals(final.Base?.Sha, facts.BaseSha, StringComparison.OrdinalIgnoreCase)
              || !string.Equals(final.Base?.Ref, facts.BaseRef, StringComparison.Ordinal)
              || final.Draft != facts.Draft || final.Merged != facts.Merged
              || (final.State.Value == ItemState.Closed) != facts.Closed
              || final.Mergeable != facts.Mergeable
              || !string.Equals(final.MergeableState?.StringValue, facts.MergeableState, StringComparison.OrdinalIgnoreCase)
                ? "changed" : "stable";

    private static bool IsCommitSha(string? value) => value is { Length: 40 }
        && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

    private async Task<PullRequestFacts?> ReadPullRequestAsync(GitHubTarget t, List<Unknown> unknowns, PullRequest? anchor = null)
    {
        var pr = anchor ?? await Try(() => _client.PullRequest.Get(t.Owner, t.Repo, t.Number), "The pull request", unknowns,
            notFound: "The pull request was not found, or is not visible to this token.").ConfigureAwait(false);
        if (pr is null)
            return null;
        if (string.IsNullOrWhiteSpace(pr.Head?.Sha) || string.IsNullOrWhiteSpace(pr.Base?.Ref))
        {
            unknowns.Add(new Unknown("Pull-request identity", "GitHub returned an incomplete head or base reference."));
            return null;
        }

        foreach (var wait in MergeableRereadDelays)
        {
            if (pr.Mergeable is not null || pr.Merged || pr.State.Value == ItemState.Closed)
                break;
            await _delay(wait).ConfigureAwait(false);
            pr = await RereadAsync(t).ConfigureAwait(false) ?? pr;
        }

        // These are API-owned identity fields, not display text authored by a PR contributor.
        // Keep collecting known facts, but never turn a malformed or mismatched identity into
        // passing evidence. Use the requested canonical URL throughout, including legacy links.
        if (!ValidPullMetadata(pr, t))
            unknowns.Add(new Unknown("Pull-request metadata", "GitHub returned malformed or mismatched PR identity metadata; its source URL or branch references could not be verified."));
        if (string.IsNullOrWhiteSpace(pr.Head?.Sha) || string.IsNullOrWhiteSpace(pr.Base?.Ref))
        {
            unknowns.Add(new Unknown("Pull-request identity", "The mergeability re-read returned an incomplete head or base reference."));
            return null;
        }

        var closed = pr.State.Value == ItemState.Closed;
        if (pr.Merged || closed)
        {
            return new PullRequestFacts(t.WebUrl, pr.Title, closed, pr.Merged, pr.Draft, pr.Mergeable,
                pr.MergeableState?.StringValue, pr.Base.Ref, Array.Empty<ReviewFact>(), null, new ProtectionFacts(Array.Empty<string>(), 0), pr.Head?.Sha, pr.Base?.Sha);
        }

        var reviews = await Try(() => _client.PullRequest.Review.GetAll(t.Owner, t.Repo, t.Number), "Reviews", unknowns).ConfigureAwait(false);
        var repositoryId = pr.Base.Repository is { Id: > 0 } repository
            && string.Equals(repository.FullName, t.Owner + "/" + t.Repo, StringComparison.OrdinalIgnoreCase)
                ? repository.Id : (long?)null;
        var ci = await ReadCiAsync(t, pr.Head.Sha, unknowns, repositoryId).ConfigureAwait(false);
        var protection = await ReadProtectionAsync(t, pr.Base.Ref, pr.Head.Sha).ConfigureAwait(false);

        return new PullRequestFacts(
            t.WebUrl,
            pr.Title,
            closed,
            pr.Merged,
            pr.Draft,
            pr.Mergeable,
            pr.MergeableState?.StringValue,
            pr.Base.Ref,
            reviews?.Select(r => new ReviewFact(r.User?.Login ?? "(unknown user)", r.State.StringValue, r.HtmlUrl, r.SubmittedAt, r.CommitId)).ToList(),
            ci,
            protection, pr.Head.Sha, pr.Base.Sha);
    }

    private static bool ValidPullMetadata(PullRequest pr, GitHubTarget target) =>
        pr.Number == target.Number
        && !string.IsNullOrWhiteSpace(pr.HtmlUrl)
        && !pr.HtmlUrl.Any(char.IsControl)
        && GitHubTarget.TryParse(pr.HtmlUrl, out var reported, out _)
        && reported!.Kind == GitHubTargetKind.PullRequest
        && string.Equals(reported.Owner, target.Owner, StringComparison.OrdinalIgnoreCase)
        && string.Equals(reported.Repo, target.Repo, StringComparison.OrdinalIgnoreCase)
        && reported.Number == target.Number
        && (pr.Head?.Ref is null || ValidBranchRef(pr.Head.Ref))
        && pr.Base?.Ref is { } baseRef && ValidBranchRef(baseRef)
        && (pr.Base.Repository?.FullName is not { } baseRepository
            || string.Equals(baseRepository, target.Owner + "/" + target.Repo, StringComparison.OrdinalIgnoreCase));

    private static bool ValidBranchRef(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && !value.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) || "~^:?*[\\".Contains(c))
        && !value.Contains("..", StringComparison.Ordinal)
        && !value.Contains("@{", StringComparison.Ordinal)
        && !value.Contains("//", StringComparison.Ordinal)
        && value != "@" && value[0] != '/' && value[^1] is not '/' and not '.'
        && value.Split('/').All(part => part[0] != '.' && !part.EndsWith(".lock", StringComparison.Ordinal));

    /// <summary>A failed re-read keeps the first read's answer, whose unknown mergeability is already reported.</summary>
    private async Task<PullRequest?> RereadAsync(GitHubTarget t)
    {
        try
        {
            return await _client.PullRequest.Get(t.Owner, t.Repo, t.Number).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsReadFailure(ex))
        {
            return null;
        }
    }

    private async Task<ReleaseFacts?> ReadReleaseAsync(GitHubTarget t, List<Unknown> unknowns)
    {
        Reference? reference;
        try
        {
            reference = await _client.Git.Reference.Get(t.Owner, t.Repo, "tags/" + t.Tag).ConfigureAwait(false);
        }
        catch (NotFoundException)
        {
            return new ReleaseFacts(t.Tag, TagExists: false, null, null, null, null, null);
        }
        catch (Exception ex) when (IsReadFailure(ex))
        {
            unknowns.Add(new Unknown("The tag", Describe(ex)));
            return null;
        }

        var sha = reference.Object.Sha;
        if (reference.Object.Type.Value == TaggedType.Tag)
        {
            var annotated = await Try(() => _client.Git.Tag.Get(t.Owner, t.Repo, sha), "The annotated tag", unknowns).ConfigureAwait(false);
            sha = annotated?.Object.Sha ?? sha;
        }

        Release? release = null;
        try
        {
            release = await _client.Repository.Release.Get(t.Owner, t.Repo, t.Tag).ConfigureAwait(false);
        }
        catch (NotFoundException)
        {
            // Drafts are not found by tag; they are listed only to tokens with push access.
            var recent = await Try(() => _client.Repository.Release.GetAll(t.Owner, t.Repo, new ApiOptions { PageSize = 30, PageCount = 1 }),
                "Draft releases", unknowns).ConfigureAwait(false);
            release = recent?.FirstOrDefault(r => string.Equals(r.TagName, t.Tag, StringComparison.Ordinal));
            if (release is null && recent is not null)
                unknowns.Add(new Unknown("Draft releases", "A draft for this tag is visible only to tokens with push access, and only the 30 newest releases were searched."));
        }
        catch (Exception ex) when (IsReadFailure(ex))
        {
            unknowns.Add(new Unknown("The release", Describe(ex)));
            return new ReleaseFacts(t.Tag, true, null, null, null, null, await ReadCiAsync(t, sha, unknowns).ConfigureAwait(false));
        }

        return new ReleaseFacts(t.Tag, true, release?.HtmlUrl, release?.Draft, release?.Prerelease, release?.Assets?.Count,
            await ReadCiAsync(t, sha, unknowns).ConfigureAwait(false));
    }

    private async Task<CiFacts> ReadCiAsync(GitHubTarget t, string sha, List<Unknown> unknowns, long? repositoryId = null)
    {
        // GitHub legitimately returns null for unstarted check timestamps. Read only the
        // fields this workflow uses instead of requiring unused timestamps in Octokit's full model.
        var checks = await ReadCheckRunsAsync(t, sha, unknowns, repositoryId).ConfigureAwait(false);
        var statuses = await Try(() => _client.Repository.Status.GetCombined(t.Owner, t.Repo, sha), "Commit statuses", unknowns).ConfigureAwait(false);
        return new CiFacts(
            sha,
            checks.Items?.Select(c => new CheckFact(c.Name!, c.Status!, c.Conclusion, c.HtmlUrl, RequiredCheckEvaluation.PositiveProviderId(c.App?.Id))).ToList(),
            checks.Reported,
            statuses?.Statuses?.Select(s => new StatusFact(s.Context, s.State.StringValue, s.TargetUrl, s.Description)).ToList(),
            statuses?.TotalCount ?? 0, checks.Complete);
    }

    private async Task<(IReadOnlyList<CheckRunDto>? Items, int Reported, bool Complete)> ReadCheckRunsAsync(
        GitHubTarget t, string sha, List<Unknown> unknowns, long? repositoryId)
    {
        var path = $"/repos/{t.Owner}/{t.Repo}/commits/{Uri.EscapeDataString(sha)}/check-runs";
        var canonicalPath = repositoryId is { } id ? CanonicalCheckPath(id, sha) : null;
        var url = new Uri(path[1..], UriKind.Relative);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var runIds = new HashSet<long>();
        var idsKnown = true;
        List<CheckRunDto>? items = null;
        int? reported = null;
        for (var page = 0; page < 100; page++)
        {
            var response = await Try(() => _client.Connection.Get<CheckRunsDto>(url, null), "Check runs", unknowns).ConfigureAwait(false);
            if (response is null)
                return (items, reported ?? 0, false);
            var body = response.Body;
            if (body is null || body.TotalCount is null or < 0 || body.CheckRuns is null
                || body.CheckRuns.Any(c => c is null || string.IsNullOrWhiteSpace(c.Name) || string.IsNullOrWhiteSpace(c.Status)))
            {
                unknowns.Add(new Unknown("Check runs", "GitHub returned incomplete check-run identity, status or count metadata."));
                return (items, reported ?? 0, false);
            }
            if (body.CheckRuns.Any(c => c.HeadSha is { } checkSha && !string.Equals(checkSha, sha, StringComparison.OrdinalIgnoreCase)))
            {
                unknowns.Add(new Unknown("Check runs", "GitHub returned check runs for a different commit; that page was not attributed to the requested head."));
                return (items, reported ?? body.TotalCount.Value, false);
            }
            items ??= [];
            items.AddRange(body.CheckRuns);
            reported ??= body.TotalCount;
            foreach (var check in body.CheckRuns)
            {
                if (check.Id is null)
                    idsKnown = false;
                else if (check.Id <= 0 || !runIds.Add(check.Id.Value))
                {
                    unknowns.Add(new Unknown("Check runs", "GitHub returned invalid or overlapping check-run identities; collection completeness is unknown."));
                    return (items, reported.Value, false);
                }
            }
            if (reported != body.TotalCount)
            {
                unknowns.Add(new Unknown("Check runs", "GitHub's check-run count changed during pagination; collection completeness is unknown."));
                return (items, reported.Value, false);
            }
            var next = response.HttpResponse.ApiInfo.Links.FirstOrDefault(p => string.Equals(p.Key, "next", StringComparison.OrdinalIgnoreCase)).Value;
            if (next is null)
            {
                var complete = items.Count == reported && (page == 0 || idsKnown);
                if (!complete)
                    unknowns.Add(new Unknown("Check runs", "GitHub's reported check-run count or paginated identities did not establish complete results."));
                return (items, reported.Value, complete);
            }
            // Release reads do not include a PR's base repository identity. Resolve it only
            // when GitHub actually supplies a canonical-ID next page, without changing ordinary reads.
            if (canonicalPath is null && t.Kind == GitHubTargetKind.Release && IsCanonicalCheckPage(next, sha))
            {
                var repository = await Try(() => _client.Repository.Get(t.Owner, t.Repo), "Check-run repository identity", unknowns).ConfigureAwait(false);
                if (repository is not { Id: > 0 }
                    || !string.Equals(repository.FullName, t.Owner + "/" + t.Repo, StringComparison.OrdinalIgnoreCase))
                {
                    unknowns.Add(new Unknown("Check runs", "The canonical pagination repository could not be bound to the requested repository; further pages were not requested."));
                    return (items, reported.Value, false);
                }
                canonicalPath = CanonicalCheckPath(repository.Id, sha);
            }
            if (!ValidCheckPage(next, path, canonicalPath) || !seen.Add(next.AbsoluteUri))
            {
                unknowns.Add(new Unknown("Check runs", "GitHub returned an unsafe or repeated pagination link; further check-run pages were not requested."));
                return (items, reported.Value, false);
            }
            url = next;
        }
        unknowns.Add(new Unknown("Check runs", "The 100-page check-run collection limit was reached; further results were not inspected."));
        return (items, reported ?? 0, false);
    }

    private static string CanonicalCheckPath(long id, string sha) =>
        $"/repositories/{id.ToString(System.Globalization.CultureInfo.InvariantCulture)}/commits/{Uri.EscapeDataString(sha)}/check-runs";

    private static bool IsCanonicalCheckPage(Uri url, string sha) => url.IsAbsoluteUri
        && url.AbsolutePath.Split('/') is ["", "repositories", var id, "commits", var commit, "check-runs"]
        && long.TryParse(id, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var repositoryId)
        && repositoryId > 0 && string.Equals(commit, Uri.EscapeDataString(sha), StringComparison.Ordinal)
        && ValidCheckPage(url, url.AbsolutePath, null);

    private static bool ValidCheckPage(Uri url, string expectedPath, string? canonicalPath) =>
        url.IsAbsoluteUri && url.Scheme == Uri.UriSchemeHttps && url.Host == "api.github.com" && url.Port == 443
        && url.UserInfo.Length == 0 && url.Fragment.Length == 0
        && (string.Equals(url.AbsolutePath, expectedPath, StringComparison.Ordinal)
            || canonicalPath is not null && string.Equals(url.AbsolutePath, canonicalPath, StringComparison.Ordinal))
        && url.Query.Length > 1 && url.Query[1..].Split('&').All(pair =>
            pair.Split('=') is ["page" or "per_page", var value] && int.TryParse(value, out var number) && number > 0);

    // Branch protection's own endpoints need admin rights. Without them, the same requirements are public in three
    // places: the protection summary on GET /branches/{base}, the active rulesets on GET /rules/branches/{base}, and
    // GraphQL's per-check isRequired and the pull request's reviewDecision. The required-check list counts as known
    // only when both REST lists were read, since a check that never reported is absent from GraphQL.
    private async Task<ProtectionFacts> ReadProtectionAsync(GitHubTarget t, string branch, string headSha)
    {
        var adminChecks = await ReadRequiredChecksAsync(t, branch).ConfigureAwait(false);
        var adminApprovals = await ReadRequiredApprovalsAsync(t, branch).ConfigureAwait(false);
        var rules = await ReadRulesAsync(t, branch).ConfigureAwait(false);

        var unsupportedRules = rules?.Where(r => r.Type is not ("required_status_checks" or "pull_request"))
            .Select(r => r.Type!).Distinct(StringComparer.Ordinal).ToList();
        var rulesAdditionalReviews = rules?.Where(r => r.Type == "pull_request").Any(r =>
            r.Parameters!.RequireCodeOwnerReviews != false || r.Parameters.RequireLastPushApproval != false || r.Parameters.DismissStaleReviews != false) != false;
        var ruleRequirements = rules?.Where(r => r.Type == "required_status_checks")
            .SelectMany(r => ReadBindings(r.Parameters!.RequiredStatusChecks!, integration: true)).ToList();
        var rulesChecks = rules?
            .Where(r => r.Type == "required_status_checks")
            .SelectMany(r => r.Parameters?.RequiredStatusChecks ?? [])
            .Select(c => c.Context)
            .OfType<string>()
            .ToList();
        int? rulesApprovals = rules is null
            ? null
            : rules.Where(r => r.Type == "pull_request").Select(r => r.Parameters?.RequiredApprovingReviewCount ?? 0).DefaultIfEmpty(0).Max();

        var pull = await ReadPullRequestGraphAsync(t, headSha).ConfigureAwait(false);
        if (adminChecks is not null && adminApprovals is not null)
        {
            return new ProtectionFacts(
                adminChecks.Names.Union(rulesChecks ?? [], StringComparer.Ordinal).ToList(),
                Math.Max(adminApprovals.Count, rulesApprovals ?? 0), pull?.ReviewDecision, RulesRead: rules is not null,
                RequiredCheckOriginsKnown: adminChecks.Requirements.All(r => r.OriginKnown) && ruleRequirements?.All(r => r.OriginKnown) == true,
                UnsupportedRules: unsupportedRules, AdditionalReviewRules: adminApprovals.AdditionalRules || rulesAdditionalReviews,
                CheckRequirements: adminChecks.Requirements.Concat(ruleRequirements ?? []).Distinct().ToList());
        }

        var summaryChecks = adminChecks is null ? await ReadProtectionSummaryChecksAsync(t, branch).ConfigureAwait(false) : adminChecks;
        IReadOnlyList<string>? checks = null;
        if (summaryChecks is not null && rulesChecks is not null)
            checks = summaryChecks.Names.Union(rulesChecks, StringComparer.Ordinal).Union(pull?.RequiredContexts ?? [], StringComparer.Ordinal).ToList();

        int? approvals = adminApprovals is null ? rulesApprovals : rulesApprovals is null ? null : Math.Max(adminApprovals.Count, rulesApprovals.Value);
        return new ProtectionFacts(checks, approvals, pull?.ReviewDecision, ApprovalsPartial: adminApprovals is null && approvals is not null, RulesRead: rules is not null,
            RequiredCheckOriginsKnown: summaryChecks?.Requirements.All(r => r.OriginKnown) == true && ruleRequirements?.All(r => r.OriginKnown) == true,
            UnsupportedRules: unsupportedRules, AdditionalReviewRules: adminApprovals?.AdditionalRules != false || rulesAdditionalReviews,
            CheckRequirements: MergeRequirements(summaryChecks?.Requirements, ruleRequirements, pull?.RequiredContexts));
    }

    private async Task<List<RuleDto>?> ReadRulesAsync(GitHubTarget t, string branch)
    {
        try
        {
            var response = await _client.Connection.Get<List<RuleDto>>(
                new Uri($"repos/{t.Owner}/{t.Repo}/rules/branches/{Uri.EscapeDataString(branch)}", UriKind.Relative),
                new Dictionary<string, string> { ["per_page"] = "100" }).ConfigureAwait(false);
            return response.Body is { Count: < 100 } rules && rules.All(r => r is not null && !string.IsNullOrWhiteSpace(r.Type)
                && (r.Type != "required_status_checks" || r.Parameters?.RequiredStatusChecks is { } checks && checks.All(c => c is not null && !string.IsNullOrWhiteSpace(c.Context)))
                && (r.Type != "pull_request" || r.Parameters?.RequiredApprovingReviewCount is >= 0)) ? rules : null;
        }
        catch (Exception ex) when (IsReadFailure(ex) || IsParseFailure(ex))
        {
            return null;
        }
    }

    // The public branch summary: protection.required_status_checks, readable without admin rights.
    private async Task<RequiredChecksRead?> ReadProtectionSummaryChecksAsync(GitHubTarget t, string branch)
    {
        try
        {
            var response = await _client.Connection.Get<BranchDto>(
                new Uri($"repos/{t.Owner}/{t.Repo}/branches/{Uri.EscapeDataString(branch)}", UriKind.Relative), null).ConfigureAwait(false);
            if (response.Body is null)
                return null;
            var required = response.Body.Protection?.RequiredStatusChecks;
            if (required is null)
                return response.Body.Protected == false ? new RequiredChecksRead([], []) : null;
            if (string.Equals(required.EnforcementLevel, "off", StringComparison.OrdinalIgnoreCase))
                return new RequiredChecksRead([], []);
            return ReadCheckNames(required);
        }
        catch (Exception ex) when (IsReadFailure(ex) || IsParseFailure(ex))
        {
            return null;
        }
    }

    private const string PullRequestQuery = """
        query($o:String!,$r:String!,$n:Int!){repository(owner:$o,name:$r){pull_request: pullRequest(number:$n){
          review_decision: reviewDecision
          commits(last:1){nodes{commit{oid status_check_rollup: statusCheckRollup{contexts(first:100){nodes{
            type_name: __typename
            ... on CheckRun{name is_required: isRequired(pullRequestNumber:$n)}
            ... on StatusContext{context is_required: isRequired(pullRequestNumber:$n)}
          }}}}}}
        }}}
        """;

    // GraphQL needs a token; read anonymously, or with any other failure, this is null and the caller falls back.
    private async Task<PullRequestGraph?> ReadPullRequestGraphAsync(GitHubTarget t, string headSha)
    {
        var body = System.Text.Json.JsonSerializer.Serialize(new
        {
            query = PullRequestQuery,
            variables = new { o = t.Owner, r = t.Repo, n = t.Number },
        });
        try
        {
            var response = await _client.Connection.Post<GraphResponseDto>(
                new Uri("graphql", UriKind.Relative), body, "application/json", "application/json").ConfigureAwait(false);
            var pull = response.Body?.Data?.Repository?.PullRequest;
            if (pull is null || response.Body!.Errors is { Count: > 0 })
                return null;
            var commit = pull.Commits?.Nodes?.FirstOrDefault()?.Commit;
            if (commit is null || !string.Equals(commit.Oid, headSha, StringComparison.OrdinalIgnoreCase))
                return null;
            var required = commit is not null && string.Equals(commit.Oid, headSha, StringComparison.OrdinalIgnoreCase)
                ? (commit.StatusCheckRollup?.Contexts?.Nodes ?? [])
                    .Where(c => c.IsRequired == true)
                    .Select(c => c.TypeName == "StatusContext" ? c.Context : c.Name)
                    .OfType<string>()
                    .ToList()
                : new List<string>();
            return string.IsNullOrWhiteSpace(pull.ReviewDecision) ? null : new PullRequestGraph(pull.ReviewDecision, required);
        }
        catch (Exception ex) when (IsReadFailure(ex) || IsParseFailure(ex))
        {
            return null;
        }
    }

    private sealed record PullRequestGraph(string ReviewDecision, IReadOnlyList<string> RequiredContexts);

    // Branch protection is readable only with admin rights. GitHub answers 404 with "not protected" or "not enabled"
    // when the rule does not exist, which is a fact (none required); any other failure leaves it unknown.
    private async Task<RequiredChecksRead?> ReadRequiredChecksAsync(GitHubTarget t, string branch)
    {
        try
        {
            var response = await _client.Connection.Get<RequiredStatusChecksDto>(
                new Uri($"repos/{t.Owner}/{t.Repo}/branches/{Uri.EscapeDataString(branch)}/protection/required_status_checks", UriKind.Relative), null).ConfigureAwait(false);
            return response.Body is null ? null : ReadCheckNames(response.Body);
        }
        catch (NotFoundException ex) when (SaysRuleAbsent(ex))
        {
            return new RequiredChecksRead([], []);
        }
        catch (Exception ex) when (IsReadFailure(ex))
        {
            return null;
        }
    }

    private sealed record RequiredChecksRead(IReadOnlyList<string> Names, IReadOnlyList<RequiredCheckRequirement> Requirements);

    private static IReadOnlyList<RequiredCheckRequirement> MergeRequirements(
        IReadOnlyList<RequiredCheckRequirement>? branch, IReadOnlyList<RequiredCheckRequirement>? rules, IReadOnlyList<string>? graphNames)
    {
        var result = (branch ?? []).Concat(rules ?? []).Distinct().ToList();
        foreach (var name in graphNames ?? [])
            if (!result.Any(r => r.Name == name))
                result.Add(new RequiredCheckRequirement(name, null, false));
        return result;
    }

    private static IReadOnlyList<RequiredCheckRequirement> ReadBindings(IReadOnlyList<StatusCheckDto> checks, bool integration)
    {
        return checks.GroupBy(c => c.Context!, StringComparer.Ordinal).Select(group =>
        {
            var bindings = group.Select(c =>
            {
                var read = integration ? c.IntegrationIdRead : c.AppIdRead;
                var id = integration ? c.IntegrationId : c.AppId;
                var otherRead = integration ? c.AppIdRead : c.IntegrationIdRead;
                var other = integration ? c.AppId : c.IntegrationId;
                // -1 is documented for branch protection, not for ruleset integration_id.
                var numeric = RequiredCheckEvaluation.ReadProviderId(id, out var appId);
                var otherNumeric = RequiredCheckEvaluation.ReadProviderId(other, out var otherId);
                var valid = read && numeric && (appId is null or > 0 || !integration && appId == -1)
                    && (!otherRead || otherNumeric && otherId == appId);
                return new RequiredCheckRequirement(group.Key, appId == -1 ? null : appId, valid);
            }).Distinct().ToArray();
            // Conflicting or incomplete duplicate bindings within one source never become unbound.
            return bindings.Length == 1 ? bindings[0] : new RequiredCheckRequirement(group.Key, null, false);
        }).ToArray();
    }

    private static RequiredChecksRead? ReadCheckNames(RequiredStatusChecksDto required)
    {
        if (required.Contexts is null && required.Checks is null)
            return null;
        if (required.Checks?.Any(c => c is null || string.IsNullOrWhiteSpace(c.Context)) == true || required.Contexts?.Any(string.IsNullOrWhiteSpace) == true)
            return null;
        var names = (required.Contexts ?? []).Concat((required.Checks ?? []).Select(c => c.Context!))
            .Distinct(StringComparer.Ordinal).ToList();
        var bindings = ReadBindings(required.Checks ?? [], integration: false).ToList();
        foreach (var name in names)
            if (!bindings.Any(r => r.Name == name))
                bindings.Add(new RequiredCheckRequirement(name, null, false));
        return new RequiredChecksRead(names, bindings);
    }

    private async Task<ApprovalsRead?> ReadRequiredApprovalsAsync(GitHubTarget t, string branch)
    {
        try
        {
            var response = await _client.Connection.Get<RuleParametersDto>(
                new Uri($"repos/{t.Owner}/{t.Repo}/branches/{Uri.EscapeDataString(branch)}/protection/required_pull_request_reviews", UriKind.Relative), null).ConfigureAwait(false);
            var reviews = response.Body;
            return reviews?.RequiredApprovingReviewCount is >= 0
                ? new ApprovalsRead(reviews.RequiredApprovingReviewCount.Value,
                    reviews.RequireCodeOwnerReviews != false || reviews.RequireLastPushApproval != false || reviews.DismissStaleReviews != false)
                : null;
        }
        catch (NotFoundException ex) when (SaysRuleAbsent(ex))
        {
            return new ApprovalsRead(0, false);
        }
        catch (Exception ex) when (IsReadFailure(ex))
        {
            return null;
        }
    }

    private sealed record ApprovalsRead(int Count, bool AdditionalRules);

    private static bool SaysRuleAbsent(ApiException ex) =>
        ex.Message.Contains("not protected", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("not enabled", StringComparison.OrdinalIgnoreCase);

    private static async Task<T?> Try<T>(Func<Task<T>> read, string what, List<Unknown> unknowns, string? notFound = null)
        where T : class
    {
        try
        {
            return await read().ConfigureAwait(false);
        }
        catch (NotFoundException) when (notFound is not null)
        {
            unknowns.Add(new Unknown(what, notFound));
            return null;
        }
        catch (Exception ex) when (IsReadFailure(ex))
        {
            unknowns.Add(new Unknown(what, Describe(ex)));
            return null;
        }
    }

    private static bool IsReadFailure(Exception ex) => ex is ApiException or HttpRequestException or TaskCanceledException || IsParseFailure(ex);

    private static bool IsParseFailure(Exception ex) =>
        ex is System.Runtime.Serialization.SerializationException or System.Text.Json.JsonException or InvalidCastException or FormatException or ArgumentException;

    private static string Describe(Exception ex) => ex switch
    {
        RateLimitExceededException rate => $"GitHub's rate limit is exhausted until {rate.Reset:u}; pass a token for a higher limit.",
        AuthorizationException => "GitHub refused the token (401).",
        ApiException api => $"GitHub answered {(int)api.StatusCode} {api.StatusCode}; response content is omitted.",
        TaskCanceledException => "The request timed out.",
        _ => "The request failed or returned a malformed response; response content is omitted.",
    };
}
