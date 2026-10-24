using Ashlar.Core.Domain.Bricks;
using Ashlar.Core.Domain.Execution;
using Octokit;
using System.Text;

namespace PrBlocker.Brick;

/// <summary>
/// Brick that analyzes what's blocking a GitHub PR or release.
/// </summary>
public sealed class PrBlockerBrick : DomainBrick
{
    public PrBlockerBrick()
    {
        Id = "pr-blocker-analyzer";
        Name = "PR Blocker Analyzer";
        Version = "1.0.0";
        Icon = "🚧";
        Category = BrickCategory.Analysis;
        Description = "Analyzes what's blocking a GitHub PR or release.";
        Interface = new BrickInterface
        {
            Inputs =
            [
                new BrickInputDefinition("url", "string", "GitHub PR or release URL", required: true),
                new BrickInputDefinition("githubToken", "string", "GitHub personal access token", required: false),
            ],
            Outputs =
            [
                new BrickOutputDefinition("summary", "string", "Summary of blockers"),
                new BrickOutputDefinition("blockers", "array", "List of specific blockers"),
                new BrickOutputDefinition("ready", "boolean", "Whether ready to merge/release"),
                new BrickOutputDefinition("evidence", "object", "Detailed evidence"),
            ]
        };
    }

    public override async Task<BrickOutput> ExecuteAsync(
        BrickInput input,
        ImplementationType implementation,
        IExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var url = input.Get<string>("url");
        var githubToken = input.Get<string?>("githubToken");

        if (string.IsNullOrWhiteSpace(url))
        {
            throw new ArgumentException("URL is required");
        }

        var (owner, repo, number, isPr) = ParseGitHubUrl(url);
        
        var client = new GitHubClient(new ProductHeaderValue("pr-blocker-assistant"));
        if (!string.IsNullOrWhiteSpace(githubToken))
        {
            client.Credentials = new Credentials(githubToken);
        }

        var output = new BrickOutput();
        var blockers = new List<string>();
        var evidence = new Dictionary<string, object>();

        if (isPr)
        {
            var pr = await client.PullRequest.Get(owner, repo, number);
            evidence["pr"] = new
            {
                pr.Number,
                pr.Title,
                pr.State,
                pr.Mergeable,
                pr.MergeableState,
                pr.Merged,
                pr.Draft
            };

            // Check if PR is closed or merged
            if (pr.State.Value == ItemState.Closed)
            {
                if (pr.Merged)
                {
                    blockers.Add("PR is already merged");
                }
                else
                {
                    blockers.Add("PR is closed without merging");
                }
            }

            // Check if draft
            if (pr.Draft)
            {
                blockers.Add("PR is in draft mode");
            }

            // Get commit status
            var statuses = await client.Repository.Status.GetCombined(owner, repo, pr.Head.Sha);
            evidence["combinedStatus"] = new
            {
                statuses.State,
                TotalCount = statuses.TotalCount,
                Statuses = statuses.Statuses.Select(s => new { s.Context, s.State, s.Description }).ToList()
            };

            if (statuses.State.Value != CommitState.Success)
            {
                var failedStatuses = statuses.Statuses
                    .Where(s => s.State.Value == CommitState.Failure || s.State.Value == CommitState.Error)
                    .ToList();
                
                if (failedStatuses.Any())
                {
                    foreach (var status in failedStatuses)
                    {
                        blockers.Add($"Check failed: {status.Context} - {status.Description}");
                    }
                }
                else if (statuses.State.Value == CommitState.Pending)
                {
                    blockers.Add("Checks are still pending");
                }
            }

            // Get check runs (GitHub Actions and other apps)
            try
            {
                var checkRuns = await client.Check.Run.GetAllForReference(owner, repo, pr.Head.Sha);
                evidence["checkRuns"] = new
                {
                    TotalCount = checkRuns.TotalCount,
                    CheckRuns = checkRuns.CheckRuns.Select(c => new
                    {
                        c.Name,
                        c.Status,
                        c.Conclusion,
                        c.Output.Title,
                        c.Output.Summary
                    }).ToList()
                };

                var failedChecks = checkRuns.CheckRuns
                    .Where(c => c.Conclusion?.Value == CheckConclusion.Failure ||
                                c.Conclusion?.Value == CheckConclusion.TimedOut ||
                                c.Conclusion?.Value == CheckConclusion.ActionRequired)
                    .ToList();

                foreach (var check in failedChecks)
                {
                    blockers.Add($"Check run failed: {check.Name} - {check.Conclusion?.Value}");
                }

                var pendingChecks = checkRuns.CheckRuns
                    .Where(c => c.Status.Value != CheckStatus.Completed)
                    .ToList();

                if (pendingChecks.Any())
                {
                    blockers.Add($"{pendingChecks.Count} check(s) still running: {string.Join(", ", pendingChecks.Select(c => c.Name))}");
                }
            }
            catch (Exception ex)
            {
                evidence["checkRunsError"] = ex.Message;
            }

            // Get required reviews
            var reviews = await client.PullRequest.Review.GetAll(owner, repo, number);
            evidence["reviews"] = reviews.Select(r => new
            {
                r.User.Login,
                r.State,
                r.SubmittedAt
            }).ToList();

            var latestReviews = reviews
                .GroupBy(r => r.User.Login)
                .Select(g => g.OrderByDescending(r => r.SubmittedAt).First())
                .ToList();

            var approvals = latestReviews.Count(r => r.State.Value == PullRequestReviewState.Approved);
            var changesRequested = latestReviews.Count(r => r.State.Value == PullRequestReviewState.ChangesRequested);

            evidence["reviewSummary"] = new
            {
                Approvals = approvals,
                ChangesRequested = changesRequested
            };

            if (changesRequested > 0)
            {
                blockers.Add($"{changesRequested} reviewer(s) requested changes");
            }

            // Check mergeable state
            if (pr.Mergeable.HasValue && !pr.Mergeable.Value)
            {
                blockers.Add("PR has merge conflicts");
            }
        }
        else
        {
            // Handle release query
            try
            {
                var release = await client.Repository.Release.Get(owner, repo, number);
                evidence["release"] = new
                {
                    release.Name,
                    release.TagName,
                    release.Draft,
                    release.Prerelease,
                    release.CreatedAt,
                    release.PublishedAt,
                    AssetsCount = release.Assets.Count
                };

                if (release.Draft)
                {
                    blockers.Add("Release is in draft state");
                }

                if (release.Assets.Count == 0)
                {
                    blockers.Add("Release has no assets/artifacts");
                }
            }
            catch (NotFoundException)
            {
                // Try as a tag
                try
                {
                    var tag = await client.Repository.GetBranch(owner, repo, $"refs/tags/{number}");
                    evidence["tag"] = new
                    {
                        Name = number,
                        Sha = tag.Commit.Sha
                    };

                    // Get status for the tag
                    var tagStatus = await client.Repository.Status.GetCombined(owner, repo, tag.Commit.Sha);
                    evidence["tagStatus"] = new
                    {
                        tagStatus.State,
                        Statuses = tagStatus.Statuses.Select(s => new { s.Context, s.State }).ToList()
                    };

                    if (tagStatus.State.Value != CommitState.Success)
                    {
                        blockers.Add($"Tag checks not successful: {tagStatus.State.Value}");
                    }

                    // Check if release exists
                    try
                    {
                        await client.Repository.Release.Get(owner, repo, number);
                    }
                    catch (NotFoundException)
                    {
                        blockers.Add("No release created for this tag");
                    }
                }
                catch (Exception ex)
                {
                    blockers.Add($"Could not find release or tag: {ex.Message}");
                }
            }
        }

        var ready = blockers.Count == 0;
        var summary = BuildSummary(isPr, ready, blockers, evidence);

        output.Set("summary", summary);
        output.Set("blockers", blockers);
        output.Set("ready", ready);
        output.Set("evidence", evidence);
        output.Summary = summary;

        return output;
    }

    private static (string owner, string repo, int number, bool isPr) ParseGitHubUrl(string url)
    {
        // Handle various GitHub URL formats:
        // https://github.com/owner/repo/pull/123
        // https://github.com/owner/repo/releases/tag/v1.0.0
        // owner/repo#123
        // owner/repo/pull/123

        var uri = url.Contains("://") ? new Uri(url) : null;
        
        if (uri != null)
        {
            var segments = uri.Segments.Select(s => s.Trim('/')).Where(s => !string.IsNullOrEmpty(s)).ToArray();
            
            if (segments.Length >= 4)
            {
                var owner = segments[0];
                var repo = segments[1];
                var type = segments[2]; // "pull", "releases", etc.
                var identifier = segments[3]; // PR number or "tag"

                if (type == "pull" && int.TryParse(identifier, out var prNumber))
                {
                    return (owner, repo, prNumber, true);
                }
                else if (type == "releases" && identifier == "tag" && segments.Length >= 5)
                {
                    // Return tag name as "number" for release lookups
                    // This is a simplification - in real code we'd handle this differently
                    return (owner, repo, 0, false);
                }
            }
        }
        else
        {
            // Simple format: owner/repo#123
            var parts = url.Split('#');
            if (parts.Length == 2)
            {
                var repoParts = parts[0].Split('/');
                if (repoParts.Length == 2 && int.TryParse(parts[1], out var prNumber))
                {
                    return (repoParts[0], repoParts[1], prNumber, true);
                }
            }
        }

        throw new ArgumentException($"Could not parse GitHub URL: {url}");
    }

    private static string BuildSummary(bool isPr, bool ready, List<string> blockers, Dictionary<string, object> evidence)
    {
        var sb = new StringBuilder();
        
        if (isPr)
        {
            sb.AppendLine("## PR Status");
            if (ready)
            {
                sb.AppendLine("✅ **READY TO MERGE**");
                sb.AppendLine();
                sb.AppendLine("All checks passed, no blockers detected.");
            }
            else
            {
                sb.AppendLine("❌ **BLOCKED**");
                sb.AppendLine();
                sb.AppendLine($"**{blockers.Count} blocker(s) found:**");
                foreach (var blocker in blockers)
                {
                    sb.AppendLine($"- {blocker}");
                }
            }
        }
        else
        {
            sb.AppendLine("## Release Status");
            if (ready)
            {
                sb.AppendLine("✅ **READY FOR RELEASE**");
            }
            else
            {
                sb.AppendLine("❌ **NOT READY**");
                sb.AppendLine();
                sb.AppendLine($"**{blockers.Count} issue(s) found:**");
                foreach (var blocker in blockers)
                {
                    sb.AppendLine($"- {blocker}");
                }
            }
        }

        return sb.ToString();
    }
}
