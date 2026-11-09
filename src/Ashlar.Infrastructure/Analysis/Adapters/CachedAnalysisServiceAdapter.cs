using Microsoft.Extensions.Logging;
using Ashlar.Core.Application.Analysis.Models;
using Ashlar.Core.Application.Analysis.Ports;
using Ashlar.Core.Application.Common.Models;
using Ashlar.Core.Application.Common.Ports;
using Ashlar.Infrastructure.Caching;

namespace Ashlar.Infrastructure.Analysis.Adapters;

/// <summary>
/// Decorator for IAnalysisService that adds caching (Decorator pattern - OCP).
/// 
/// Provides caching layer for analysis results:
/// - Generates cache keys from root path + complete local file contents
/// - Caches results for 30 minutes
/// - Returns cached results when available
/// - Falls through to inner service on cache miss
/// 
/// Implements the Decorator pattern to add caching without modifying the base service.
/// </summary>
public class CachedAnalysisServiceAdapter : IAnalysisService
{
    private readonly IAnalysisService _inner;
    private readonly ICacheStrategy _cache;
    private readonly ILogger<CachedAnalysisServiceAdapter> _logger;

    /// <summary>Initializes a new cached analysis service adapter.</summary>
    public CachedAnalysisServiceAdapter(
        IAnalysisService inner,
        ICacheStrategy cache,
        ILogger<CachedAnalysisServiceAdapter> logger)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Analyze asynchronously.</summary>
    public async Task<AnalysisResult> AnalyzeAsync(
        DirectoryInfo path,
        IProgress<ProgressReport>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // Generate cache key from path + file hash
        var cacheKey = await GenerateCacheKeyAsync(path, cancellationToken);

        // Try to get from cache
        var cachedResult = cacheKey is null ? null : await _cache.GetAsync<AnalysisResult>(cacheKey, cancellationToken);
        if (cachedResult != null)
        {
            _logger.LogInformation("Returning cached analysis result for path: {Path}", path.FullName);
            progress?.Report(new ProgressReport
            {
                Percentage = 100,
                Message = "Returning cached analysis result",
                Metadata = new Dictionary<string, object> { ["Cached"] = true }
            });
            return cachedResult;
        }

        // Cache miss, call inner service
        _logger.LogInformation("Cache miss, analyzing path: {Path}", path.FullName);
        var result = await _inner.AnalyzeAsync(path, progress, cancellationToken);

        // Store in cache (30 minute expiration)
        // Do not store a result under a snapshot that changed while analysis ran.
        if (cacheKey is not null && cacheKey == await GenerateCacheKeyAsync(path, cancellationToken))
            await _cache.SetAsync(cacheKey, result, TimeSpan.FromMinutes(30), cancellationToken);

        return result;
    }

    private static async Task<string?> GenerateCacheKeyAsync(DirectoryInfo path, CancellationToken cancellationToken)
    {
        var hash = await DirectoryFingerprint.TryComputeAsync(path, cancellationToken).ConfigureAwait(false);
        return hash is null ? null : $"analysis:v2:{hash}";
    }
}
