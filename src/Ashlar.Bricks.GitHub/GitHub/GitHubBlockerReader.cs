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
        var protection = new ProtectionFacts(
            await ReadRequiredChecksAsync(t, pr.Base.Ref).ConfigureAwait(false),
            await ReadRequiredApprovalsAsync(t, pr.Base.Ref).ConfigureAwait(false));

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

    private static string Describe(Exception ex) => ex switch
    {
        RateLimitExceededException rate => $"GitHub's rate limit is exhausted until {rate.Reset:u}; pass a token for a higher limit.",
        AuthorizationException => "GitHub refused the token (401).",
        ApiException api => $"GitHub answered {(int)api.StatusCode} {api.StatusCode}: {api.Message}",
        TaskCanceledException => "The request timed out.",
        _ => $"The request failed: {ex.Message}",
    };
}
