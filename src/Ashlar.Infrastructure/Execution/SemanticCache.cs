using Microsoft.Extensions.Logging;
using Ashlar.Core.Domain.Execution;

namespace Ashlar.Infrastructure.Execution;

/// <summary>Thread-safe, capacity-bounded least-recently-used cache for brick results.</summary>
public class SemanticCache : ISemanticCache
{
    private readonly object _gate = new();
    private readonly Dictionary<string, LinkedListNode<(string Key, BrickOutput Output)>> _cache = new(StringComparer.Ordinal);
    private readonly LinkedList<(string Key, BrickOutput Output)> _recency = new();
    private readonly ILogger<SemanticCache> _logger;
    private readonly int _capacity;

    /// <summary>Initializes a cache retaining at most 1024 results.</summary>
    public SemanticCache(ILogger<SemanticCache> logger) : this(logger, 1024) { }

    /// <summary>Initializes a cache with an explicit positive result capacity.</summary>
    public SemanticCache(ILogger<SemanticCache> logger, int capacity)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    /// <inheritdoc />
    public Task<BrickOutput?> GetAsync(string cacheKey, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BrickOutput? output = null;
        lock (_gate)
        {
            if (_cache.TryGetValue(cacheKey, out var node))
            {
                _recency.Remove(node);
                _recency.AddLast(node);
                output = node.Value.Output;
            }
        }
        _logger.LogDebug("Semantic cache lookup for key {CacheKey}: {Hit}", cacheKey, output is not null);
        return Task.FromResult(output);
    }

    /// <inheritdoc />
    public Task SetAsync(string cacheKey, BrickOutput output, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(output);
        lock (_gate)
        {
            if (_cache.TryGetValue(cacheKey, out var existing))
            {
                _recency.Remove(existing);
                existing.Value = (cacheKey, output);
                _recency.AddLast(existing);
            }
            else
            {
                if (_cache.Count == _capacity)
                {
                    var oldest = _recency.First!;
                    _cache.Remove(oldest.Value.Key);
                    _recency.RemoveFirst();
                }
                _cache.Add(cacheKey, _recency.AddLast((cacheKey, output)));
            }
        }
        _logger.LogDebug("Cached output for key {CacheKey}", cacheKey);
        return Task.CompletedTask;
    }
}
