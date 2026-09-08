using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ashlar.Commercial.Fleet.Contracts.Networking.Models;
using Ashlar.Commercial.Fleet.Contracts.Networking.Ports;
using Ashlar.Core.Domain.Bricks;
using Ashlar.Core.Domain.Execution;

namespace Ashlar.Commercial.Fleet.Infrastructure.Execution;
/// <summary>
/// Wraps a brick registry with usage-aware caching: hot bricks get longer TTL,
/// cold bricks short TTL, expired entries evicted. Tracks hit rate and evictions.
/// </summary>
public sealed class AdaptiveBrickCache : IAdaptiveBrickCache
{
    private readonly IBrickRegistry _inner;
    private readonly IBrickUsageTracker _usageTracker;
    private readonly AdaptiveBrickCacheOptions _options;
    private readonly ILogger<AdaptiveBrickCache>? _logger;
    private readonly ConcurrentDictionary<string, (DomainBrick Brick, DateTimeOffset ExpiresAt)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private long _hits;
    private long _misses;
    private long _evictionCount;

    public AdaptiveBrickCache(
        IBrickRegistry inner,
        IBrickUsageTracker usageTracker,
        IOptions<AdaptiveBrickCacheOptions> options,
        ILogger<AdaptiveBrickCache>? logger = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _usageTracker = usageTracker ?? throw new ArgumentNullException(nameof(usageTracker));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;
    }

    /// <inheritdoc />
    public DomainBrick? GetBrick(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;

        var now = DateTimeOffset.UtcNow;

        if (_cache.TryGetValue(id, out var entry))
        {
            if (entry.ExpiresAt > now)
            {
                Interlocked.Increment(ref _hits);
                return entry.Brick;
            }
            _cache.TryRemove(id, out _);
            Interlocked.Increment(ref _evictionCount);
        }

        Interlocked.Increment(ref _misses);
        var brick = _inner.GetBrick(id);
        if (brick == null) return null;

        // Cache all bricks (not just remote) with usage-aware TTL
        var ttlSeconds = GetTtlSeconds(id);
        _cache[id] = (brick, now.AddSeconds(ttlSeconds));

        return brick;
    }

    /// <inheritdoc />
    public IReadOnlyList<DomainBrick> GetAllBricks()
    {
        return _inner.GetAllBricks();
    }

    /// <inheritdoc />
    public AdaptiveBrickCacheStats GetCacheStats()
    {
        var total = _hits + _misses;
        var hitRate = total > 0 ? (double)_hits / total : 0;
        return new AdaptiveBrickCacheStats
        {
            HitRate = hitRate,
            Entries = _cache.Count,
            EvictionCount = Interlocked.Read(ref _evictionCount)
        };
    }

    private int GetTtlSeconds(string brickId)
    {
        var hot = _usageTracker.GetHotBricks(_options.HotBrickTopN);
        var isHot = hot.Contains(brickId, StringComparer.OrdinalIgnoreCase);
        return isHot ? _options.HotBrickTtlSeconds : _options.ColdBrickTtlSeconds;
    }
}
