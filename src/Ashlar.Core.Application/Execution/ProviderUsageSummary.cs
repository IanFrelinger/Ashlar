namespace Ashlar.Core.Application.Execution;

/// <summary>
/// Provider-reported subtotals for observed attempts only. This is not a billing total or a claim that every
/// workflow provider is instrumented. HTTP and logical SDK subtotals are separate because they may observe
/// the same work. Missing counts remain unknown; cached and reasoning counts are subsets.
/// </summary>
public sealed record ProviderUsageSummary
{
    /// <summary>Total number of observations at either boundary.</summary>
    public int ObservationCount { get; init; }
    /// <summary>Number of observed actual HTTP attempts, including retries, failures and cancellation.</summary>
    public int AttemptCount { get; init; }
    /// <summary>Number of logical SDK calls, whose internal HTTP attempts are not visible.</summary>
    public int LogicalCallCount { get; init; }
    /// <summary>Sum of available HTTP-attempt reported input counts, or null if none are known or the sum overflows.</summary>
    public long? ReportedInputTokenCount { get; init; }
    /// <summary>Sum of available HTTP-attempt reported output counts, or null if none are known or the sum overflows.</summary>
    public long? ReportedOutputTokenCount { get; init; }
    /// <summary>Sum of available HTTP-attempt reported cached input counts, or null if none are known or the sum overflows.</summary>
    public long? ReportedCachedInputTokenCount { get; init; }
    /// <summary>Sum of available HTTP-attempt reported reasoning counts, or null if none are known or the sum overflows.</summary>
    public long? ReportedReasoningTokenCount { get; init; }
    /// <summary>Reported logical-call input subtotal. May overlap HTTP-attempt usage; never add the two without linkage.</summary>
    public long? LogicalReportedInputTokenCount { get; init; }
    /// <summary>Reported logical-call output subtotal. May overlap HTTP-attempt usage; never add the two without linkage.</summary>
    public long? LogicalReportedOutputTokenCount { get; init; }
    /// <summary>Reported logical-call cached input subtotal, or null if none are known or the sum overflows.</summary>
    public long? LogicalReportedCachedInputTokenCount { get; init; }
    /// <summary>Reported logical-call reasoning subtotal, or null if none are known or the sum overflows.</summary>
    public long? LogicalReportedReasoningTokenCount { get; init; }
    /// <summary>Whether every observation is an HTTP attempt with valid reported input and output counts. False for empty snapshots and logical SDK calls with hidden retries.</summary>
    public bool HasCompleteReportedInputAndOutput { get; init; }
    /// <summary>Whether any observed usage contained malformed counts.</summary>
    public bool HasInvalidCounts { get; init; }
    /// <summary>Whether a subtotal exceeded the supported integer range.</summary>
    public bool HasOverflow { get; init; }

    /// <summary>Summarizes a snapshot. Missing usage is never treated as zero, and estimates are excluded.</summary>
    public static ProviderUsageSummary FromAttempts(IEnumerable<ProviderUsageRecord> attempts)
    {
        if (attempts is null) throw new ArgumentNullException(nameof(attempts));
        // Records can also arrive from persistence or a caller, bypassing the HTTP parser.
        var observations = attempts.ToArray();
        var usages = observations.Select(r => Normalize(r?.Usage)).ToArray();
        var httpCount = observations.Count(r => r?.Boundary == ProviderUsageBoundary.HttpAttempt);
        var logicalCount = observations.Count(r => r?.Boundary == ProviderUsageBoundary.LogicalCall);
        var reported = usages.Where((u, i) => observations[i]?.Boundary == ProviderUsageBoundary.HttpAttempt
            && u.Source == ProviderUsageSource.ProviderReported).ToArray();
        var logicalReported = usages.Where((u, i) => observations[i]?.Boundary == ProviderUsageBoundary.LogicalCall
            && u.Source == ProviderUsageSource.ProviderReported).ToArray();
        var overflow = false;
        var input = Sum(reported.Select(u => u.InputTokenCount), ref overflow);
        var output = Sum(reported.Select(u => u.OutputTokenCount), ref overflow);
        var cached = Sum(reported.Select(u => u.CachedInputTokenCount), ref overflow);
        var reasoning = Sum(reported.Select(u => u.ReasoningTokenCount), ref overflow);
        var logicalInput = Sum(logicalReported.Select(u => u.InputTokenCount), ref overflow);
        var logicalOutput = Sum(logicalReported.Select(u => u.OutputTokenCount), ref overflow);
        var logicalCached = Sum(logicalReported.Select(u => u.CachedInputTokenCount), ref overflow);
        var logicalReasoning = Sum(logicalReported.Select(u => u.ReasoningTokenCount), ref overflow);
        var invalid = usages.Any(u => u.HasInvalidCounts);
        return new ProviderUsageSummary
        {
            ObservationCount = usages.Length, AttemptCount = httpCount, LogicalCallCount = logicalCount,
            ReportedInputTokenCount = input, ReportedOutputTokenCount = output,
            ReportedCachedInputTokenCount = cached, ReportedReasoningTokenCount = reasoning,
            LogicalReportedInputTokenCount = logicalInput, LogicalReportedOutputTokenCount = logicalOutput,
            LogicalReportedCachedInputTokenCount = logicalCached, LogicalReportedReasoningTokenCount = logicalReasoning,
            HasInvalidCounts = invalid, HasOverflow = overflow,
            HasCompleteReportedInputAndOutput = usages.Length > 0 && httpCount == usages.Length && !invalid && !overflow && usages.All(u =>
                u.Source == ProviderUsageSource.ProviderReported && u.InputTokenCount.HasValue && u.OutputTokenCount.HasValue)
        };
    }

    internal static ProviderTokenUsage Normalize(ProviderTokenUsage? usage)
    {
        if (usage is null) return new ProviderTokenUsage { HasInvalidCounts = true };
        var input = usage.InputTokenCount < 0 ? null : usage.InputTokenCount;
        var output = usage.OutputTokenCount < 0 ? null : usage.OutputTokenCount;
        var cached = usage.CachedInputTokenCount < 0 || usage.CachedInputTokenCount > input ? null : usage.CachedInputTokenCount;
        var reasoning = usage.ReasoningTokenCount < 0 || usage.ReasoningTokenCount > output ? null : usage.ReasoningTokenCount;
        return usage with
        {
            InputTokenCount = input, OutputTokenCount = output, CachedInputTokenCount = cached, ReasoningTokenCount = reasoning,
            HasInvalidCounts = usage.HasInvalidCounts || input != usage.InputTokenCount || output != usage.OutputTokenCount
                || cached != usage.CachedInputTokenCount || reasoning != usage.ReasoningTokenCount
        };
    }

    private static long? Sum(IEnumerable<long?> counts, ref bool overflow)
    {
        long? total = null;
        foreach (var count in counts)
        {
            if (!count.HasValue) continue;
            try { total = checked((total ?? 0) + count.Value); }
            catch (OverflowException) { overflow = true; return null; }
        }
        return total;
    }
}
