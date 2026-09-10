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

    // Pins the dimension guard in InMemoryVectorStore.SearchAsync, the twin of the one in
    // SqliteVectorStore.SearchAsync. Without it the document below is still scored:
    // VectorMath.CosineSimilarity returns 0 when the two vectors differ in length, and the
    // search admits everything at or above minScore -- so at minScore 0.0 a document written
    // by a differently-dimensioned generator comes back as a score-0.0 hit.
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
}
