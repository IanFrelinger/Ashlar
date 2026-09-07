using FluentAssertions;
using Microsoft.Extensions.Options;
using Ashlar.Brick.Contracts;
using Ashlar.Commercial.Fleet.Contracts.Networking.Models;
using Ashlar.Commercial.Fleet.Contracts.Networking.Ports;
using Ashlar.Core.Domain.Bricks;
using Ashlar.Core.Domain.Execution;
using Ashlar.Commercial.Fleet.Infrastructure.Execution;
using Xunit;

namespace Ashlar.Commercial.Tests.Fleet.Execution;

/// <summary>
/// Tests for AdaptiveBrickCache: usage-aware brick caching (synaptic plasticity).
/// </summary>
public class AdaptiveBrickCacheTests
{
    private sealed class StubBrickRegistry : IBrickRegistry
    {
        private readonly Dictionary<string, DomainBrick> _bricks = new(StringComparer.OrdinalIgnoreCase);

        public void Add(DomainBrick b) => _bricks[b.Id] = b;

        public DomainBrick? GetBrick(string id) => _bricks.TryGetValue(id, out var b) ? b : null;

        public IReadOnlyList<DomainBrick> GetAllBricks() => _bricks.Values.ToList();
    }

    [Fact]
    public void GetBrick_DelegatesToInner_AndCaches()
    {
        var inner = new StubBrickRegistry();
        var brick = new TestBrick { Id = "b1", Name = "B1" };
        inner.Add(brick);

        var tracker = new BrickUsageTracker(Options.Create(new BrickUsageTrackerOptions()));
        var cache = new AdaptiveBrickCache(inner, tracker, Options.Create(new AdaptiveBrickCacheOptions()));

        var result = cache.GetBrick("b1");

        result.Should().NotBeNull();
        result!.Id.Should().Be("b1");
        var stats = cache.GetCacheStats();
        stats.HitRate.Should().Be(0);
        stats.Entries.Should().Be(1); // Now caches all bricks
    }

    [Fact]
    public void GetBrick_ReturnsNull_WhenInnerReturnsNull()
    {
        var inner = new StubBrickRegistry();
        var tracker = new BrickUsageTracker(Options.Create(new BrickUsageTrackerOptions()));
        var cache = new AdaptiveBrickCache(inner, tracker, Options.Create(new AdaptiveBrickCacheOptions()));

        var result = cache.GetBrick("nonexistent");

        result.Should().BeNull();
    }

    [Fact]
    public void GetCacheStats_ReturnsEntriesAndEvictionCount()
    {
        var inner = new StubBrickRegistry();
        inner.Add(new TestBrick { Id = "x", Name = "X" });

        var tracker = new BrickUsageTracker(Options.Create(new BrickUsageTrackerOptions()));
        var cache = new AdaptiveBrickCache(inner, tracker, Options.Create(new AdaptiveBrickCacheOptions()));

        cache.GetBrick("x");
        var stats = cache.GetCacheStats();

        stats.Entries.Should().Be(1); // Now caches all bricks
        stats.EvictionCount.Should().Be(0);
    }

    [Fact]
    public void GetBrick_empty_id_returns_null()
    {
        var inner = new StubBrickRegistry();
        var tracker = new BrickUsageTracker(Options.Create(new BrickUsageTrackerOptions()));
        var cache = new AdaptiveBrickCache(inner, tracker, Options.Create(new AdaptiveBrickCacheOptions()));

        cache.GetBrick("").Should().BeNull();
        cache.GetBrick("   ").Should().BeNull();
    }

    [Fact]
    public void GetBrick_caches_bricks_and_reports_hits()
    {
        var inner = new StubBrickRegistry();
        inner.Add(new TestBrick { Id = "cached-1", Name = "Cached1" });

        var tracker = new BrickUsageTracker(Options.Create(new BrickUsageTrackerOptions()));
        var cache = new AdaptiveBrickCache(inner, tracker, Options.Create(new AdaptiveBrickCacheOptions
        {
            ColdBrickTtlSeconds = 60,
            HotBrickTtlSeconds = 120,
        }));

        cache.GetBrick("cached-1").Should().NotBeNull();
        cache.GetBrick("cached-1").Should().NotBeNull();

        var stats = cache.GetCacheStats();
        stats.Entries.Should().Be(1);
        stats.HitRate.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task GetBrick_evicts_expired_entries()
    {
        var inner = new StubBrickRegistry();
        inner.Add(new TestBrick { Id = "expire-1", Name = "Expire1" });

        var tracker = new BrickUsageTracker(Options.Create(new BrickUsageTrackerOptions()));
        var cache = new AdaptiveBrickCache(inner, tracker, Options.Create(new AdaptiveBrickCacheOptions
        {
            ColdBrickTtlSeconds = 0,
            HotBrickTtlSeconds = 0,
        }));

        cache.GetBrick("expire-1");
        await Task.Delay(15);
        cache.GetBrick("expire-1");

        cache.GetCacheStats().EvictionCount.Should().BeGreaterThan(0);
    }

    private sealed class TestBrick : DomainBrick
    {
        public override Task<BrickOutput> ExecuteAsync(BrickInput input, ImplementationType implementation, IExecutionContext context, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
    }
}
