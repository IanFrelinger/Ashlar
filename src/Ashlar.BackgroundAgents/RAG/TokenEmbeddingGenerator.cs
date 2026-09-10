using System.Collections.Concurrent;
using System.Text;
using Ashlar.Core.Domain;

namespace Ashlar.BackgroundAgents.RAG;

/// <summary>
/// Simple token-based embedding generator (fallback when no external embedding API).
/// Produces deterministic vectors from word tokens for approximate similarity search.
/// </summary>
/// <remarks>
/// <para>
/// Determinism here means <em>cross-process</em> determinism: the same token yields the same
/// vector in every process, on every machine, forever. That is a hard requirement rather than a
/// nicety, because <see cref="SqliteVectorStore"/> persists these vectors as raw float32 blobs and
/// a later process compares its freshly generated query vector against them. A vector basis that
/// changed between runs would turn every persisted corpus into noise the moment the writing
/// process exited.
/// </para>
/// <para>
/// This is why the per-token seed is hashed by hand with FNV-1a rather than with
/// <see cref="string.GetHashCode()"/> or <see cref="StringComparer.OrdinalIgnoreCase"/>: .NET
/// randomizes string hashing per process (Marvin hash, per-process seed), so a seed taken from
/// those APIs is stable only for the lifetime of one process. Using one here produced a real CI
/// failure — indexing reported a document, and the search that followed scored it below zero and
/// returned nothing.
/// </para>
/// </remarks>
public sealed class TokenEmbeddingGenerator : IEmbeddingGenerator
{
    private const int DefaultDimension = AshlarDefaults.EmbeddingDefaultDimension;
    private readonly int _dimension;
    private readonly ConcurrentDictionary<string, float[]> _tokenCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Dimension of produced vectors.
    /// </summary>
    public int Dimension => _dimension;

    /// <summary>
    /// Initializes a new instance of the <see cref="TokenEmbeddingGenerator"/> class.
    /// </summary>
    /// <param name="dimension">Vector dimension (default 64).</param>
    public TokenEmbeddingGenerator(int dimension = DefaultDimension)
    {
        if (dimension <= 0)
            throw new ArgumentOutOfRangeException(nameof(dimension));
        _dimension = dimension;
    }

    /// <inheritdoc />
    public Task<float[]> GenerateAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(text))
            return Task.FromResult(new float[_dimension]);

        var tokens = Tokenize(text);
        var vector = new float[_dimension];

        foreach (var token in tokens)
        {
            var tokenVector = GetOrCreateTokenVector(token);
            for (var i = 0; i < _dimension; i++)
                vector[i] += tokenVector[i];
        }

        VectorMath.NormalizeInPlace(vector);
        return Task.FromResult(vector);
    }

    private float[] GetOrCreateTokenVector(string token)
    {
        return _tokenCache.GetOrAdd(token, t =>
        {
            var v = new float[_dimension];
            var hash = StableTokenHash(t);
            for (var i = 0; i < _dimension; i++)
            {
                hash = (hash * 31 + (uint)i) ^ (hash >> 13);
                v[i] = (hash % 1000) / 1000f - 0.5f;
            }
            VectorMath.NormalizeInPlace(v);
            return v;
        });
    }

    /// <summary>
    /// FNV-1a 32-bit over the UTF-8 bytes of the lower-invariant token.
    /// </summary>
    /// <remarks>
    /// Hand-rolled on purpose. <see cref="string.GetHashCode()"/> and the
    /// <see cref="StringComparer"/> hash codes are randomized per process, and these vectors are
    /// persisted and compared across processes. FNV-1a is fixed by its constants (offset basis
    /// 2166136261, prime 16777619), so this value is part of the on-disk contract: changing it
    /// invalidates every persisted embedding.
    /// The token is lowercased here so that the vector never depends on the caller's casing, which
    /// keeps it consistent with the case-insensitive token cache.
    /// </remarks>
    /// <param name="token">Token to hash.</param>
    /// <returns>A process-independent 32-bit hash.</returns>
    internal static uint StableTokenHash(string token)
    {
        const uint offsetBasis = 2166136261;
        const uint prime = 16777619;

        var hash = offsetBasis;
        foreach (var b in Encoding.UTF8.GetBytes(token.ToLowerInvariant()))
        {
            hash ^= b;
            hash *= prime;
        }

        return hash;
    }

    private static IEnumerable<string> Tokenize(string text)
    {
        var sb = new StringBuilder();
        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c) || c == '\'')
                sb.Append(char.ToLowerInvariant(c));
            else if (sb.Length > 0)
            {
                yield return sb.ToString();
                sb.Clear();
            }
        }
        if (sb.Length > 0)
            yield return sb.ToString();
    }
}
