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

### GitHubBlockersBrick

Answers "what is blocking this pull request or release?". Each blocker comes with a link to its evidence, and
anything that could not be read is listed as an unknown with the reason, never read as "passed".

**Inputs:**
- `url` (string, required): `https://github.com/<owner>/<repo>/pull/<n>` or `https://github.com/<owner>/<repo>/releases/tag/<tag>`.
  Any other host is refused with `ArgumentException` before a request is made.
- `githubToken` (string, optional)

**Outputs:**
- `verdict` (string): `blocked`, `ready`, `unknown` (no blocker found, but something that could hide one was not read),
  `merged` or `closed`
- `blockers` (object[]): `reason` and `evidence` (a URL: the check run, the review, the pull request, the tag list)
- `unknowns` (object[]): `what` and `why`
- `notes` (object[]): non-blocking observations, such as a failing check that is not required
- `evidence` (string[]): every URL the answer relies on
- `answer` (string): the whole answer as Markdown

Check-run names, commit-status contexts and descriptions, reviewer logins, branch and tag names come from GitHub,
and a fork's workflow or any integration with status access chooses some of them. Each is quoted as one inline code
span in `blockers`, `notes` and `answer` (one line, at most 100 characters, a backtick becomes `'`), and an evidence
URL from GitHub is kept only when it is an absolute `http(s)` URL. So the text arrives as data, not as headings,
links, images or instructions to whatever reads the answer.

**What it checks.** For a pull request: draft state, merge conflicts, a branch behind a base that requires up-to-date
branches, reviewers whose latest review requests changes, required approvals, and failing, pending or never-reported
required checks (check runs and commit statuses). For a release: the tag exists, a published (not draft) release
exists for it, and CI on the tagged commit.

**Known limits.**
- Branch protection's own endpoints need admin rights. Without them, the brick reads the same requirements from
  sources that need none: the protection summary on `GET /repos/{o}/{r}/branches/{base}`, the active rulesets on
  `GET /repos/{o}/{r}/rules/branches/{base}`, and GraphQL's `isRequired` per check and `reviewDecision` for the pull
  request. A failing check none of them requires is a note, not a blocker; a required check with no run is a blocker.
- GraphQL needs a token. Read anonymously, GitHub's review decision is not available, so an approval rule that only
  classic branch protection states stays an unknown (a ruleset's approval count is still read).
- Only if both public required-check lists fail to read does GitHub's `mergeable_state` decide whether a failing check
  blocks: `clean`, `unstable` and `has_hooks` mean GitHub would allow the merge, so failures are notes; otherwise every
  failing or pending check is listed as a blocker.
- A `blocked` state nothing else explains (for example a rule type the brick does not read) is reported as a blocker
  naming that gap.
- Draft releases are found only among the 30 newest releases, and only with a token that has push access.

## Network access

Every request goes to `api.github.com` through the Ashlar egress guard (`EgressHttp.Wrap`, inventory row `EG-HTTP-08`
in `docs/EgressInventory.md`), so a deployment profile that refuses external egress refuses these bricks too, with
`EgressRefusedException`.

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
- Ashlar.Abstractions (the egress guard)

## Consumer-Driven Design

This package was created based on real consumer needs identified during PR Blocker Assistant validation. It demonstrates the `TryGet()` pattern added after Ashlar 0.3.0:

```csharp
// Optional parameter handling with TryGet
input.TryGet<string>("githubToken", out var token);
var client = GitHubClientFactory.Create(token); // internal: anonymous when the token is null or empty
```

## Future Additions

Potential bricks for future releases:
- `IssueAnalyzerBrick`: GitHub issue analysis and classification
- `ReleaseCheckerBrick`: Changelog generation (release blockers are `GitHubBlockersBrick`)
- `CodeReviewAssistantBrick`: Automated code review assistance
- `RepoMetricsBrick`: Repository health metrics and statistics

