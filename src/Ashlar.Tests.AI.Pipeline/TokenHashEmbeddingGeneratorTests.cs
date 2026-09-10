using FluentAssertions;
using Microsoft.Extensions.AI;
using Ashlar.AI.Pipeline.Embeddings;
using Xunit;

namespace Ashlar.Tests.AI.Pipeline;

/// <summary>
/// Cross-process determinism for the MEAI token-hash embedding generator.
/// </summary>
/// <remarks>
/// This generator buckets each token by hash, and those bucket indices are written into a vector
/// store and later scored against a query embedded by a different process. It previously bucketed
/// with <c>token.ToLowerInvariant().GetHashCode()</c>, which .NET randomizes per process, so the
/// same token landed in a different bucket on every launch.
///
/// The pinned bucket indices below are the assertion that catches a return to that: they were
/// computed in a different process and checked in, so a per-process seed fails them immediately.
/// An in-process "same input, same output" check cannot see the defect at all.
/// </remarks>
public sealed class TokenHashEmbeddingGeneratorTests
{
    private const int Dimensions = 64;

    private static async Task<float[]> EmbedAsync(IEmbeddingGenerator<string, Embedding<float>> gen, string text)
    {
        var result = await gen.GenerateAsync([text]);
        return result[0].Vector.ToArray();
    }

    [Theory]
    // FNV-1a 32-bit over the UTF-8 bytes of the lower-invariant token, modulo 64.
    [InlineData("wolverine", 8)]
    [InlineData("protocol", 53)]
    [InlineData("alpha", 43)]
    [InlineData("beta", 7)]
    public async Task Single_token_lands_in_a_pinned_bucket(string token, int expectedIndex)
    {
        using var gen = new TokenHashEmbeddingGenerator(Dimensions);

        var vector = await EmbedAsync(gen, token);

        vector.Should().HaveCount(Dimensions);
        vector[expectedIndex].Should().Be(1f);
        vector.Where((_, i) => i != expectedIndex).Should().OnlyContain(x => x == 0f);
    }

    [Fact]
    public async Task Bucket_does_not_depend_on_casing()
    {
        using var gen = new TokenHashEmbeddingGenerator(Dimensions);

        var lower = await EmbedAsync(gen, "wolverine protocol");
        var upper = await EmbedAsync(gen, "WOLVERINE Protocol");

        upper.Should().Equal(lower);
    }

    [Fact]
    public async Task Multi_token_embedding_matches_golden_vector()
    {
        using var gen = new TokenHashEmbeddingGenerator(Dimensions);

        var vector = await EmbedAsync(gen, "alpha wolverine protocol");

        // Three distinct buckets, each hit once, then L2-normalized: 1/sqrt(3).
        var expected = 1f / MathF.Sqrt(3f);
        vector[43].Should().BeApproximately(expected, 1e-6f);
        vector[8].Should().BeApproximately(expected, 1e-6f);
        vector[53].Should().BeApproximately(expected, 1e-6f);
        vector.Where((_, i) => i != 43 && i != 8 && i != 53).Should().OnlyContain(x => x == 0f);
    }

    [Fact]
    public async Task Two_independent_generators_agree()
    {
        // Separate instances hold no shared state, so agreement comes from the hash itself.
        using var first = new TokenHashEmbeddingGenerator(Dimensions);
        using var second = new TokenHashEmbeddingGenerator(Dimensions);

        var a = await EmbedAsync(first, "shared playbook guidance");
        var b = await EmbedAsync(second, "shared playbook guidance");

        a.Should().Equal(b);
    }

    [Fact]
    public async Task Bucket_index_is_always_in_range()
    {
        // The previous implementation took Math.Abs(hash) % dimensions, which throws
        // OverflowException when GetHashCode returns int.MinValue. The unsigned modulo cannot.
        using var gen = new TokenHashEmbeddingGenerator(Dimensions);

        for (var i = 0; i < 5000; i++)
        {
            var vector = await EmbedAsync(gen, $"token{i}");
            vector.Should().HaveCount(Dimensions);
            vector.Should().Contain(x => x == 1f);
        }
    }
}
