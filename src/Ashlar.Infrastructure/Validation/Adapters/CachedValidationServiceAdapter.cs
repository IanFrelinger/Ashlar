using Microsoft.Extensions.Logging;
using Ashlar.Core.Application.Validation.Models;
using Ashlar.Core.Application.Validation.Ports;
using Ashlar.Core.Application.Common.Models;
using Ashlar.Core.Application.Common.Ports;
using Ashlar.Infrastructure.Caching;

namespace Ashlar.Infrastructure.Validation.Adapters;

/// <summary>
/// Decorator for IValidationService that adds caching (Decorator pattern - OCP).
/// 
/// Provides caching layer for validation results:
/// - Generates cache keys from filter + root path + local source/config/fixture contents (excluding generated build/test directories)
/// - Caches results for 15 minutes
/// - Returns cached results when available
/// - Falls through to inner service on cache miss
/// 
/// Implements the Decorator pattern to add caching without modifying the base service.
/// </summary>
public class CachedValidationServiceAdapter : IValidationService
{
    private readonly IValidationService _inner;
    private readonly ICacheStrategy _cache;
    private readonly ILogger<CachedValidationServiceAdapter> _logger;

    /// <summary>Initializes a new cached validation service adapter.</summary>
    public CachedValidationServiceAdapter(
        IValidationService inner,
        ICacheStrategy cache,
        ILogger<CachedValidationServiceAdapter> logger)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Validate asynchronously.</summary>
    public async Task<ValidationResult> ValidateAsync(
        string? filter,
        IProgress<ProgressReport>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var cacheKey = await GenerateCacheKeyAsync(filter, cancellationToken);

        var cachedResult = cacheKey is null ? null : await _cache.GetAsync<ValidationResult>(cacheKey, cancellationToken);
        if (cachedResult != null)
        {
            _logger.LogInformation("Returning cached validation result for filter: {Filter}", filter ?? "none");
            progress?.Report(new ProgressReport
            {
                Percentage = 100,
                Message = "Returning cached validation result",
                Metadata = new Dictionary<string, object> { ["Cached"] = true }
            });
            return cachedResult;
        }

        _logger.LogInformation("Cache miss, running validation with filter: {Filter}", filter ?? "none");
        var result = await _inner.ValidateAsync(filter, progress, cancellationToken);
        // Only reuse a result when local inputs stayed unchanged during validation.
        if (cacheKey is not null && cacheKey == await GenerateCacheKeyAsync(filter, cancellationToken))
            await _cache.SetAsync(cacheKey, result, TimeSpan.FromMinutes(15), cancellationToken);

        return result;
    }

    private static async Task<string?> GenerateCacheKeyAsync(string? filter, CancellationToken cancellationToken)
    {
        var hash = await DirectoryFingerprint.TryComputeAsync(
            new DirectoryInfo(Directory.GetCurrentDirectory()), cancellationToken, excludeBuildOutputs: true).ConfigureAwait(false);
        // JSON preserves the distinction between null and the literal filter "none".
        return hash is null ? null : $"validation:v2:{CacheKeyGenerator.ComputeHash(hash + ":" + System.Text.Json.JsonSerializer.Serialize(filter))}";
    }
}
