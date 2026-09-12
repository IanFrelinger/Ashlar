using System.Security.Cryptography;
using System.Text;

namespace Ashlar.Infrastructure.Caching;

/// <summary>
/// Shared utility for generating cache keys from content.
/// Used by CachedValidationServiceAdapter and CachedAnalysisServiceAdapter.
/// </summary>
public static class CacheKeyGenerator
{
    /// <summary>
    /// Computes a SHA256 hash of the content, returned as Base64.
    /// </summary>
    /// <remarks>
    /// Prefer <see cref="ComputeHash"/> on a synchronous path. This overload exists for callers
    /// that were already async; the hash itself is a handful of microseconds, and wrapping it in
    /// <c>Task.Run</c> buys a thread-pool hop rather than any parallelism.
    /// </remarks>
    public static Task<string> ComputeHashAsync(string content, CancellationToken cancellationToken = default)
        => Task.Run(() => ComputeHash(content), cancellationToken);

    /// <summary>
    /// Computes a SHA256 hash of the content, returned as Base64. Synchronous: one algorithm,
    /// shared with <see cref="ComputeHashAsync"/>, so the two can never disagree about a key.
    /// </summary>
    public static string ComputeHash(string content)
        => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(content ?? string.Empty)));
}
