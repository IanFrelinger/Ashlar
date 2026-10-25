# Ashlar.Bricks.GitHub

GitHub integration bricks for Ashlar - analyze PRs, check CI status, and work with GitHub data.

## Bricks

### PrStatusBrick

Analyzes GitHub Pull Request status including reviews, checks, and merge blockers.

**Inputs:**
- `prUrl` (string, required): GitHub Pull Request URL
- `githubToken` (string, optional): GitHub Personal Access Token for higher rate limits

**Outputs:**
- `state` (string): PR state (open, closed, merged)
- `mergeable` (boolean): Whether PR is mergeable
- `blockers` (string[]): List of merge blockers
- `checks` (object): CI check status summary
- `reviews` (object): Review status summary

**Example:**

```csharp
var input = new BrickInput();
input.Set("prUrl", "https://github.com/owner/repo/pull/123");

var output = await prStatusBrick.ExecuteAsync(input, ImplementationType.Deterministic, context);

var blockers = output.Get<List<string>>("blockers");
Console.WriteLine($"Blockers: {string.Join(", ", blockers)}");
```

### CiStatusBrick

Analyzes GitHub Actions CI/CD workflow runs and check status for a commit or branch.

**Inputs:**
- `owner` (string, required): Repository owner
- `repo` (string, required): Repository name
- `ref` (string, required): Git reference (branch, tag, or SHA)
- `githubToken` (string, optional): GitHub Personal Access Token for higher rate limits

**Outputs:**
- `status` (string): Overall CI status (success, failure, pending, skipped)
- `totalRuns` (int): Total workflow runs
- `successCount` (int): Number of successful runs
- `failureCount` (int): Number of failed runs
- `pendingCount` (int): Number of pending runs
- `runs` (object[]): Detailed workflow run information
- `checks` (object[]): Check run details

**Example:**

```csharp
var input = new BrickInput();
input.Set("owner", "IanFrelinger");
input.Set("repo", "Ashlar");
input.Set("ref", "master");

var output = await ciStatusBrick.ExecuteAsync(input, ImplementationType.Deterministic, context);

var status = output.Get<string>("status");
var failureCount = output.Get<int>("failureCount");
Console.WriteLine($"CI Status: {status}, Failures: {failureCount}");
```

## Installation

`Ashlar.Bricks.GitHub` ships from the first release after 0.3.0; 0.3.0 on nuget.org does not contain it.
Pin it at the same version as the other `Ashlar.*` packages:

```xml
<PackageReference Include="Ashlar.Bricks.GitHub" Version="$(AshlarConsumerPackageVersion)" />
```

The bricks live in the `Ashlar.Bricks.GitHub` namespace.

## Authentication

GitHub API has rate limits:
- **Unauthenticated**: 60 requests/hour
- **Authenticated**: 5000 requests/hour

Provide a `githubToken` input for higher rate limits:

```csharp
input.Set("githubToken", Environment.GetEnvironmentVariable("GITHUB_TOKEN"));
```

## Dependencies

- [Octokit](https://github.com/octokit/octokit.net) - Official GitHub API client for .NET
- Ashlar.Core.Domain

## Consumer-Driven Design

This package was created based on real consumer needs identified during PR Blocker Assistant validation. It demonstrates the `TryGet()` pattern added after Ashlar 0.3.0:

```csharp
// Optional parameter handling with TryGet
if (input.TryGet<string>("githubToken", out var token) && !string.IsNullOrEmpty(token))
{
    client.Credentials = new Credentials(token);
}
```

## Future Additions

Potential bricks for future releases:
- `IssueAnalyzerBrick`: GitHub issue analysis and classification
- `ReleaseCheckerBrick`: Release validation and changelog generation
- `CodeReviewAssistantBrick`: Automated code review assistance
- `RepoMetricsBrick`: Repository health metrics and statistics

