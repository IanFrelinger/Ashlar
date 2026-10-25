using Ashlar.AI.Pipeline.Governance;
using Ashlar.Core.Application.Execution;

namespace Ashlar.Hosting.Meai;

/// <summary>Bridges per-target MEAI invocation observations into the current workflow's usage scope.</summary>
internal sealed class MeaiProviderUsageObserver : IChatInvocationUsageObserver
{
    public MeaiProviderUsageObserver() { }

    public void Observe(ChatInvocationAuditRecord record)
    {
        if (!record.IsProviderInvocation || record.InvocationId is not { } invocationId || invocationId == Guid.Empty)
            return;
        if (record.Outcome is not ("success" or "fault" or "cancelled" or "denied"))
            return;

        var hasCounts = record.InputTokenCount.HasValue || record.OutputTokenCount.HasValue ||
                        record.CachedInputTokenCount.HasValue || record.ReasoningTokenCount.HasValue;
        ProviderUsageScope.RecordCurrent(new ProviderUsageRecord
        {
            RequestId = invocationId,
            Attempt = 1,
            Boundary = ProviderUsageBoundary.LogicalCall,
            Provider = record.TargetKey,
            Model = record.ModelId ?? string.Empty,
            StartedAt = record.StartedAt ?? record.Timestamp,
            Duration = TimeSpan.FromMilliseconds(Math.Max(0, record.LatencyMs)),
            Outcome = record.Outcome switch
            {
                "success" => ProviderAttemptOutcome.Succeeded,
                "cancelled" => ProviderAttemptOutcome.Cancelled,
                _ => ProviderAttemptOutcome.Failed
            },
            // Audits observe the SDK call boundary, not its individual HTTP responses or retries.
            HttpStatusCode = null,
            Usage = new ProviderTokenUsage
            {
                InputTokenCount = record.InputTokenCount,
                OutputTokenCount = record.OutputTokenCount,
                CachedInputTokenCount = record.CachedInputTokenCount,
                ReasoningTokenCount = record.ReasoningTokenCount,
                Source = hasCounts && record.UsageIsProviderReported ? ProviderUsageSource.ProviderReported : ProviderUsageSource.Unknown,
                HasInvalidCounts = record.UsageHasInvalidCounts
            }
        });
    }
}
