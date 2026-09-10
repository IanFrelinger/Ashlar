using FluentAssertions;
using Ashlar.BackgroundAgents.RAG;
using Xunit;

namespace Ashlar.Tests.BackgroundAgents.RAG;

/// <summary>Tests for sqlite vector store.</summary>
public class SqliteVectorStoreTests
{
    [Fact]
    public async Task IndexAsync_And_SearchAsync_ReturnsMatchingDocument()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rag_test_{Guid.NewGuid():N}.db");
        await using var store = new SqliteVectorStore(path);
        var gen = new TokenEmbeddingGenerator(32);
        var emb = await gen.GenerateAsync("hello sqlite", default);
        await store.IndexAsync("doc1", "hello sqlite", emb, null, default);

        var queryEmb = await gen.GenerateAsync("hello sqlite", default);
        var results = await store.SearchAsync(queryEmb, 5, 0.0, null, default);

        results.Should().HaveCount(1);
        results[0].Id.Should().Be("doc1");
        results[0].Text.Should().Be("hello sqlite");
    }

    [Fact]
    public async Task RemoveAsync_RemovesDocument()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rag_test_{Guid.NewGuid():N}.db");
        await using var store = new SqliteVectorStore(path);
        var gen = new TokenEmbeddingGenerator(32);
        await store.IndexAsync("doc1", "text", await gen.GenerateAsync("text", default), null, default);
        await store.RemoveAsync("doc1", default);

        var results = await store.SearchAsync(await gen.GenerateAsync("text", default), 5, 0.0, null, default);
        results.Should().BeEmpty();
    }

    // Pins the dimension guard in SqliteVectorStore.SearchAsync. Without it the row below is
    // still scored: VectorMath.CosineSimilarity returns 0 when the two vectors differ in
    // length, and the search admits everything at or above minScore -- so at minScore 0.0 a
    // row left behind by a differently-dimensioned generator comes back as a score-0.0 hit.
    // Delete the two guard lines at SqliteVectorStore.cs and this test fails with one result.
    [Fact]
    public async Task SearchAsync_RowOfDifferentDimension_IsSkippedNotReturnedAtScoreZero()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rag_test_{Guid.NewGuid():N}.db");
        await using var store = new SqliteVectorStore(path);

        var indexedGen = new TokenEmbeddingGenerator(16);
        await store.IndexAsync(
            "stale-doc",
            "hello sqlite",
            await indexedGen.GenerateAsync("hello sqlite", default),
            null,
            default);

        var queryGen = new TokenEmbeddingGenerator(32);
        var queryEmb = await queryGen.GenerateAsync("hello sqlite", default);
        var results = await store.SearchAsync(queryEmb, 5, 0.0, null, default);

        results.Should().BeEmpty();
    }
}
