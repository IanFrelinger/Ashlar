namespace Ashlar.Bricks.GitHub;

// Narrow shapes GitHubBlockerReader reads where Octokit lacks a suitable model (including nullable timestamps on
// queued checks). Octokit's serializer maps PascalCase to snake_case, so GraphQL aliases its fields to snake_case.

internal sealed class CheckRunsDto
{
    public int? TotalCount { get; set; }

    public IReadOnlyList<CheckRunDto>? CheckRuns { get; set; }
}

internal sealed class CheckRunDto
{
    public long? Id { get; set; }

    public string? HeadSha { get; set; }

    public string? Name { get; set; }

    public string? Status { get; set; }

    public string? Conclusion { get; set; }

    public string? HtmlUrl { get; set; }
}

internal sealed class RuleDto
{
    public string? Type { get; set; }

    public RuleParametersDto? Parameters { get; set; }
}

internal sealed class RuleParametersDto
{
    public int? RequiredApprovingReviewCount { get; set; }

    public bool? RequireCodeOwnerReviews { get; set; }

    public bool? RequireLastPushApproval { get; set; }

    public bool? DismissStaleReviews { get; set; }

    public IReadOnlyList<StatusCheckDto>? RequiredStatusChecks { get; set; }
}

internal sealed class StatusCheckDto
{
    public string? Context { get; set; }

    private long? _appId;
    private long? _integrationId;

    public long? AppId
    {
        get => _appId;
        set { _appId = value; AppIdRead = true; }
    }

    public long? IntegrationId
    {
        get => _integrationId;
        set { _integrationId = value; IntegrationIdRead = true; }
    }

    public bool AppIdRead { get; private set; }

    public bool IntegrationIdRead { get; private set; }
}

internal sealed class BranchDto
{
    public bool? Protected { get; set; }

    public BranchProtectionDto? Protection { get; set; }
}

internal sealed class BranchProtectionDto
{
    public RequiredStatusChecksDto? RequiredStatusChecks { get; set; }
}

internal sealed class RequiredStatusChecksDto
{
    public string? EnforcementLevel { get; set; }

    public IReadOnlyList<string>? Contexts { get; set; }

    public IReadOnlyList<StatusCheckDto>? Checks { get; set; }
}

internal sealed class GraphResponseDto
{
    public GraphDataDto? Data { get; set; }

    public IReadOnlyList<GraphErrorDto>? Errors { get; set; }
}

internal sealed class GraphErrorDto
{
    public string? Message { get; set; }
}

internal sealed class GraphDataDto
{
    public GraphRepositoryDto? Repository { get; set; }
}

internal sealed class GraphRepositoryDto
{
    public GraphPullRequestDto? PullRequest { get; set; }
}

internal sealed class GraphPullRequestDto
{
    public string? ReviewDecision { get; set; }

    public GraphCommitsDto? Commits { get; set; }
}

internal sealed class GraphCommitsDto
{
    public IReadOnlyList<GraphCommitNodeDto>? Nodes { get; set; }
}

internal sealed class GraphCommitNodeDto
{
    public GraphCommitDto? Commit { get; set; }
}

internal sealed class GraphCommitDto
{
    public string? Oid { get; set; }

    public GraphRollupDto? StatusCheckRollup { get; set; }
}

internal sealed class GraphRollupDto
{
    public GraphContextsDto? Contexts { get; set; }
}

internal sealed class GraphContextsDto
{
    public IReadOnlyList<GraphContextDto>? Nodes { get; set; }
}

internal sealed class GraphContextDto
{
    public string? TypeName { get; set; }

    public string? Name { get; set; }

    public string? Context { get; set; }

    public bool? IsRequired { get; set; }
}
