using Octokit;

namespace Ashlar.Bricks.GitHub;

/// <summary>
/// Reads what <see cref="BlockerAnalysis"/> needs from the GitHub REST API. A read that fails becomes an
/// <see cref="Unknown"/> with GitHub's answer; it never becomes "passed" or "none". An egress refusal is not caught:
/// it is a policy decision, not a missing fact.
/// </summary>
internal sealed class GitHubBlockerReader
{
    private readonly IGitHubClient _client;

    public GitHubBlockerReader(IGitHubClient client) => _client = client;

    public async Task<BlockerSnapshot> ReadAsync(GitHubTarget target)
    {
        var unknowns = new List<Unknown>();
        return target.Kind == GitHubTargetKind.PullRequest
            ? new BlockerSnapshot(target, await ReadPullRequestAsync(target, unknowns).ConfigureAwait(false), null, unknowns)
            : new BlockerSnapshot(target, null, await ReadReleaseAsync(target, unknowns).ConfigureAwait(false), unknowns);
    }

    private async Task<PullRequestFacts?> ReadPullRequestAsync(GitHubTarget t, List<Unknown> unknowns)
    {
        var pr = await Try(() => _client.PullRequest.Get(t.Owner, t.Repo, t.Number), "The pull request", unknowns,
            notFound: "The pull request was not found, or is not visible to this token.").ConfigureAwait(false);
        if (pr is null)
            return null;

        var closed = pr.State.Value == ItemState.Closed;
        if (pr.Merged || closed)
        {
            return new PullRequestFacts(pr.HtmlUrl, pr.Title, closed, pr.Merged, pr.Draft, pr.Mergeable,
                pr.MergeableState?.StringValue, pr.Base.Ref, Array.Empty<ReviewFact>(), null, new ProtectionFacts(Array.Empty<string>(), 0));
        }

        var reviews = await Try(() => _client.PullRequest.Review.GetAll(t.Owner, t.Repo, t.Number), "Reviews", unknowns).ConfigureAwait(false);
        var ci = await ReadCiAsync(t, pr.Head.Sha, unknowns).ConfigureAwait(false);
        var protection = await ReadProtectionAsync(t, pr.Base.Ref, pr.Head.Sha).ConfigureAwait(false);

        return new PullRequestFacts(
            pr.HtmlUrl,
            pr.Title,
            closed,
            pr.Merged,
            pr.Draft,
            pr.Mergeable,
            pr.MergeableState?.StringValue,
            pr.Base.Ref,
            reviews?.Select(r => new ReviewFact(r.User?.Login ?? "(unknown user)", r.State.StringValue, r.HtmlUrl, r.SubmittedAt)).ToList(),
            ci,
            protection);
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

    private async Task<CiFacts> ReadCiAsync(GitHubTarget t, string sha, List<Unknown> unknowns)
    {
        var checks = await Try(() => _client.Check.Run.GetAllForReference(t.Owner, t.Repo, sha), "Check runs", unknowns).ConfigureAwait(false);
        var statuses = await Try(() => _client.Repository.Status.GetCombined(t.Owner, t.Repo, sha), "Commit statuses", unknowns).ConfigureAwait(false);
        return new CiFacts(
            sha,
            checks?.CheckRuns.Select(c => new CheckFact(c.Name, c.Status.StringValue, c.Conclusion?.StringValue, c.HtmlUrl)).ToList(),
            checks?.TotalCount ?? 0,
            statuses?.Statuses.Select(s => new StatusFact(s.Context, s.State.StringValue, s.TargetUrl, s.Description)).ToList(),
            statuses?.TotalCount ?? 0);
    }

    // Branch protection's own endpoints need admin rights. Without them, the same requirements are public in three
    // places: the protection summary on GET /branches/{base}, the active rulesets on GET /rules/branches/{base}, and
    // GraphQL's per-check isRequired and the pull request's reviewDecision. The required-check list counts as known
    // only when both REST lists were read, since a check that never reported is absent from GraphQL.
    private async Task<ProtectionFacts> ReadProtectionAsync(GitHubTarget t, string branch, string headSha)
    {
        var adminChecks = await ReadRequiredChecksAsync(t, branch).ConfigureAwait(false);
        var adminApprovals = await ReadRequiredApprovalsAsync(t, branch).ConfigureAwait(false);
        var rules = await ReadRulesAsync(t, branch).ConfigureAwait(false);

        var rulesChecks = rules?
            .Where(r => r.Type == "required_status_checks")
            .SelectMany(r => r.Parameters?.RequiredStatusChecks ?? [])
            .Select(c => c.Context)
            .OfType<string>()
            .ToList();
        int? rulesApprovals = rules is null
            ? null
            : rules.Where(r => r.Type == "pull_request").Select(r => r.Parameters?.RequiredApprovingReviewCount ?? 0).DefaultIfEmpty(0).Max();

        if (adminChecks is not null && adminApprovals is not null)
        {
            return new ProtectionFacts(
                adminChecks.Union(rulesChecks ?? [], StringComparer.Ordinal).ToList(),
                Math.Max(adminApprovals.Value, rulesApprovals ?? 0));
        }

        var summaryChecks = adminChecks is null ? await ReadProtectionSummaryChecksAsync(t, branch).ConfigureAwait(false) : adminChecks;
        var pull = await ReadPullRequestGraphAsync(t, headSha).ConfigureAwait(false);

        IReadOnlyList<string>? checks = null;
        if (summaryChecks is not null && rulesChecks is not null)
            checks = summaryChecks.Union(rulesChecks, StringComparer.Ordinal).Union(pull?.RequiredContexts ?? [], StringComparer.Ordinal).ToList();

        int? approvals = adminApprovals is null ? rulesApprovals : rulesApprovals is null ? null : Math.Max(adminApprovals.Value, rulesApprovals.Value);
        return new ProtectionFacts(checks, approvals, pull?.ReviewDecision, ApprovalsPartial: adminApprovals is null && approvals is not null);
    }

    private async Task<List<RuleDto>?> ReadRulesAsync(GitHubTarget t, string branch)
    {
        try
        {
            var response = await _client.Connection.Get<List<RuleDto>>(
                new Uri($"repos/{t.Owner}/{t.Repo}/rules/branches/{branch}", UriKind.Relative),
                new Dictionary<string, string> { ["per_page"] = "100" }).ConfigureAwait(false);
            return response.Body ?? [];
        }
        catch (Exception ex) when (IsReadFailure(ex) || IsParseFailure(ex))
        {
            return null;
        }
    }

    // The public branch summary: protection.required_status_checks, readable without admin rights.
    private async Task<IReadOnlyList<string>?> ReadProtectionSummaryChecksAsync(GitHubTarget t, string branch)
    {
        try
        {
            var response = await _client.Connection.Get<BranchDto>(
                new Uri($"repos/{t.Owner}/{t.Repo}/branches/{branch}", UriKind.Relative), null).ConfigureAwait(false);
            if (response.Body is null)
                return null;
            var required = response.Body.Protection?.RequiredStatusChecks;
            if (required is null || string.Equals(required.EnforcementLevel, "off", StringComparison.OrdinalIgnoreCase))
                return [];
            return (required.Contexts ?? [])
                .Concat((required.Checks ?? []).Select(c => c.Context).OfType<string>())
                .Distinct(StringComparer.Ordinal)
                .ToList();
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
            var required = commit is not null && string.Equals(commit.Oid, headSha, StringComparison.OrdinalIgnoreCase)
                ? (commit.StatusCheckRollup?.Contexts?.Nodes ?? [])
                    .Where(c => c.IsRequired == true)
                    .Select(c => c.TypeName == "StatusContext" ? c.Context : c.Name)
                    .OfType<string>()
                    .ToList()
                : new List<string>();
            return new PullRequestGraph(pull.ReviewDecision ?? "NONE", required);
        }
        catch (Exception ex) when (IsReadFailure(ex) || IsParseFailure(ex))
        {
            return null;
        }
    }

    private sealed record PullRequestGraph(string ReviewDecision, IReadOnlyList<string> RequiredContexts);

    // Branch protection is readable only with admin rights. GitHub answers 404 with "not protected" or "not enabled"
    // when the rule does not exist, which is a fact (none required); any other failure leaves it unknown.
    private async Task<IReadOnlyList<string>?> ReadRequiredChecksAsync(GitHubTarget t, string branch)
    {
        try
        {
            var required = await _client.Repository.Branch.GetRequiredStatusChecks(t.Owner, t.Repo, branch).ConfigureAwait(false);
            return required.Contexts?.ToList() ?? new List<string>();
        }
        catch (NotFoundException ex) when (SaysRuleAbsent(ex))
        {
            return Array.Empty<string>();
        }
        catch (Exception ex) when (IsReadFailure(ex))
        {
            return null;
        }
    }

    private async Task<int?> ReadRequiredApprovalsAsync(GitHubTarget t, string branch)
    {
        try
        {
            var reviews = await _client.Repository.Branch.GetReviewEnforcement(t.Owner, t.Repo, branch).ConfigureAwait(false);
            return reviews.RequiredApprovingReviewCount;
        }
        catch (NotFoundException ex) when (SaysRuleAbsent(ex))
        {
            return 0;
        }
        catch (Exception ex) when (IsReadFailure(ex))
        {
            return null;
        }
    }

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

    private static bool IsReadFailure(Exception ex) => ex is ApiException or HttpRequestException or TaskCanceledException;

    private static bool IsParseFailure(Exception ex) =>
        ex is System.Runtime.Serialization.SerializationException or InvalidCastException or FormatException or ArgumentException;

    private static string Describe(Exception ex) => ex switch
    {
        RateLimitExceededException rate => $"GitHub's rate limit is exhausted until {rate.Reset:u}; pass a token for a higher limit.",
        AuthorizationException => "GitHub refused the token (401).",
        ApiException api => $"GitHub answered {(int)api.StatusCode} {api.StatusCode}: {api.Message}",
        TaskCanceledException => "The request timed out.",
        _ => $"The request failed: {ex.Message}",
    };
}
