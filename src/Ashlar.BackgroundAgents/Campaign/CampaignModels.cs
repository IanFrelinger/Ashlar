using System.Text.Json.Serialization;

namespace Ashlar.BackgroundAgents.Campaign;

/// <summary>
/// Specialist lanes in the automated dogfood campaign. Each lane is executed
/// by a sub-agent that reports a structured verdict to the release manager.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CampaignLane
{
    /// <summary>Documentation claims versus the tree (paths, version pins, CLI surface).</summary>
    DocsDrift,

    /// <summary>Regression surface: cert-gate / dogfood tests still exist and (in full mode) still pass.</summary>
    Regression,

    /// <summary>Ashlar still works as a developer tool (CLI, scaffold, authoring docs).</summary>
    DevTool
}

/// <summary>Pass / fail / error for one specialist or the aggregated campaign.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CampaignVerdictKind
{
    /// <summary>Lane completed and found no blockers.</summary>
    Pass,

    /// <summary>Lane completed and found one or more blockers.</summary>
    Fail,

    /// <summary>Lane could not complete (missing input, crashed, timed out).</summary>
    Error
}

/// <summary>One blocker or note a specialist wants the release manager to see.</summary>
public sealed record CampaignFinding(
    string Code,
    string Message,
    string? Path = null,
    int? Line = null,
    string Severity = "error");

/// <summary>Structured report one specialist sub-agent returns to the release manager.</summary>
public sealed record CampaignAgentReport(
    string AgentId,
    string Role,
    CampaignLane Lane,
    CampaignVerdictKind Verdict,
    string Summary,
    IReadOnlyList<CampaignFinding> Findings,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    IReadOnlyDictionary<string, string>? Facts = null);

/// <summary>
/// Aggregated campaign report the release manager writes after every specialist
/// has reported (or been recorded as silent — which is a fail-closed Error).
/// </summary>
public sealed record CampaignReport(
    string CampaignId,
    string RepoRoot,
    string CommitSha,
    CampaignVerdictKind Verdict,
    string Summary,
    IReadOnlyList<CampaignAgentReport> Reports,
    IReadOnlyList<string> MissingReports,
    DateTimeOffset GeneratedAt,
    bool Full);

/// <summary>Inputs for one campaign run.</summary>
public sealed record CampaignRunContext(
    string RepoRoot,
    string CampaignId,
    string AgentId,
    string Role,
    bool Full,
    bool SkipProcessLanes,
    string? OutputDirectory = null,
    string? LaneFilter = null,
    IReadOnlyDictionary<string, string>? Parameters = null);

/// <summary>A specialist defined in the campaign agent-set JSON.</summary>
public sealed record CampaignSpecialistSpec(
    string AgentId,
    string Name,
    string Role,
    CampaignLane Lane,
    string? ParentId,
    IReadOnlyDictionary<string, string>? Parameters = null);

/// <summary>Parsed campaign agent set: one release manager plus its specialists.</summary>
public sealed record CampaignAgentSet(
    string ManagerId,
    string ManagerName,
    IReadOnlyList<CampaignSpecialistSpec> Specialists);
