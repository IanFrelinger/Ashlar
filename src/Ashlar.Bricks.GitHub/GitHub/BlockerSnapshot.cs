namespace Ashlar.Bricks.GitHub;

/// <summary>A fact the reader could not establish, and why. An unknown is never silently read as "fine".</summary>
internal sealed record Unknown(string What, string Why);

internal sealed record CheckFact(string Name, string Status, string? Conclusion, string? Url);

internal sealed record StatusFact(string Context, string State, string? Url, string? Description);

internal sealed record ReviewFact(string User, string State, string? Url, DateTimeOffset? SubmittedAt);

/// <summary>CI on one commit: check runs and legacy commit statuses, with what could not be read.</summary>
internal sealed record CiFacts(
    string Sha,
    IReadOnlyList<CheckFact>? Checks,
    int ChecksReported,
    IReadOnlyList<StatusFact>? Statuses,
    int StatusesReported);

/// <summary>Branch protection of the base branch. Each part is <see langword="null"/> when it could not be read.</summary>
internal sealed record ProtectionFacts(IReadOnlyList<string>? RequiredChecks, int? RequiredApprovals);

internal sealed record PullRequestFacts(
    string Url,
    string Title,
    bool Closed,
    bool Merged,
    bool Draft,
    bool? Mergeable,
    string? MergeableState,
    string BaseRef,
    IReadOnlyList<ReviewFact>? Reviews,
    CiFacts? Ci,
    ProtectionFacts Protection);

internal sealed record ReleaseFacts(
    string Tag,
    bool TagExists,
    string? ReleaseUrl,
    bool? Draft,
    bool? Prerelease,
    int? AssetCount,
    CiFacts? Ci);

/// <summary>Everything read for one target. Exactly one of <see cref="PullRequest"/> and <see cref="Release"/> is set,
/// unless the target itself could not be read, in which case both are null and <see cref="Unknowns"/> says why.</summary>
internal sealed record BlockerSnapshot(
    GitHubTarget Target,
    PullRequestFacts? PullRequest,
    ReleaseFacts? Release,
    IReadOnlyList<Unknown> Unknowns);
