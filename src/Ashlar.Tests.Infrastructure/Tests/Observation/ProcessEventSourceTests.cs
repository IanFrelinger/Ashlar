using FluentAssertions;
using Microsoft.Extensions.Logging;
using Ashlar.Infrastructure.Observation;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Observation;

/// <summary>Tests for process event source.</summary>
public sealed class ProcessEventSourceTests
{
    [Fact]
    public async Task SubscribeAsync_WithProcessFilters_DoesNotThrow()
    {
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<ProcessEventSource>.Instance;
        var source = new ProcessEventSource(new[] { "dotnet" }, projectPath: null, pollInterval: TimeSpan.FromMilliseconds(50), logger);

        var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var count = 0;

        try
        {
            await foreach (var _ in source.SubscribeAsync(cts.Token).WithCancellation(cts.Token))
            {
                count++;
                if (count >= 5) break;
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when token expires
        }

        count.Should().BeLessThanOrEqualTo(5);
    }

    [Fact]
    public async Task SubscribeAsync_WithEmptyFilters_YieldsNothing()
    {
        var source = new ProcessEventSource(Array.Empty<string>(), projectPath: null, pollInterval: TimeSpan.FromMilliseconds(50));
        var collected = new List<object>();

        await foreach (var evt in source.SubscribeAsync(CancellationToken.None).WithCancellation(CancellationToken.None))
        {
            collected.Add(evt);
            break;
        }

        collected.Should().BeEmpty();
    }

    [Fact]
    public async Task SubscribeAsync_break_without_cancelling_stops_the_subscription_and_allows_restart()
    {
        using var current = System.Diagnostics.Process.GetCurrentProcess();
        var source = new ProcessEventSource([current.ProcessName], pollInterval: TimeSpan.FromMilliseconds(20));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        for (var i = 0; i < 2; i++)
        {
            var enumerator = source.SubscribeAsync(cts.Token).GetAsyncEnumerator();
            Assert.True(await enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)));
            var disposal = enumerator.DisposeAsync().AsTask();
            try
            {
                await disposal.WaitAsync(TimeSpan.FromSeconds(3));
                Assert.False(cts.IsCancellationRequested);
            }
            catch
            {
                cts.Cancel();
                await disposal;
                throw;
            }
        }
    }
}
