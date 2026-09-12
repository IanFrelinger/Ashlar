using FluentAssertions;
using Microsoft.Extensions.VectorData;
using Ashlar.AI.Pipeline.Embeddings;
using Ashlar.AI.Pipeline.Rag;
using Xunit;

namespace Ashlar.Tests.AI.Pipeline;

/// <summary>
/// The third vector store, and the one that matters most at runtime: <c>AshlarKernelRegistrar</c>
/// phase 13b overrides <c>IRAGService</c> with <c>MeaiVectorDataRagAdapter</c> unconditionally, so
/// the shipped CLI and MCP hosts search this collection and not either of the two
/// <c>Ashlar.BackgroundAgents</c> stores. It had both of the defects #582 dealt with elsewhere,
/// and one of them in a worse form.
/// </summary>
public sealed class InProcessChunkCollectionRankabilityTests
{
    private static VectorStoreCollection<string, ChunkRecord> NewCollection(string name) =>
        new InProcessVectorStore().GetCollection<string, ChunkRecord>(name);

    private static async Task<ReadOnlyMemory<float>> Embed(string text, int dimensions = 64)
    {
        var generator = new TokenHashEmbeddingGenerator(dimensions);
        return (await generator.GenerateAsync([text])).First().Vector;
    }

    private static Task Upsert(VectorStoreCollection<string, ChunkRecord> collection, string key, string text, ReadOnlyMemory<float> embedding) =>
        collection.UpsertAsync(
            new ChunkRecord { Key = key, Text = text, TrustTier = "Public", Embedding = embedding },
            default);

    private static async Task<List<(string Key, double Score)>> Search(
        VectorStoreCollection<string, ChunkRecord> collection,
        ReadOnlyMemory<float> query,
        double? threshold = 0.0)
    {
        var options = new VectorSearchOptions<ChunkRecord> { ScoreThreshold = threshold };
        var hits = new List<(string, double)>();
        await foreach (var hit in collection.SearchAsync(query, 10, options, default))
            hits.Add((hit.Record.Key, hit.Score ?? 0d));
        return hits;
    }

    // A query nobody can rank is refused, not answered with the whole collection.
    //
    // The old code did have a zero guard -- `if (na <= double.Epsilon ...) return 0;` -- but
    // returning 0 is not skipping. The caller's filter is `score < threshold`, so at the
    // ScoreThreshold of 0.0 that MeaiVectorDataRagAdapter forwards from the CLI's --min-score
    // default, `0.0 < 0.0` is false and every record survives. Measured before the fix: three
    // records, query "!!!", three hits at 0.0000.
    //
    // Positive control in the same test, because the interesting assertion is a refusal.
    [Fact]
    public async Task SearchAsync_ZeroNormQuery_IsRefused_WhileRealQueriesStillAnswer()
    {
        var collection = NewCollection("refusal");
        await Upsert(collection, "a", "alpha beta gamma", await Embed("alpha beta gamma"));
        await Upsert(collection, "b", "gamma delta epsilon", await Embed("gamma delta epsilon"));

        // TokenHashEmbeddingGenerator splits on a fixed punctuation set and leaves the vector
        // all-zero when no token survives, so this needs no malformed input to reach.
        var unrankable = await Embed("!!!");
        var refuse = async () => await Search(collection, unrankable);

        (await refuse.Should().ThrowAsync<ArgumentException>()).WithMessage("*magnitude is zero*");

        // POSITIVE CONTROL.
        var real = await Search(collection, await Embed("alpha beta gamma"));
        real.Should().NotBeEmpty();
        real[0].Key.Should().Be("a");
    }

    // A record whose embedding has no magnitude matched every query at 0.0. Skipped, not
    // refused -- one unrankable record must not take a good query down with it -- so the
    // surviving record is the positive control.
    [Fact]
    public async Task SearchAsync_ZeroNormRecord_IsSkippedWhileTheRestOfTheQueryStillAnswers()
    {
        var collection = NewCollection("skip");
        await Upsert(collection, "real", "alpha beta gamma", await Embed("alpha beta gamma"));
        await Upsert(collection, "empty", "", await Embed(""));

        var hits = await Search(collection, await Embed("alpha beta gamma"));

        hits.Should().ContainSingle().Which.Key.Should().Be("real");
    }

    // The defect #582 removed from the two BackgroundAgents stores, still present here and in a
    // worse form. The old CosineSimilarity began `var len = Math.Min(a.Length, b.Length)` and
    // scored the shared prefix, so a record of a different dimension produced a CONFIDENT
    // nonzero score from an incomparable pair -- measured, a dim-16 record answered a dim-64
    // query at 0.8165, which outranks most real results rather than merely appearing beneath
    // them. Delete the length clause from TryCosineSimilarity and this test fails with one hit.
    [Fact]
    public async Task SearchAsync_RecordOfDifferentDimension_IsNotScoredOnItsSharedPrefix()
    {
        var collection = NewCollection("dimensions");
        await Upsert(collection, "stale16", "alpha beta gamma", await Embed("alpha beta gamma", dimensions: 16));

        var hits = await Search(collection, await Embed("alpha beta gamma", dimensions: 64));

        hits.Should().BeEmpty();
    }

    // The state that must survive both guards: two orthogonal vectors have a real similarity of
    // exactly 0.0, and at a ScoreThreshold of 0.0 that record is a legitimate result. A fix
    // written as `score > 0` would have discarded it along with the undefined ones. Hand-built
    // unit vectors so the orthogonality is exact.
    [Fact]
    public async Task SearchAsync_GenuinelyOrthogonalRecord_IsStillReturnedAtScoreZero()
    {
        var collection = NewCollection("orthogonal");
        await Upsert(collection, "orthogonal", "unrelated but real", new float[] { 0f, 1f, 0f, 0f });

        var hits = await Search(collection, new float[] { 1f, 0f, 0f, 0f });

        hits.Should().ContainSingle().Which.Score.Should().Be(0d);
    }

    private static float[] Filled(int dim, float value)
    {
        var v = new float[dim];
        Array.Fill(v, value);
        return v;
    }

    // The other end of the accumulator the zero guard watches. `na += a[i] * a[i]` rounds the
    // PRODUCT to binary32, so it saturates to +infinity above ~1.84e19 and is NaN if any component
    // is -- and both pass `na == 0`. Measured on net10.0/Linux/x64 with only the zero guard in
    // place, two records in the collection, ScoreThreshold 0.0: the 1e20f query returned BOTH
    // records at score 0 (finite dot over infinite norm is exactly 0.0), and the NaN query returned
    // BOTH records at score NaN.
    //
    // Positive control in the same test, because the interesting assertion is a refusal.
    [Theory]
    [InlineData(1e20f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public async Task SearchAsync_NonFiniteMagnitudeQuery_IsRefused_WhileRealQueriesStillAnswer(float component)
    {
        var collection = NewCollection("nonfinite-query-" + component);
        await Upsert(collection, "a", "alpha beta gamma", await Embed("alpha beta gamma"));
        await Upsert(collection, "b", "gamma delta epsilon", await Embed("gamma delta epsilon"));

        var refuse = async () => await Search(collection, Filled(64, component));
        (await refuse.Should().ThrowAsync<ArgumentException>()).WithMessage("*cannot be ranked*");

        // POSITIVE CONTROL.
        var real = await Search(collection, await Embed("alpha beta gamma"));
        real.Should().NotBeEmpty();
        real[0].Key.Should().Be("a");
    }

    // The sharpest of the three, and the one unique to this store: a record whose embedding is NaN
    // defeats EVERY ScoreThreshold a caller can set. The skip here is written `score < threshold`,
    // and `NaN < anything` is false, so the record survives the filter no matter how high it goes.
    // Measured before the fix at thresholds 0.0, 0.5 AND 0.99: the NaN record came back beside the
    // real one every time.
    //
    // The three thresholds are the point of the test, not decoration -- a fix that only moved the
    // comparison around would still pass at one of them. The real record is the positive control:
    // skip, do not refuse, in the record direction.
    [Theory]
    [InlineData(0.0)]
    [InlineData(0.5)]
    [InlineData(0.99)]
    public async Task SearchAsync_NonFiniteRecord_IsSkippedAtEveryThreshold(double threshold)
    {
        var collection = NewCollection("nonfinite-record-" + threshold);
        var unit = new float[64];
        unit[0] = 1f;
        await Upsert(collection, "real", "real document", unit);
        await Upsert(collection, "nan", "malformed", Filled(64, float.NaN));
        await Upsert(collection, "overflow", "malformed", Filled(64, 1e20f));

        var hits = await Search(collection, unit, threshold);

        hits.Should().ContainSingle("only the rankable record can be a hit").Which.Key.Should().Be("real");
    }

    // One step back from the overflow cliff a large query is still answered, so none of the
    // refusals above can be satisfied by rejecting every big vector. 1e19f squares to 1e38, still
    // inside binary32; 1e20f squares to 1e40, which is not.
    [Fact]
    public async Task SearchAsync_LargeButRepresentableQuery_IsStillAnswered()
    {
        var collection = NewCollection("large");
        var unit = new float[64];
        unit[0] = 1f;
        await Upsert(collection, "real", "real document", unit);

        var hits = await Search(collection, Filled(64, 1e19f));

        hits.Should().ContainSingle().Which.Key.Should().Be("real");
    }
}
