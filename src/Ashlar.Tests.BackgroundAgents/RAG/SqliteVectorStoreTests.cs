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
    // still scored at 0.0, and the search admits everything at or above minScore -- so at
    // minScore 0.0 a row left behind by a differently-dimensioned generator comes back as a
    // score-0.0 hit.
    //
    // The guard moved. It used to be a blob-length compare in this store; it is now the first
    // clause of VectorMath.TryCosineSimilarity, so one call decides comparability for both
    // stores. Keeping both was tried and rejected: with two guards this test passed with either
    // one reverted, which is a test that asserts nothing about either. Reverting that clause is
    // now the mutation that makes this fail.
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

    // The twin of InMemoryVectorStoreTests.SearchAsync_ZeroNormQuery_IsRefused..., here because
    // the two stores must agree: a query nobody can rank is refused, not answered with the whole
    // table. Measured before the fix: three rows, query "!!!", three hits at 0.0000.
    // Positive control in the same test, because the interesting assertion is a refusal.
    [Fact]
    public async Task SearchAsync_ZeroNormQuery_IsRefused_WhileRealQueriesStillAnswer()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rag_test_{Guid.NewGuid():N}.db");
        await using var store = new SqliteVectorStore(path);
        var gen = new TokenEmbeddingGenerator(64);
        foreach (var (id, text) in new[] { ("a", "alpha beta gamma"), ("b", "gamma delta epsilon") })
            await store.IndexAsync(id, text, await gen.GenerateAsync(text, default), null, default);

        var unrankable = await gen.GenerateAsync("!!!", default);
        var refuse = async () => await store.SearchAsync(unrankable, 5, 0.0, null, default);

        (await refuse.Should().ThrowAsync<ArgumentException>()).WithMessage("*magnitude is zero*");

        // POSITIVE CONTROL.
        var real = await store.SearchAsync(await gen.GenerateAsync("alpha beta gamma", default), 5, 0.0, null, default);
        real.Should().NotBeEmpty();
        real[0].Id.Should().Be("a");
    }

    // A row whose embedding has no magnitude matched every query at 0.0. Skipped, not refused --
    // one bad row must not poison a good query -- so the surviving row is the positive control.
    [Fact]
    public async Task SearchAsync_ZeroNormRow_IsSkippedWhileTheRestOfTheQueryStillAnswers()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rag_test_{Guid.NewGuid():N}.db");
        await using var store = new SqliteVectorStore(path);
        var gen = new TokenEmbeddingGenerator(64);
        await store.IndexAsync("real", "alpha beta gamma", await gen.GenerateAsync("alpha beta gamma", default), null, default);
        await store.IndexAsync("empty", "", await gen.GenerateAsync("", default), null, default);

        var results = await store.SearchAsync(await gen.GenerateAsync("alpha beta gamma", default), 5, 0.0, null, default);

        results.Should().ContainSingle().Which.Id.Should().Be("real");
    }

    // DisposeAsync used to take the lock, set `_initialized = true`, and release it. It disposed
    // nothing, so a store kept working after `await using` had ended -- measured: SearchAsync on
    // a disposed store returned 1 hit and threw nothing. Disposal was unobservable, which is why
    // nobody noticed it was a no-op.
    [Fact]
    public async Task DisposeAsync_MakesFurtherUseAnError()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rag_test_{Guid.NewGuid():N}.db");
        var store = new SqliteVectorStore(path);
        var gen = new TokenEmbeddingGenerator(32);
        var emb = await gen.GenerateAsync("hello sqlite", default);
        await store.IndexAsync("doc1", "hello sqlite", emb, null, default);

        // POSITIVE CONTROL: the store works before disposal, so what follows is about disposal
        // and not about a store that never worked.
        (await store.SearchAsync(emb, 5, 0.0, null, default)).Should().ContainSingle();

        await store.DisposeAsync();

        var search = async () => await store.SearchAsync(emb, 5, 0.0, null, default);
        await search.Should().ThrowAsync<ObjectDisposedException>();

        var index = async () => await store.IndexAsync("doc2", "more", emb, null, default);
        await index.Should().ThrowAsync<ObjectDisposedException>();

        var count = async () => await store.GetDocumentCountAsync(default);
        await count.Should().ThrowAsync<ObjectDisposedException>();
    }

    // `_initialized = true` on the way out was the one value that could break the instance.
    // _initialized is the memo for schema creation, so setting it during dispose claimed CREATE
    // TABLE had run when it had not. Measured before the fix: construct, DisposeAsync, then
    // IndexAsync died with SqliteException "SQLite Error 1: 'no such table: rag_vectors'" -- a
    // SQL error blaming the schema for a use-after-dispose.
    //
    // This asserts the error names the actual mistake. On the old code it fails not because
    // nothing is thrown, but because the wrong thing is.
    [Fact]
    public async Task DisposeAsync_BeforeFirstUse_ReportsUseAfterDispose_NotAMissingTable()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rag_test_{Guid.NewGuid():N}.db");
        var store = new SqliteVectorStore(path);
        await store.DisposeAsync();

        var gen = new TokenEmbeddingGenerator(32);
        var emb = await gen.GenerateAsync("hello sqlite", default);

        var index = async () => await store.IndexAsync("doc1", "hello sqlite", emb, null, default);

        var thrown = await index.Should().ThrowAsync<ObjectDisposedException>();
        thrown.And.Message.Should().NotContain(
            "rag_vectors",
            "a use-after-dispose must not surface as a complaint about the schema");
    }

    [Fact]
    public async Task DisposeAsync_IsIdempotent()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rag_test_{Guid.NewGuid():N}.db");
        var store = new SqliteVectorStore(path);
        await store.IndexAsync("doc1", "t", await new TokenEmbeddingGenerator(32).GenerateAsync("t", default), null, default);

        await store.DisposeAsync();

        var again = async () => await store.DisposeAsync();
        await again.Should().NotThrowAsync("a second DisposeAsync is a no-op, not a crash");
    }

    // The resource the old DisposeAsync leaked was not a connection field -- this class holds
    // none, every method opens and disposes its own. What survived was the Microsoft.Data.Sqlite
    // POOL entry for the connection string and, through it, an OS handle on the .db file.
    // Measured by walking /proc/self/fd after `await using` exited: 3 descriptors on the
    // database before ClearPool, 0 after.
    //
    // Asserting on the handle is not portable, and the visible Windows symptom (File.Delete
    // refused) cannot be reproduced on the Linux lane these tests run in -- a test that cannot
    // fail where it runs is not a test. So this observes the POOL, which behaves the same on
    // both: with the file unlinked, a pooled connection still reads the old inode and reports
    // the row, while a fresh connection creates a new empty database and reports none. Delete
    // the ClearPool call in DisposeAsync and this fails with a count of 1.
    [Fact]
    public async Task DisposeAsync_DropsThePooledConnection_SoALaterStoreOpensTheFileAfresh()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rag_test_{Guid.NewGuid():N}.db");
        var gen = new TokenEmbeddingGenerator(32);

        var first = new SqliteVectorStore(path);
        await first.IndexAsync("doc1", "hello sqlite", await gen.GenerateAsync("hello sqlite", default), null, default);
        (await first.GetDocumentCountAsync(default)).Should().Be(1, "positive control: the row was written");
        await first.DisposeAsync();

        // Remove the database from underneath. A connection still held in the pool keeps reading
        // the unlinked inode; a connection opened fresh creates a new, empty file.
        File.Delete(path);

        await using var second = new SqliteVectorStore(path);
        (await second.GetDocumentCountAsync(default)).Should().Be(
            0,
            "the first store's pooled connection must not survive its disposal and serve the deleted database");
    }

    private static float[] Filled(int dim, float value)
    {
        var v = new float[dim];
        Array.Fill(v, value);
        return v;
    }

    // The twin of InMemoryVectorStoreTests.SearchAsync_OverflowingQuery_... and ...NaNQuery_...,
    // here because the two stores have to agree about which queries they can rank. Measured before
    // the finiteness clause, on net10.0/Linux/x64 against a two-row table: the 1e20f query returned
    // BOTH rows at score 0 (a finite dot over an infinite norm is exactly 0.0, which
    // `score >= minScore` admits at minScore 0.0), and the NaN query returned zero rows and threw
    // nothing (`NaN >= minScore` is false, so the score filter silently ate the table) -- an answer
    // indistinguishable from an empty corpus.
    //
    // This store has a second route to a non-finite embedding that the in-memory one does not:
    // BlobToFloatArray reinterprets the stored bytes with MemoryMarshal and validates nothing, so
    // 0xFFFFFFFF in the blob decodes to NaN.
    [Theory]
    [InlineData(1e20f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public async Task SearchAsync_NonFiniteMagnitudeQuery_IsRefused_WhileRealQueriesStillAnswer(float component)
    {
        var path = Path.Combine(Path.GetTempPath(), $"rag_test_{Guid.NewGuid():N}.db");
        await using var store = new SqliteVectorStore(path);
        var gen = new TokenEmbeddingGenerator(64);
        foreach (var (id, text) in new[] { ("a", "alpha beta gamma"), ("b", "gamma delta epsilon") })
            await store.IndexAsync(id, text, await gen.GenerateAsync(text, default), null, default);

        var refuse = async () => await store.SearchAsync(Filled(64, component), 5, 0.0, null, default);
        await refuse.Should().ThrowAsync<ArgumentException>();

        // POSITIVE CONTROL: the table is populated, so neither "every row at 0" nor "no rows at
        // all" was ever the honest answer to the query above.
        var real = await store.SearchAsync(await gen.GenerateAsync("alpha beta gamma", default), 5, 0.0, null, default);
        real.Should().NotBeEmpty();
        real[0].Id.Should().Be("a");
    }

    // The row direction: skipped, not refused, exactly as a zero-magnitude row is. One malformed
    // blob must not take a good query down with it, and the surviving row is the control.
    [Fact]
    public async Task SearchAsync_NonFiniteRow_IsSkippedWhileTheRestOfTheQueryStillAnswers()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rag_test_{Guid.NewGuid():N}.db");
        await using var store = new SqliteVectorStore(path);
        var gen = new TokenEmbeddingGenerator(64);
        await store.IndexAsync("real", "alpha beta gamma", await gen.GenerateAsync("alpha beta gamma", default), null, default);
        await store.IndexAsync("nan", "malformed", Filled(64, float.NaN), null, default);
        await store.IndexAsync("overflow", "malformed", Filled(64, 1e20f), null, default);

        var results = await store.SearchAsync(await gen.GenerateAsync("alpha beta gamma", default), 5, 0.0, null, default);

        results.Should().ContainSingle().Which.Id.Should().Be("real");
    }

    // One step back from the overflow cliff a large query is still answered, so none of the
    // refusals above can be satisfied by rejecting every big vector. 1e19f squares to 1e38, which
    // is still inside binary32; 1e20f squares to 1e40, which is not.
    [Fact]
    public async Task SearchAsync_LargeButRepresentableQuery_IsStillAnswered()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rag_test_{Guid.NewGuid():N}.db");
        await using var store = new SqliteVectorStore(path);
        var unit = new float[64];
        unit[0] = 1f;
        await store.IndexAsync("real", "real document", unit, null, default);

        var results = await store.SearchAsync(Filled(64, 1e19f), 5, 0.0, null, default);

        results.Should().ContainSingle().Which.Id.Should().Be("real");
    }
}
