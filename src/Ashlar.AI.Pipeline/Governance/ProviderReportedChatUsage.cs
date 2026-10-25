using Microsoft.Extensions.AI;

namespace Ashlar.AI.Pipeline.Governance;

/// <summary>Usage explicitly obtained from provider-reported counters, rather than a local estimate.</summary>
public sealed class ProviderReportedChatUsage : UsageDetails
{
    /// <summary>Whether a provider supplied malformed counters. Valid counters remain available.</summary>
    public bool HasInvalidCounts { get; init; }
}
