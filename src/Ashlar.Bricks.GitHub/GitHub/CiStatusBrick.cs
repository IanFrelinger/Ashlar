using Microsoft.Extensions.Logging;
using Octokit;
using Ashlar.Core.Domain.Bricks;
using Ashlar.Core.Domain.Execution;

namespace Ashlar.Bricks.GitHub.GitHub;

/// <summary>
/// Analyzes GitHub Actions CI/CD workflow status for a repository or commit.
/// </summary>
public class CiStatusBrick : Ashlar.Core.Domain.Bricks.Brick
{
    private readonly ILogger<CiStatusBrick> _logger;

    public CiStatusBrick(ILogger<CiStatusBrick> logger)
    {
        _logger = logger;

        Id = "github.ci-status";
        Name = "GitHub CI Status";
        Version = "1.0.0";
        Icon = "🔧";
        Category = BrickCategory.Analysis;
        Description = "Analyzes GitHub Actions CI/CD workflow runs and check status for a commit or branch.";

        Interface = new BrickInterface
        {
            Inputs =
            [
                new BrickInputDefinition("owner", "string", "Repository owner", required: true),
                new BrickInputDefinition("repo", "string", "Repository name", required: true),
                new BrickInputDefinition("ref", "string", "Git reference (branch, tag, or SHA)", required: true),
                new BrickInputDefinition("githubToken", "string", "GitHub Personal Access Token (optional - for higher rate limits)", required: false)
            ],
            Outputs =
            [
                new BrickOutputDefinition("status", "string", "Overall CI status (success, failure, pending, skipped)"),
                new BrickOutputDefinition("totalRuns", "int", "Total workflow runs"),
                new BrickOutputDefinition("successCount", "int", "Number of successful runs"),
                new BrickOutputDefinition("failureCount", "int", "Number of failed runs"),
                new BrickOutputDefinition("pendingCount", "int", "Number of pending runs"),
                new BrickOutputDefinition("runs", "object[]", "Detailed workflow run information"),
                new BrickOutputDefinition("checks", "object[]", "Check run details")
            ]
        };
    }

    public override async Task<BrickOutput> ExecuteAsync(
        BrickInput input,
        ImplementationType implementation,
        IExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var owner = input.Get<string>("owner");
        var repo = input.Get<string>("repo");
        var gitRef = input.Get<string>("ref");
        var output = new BrickOutput();

        try
        {
            var client = CreateGitHubClient(input);

            // Resolve ref to SHA if needed
            var sha = await ResolveRefToSha(client, owner, repo, gitRef);

            // Get workflow runs for this commit
            var workflowRuns = await client.Actions.Workflows.Runs.List(
                owner,
                repo,
                new WorkflowRunsRequest { HeadSha = sha });

            // Get check runs for this commit
            var checkRuns = await client.Check.Run.GetAllForReference(owner, repo, sha);

            // Analyze status
            var (overallStatus, successCount, failureCount, pendingCount) = AnalyzeStatus(
                workflowRuns.WorkflowRuns,
                checkRuns);

            output.Set("status", overallStatus);
            output.Set("totalRuns", workflowRuns.TotalCount);
            output.Set("successCount", successCount);
            output.Set("failureCount", failureCount);
            output.Set("pendingCount", pendingCount);

            // Detailed run information
            var runs = workflowRuns.WorkflowRuns.Select(r => new
            {
                id = r.Id,
                name = r.Name,
                status = r.Status.StringValue,
                conclusion = r.Conclusion?.StringValue,
                createdAt = r.CreatedAt,
                updatedAt = r.UpdatedAt,
                url = r.HtmlUrl
            }).ToList();

            var checks = checkRuns.CheckRuns.Select(c => new
            {
                id = c.Id,
                name = c.Name,
                status = c.Status.StringValue,
                conclusion = c.Conclusion?.StringValue,
                startedAt = c.StartedAt,
                completedAt = c.CompletedAt,
                url = c.HtmlUrl
            }).ToList();

            output.Set("runs", runs);
            output.Set("checks", checks);

            output.Summary = $"CI Status: {overallStatus} ({successCount} success, {failureCount} failure, {pendingCount} pending)";
        }
        catch (NotFoundException)
        {
            output.Summary = $"Repository or ref not found: {owner}/{repo}@{gitRef}";
            output.Set("status", "not_found");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error analyzing CI status for {Owner}/{Repo}@{Ref}", owner, repo, gitRef);
            output.Summary = $"Error: {ex.Message}";
            output.Set("status", "error");
        }

        return output;
    }

    private GitHubClient CreateGitHubClient(BrickInput input)
    {
        var client = new GitHubClient(new ProductHeaderValue("Ashlar-GitHub-Bricks"));

        if (input.TryGet<string>("githubToken", out var token) && !string.IsNullOrEmpty(token))
        {
            client.Credentials = new Credentials(token);
        }

        return client;
    }

    private async Task<string> ResolveRefToSha(GitHubClient client, string owner, string repo, string gitRef)
    {
        // If it's already a SHA (40 hex characters), return as-is
        if (gitRef.Length == 40 && gitRef.All(c => char.IsDigit(c) || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
        {
            return gitRef;
        }

        try
        {
            // Try as a branch
            var branch = await client.Repository.Branch.Get(owner, repo, gitRef);
            return branch.Commit.Sha;
        }
        catch (NotFoundException)
        {
            // Try as a tag
            try
            {
                var reference = await client.Git.Reference.Get(owner, repo, $"tags/{gitRef}");
                return reference.Object.Sha;
            }
            catch (NotFoundException)
            {
                // Assume it's a partial SHA or invalid ref - let the API handle it
                return gitRef;
            }
        }
    }

    private (string overallStatus, int successCount, int failureCount, int pendingCount) AnalyzeStatus(
        IReadOnlyList<WorkflowRun> runs,
        CheckRunsResponse checks)
    {
        var successCount = 0;
        var failureCount = 0;
        var pendingCount = 0;

        // Analyze workflow runs
        foreach (var run in runs)
        {
            if (run.Status == WorkflowRunStatus.Completed)
            {
                if (run.Conclusion == WorkflowRunConclusion.Success)
                    successCount++;
                else if (run.Conclusion == WorkflowRunConclusion.Failure)
                    failureCount++;
            }
            else
            {
                pendingCount++;
            }
        }

        // Analyze check runs
        foreach (var check in checks.CheckRuns)
        {
            if (check.Status == CheckStatus.Completed)
            {
                if (check.Conclusion == CheckConclusion.Success)
                    successCount++;
                else if (check.Conclusion == CheckConclusion.Failure)
                    failureCount++;
            }
            else
            {
                pendingCount++;
            }
        }

        // Determine overall status
        string overallStatus;
        if (failureCount > 0)
            overallStatus = "failure";
        else if (pendingCount > 0)
            overallStatus = "pending";
        else if (successCount > 0)
            overallStatus = "success";
        else
            overallStatus = "skipped";

        return (overallStatus, successCount, failureCount, pendingCount);
    }
}
