using Ashlar.Core.Application.Execution;
using Ashlar.Core.Application.Pipelines.Models;
using Xunit;

namespace Ashlar.Tests.Application.Tests.Execution;

/// <summary>Contract tests for workflow attribution and accounting without provider or storage integration.</summary>
public sealed class ProviderUsageContractTests
{
    [Fact(Timeout = 60_000)]
    public async Task Concurrent_child_scopes_contribute_once_to_parent_and_keep_child_attribution()
    {
        using var parent = new ProviderUsageScope("workflow");
        var children = await Task.WhenAll(Enumerable.Range(1, 16).Select(index => Task.Run(async () =>
        {
            using var child = new ProviderUsageScope($"stage-{index}");
            await Task.Yield();
            var observation = Observation(index, 1) with { CorrelationId = "untrusted supplied correlation" };
            ProviderUsageScope.RecordCurrent(observation);
            ProviderUsageScope.RecordCurrent(observation); // Duplicate terminal notification from the same invocation.
            var recorded = Assert.Single(child.Snapshot());
            Assert.Equal($"stage-{index}", recorded.CorrelationId);
            return recorded;
        }))).WaitAsync(TimeSpan.FromSeconds(30));

        var snapshot = parent.Snapshot();
        Assert.Equal(16, snapshot.Count);
        Assert.Equal(children.OrderBy(record => record.CorrelationId), snapshot.OrderBy(record => record.CorrelationId));
        var summary = ProviderUsageSummary.FromAttempts(snapshot);
        Assert.Equal(136L, summary.ReportedInputTokenCount);
        Assert.Equal(16L, summary.ReportedOutputTokenCount);
        Assert.Equal(16, summary.AttemptCount);
        Assert.True(summary.HasCompleteReportedInputAndOutput);

        ProviderUsageScope.RecordCurrent(Observation(5, 2));
        Assert.Equal(16, snapshot.Count); // Snapshots are stable copies, not the live collection.
        Assert.Equal("workflow", parent.Snapshot()[16].CorrelationId);
    }

    [Fact]
    public void Disposal_freezes_a_child_and_restores_parent_collection_without_leaking_into_later_scopes()
    {
        using var parent = new ProviderUsageScope("parent");
        var child = new ProviderUsageScope("child");
        var observation = Observation(2, 1);
        try { ProviderUsageScope.RecordCurrent(observation); }
        finally { child.Dispose(); }

        ProviderUsageScope.RecordCurrent(Observation(3, 1));
        child.Dispose(); // Repeated disposal must not replace the active parent.
        ProviderUsageScope.RecordCurrent(Observation(4, 1));
        Assert.Single(child.Snapshot());
        Assert.Equal(new[] { "child", "parent", "parent" }, parent.Snapshot().Select(record => record.CorrelationId));

        parent.Dispose();
        ProviderUsageScope.RecordCurrent(Observation(50, 1)); // No active scope: intentionally uncollected.
        using var later = new ProviderUsageScope("later");
        ProviderUsageScope.RecordCurrent(observation); // Deduplication belongs to a scope, not process-global history.
        Assert.Equal("later", Assert.Single(later.Snapshot()).CorrelationId);
        Assert.Equal(3, parent.Snapshot().Count);
    }

    [Fact]
    public void Deduplication_preserves_retries_and_distinguishes_http_from_logical_observations()
    {
        using var scope = new ProviderUsageScope("run");
        var first = Observation(10, 2);
        ProviderUsageScope.RecordCurrent(first);
        ProviderUsageScope.RecordCurrent(first with { Outcome = ProviderAttemptOutcome.Failed });
        ProviderUsageScope.RecordCurrent(first with { Attempt = 2, Usage = Reported(3, 1) });
        ProviderUsageScope.RecordCurrent(first with { Boundary = ProviderUsageBoundary.LogicalCall });
        ProviderUsageScope.RecordCurrent(first with { Boundary = ProviderUsageBoundary.LogicalCall });

        var snapshot = scope.Snapshot();
        Assert.Equal(3, snapshot.Count);
        Assert.Equal(ProviderAttemptOutcome.Succeeded, snapshot[0].Outcome);
        var summary = ProviderUsageSummary.FromAttempts(snapshot);
        Assert.Equal(3, summary.ObservationCount);
        Assert.Equal(2, summary.AttemptCount);
        Assert.Equal(1, summary.LogicalCallCount);
        Assert.Equal(13L, summary.ReportedInputTokenCount);
        Assert.Equal(3L, summary.ReportedOutputTokenCount);
        Assert.Equal(10L, summary.LogicalReportedInputTokenCount);
        Assert.Equal(2L, summary.LogicalReportedOutputTokenCount);
        Assert.False(summary.HasCompleteReportedInputAndOutput);
    }

    [Fact]
    public void Empty_unknown_partial_and_reported_zero_usage_remain_distinct()
    {
        var empty = new PipelineRun().ProviderUsage;
        Assert.Equal(0, empty.ObservationCount);
        Assert.Null(empty.ReportedInputTokenCount);
        Assert.Null(empty.ReportedOutputTokenCount);
        Assert.False(empty.HasCompleteReportedInputAndOutput);

        var unknown = ProviderUsageSummary.FromAttempts([Observation(null, null) with { Usage = new ProviderTokenUsage() }]);
        Assert.Equal(1, unknown.AttemptCount);
        Assert.Null(unknown.ReportedInputTokenCount);
        Assert.False(unknown.HasCompleteReportedInputAndOutput);

        var partial = ProviderUsageSummary.FromAttempts([Observation(7, null)]);
        Assert.Equal(7L, partial.ReportedInputTokenCount);
        Assert.Null(partial.ReportedOutputTokenCount);
        Assert.False(partial.HasCompleteReportedInputAndOutput);

        var zero = new PipelineRun { ProviderUsageAttempts = [Observation(0, 0)] }.ProviderUsage;
        Assert.Equal(0L, zero.ReportedInputTokenCount);
        Assert.Equal(0L, zero.ReportedOutputTokenCount);
        Assert.True(zero.HasCompleteReportedInputAndOutput);
    }

    [Theory]
    [InlineData(ProviderUsageSource.Unknown)]
    [InlineData(ProviderUsageSource.Estimated)]
    public void Counts_without_provider_reported_provenance_are_excluded_from_reported_subtotals(ProviderUsageSource source)
    {
        var observation = Observation(30, 5) with { Usage = Reported(30, 5) with { Source = source } };
        var summary = ProviderUsageSummary.FromAttempts([observation]);
        Assert.Equal(1, summary.AttemptCount);
        Assert.Null(summary.ReportedInputTokenCount);
        Assert.Null(summary.ReportedOutputTokenCount);
        Assert.False(summary.HasCompleteReportedInputAndOutput);
    }

    [Fact]
    public void Invalid_counts_are_normalized_both_at_collection_and_when_summarizing_persisted_records()
    {
        var corrupt = Observation(-1, 8) with
        {
            Usage = Reported(-1, 8) with { CachedInputTokenCount = -2, ReasoningTokenCount = 9 }
        };
        using var scope = new ProviderUsageScope("invalid");
        ProviderUsageScope.RecordCurrent(corrupt);
        var usage = Assert.Single(scope.Snapshot()).Usage;
        Assert.Null(usage.InputTokenCount);
        Assert.Null(usage.CachedInputTokenCount);
        Assert.Null(usage.ReasoningTokenCount);
        Assert.Equal(8L, usage.OutputTokenCount);
        Assert.True(usage.HasInvalidCounts);

        var persistedSummary = ProviderUsageSummary.FromAttempts([corrupt]);
        Assert.Equal(persistedSummary, ProviderUsageSummary.FromAttempts(scope.Snapshot()));
        Assert.Equal(8L, persistedSummary.ReportedOutputTokenCount);
        Assert.False(persistedSummary.HasCompleteReportedInputAndOutput);
        Assert.True(persistedSummary.HasInvalidCounts);
    }

    [Fact]
    public void Cache_and_reasoning_are_subsets_and_cannot_exceed_their_parent_counts()
    {
        var valid = Observation(10, 4) with { Usage = Reported(10, 4) with { CachedInputTokenCount = 6, ReasoningTokenCount = 2 } };
        var summary = ProviderUsageSummary.FromAttempts([valid]);
        Assert.Equal(10L, summary.ReportedInputTokenCount);
        Assert.Equal(4L, summary.ReportedOutputTokenCount);
        Assert.Equal(6L, summary.ReportedCachedInputTokenCount);
        Assert.Equal(2L, summary.ReportedReasoningTokenCount);
        Assert.True(summary.HasCompleteReportedInputAndOutput);

        var invalid = valid with { Usage = valid.Usage with { CachedInputTokenCount = 11, ReasoningTokenCount = -1 } };
        var invalidSummary = ProviderUsageSummary.FromAttempts([invalid]);
        Assert.Equal(10L, invalidSummary.ReportedInputTokenCount);
        Assert.Equal(4L, invalidSummary.ReportedOutputTokenCount);
        Assert.Null(invalidSummary.ReportedCachedInputTokenCount);
        Assert.Null(invalidSummary.ReportedReasoningTokenCount);
        Assert.True(invalidSummary.HasInvalidCounts);
        Assert.False(invalidSummary.HasCompleteReportedInputAndOutput);
    }

    [Theory]
    [InlineData(ProviderUsageBoundary.HttpAttempt)]
    [InlineData(ProviderUsageBoundary.LogicalCall)]
    public void Overflow_invalidates_only_the_affected_subtotal_without_wrapping_or_erasing_other_counts(ProviderUsageBoundary boundary)
    {
        var observations = new[]
        {
            Observation(long.MaxValue, 1) with { Boundary = boundary },
            Observation(1, 2) with { Boundary = boundary }
        };
        var summary = ProviderUsageSummary.FromAttempts(observations);
        Assert.True(summary.HasOverflow);
        Assert.False(summary.HasInvalidCounts);
        Assert.False(summary.HasCompleteReportedInputAndOutput);
        Assert.Null(summary.ReportedInputTokenCount);
        Assert.Null(summary.LogicalReportedInputTokenCount);
        Assert.Equal(boundary == ProviderUsageBoundary.HttpAttempt ? 3L : null, summary.ReportedOutputTokenCount);
        Assert.Equal(boundary == ProviderUsageBoundary.LogicalCall ? 3L : null, summary.LogicalReportedOutputTokenCount);
    }

    [Fact]
    public void Missing_usage_in_persisted_data_is_invalid_and_never_becomes_a_zero_total()
    {
        var summary = ProviderUsageSummary.FromAttempts([Observation(1, 1) with { Usage = null! }, null!]);
        Assert.Equal(2, summary.ObservationCount);
        Assert.True(summary.HasInvalidCounts);
        Assert.False(summary.HasCompleteReportedInputAndOutput);
        Assert.Null(summary.ReportedInputTokenCount);
        Assert.Null(summary.ReportedOutputTokenCount);
        Assert.Throws<ArgumentNullException>(() => ProviderUsageSummary.FromAttempts(null!));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Scope_requires_a_correlation_identity(string? correlation)
        => Assert.Throws<ArgumentException>(() => new ProviderUsageScope(correlation!));

    [Fact]
    public void Invalid_observation_identity_is_rejected_without_polluting_the_active_scope()
    {
        using var scope = new ProviderUsageScope("validation");
        Assert.Throws<ArgumentNullException>(() => ProviderUsageScope.RecordCurrent(null!));
        Assert.Throws<ArgumentException>(() => ProviderUsageScope.RecordCurrent(Observation(1, 1) with { RequestId = Guid.Empty }));
        Assert.Throws<ArgumentException>(() => ProviderUsageScope.RecordCurrent(Observation(1, 1) with { Attempt = 0 }));
        Assert.Throws<ArgumentException>(() => ProviderUsageScope.RecordCurrent(Observation(1, 1) with { Attempt = -1 }));
        Assert.Throws<ArgumentException>(() => ProviderUsageScope.RecordCurrent(Observation(1, 1) with { Boundary = (ProviderUsageBoundary)99 }));
        Assert.Empty(scope.Snapshot());
    }

    private static ProviderUsageRecord Observation(long? input, long? output) => new()
    {
        RequestId = Guid.NewGuid(), Attempt = 1, Provider = "contract-test", Model = "fixture",
        Outcome = ProviderAttemptOutcome.Succeeded, Usage = Reported(input, output)
    };

    private static ProviderTokenUsage Reported(long? input, long? output) => new()
    {
        InputTokenCount = input, OutputTokenCount = output, Source = ProviderUsageSource.ProviderReported
    };
}
