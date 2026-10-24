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

## Installation

```xml
<PackageReference Include="Ashlar.Bricks.GitHub" Version="0.4.0" />
```

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
- Ashlar.Infrastructure

## Consumer-Driven Design

This package was created based on real consumer needs identified during PR Blocker Assistant validation. It demonstrates the new `TryGet()` pattern added in Ashlar 0.4.0:

```csharp
// Optional parameter handling with TryGet
if (input.TryGet<string>("githubToken", out var token) && !string.IsNullOrEmpty(token))
{
    client.Credentials = new Credentials(token);
}
```
