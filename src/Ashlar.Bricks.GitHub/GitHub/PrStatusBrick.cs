using Microsoft.Extensions.Logging;
using Octokit;
using Ashlar.Core.Domain.Bricks;
using Ashlar.Core.Domain.Execution;

namespace Ashlar.Bricks.GitHub.GitHub;

/// <summary>
/// Analyzes GitHub Pull Request status including reviews, checks, and merge blockers.
/// </summary>
public class PrStatusBrick : Ashlar.Core.Domain.Bricks.Brick
{
    private readonly ILogger<PrStatusBrick> _logger;

    public PrStatusBrick(ILogger<PrStatusBrick> logger)
    {
        _logger = logger;

        Id = "github.pr-status";
        Name = "GitHub PR Status";
        Version = "1.0.0";
        Icon = "🔍";
        Category = BrickCategory.Analysis;
        Description = "Analyzes GitHub Pull Request status, reviews, CI checks, and identifies merge blockers.";

        Interface = new BrickInterface
        {
            Inputs =
            [
                new BrickInputDefinition("prUrl", "string", "GitHub Pull Request URL", required: true),
                new BrickInputDefinition("githubToken", "string", "GitHub Personal Access Token (optional - for higher rate limits)", required: false)
            ],
            Outputs =
            [
                new BrickOutputDefinition("state", "string", "PR state (open, closed, merged)"),
                new BrickOutputDefinition("mergeable", "boolean", "Whether PR is mergeable"),
                new BrickOutputDefinition("blockers", "string[]", "List of merge blockers"),
                new BrickOutputDefinition("checks", "object", "CI check status summary"),
                new BrickOutputDefinition("reviews", "object", "Review status summary")
            ]
        };
    }

    public override async Task<BrickOutput> ExecuteAsync(
        BrickInput input,
        ImplementationType implementation,
        IExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var prUrl = input.Get<string>("prUrl");
        var output = new BrickOutput();

        try
        {
            // Parse PR URL
            var (owner, repo, prNumber) = ParsePrUrl(prUrl);
            
            // Create GitHub client
            var client = CreateGitHubClient(input);

            // Fetch PR data
            var pullRequest = await client.PullRequest.Get(owner, repo, prNumber);
            var reviews = await client.PullRequest.Review.GetAll(owner, repo, prNumber);
            
            // Get check runs
            var checkRuns = await client.Check.Run.GetAllForReference(
                owner, 
                repo, 
                pullRequest.Head.Sha);

            // Analyze state
            output.Set("state", pullRequest.State.StringValue);
            output.Set("mergeable", pullRequest.Mergeable ?? false);

            // Identify blockers
            var blockers = IdentifyBlockers(pullRequest, reviews, checkRuns);
            output.Set("blockers", blockers);

            // Summarize checks
            var checksSummary = SummarizeChecks(checkRuns);
            output.Set("checks", checksSummary);

            // Summarize reviews
            var reviewsSummary = SummarizeReviews(reviews);
            output.Set("reviews", reviewsSummary);

            output.Summary = blockers.Count > 0
                ? $"PR #{prNumber} is BLOCKED by {blockers.Count} issue(s)"
                : $"PR #{prNumber} is READY to merge";
        }
        catch (NotFoundException)
        {
            output.Summary = $"PR not found: {prUrl}";
            output.Set("state", "not_found");
            output.Set("mergeable", false);
            output.Set("blockers", new[] { "PR not found" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error analyzing PR {PrUrl}", prUrl);
            output.Summary = $"Error: {ex.Message}";
            output.Set("state", "error");
            output.Set("mergeable", false);
            output.Set("blockers", new[] { $"Error: {ex.Message}" });
        }

        return output;
    }

    private GitHubClient CreateGitHubClient(BrickInput input)
    {
        var client = new GitHubClient(new ProductHeaderValue("Ashlar-GitHub-Bricks"));

        // Use token if provided for higher rate limits
        if (input.TryGet<string>("githubToken", out var token) && !string.IsNullOrEmpty(token))
        {
            client.Credentials = new Credentials(token);
        }

        return client;
    }

    private (string owner, string repo, int prNumber) ParsePrUrl(string prUrl)
    {
        // Parse URLs like: https://github.com/owner/repo/pull/123
        var uri = new Uri(prUrl);
        var segments = uri.Segments;

        if (segments.Length < 5 || segments[^2].TrimEnd('/') != "pull")
        {
            throw new ArgumentException($"Invalid PR URL format: {prUrl}. Expected format: https://github.com/owner/repo/pull/123");
        }

        var owner = segments[1].TrimEnd('/');
        var repo = segments[2].TrimEnd('/');
        var prNumber = int.Parse(segments[^1]);

        return (owner, repo, prNumber);
    }

    private List<string> IdentifyBlockers(
        PullRequest pr,
        IReadOnlyList<PullRequestReview> reviews,
        CheckRunsResponse checkRuns)
    {
        var blockers = new List<string>();

        // Check if PR is already merged or closed
        if (pr.State == ItemState.Closed)
        {
            if (pr.Merged)
                blockers.Add("PR is already merged");
            else
                blockers.Add("PR is closed");
        }

        // Check if PR is in draft state
        if (pr.Draft)
        {
            blockers.Add("PR is in draft state");
        }

        // Check for merge conflicts
        if (pr.Mergeable == false)
        {
            blockers.Add("PR has merge conflicts");
        }

        // Check for required reviews
        var latestReviews = reviews
            .GroupBy(r => r.User.Login)
            .Select(g => g.OrderByDescending(r => r.SubmittedAt).First())
            .ToList();

        var changesRequested = latestReviews.Count(r => r.State == PullRequestReviewState.ChangesRequested);
        if (changesRequested > 0)
        {
            blockers.Add($"{changesRequested} reviewer(s) requested changes");
        }

        // Check CI status
        var failedChecks = checkRuns.CheckRuns
            .Where(c => c.Status == CheckStatus.Completed && c.Conclusion != CheckConclusion.Success && c.Conclusion != CheckConclusion.Skipped)
            .ToList();

        if (failedChecks.Any())
        {
            blockers.Add($"{failedChecks.Count} CI check(s) failed");
        }

        var pendingChecks = checkRuns.CheckRuns
            .Where(c => c.Status != CheckStatus.Completed)
            .ToList();

        if (pendingChecks.Any())
        {
            blockers.Add($"{pendingChecks.Count} CI check(s) still pending");
        }

        return blockers;
    }

    private object SummarizeChecks(CheckRunsResponse checkRuns)
    {
        var summary = new
        {
            total = checkRuns.TotalCount,
            completed = checkRuns.CheckRuns.Count(c => c.Status == CheckStatus.Completed),
            pending = checkRuns.CheckRuns.Count(c => c.Status != CheckStatus.Completed),
            passed = checkRuns.CheckRuns.Count(c => c.Conclusion == CheckConclusion.Success),
            failed = checkRuns.CheckRuns.Count(c => c.Conclusion == CheckConclusion.Failure),
            checks = checkRuns.CheckRuns.Select(c => new
            {
                name = c.Name,
                status = c.Status.StringValue,
                conclusion = c.Conclusion?.StringValue
            }).ToList()
        };

        return summary;
    }

    private object SummarizeReviews(IReadOnlyList<PullRequestReview> reviews)
    {
        var latestReviews = reviews
            .GroupBy(r => r.User.Login)
            .Select(g => g.OrderByDescending(r => r.SubmittedAt).First())
            .ToList();

        var summary = new
        {
            total = latestReviews.Count,
            approved = latestReviews.Count(r => r.State == PullRequestReviewState.Approved),
            changesRequested = latestReviews.Count(r => r.State == PullRequestReviewState.ChangesRequested),
            commented = latestReviews.Count(r => r.State == PullRequestReviewState.Commented),
            reviewers = latestReviews.Select(r => new
            {
                user = r.User.Login,
                state = r.State.StringValue,
                submittedAt = r.SubmittedAt
            }).ToList()
        };

        return summary;
    }
}
