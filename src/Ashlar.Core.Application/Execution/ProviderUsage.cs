namespace Ashlar.Core.Application.Execution;

/// <summary>Where the available token counts came from. Missing counts remain null.</summary>
public enum ProviderUsageSource
{
    /// <summary>No usable token counts were supplied.</summary>
    Unknown,
    /// <summary>The provider reported the available counts.</summary>
    ProviderReported,
    /// <summary>The available counts were estimated locally.</summary>
    Estimated
}

/// <summary>Terminal state of an inference HTTP attempt, including attempts that may have consumed tokens before failing.</summary>
public enum ProviderAttemptOutcome
{
    /// <summary>A usable response was returned.</summary>
    Succeeded,
    /// <summary>The attempt failed. Unknown usage does not mean zero cost.</summary>
    Failed,
    /// <summary>The attempt was cancelled. The provider may still have performed work.</summary>
    Cancelled
}

/// <summary>The observation boundary. A logical SDK call may contain unobserved HTTP retries.</summary>
public enum ProviderUsageBoundary
{
    /// <summary>One instrumented HTTP send attempt.</summary>
    HttpAttempt,
    /// <summary>One model SDK invocation, whose internal HTTP attempts are not visible.</summary>
    LogicalCall
}

/// <summary>Token counts with provenance. Cached input and reasoning counts are subsets, not additional tokens to sum.</summary>
public sealed record ProviderTokenUsage
{
    /// <summary>Input tokens, or null when unavailable.</summary>
    public long? InputTokenCount { get; init; }
    /// <summary>Output tokens, or null when unavailable.</summary>
    public long? OutputTokenCount { get; init; }
    /// <summary>Cached input tokens, or null when unavailable.</summary>
    public long? CachedInputTokenCount { get; init; }
    /// <summary>Reasoning output tokens, or null when unavailable.</summary>
    public long? ReasoningTokenCount { get; init; }
    /// <summary>Provenance of the non-null fields.</summary>
    public ProviderUsageSource Source { get; init; }
    /// <summary>Whether a supplied count or usage object was malformed. Valid fields remain available.</summary>
    public bool HasInvalidCounts { get; init; }
}

/// <summary>One inference observation at its declared boundary. Contains no prompts, generated content, credentials or endpoint URLs.</summary>
public sealed record ProviderUsageRecord
{
    /// <summary>Whether this observation covers an actual HTTP attempt or a logical SDK call.</summary>
    public ProviderUsageBoundary Boundary { get; init; }
    /// <summary>Identifier shared by attempts of one provider invocation.</summary>
    public Guid RequestId { get; init; }
    /// <summary>One-based attempt number, or one for a logical SDK call whose internal retries are unknown.</summary>
    public int Attempt { get; init; }
    /// <summary>Provider identifier.</summary>
    public string Provider { get; init; } = string.Empty;
    /// <summary>Requested model or Azure deployment identifier.</summary>
    public string Model { get; init; } = string.Empty;
    /// <summary>Caller-supplied correlation identifier of the nearest collecting scope.</summary>
    public string? CorrelationId { get; init; }
    /// <summary>UTC time at which the attempt began.</summary>
    public DateTimeOffset StartedAt { get; init; }
    /// <summary>Elapsed time through response processing or failure.</summary>
    public TimeSpan Duration { get; init; }
    /// <summary>Terminal attempt state.</summary>
    public ProviderAttemptOutcome Outcome { get; init; }
    /// <summary>HTTP status when a response was received.</summary>
    public int? HttpStatusCode { get; init; }
    /// <summary>Available usage, including partial usage on a failed response.</summary>
    public ProviderTokenUsage Usage { get; init; } = new();
}
