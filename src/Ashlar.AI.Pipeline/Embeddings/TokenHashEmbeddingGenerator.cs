using System.Diagnostics.CodeAnalysis;
using System.Text;
using Microsoft.Extensions.AI;

namespace Ashlar.AI.Pipeline.Embeddings;

/// <summary>
/// Deterministic local embedding generator (token-hash bag-of-words), MEAI-shaped.
/// Does not collide with Ashlar.BackgroundAgents.RAG.IEmbeddingGenerator.
/// </summary>
/// <remarks>
/// <para>
/// Deterministic means <em>across processes</em>, not merely within one. These vectors are written
/// into a vector store and later scored against a query vector produced by a different process, so
/// a token must land in the same bucket in every run or the whole index becomes noise on restart.
/// </para>
/// <para>
/// The bucket index is therefore derived from a hand-rolled FNV-1a hash rather than from
/// <see cref="string.GetHashCode()"/>, which .NET randomizes per process (Marvin hash, per-process
/// seed). The same defect in the sibling generator in Ashlar.BackgroundAgents.RAG produced a real
/// CI failure where an indexed document could not be found by a search for its own text.
/// </para>
/// </remarks>
public sealed class TokenHashEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    private readonly int _dimensions;

    /// <summary>Creates a generator with the given dimensionality (default 64).</summary>
    public TokenHashEmbeddingGenerator(int dimensions = ChunkRecordDimensions.Default)
    {
        if (dimensions <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(dimensions));
        }

        _dimensions = dimensions;
    }

    /// <inheritdoc />
    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var list = new List<Embedding<float>>();
        foreach (var value in values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            list.Add(new Embedding<float>(Embed(value ?? string.Empty)));
        }

        return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(list));
    }

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType.IsInstanceOfType(this) ? this : null;

    /// <inheritdoc />
    public void Dispose()
    {
    }

    /// <summary>
    /// FNV-1a 32-bit over the UTF-8 bytes of the lower-invariant token.
    /// </summary>
    /// <remarks>
    /// Hand-rolled on purpose: <see cref="string.GetHashCode()"/> is randomized per process and
    /// these bucket indices are compared across processes. Kept unsigned so the bucket is taken
    /// with an unsigned modulo — the previous <c>Math.Abs(hash) % _dimensions</c> also threw
    /// <see cref="OverflowException"/> whenever the hash came back as <see cref="int.MinValue"/>.
    /// Mirrors Ashlar.BackgroundAgents.RAG.TokenEmbeddingGenerator.StableTokenHash; the two live in
    /// assemblies with no shared dependency, so the constants are duplicated rather than referenced.
    /// </remarks>
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

    private float[] Embed(string text)
    {
        var vector = new float[_dimensions];
        var tokens = text.Split([' ', '\t', '\r', '\n', ',', '.', ';', ':', '!', '?'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var token in tokens)
        {
            var index = (int)(StableTokenHash(token) % (uint)_dimensions);
            vector[index] += 1f;
        }

        // L2 normalize
        double sumSq = 0;
        for (var i = 0; i < vector.Length; i++)
        {
            sumSq += vector[i] * vector[i];
        }

        if (sumSq > double.Epsilon)
        {
            var norm = (float)Math.Sqrt(sumSq);
            for (var i = 0; i < vector.Length; i++)
            {
                vector[i] /= norm;
            }
        }

        return vector;
    }
}

/// <summary>Shared embedding dimension constant to avoid circular refs with Rag namespace.</summary>
internal static class ChunkRecordDimensions
{
    public const int Default = 64;
}
