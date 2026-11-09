using Ashlar.Core.Domain.Execution;
using Ashlar.Infrastructure.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Execution;

public sealed class SemanticCacheConcurrencyTests
{
    [Fact]
    public async Task Capacity_evicts_least_recently_used_and_replacement_does_not_grow_the_cache()
    {
        var cache = new SemanticCache(NullLogger<SemanticCache>.Instance, 2);
        await cache.SetAsync("first", new BrickOutput());
        await cache.SetAsync("second", new BrickOutput());
        await cache.GetAsync("first");
        await cache.SetAsync("third", new BrickOutput());
        Assert.Null(await cache.GetAsync("second"));
        var replacement = new BrickOutput { Summary = "replacement" };
        await cache.SetAsync("first", replacement);
        Assert.Same(replacement, await cache.GetAsync("first"));
        Assert.NotNull(await cache.GetAsync("third"));
    }

    [Fact]
    public async Task Concurrent_writes_and_reads_remain_bounded()
    {
        const int capacity = 16;
        var cache = new SemanticCache(NullLogger<SemanticCache>.Instance, capacity);
        await Task.WhenAll(Enumerable.Range(0, 2000).Select(i => Task.Run(async () =>
        {
            await cache.SetAsync(i.ToString(), new BrickOutput());
            await cache.GetAsync((i / 2).ToString());
        })));
        var retained = 0;
        for (var i = 0; i < 2000; i++) if (await cache.GetAsync(i.ToString()) is not null) retained++;
        Assert.Equal(capacity, retained);
    }

    [Fact]
    public void Reads_and_writes_share_the_same_synchronization_gate()
    {
        var cache = new SemanticCache(NullLogger<SemanticCache>.Instance, 2);
        // Observe the actual synchronization boundary rather than relying on the scheduler
        // to overlap tiny dictionary operations. Public cache calls run on dedicated threads.
        var field = typeof(SemanticCache).GetField("_gate", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(field);
        var gate = field.GetValue(cache)!;
        using var writerStarted = new ManualResetEventSlim();
        using var readerStarted = new ManualResetEventSlim();
        Exception? writeError = null;
        Exception? readError = null;
        var writer = new Thread(() =>
        {
            writerStarted.Set();
            try { cache.SetAsync("key", new BrickOutput()).GetAwaiter().GetResult(); }
            catch (Exception error) { writeError = error; }
        }) { IsBackground = true };
        var reader = new Thread(() =>
        {
            readerStarted.Set();
            try { cache.GetAsync("key").GetAwaiter().GetResult(); }
            catch (Exception error) { readError = error; }
        }) { IsBackground = true };
        bool writeCompletedWhileHeld;
        bool readCompletedWhileHeld;
        lock (gate)
        {
            writer.Start();
            reader.Start();
            Assert.True(writerStarted.Wait(TimeSpan.FromSeconds(5)));
            Assert.True(readerStarted.Wait(TimeSpan.FromSeconds(5)));
            writeCompletedWhileHeld = writer.Join(TimeSpan.FromMilliseconds(250));
            readCompletedWhileHeld = reader.Join(TimeSpan.FromMilliseconds(250));
        }
        Assert.True(writer.Join(TimeSpan.FromSeconds(5)));
        Assert.True(reader.Join(TimeSpan.FromSeconds(5)));
        Assert.Null(writeError);
        Assert.Null(readError);
        Assert.False(writeCompletedWhileHeld);
        Assert.False(readCompletedWhileHeld);
    }

    [Fact]
    public async Task Cancelled_writes_do_not_change_the_cache()
    {
        var cache = new SemanticCache(NullLogger<SemanticCache>.Instance);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.SetAsync("key", new BrickOutput(), new CancellationToken(true)));
        Assert.Null(await cache.GetAsync("key"));
    }
}
