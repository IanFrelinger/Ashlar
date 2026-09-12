using FluentAssertions;
using Ashlar.BackgroundAgents.DataSensitivity;
using Ashlar.BackgroundAgents.RAG;
using Xunit;

namespace Ashlar.Tests.BackgroundAgents.RAG;

/// <summary>Tests for in memory vector store.</summary>
public class InMemoryVectorStoreTests
{
    [Fact]
    public async Task IndexAsync_And_SearchAsync_ReturnsMatchingDocument()
    {
        var store = new InMemoryVectorStore();
        var gen = new TokenEmbeddingGenerator(32);
        var emb = await gen.GenerateAsync("hello world", default);
        await store.IndexAsync("doc1", "hello world", emb, null, default);

        var queryEmb = await gen.GenerateAsync("hello world", default);
        var results = await store.SearchAsync(queryEmb, 5, 0.0, null, default);

        results.Should().HaveCount(1);
        results[0].Id.Should().Be("doc1");
        results[0].Text.Should().Be("hello world");
        results[0].Score.Should().BeGreaterThan(0.9);
    }

    [Fact]
    public async Task SearchAsync_WithMinScore_FiltersByScore()
    {
        var store = new InMemoryVectorStore();
        var gen = new TokenEmbeddingGenerator(32);
        await store.IndexAsync("doc1", "alpha beta", await gen.GenerateAsync("alpha beta", default), null, default);
        await store.IndexAsync("doc2", "gamma delta", await gen.GenerateAsync("gamma delta", default), null, default);

        var queryEmb = await gen.GenerateAsync("alpha", default);
        var results = await store.SearchAsync(queryEmb, 5, 0.99, null, default);

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task RemoveAsync_RemovesDocument()
    {
        var store = new InMemoryVectorStore();
        var gen = new TokenEmbeddingGenerator(32);
        await store.IndexAsync("doc1", "text", await gen.GenerateAsync("text", default), null, default);
        await store.RemoveAsync("doc1", default);

        var results = await store.SearchAsync(await gen.GenerateAsync("text", default), 5, 0.0, null, default);
        results.Should().BeEmpty();
    }

    [Fact]
    public async Task ClearAsync_RemovesAll()
    {
        var store = new InMemoryVectorStore();
        var gen = new TokenEmbeddingGenerator(32);
        await store.IndexAsync("doc1", "a", await gen.GenerateAsync("a", default), null, default);
        await store.ClearAsync(default);

        var results = await store.SearchAsync(await gen.GenerateAsync("a", default), 5, 0.0, null, default);
        results.Should().BeEmpty();
    }

    [Fact]
    public async Task SearchAsync_WithMaxSensitivityLevel_FiltersBySensitivity()
    {
        var registry = new DataSensitivityRegistry();
        var store = new InMemoryVectorStore(registry);
        var gen = new TokenEmbeddingGenerator(32);
        await store.IndexAsync("public-doc", "public text", await gen.GenerateAsync("public text", default), "Public", default);
        await store.IndexAsync("confidential-doc", "confidential text", await gen.GenerateAsync("confidential text", default), "Confidential", default);

        var queryEmb = await gen.GenerateAsync("text", default);
        var results = await store.SearchAsync(queryEmb, 5, 0.0, "Internal", default);

        results.Should().HaveCount(1);
        results[0].Id.Should().Be("public-doc");
    }

    // Pins the dimension guard, the twin of the one in SqliteVectorStore.SearchAsync. Without it
    // the document below is still scored at 0.0 and the search admits everything at or above
    // minScore -- so at minScore 0.0 a document written by a differently-dimensioned generator
    // comes back as a score-0.0 hit.
    //
    // The guard moved. It used to be an inline length compare in this method; it is now the
    // first clause of VectorMath.TryCosineSimilarity, so that one call decides comparability for
    // both stores. The invariant this test asserts is unchanged and it still has teeth -- revert
    // that clause and this fails with one result -- but the mutation that breaks it is there,
    // not here.
    [Fact]
    public async Task SearchAsync_DocumentOfDifferentDimension_IsSkippedNotReturnedAtScoreZero()
    {
        var store = new InMemoryVectorStore();

        var indexedGen = new TokenEmbeddingGenerator(16);
        await store.IndexAsync(
            "stale-doc",
            "hello world",
            await indexedGen.GenerateAsync("hello world", default),
            null,
            default);

        var queryGen = new TokenEmbeddingGenerator(32);
        var queryEmb = await queryGen.GenerateAsync("hello world", default);
        var results = await store.SearchAsync(queryEmb, 5, 0.0, null, default);

        results.Should().BeEmpty();
    }

    // A query nobody can rank is refused, not answered with the whole corpus.
    //
    // TokenEmbeddingGenerator returns an all-zero vector for any text it finds no tokens in, so
    // "!!!" is a zero-magnitude query -- and a zero-magnitude query scores exactly 0.0 against
    // every document, which the filter `score >= minScore` admits at the common minScore of 0.0.
    // Measured before the fix: three indexed documents, query "!!!", three hits at 0.0000.
    //
    // The assertion is a refusal, so it carries its own positive control: a real query against
    // the same store must still return its documents. Without that, a store that returned
    // nothing for everything -- or threw for everything -- would pass.
    [Fact]
    public async Task SearchAsync_ZeroNormQuery_IsRefused_WhileRealQueriesStillAnswer()
    {
        var store = new InMemoryVectorStore();
        var gen = new TokenEmbeddingGenerator(64);
        foreach (var (id, text) in new[] { ("a", "alpha beta gamma"), ("b", "gamma delta epsilon"), ("c", "zeta eta theta") })
            await store.IndexAsync(id, text, await gen.GenerateAsync(text, default), null, default);

        var unrankable = await gen.GenerateAsync("!!!", default);
        var refuse = async () => await store.SearchAsync(unrankable, 5, 0.0, null, default);

        (await refuse.Should().ThrowAsync<ArgumentException>())
            .WithMessage("*zero magnitude*")
            .And.Message.Should().Contain(
                "NOT the same as 'nothing matched'",
                "the caller has to be able to tell an unrankable question from an empty answer");

        // POSITIVE CONTROL.
        var real = await store.SearchAsync(await gen.GenerateAsync("alpha beta gamma", default), 5, 0.0, null, default);
        real.Should().NotBeEmpty();
        real[0].Id.Should().Be("a");
    }

    // The float32-underflow twin of the test above. 1e-23f is a normal binary32 value, but the
    // scorer accumulates `a[i] * a[i]` as a float PRODUCT before widening, so it flushes to zero
    // and the vector has no magnitude. Measured before the fix: three hits at 0.0000, exactly as
    // for the all-zero query. A guard testing the input components rather than the accumulation
    // would let this one through.
    [Fact]
    public async Task SearchAsync_Float32UnderflowQuery_IsRefusedToo()
    {
        var store = new InMemoryVectorStore();
        var gen = new TokenEmbeddingGenerator(64);
        await store.IndexAsync("a", "alpha beta gamma", await gen.GenerateAsync("alpha beta gamma", default), null, default);

        var tiny = new float[64];
        Array.Fill(tiny, 1e-23f);

        var refuse = async () => await store.SearchAsync(tiny, 5, 0.0, null, default);
        await refuse.Should().ThrowAsync<ArgumentException>();
    }

    // The mirror case, and the more likely one in production: a stored document with a
    // zero-magnitude embedding matched EVERY query at 0.0. It needs no malformed input -- an
    // empty .log in a knowledge-source directory is enough, because KnowledgeBaseIndexer indexes
    // every text-extension file it finds with no emptiness check.
    //
    // Skipped, not refused: one unrankable row must not take a good query down with it. So this
    // test asserts both halves -- the real document still comes back, the unrankable one does
    // not -- and the first half is the positive control for the second.
    [Fact]
    public async Task SearchAsync_ZeroNormDocument_IsSkippedWhileTheRestOfTheQueryStillAnswers()
    {
        var store = new InMemoryVectorStore();
        var gen = new TokenEmbeddingGenerator(64);
        await store.IndexAsync("real", "gamma delta epsilon", await gen.GenerateAsync("gamma delta epsilon", default), null, default);
        await store.IndexAsync("empty-file.log", "", await gen.GenerateAsync("", default), null, default);

        var results = await store.SearchAsync(await gen.GenerateAsync("gamma delta epsilon", default), 5, 0.0, null, default);

        results.Should().ContainSingle().Which.Id.Should().Be("real");
    }

    // The state that must survive both guards. Two orthogonal documents score exactly 0.0 against
    // each other, and that is a real answer: a fix written as `score > 0` would have thrown this
    // document away along with the undefined ones. Hand-built unit vectors, not generated
    // embeddings, so the orthogonality is exact rather than incidental.
    [Fact]
    public async Task SearchAsync_GenuinelyOrthogonalDocument_IsStillReturnedAtScoreZero()
    {
        var store = new InMemoryVectorStore();
        var doc = new float[4] { 0f, 1f, 0f, 0f };
        var query = new float[4] { 1f, 0f, 0f, 0f };
        await store.IndexAsync("orthogonal", "unrelated but real", doc, null, default);

        var results = await store.SearchAsync(query, 5, 0.0, null, default);

        results.Should().ContainSingle().Which.Score.Should().Be(0d);
    }
}
