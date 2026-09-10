using FluentAssertions;
using Ashlar.BackgroundAgents.RAG;
using Xunit;

namespace Ashlar.Tests.BackgroundAgents.RAG;

/// <summary>Tests for token embedding generator.</summary>
public class TokenEmbeddingGeneratorTests
{
    [Fact]
    public async Task GenerateAsync_ReturnsVectorOfCorrectDimension()
    {
        var gen = new TokenEmbeddingGenerator(64);
        var v = await gen.GenerateAsync("hello world", default);
        v.Should().HaveCount(64);
    }

    [Fact]
    public async Task GenerateAsync_SameText_ReturnsSameVector()
    {
        var gen = new TokenEmbeddingGenerator(32);
        var a = await gen.GenerateAsync("deterministic", default);
        var b = await gen.GenerateAsync("deterministic", default);
        a.Should().Equal(b);
    }

    [Fact]
    public async Task GenerateAsync_EmptyString_ReturnsZeroVector()
    {
        var gen = new TokenEmbeddingGenerator(32);
        var v = await gen.GenerateAsync("", default);
        v.Should().HaveCount(32);
        v.Should().OnlyContain(x => x == 0f);
    }

    [Fact]
    public async Task GenerateAsync_SimilarText_HigherSimilarityThanDifferent()
    {
        var gen = new TokenEmbeddingGenerator(64);
        var similar = await gen.GenerateAsync("machine learning", default);
        var same = await gen.GenerateAsync("machine learning", default);
        var different = await gen.GenerateAsync("potato salad", default);
        var simSame = VectorMath.CosineSimilarity(similar.AsSpan(), same.AsSpan());
        var simDiff = VectorMath.CosineSimilarity(similar.AsSpan(), different.AsSpan());
        simSame.Should().BeGreaterThan(simDiff);
    }

    // ---------------------------------------------------------------------------------------
    // Cross-process determinism.
    //
    // The contract these tests defend is that a token maps to the same vector in EVERY process,
    // because SqliteVectorStore persists these vectors and a later process scores its query
    // against them. A single in-process assertion cannot see a per-process hash seed at all:
    // GenerateAsync_SameText_ReturnsSameVector below passed happily while the seed came from
    // StringComparer.OrdinalIgnoreCase.GetHashCode, which .NET randomizes per process.
    //
    // The golden constants are therefore the cross-process assertion. They were produced by a
    // different process than the one running the test and checked into source, so reintroducing
    // any per-process seed fails them on the next run. (A test that literally spawns a child
    // process was considered and rejected: this is an xunit v2 assembly, so it is not runnable on
    // its own, and shelling out to `dotnet` to build a helper would make a unit test depend on an
    // SDK being installed. The golden vector gets the same guarantee for free.)
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void StableTokenHash_KnownTokens_MatchPinnedConstants()
    {
        // FNV-1a 32-bit over the UTF-8 bytes of the lower-invariant token. These are fixed by the
        // algorithm's constants, not by anything about this process.
        TokenEmbeddingGenerator.StableTokenHash("rag").Should().Be(1409231905u);
        TokenEmbeddingGenerator.StableTokenHash("deterministic").Should().Be(2793360781u);

        // Casing is folded inside the hash, so it cannot depend on how the caller spelled the token.
        TokenEmbeddingGenerator.StableTokenHash("RAG").Should().Be(1409231905u);
    }

    [Fact]
    public async Task GenerateAsync_KnownToken_MatchesGoldenVector()
    {
        var gen = new TokenEmbeddingGenerator(32);
        var v = await gen.GenerateAsync("rag", default);

        v.Take(6).Should().Equal(
            0.12551282f,
            0.19945352f,
            -0.09320259f,
            0.09009582f,
            0.026718063f,
            -0.2789864f);
    }

    [Fact]
    public async Task GenerateAsync_TwoIndependentGenerators_AgreeOnColdCaches()
    {
        // Separate instances mean separate token caches, so agreement here is produced by the hash
        // rather than by a memoized value. Same process, so this alone is not the cross-process
        // proof -- it rules out the cache as the explanation for the golden vector above.
        var first = new TokenEmbeddingGenerator(32);
        var second = new TokenEmbeddingGenerator(32);

        var a = await first.GenerateAsync("ashlar background agents support rag", default);
        var b = await second.GenerateAsync("ashlar background agents support rag", default);

        a.Should().Equal(b);
    }

    [Fact]
    public async Task GenerateAsync_IgnoresCasing()
    {
        var gen = new TokenEmbeddingGenerator(32);
        var lower = await gen.GenerateAsync("rag", default);
        var upper = await new TokenEmbeddingGenerator(32).GenerateAsync("RAG", default);

        upper.Should().Equal(lower);
    }

    [Fact]
    public async Task GenerateAsync_CiFailingScenario_ScoresAFixedPositiveSimilarity()
    {
        // The exact pairing that failed in CI (Full Platform Readiness Gate run 34431870631):
        // KnowledgeBaseIndexerTests indexes this document at dimension 32 and searches for "RAG"
        // with minScore 0.0. Under the randomized seed this score was a random variable that went
        // negative roughly once in 570 process launches; it is now a constant.
        var gen = new TokenEmbeddingGenerator(32);
        var doc = await gen.GenerateAsync("Ashlar background agents support RAG.", default);
        var query = await gen.GenerateAsync("RAG", default);

        var score = VectorMath.CosineSimilarity(query.AsSpan(), doc.AsSpan());

        score.Should().BeApproximately(0.3696399, 1e-6);
    }
}
