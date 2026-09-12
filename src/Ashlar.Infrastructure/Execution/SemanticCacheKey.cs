using System.Text.Json;
using Ashlar.Core.Domain.Bricks;
using Ashlar.Core.Domain.Execution;
using Ashlar.Infrastructure.Caching;

namespace Ashlar.Infrastructure.Execution;

/// <summary>
/// The one place a semantic-cache key is derived, shared by <see cref="BehaviorExecutor"/> and
/// <see cref="ClusterExecutor"/>.
/// </summary>
/// <remarks>
/// <para><b>Why a shared helper and not two corrected copies.</b> The two executors carried
/// byte-identical private <c>ComputeCacheKey</c> methods, and they had drifted into being wrong in
/// the same three ways at once. Fixing them separately is how a third copy gets written.</para>
///
/// <para><b>What was wrong, in order of how much it costs.</b></para>
///
/// <para>1. <i>A 32-bit key returns another input's output.</i> The old key ended in a bare
/// <c>int</c> from <c>string.GetHashCode()</c>. <c>ISemanticCache</c> is keyed by string and its
/// only implementation is an unbounded <c>Dictionary</c> that never evicts, so two different
/// inputs to the same brick and implementation collide at roughly 65k distinct inputs by the
/// birthday bound — and a collision does not miss. <c>GetAsync</c> returns the OTHER input's
/// <c>BrickOutput</c> as a hit. That is a wrong answer, not a slow one, and it needs no second
/// process to happen. SHA-256 is used rather than a wider non-cryptographic hash because
/// collision resistance is the property being bought.</para>
///
/// <para>2. <i>Insertion order changed the key.</i> <c>BrickInput.ToDictionary()</c> hands back the
/// live insertion-ordered <c>Dictionary</c>, so the same logical input assembled by calling
/// <c>Set</c> in a different order serialized differently and missed. Measured inside a single
/// process: <c>{path, mode}</c> hashed 1350354404 while <c>{mode, path}</c> hashed 453566733. The
/// projection below sorts by key with <see cref="StringComparer.Ordinal"/> — ordinal, not culture,
/// so the ordering does not move with the thread's locale.</para>
///
/// <para>3. <i>The key was not stable across processes.</i> <c>string.GetHashCode()</c> is
/// randomized per process (Marvin, seeded at startup). Measured over five launches of the same
/// binary on the same input: 1958484537, 332788011, -1947922124, 1130353100, -583010558. This one
/// costs nothing today, because the only <c>ISemanticCache</c> in the repository is in-process and
/// dies with it — but the registration is <c>TryAddSingleton</c> precisely so a consumer can
/// substitute their own, and a Redis- or disk-backed cache that silently never hits is the quiet
/// failure this repository has been paying down. It is the cheapest of the three to fix and it is
/// fixed by the same line.</para>
/// </remarks>
internal static class SemanticCacheKey
{
    /// <summary>
    /// Derives the cache key for one brick execution.
    /// </summary>
    /// <param name="brickId">Id of the brick being executed.</param>
    /// <param name="input">Input parameters for the brick.</param>
    /// <param name="implementation">Implementation type being used.</param>
    /// <returns>
    /// A key that depends only on the arguments, not on insertion order and not on which process
    /// computed it.
    /// </returns>
    public static string For(string brickId, BrickInput input, ImplementationType implementation)
    {
        // An ordered projection, not the live dictionary: see remark 2 above. Values are
        // serialized as-is, which means a value type without a stable JSON shape still produces
        // an unstable key -- the inputs this cache sees are scalars and strings, and widening
        // that contract is a change that belongs with a test, not a silent one.
        var ordered = input.ToDictionary()
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        var inputHash = CacheKeyGenerator.ComputeHash(JsonSerializer.Serialize(ordered));
        return $"{brickId}:{implementation}:{inputHash}";
    }
}
