using Ashlar.Core.Application.ParallelTesting.Models;
using Ashlar.Core.Application.ParallelTesting.Ports;

namespace Ashlar.Infrastructure.ParallelTesting;

/// <summary>
/// Collects and aggregates test instance results.
/// </summary>
internal sealed class ResultCollector : IResultCollector
{
    /// <inheritdoc />
    public Task<AggregatedTestResult> CollectAsync(IReadOnlyList<TestInstance> instances, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var allPassed = instances.All(i => i.Passed);
        var best = instances.FirstOrDefault(i => i.Passed) ?? instances.FirstOrDefault();
        return Task.FromResult(new AggregatedTestResult
        {
            Results = instances,
            BestCandidate = best,
            AllPassed = allPassed,
        });
    }
}
