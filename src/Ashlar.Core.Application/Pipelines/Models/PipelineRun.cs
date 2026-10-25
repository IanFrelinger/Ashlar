using Ashlar.Core.Application.Execution;

namespace Ashlar.Core.Application.Pipelines.Models;

/// <summary>
/// Pipeline run aggregate.
/// </summary>
public sealed record PipelineRun
{
    /// <summary>Unique identifier for this run instance.</summary>
    public string RunId { get; init; } = string.Empty;

    /// <summary>Template identifier that produced this run.</summary>
    public string TemplateId { get; init; } = string.Empty;

    /// <summary>Source checkpoint when this execution resumed a prior run.</summary>
    public string? ResumeSourceRunId { get; init; }

    /// <summary>Observed provider attempts made by this invocation only; source-run usage is not copied on resume.</summary>
    public IReadOnlyList<ProviderUsageRecord> ProviderUsageAttempts { get; init; } = Array.Empty<ProviderUsageRecord>();

    /// <summary>Reported token subtotals and completeness for observed attempts; empty or missing usage remains unknown.</summary>
    public ProviderUsageSummary ProviderUsage => ProviderUsageSummary.FromAttempts(ProviderUsageAttempts);

    /// <summary>Overall lifecycle state of the run.</summary>
    public PipelineRunState State { get; init; } = PipelineRunState.Pending;

    /// <summary>UTC timestamp when the run started.</summary>
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>UTC timestamp when the run finished, if completed or failed.</summary>
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>Per-stage execution records for this run.</summary>
    public IReadOnlyList<PipelineStageRun> StageRuns { get; init; } = Array.Empty<PipelineStageRun>();
}
