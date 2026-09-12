using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;

namespace Ashlar.AI.Pipeline.Rag;

/// <summary>
/// In-process <see cref="VectorStore"/> for local-first RAG (GA Abstractions only; no preview connectors).
/// Keeps the legacy Ashlar store untouched until Phase 6 cutover.
/// </summary>
public sealed class InProcessVectorStore : VectorStore
{
    private readonly ConcurrentDictionary<string, object> _collections =
        new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public override VectorStoreCollection<TKey, TRecord> GetCollection<TKey, TRecord>(
        string name,
        VectorStoreCollectionDefinition? definition = null)
    {
        if (typeof(TKey) != typeof(string) || typeof(TRecord) != typeof(ChunkRecord))
        {
            throw new NotSupportedException("InProcessVectorStore currently supports VectorStoreCollection<string, ChunkRecord> only.");
        }

        var collection = (VectorStoreCollection<TKey, TRecord>)_collections.GetOrAdd(
            name,
            _ => new InProcessChunkCollection(name));
        return collection;
    }

    /// <inheritdoc />
    public override VectorStoreCollection<object, Dictionary<string, object?>> GetDynamicCollection(
        string name,
        VectorStoreCollectionDefinition definition) =>
        throw new NotSupportedException("Dynamic collections are not supported by InProcessVectorStore.");

    /// <inheritdoc />
    public override IAsyncEnumerable<string> ListCollectionNamesAsync(CancellationToken cancellationToken = default) =>
        _collections.Keys.ToAsyncEnumerable();

    /// <inheritdoc />
    public override Task<bool> CollectionExistsAsync(string name, CancellationToken cancellationToken = default) =>
        Task.FromResult(_collections.ContainsKey(name));

    /// <inheritdoc />
    public override Task EnsureCollectionDeletedAsync(string name, CancellationToken cancellationToken = default)
    {
        _collections.TryRemove(name, out _);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType.IsInstanceOfType(this) ? this : null;
}

/// <summary>
/// In-memory cosine-similarity collection for <see cref="ChunkRecord"/>.
/// </summary>
public sealed class InProcessChunkCollection : VectorStoreCollection<string, ChunkRecord>
{
    private readonly string _name;
    private readonly ConcurrentDictionary<string, ChunkRecord> _records =
        new(StringComparer.Ordinal);

    /// <summary>Creates a named chunk collection.</summary>
    public InProcessChunkCollection(string name) =>
        _name = string.IsNullOrWhiteSpace(name) ? "chunks" : name;

    /// <inheritdoc />
    public override string Name => _name;

    /// <inheritdoc />
    public override Task<bool> CollectionExistsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(true);

    /// <inheritdoc />
    public override Task EnsureCollectionExistsAsync(CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    /// <inheritdoc />
    public override Task EnsureCollectionDeletedAsync(CancellationToken cancellationToken = default)
    {
        _records.Clear();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override Task<ChunkRecord?> GetAsync(
        string key,
        RecordRetrievalOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        _records.TryGetValue(key, out var record);
        return Task.FromResult(record);
    }

    /// <inheritdoc />
    public override async IAsyncEnumerable<ChunkRecord> GetAsync(
        Expression<Func<ChunkRecord, bool>> filter,
        int top,
        FilteredRecordRetrievalOptions<ChunkRecord>? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var predicate = filter.Compile();
        var taken = 0;
        foreach (var record in _records.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!predicate(record))
            {
                continue;
            }

            yield return record;
            if (++taken >= top)
            {
                yield break;
            }

            await Task.Yield();
        }
    }

    /// <inheritdoc />
    public override Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        _records.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override Task UpsertAsync(ChunkRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (string.IsNullOrWhiteSpace(record.Key))
        {
            throw new ArgumentException("ChunkRecord.Key is required.", nameof(record));
        }

        record.UpdatedAt = DateTimeOffset.UtcNow;
        _records[record.Key] = Clone(record);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override async Task UpsertAsync(
        IEnumerable<ChunkRecord> records,
        CancellationToken cancellationToken = default)
    {
        foreach (var record in records)
        {
            await UpsertAsync(record, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public override async IAsyncEnumerable<VectorSearchResult<ChunkRecord>> SearchAsync<TInput>(
        TInput searchValue,
        int top,
        VectorSearchOptions<ChunkRecord>? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var query = ToVector(searchValue);

        // Refuse an unrankable query before scanning the collection. A zero-magnitude query has
        // no angle to any record, so it scores 0.0 against all of them -- and the threshold test
        // below is `score < threshold`, so at the ScoreThreshold of 0.0 that
        // MeaiVectorDataRagAdapter forwards from the CLI's `--min-score` default, `0.0 < 0.0` is
        // false and every record is kept. This is the store the shipped hosts actually search
        // (AshlarKernelRegistrar phase 13b overrides IRAGService with MeaiVectorDataRagAdapter
        // unconditionally), so it is the path where a punctuation-only query returned the first
        // `top` chunks of the corpus into an agent's context as retrieved evidence.
        //
        // Zero magnitude is reachable with no malformed input: TokenHashEmbeddingGenerator splits
        // on a fixed punctuation set and leaves the vector all-zero when no token survives, so
        // "", "   ", "!!!" and "..." all produce one.
        //
        // Duplicated rather than shared with Ashlar.BackgroundAgents.RAG.VectorMath: these two
        // assemblies have no common dependency below Ashlar.Abstractions, and #582 already
        // declined to promote a hash primitive into the public abstractions package for the same
        // reason. Keep the two in step by hand; both are pinned by tests.
        if (!IsRankable(query))
        {
            throw new ArgumentException(
                "The query embedding cannot be ranked against anything: its magnitude is zero, not "
                + "a number, or too large to represent, so cosine similarity is undefined for it and "
                + "every record is equally (un)close to it. This is NOT the same as 'nothing "
                + "matched' -- returning an empty result here would be indistinguishable from an "
                + "empty collection. A ZERO magnitude usually means the embedding generator found no "
                + "tokens in the query: an empty string, whitespace, or punctuation only (\"!!!\", "
                + "\"...\"). A NaN or OVERFLOWING magnitude means the embedding itself is malformed "
                + "-- a generator that emitted NaN or components above ~1.8e19. Supply a query whose "
                + "embedding has a finite, nonzero magnitude.",
                nameof(searchValue));
        }

        Func<ChunkRecord, bool>? filter = options?.Filter?.Compile();
        var skip = options?.Skip ?? 0;
        var threshold = options?.ScoreThreshold;

        var scored = new List<(ChunkRecord Record, double Score)>();
        foreach (var record in _records.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (filter is not null && !filter(record))
            {
                continue;
            }

            // Skip a record that cannot be ranked against this query rather than scoring it: a
            // different dimension, or a zero-magnitude embedding (an empty chunk is enough).
            // Skip, not refuse, in this direction -- one unrankable record must not take a good
            // query down with it, which is the opposite of the choice made for the query above.
            if (!TryCosineSimilarity(query, record.Embedding.Span, out var score))
            {
                continue;
            }

            if (threshold is not null && score < threshold.Value)
            {
                continue;
            }

            scored.Add((record, score));
        }

        foreach (var item in scored.OrderByDescending(s => s.Score).Skip(skip).Take(top))
        {
            yield return new VectorSearchResult<ChunkRecord>(Clone(item.Record), item.Score);
            await Task.Yield();
        }
    }

    /// <inheritdoc />
    public override object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType.IsInstanceOfType(this) ? this : null;

    /// <summary>Number of records currently stored.</summary>
    public int Count => _records.Count;

    private static float[] ToVector<TInput>(TInput searchValue)
    {
        return searchValue switch
        {
            ReadOnlyMemory<float> rom => rom.ToArray(),
            float[] arr => arr,
            Embedding<float> emb => emb.Vector.ToArray(),
            _ => throw new NotSupportedException($"Unsupported search input type: {typeof(TInput).Name}"),
        };
    }

    /// <summary>
    /// Cosine similarity, distinguishing "no score exists" from "the score is 0".
    /// </summary>
    /// <returns>
    /// False when the two vectors are empty, differ in length, or either has no usable magnitude
    /// (zero, not a number, or overflowed); true with a usable score otherwise, including a
    /// legitimate 0.0 for orthogonal vectors.
    /// </returns>
    /// <remarks>
    /// <para>Three changes from the version this replaces, each of which was returning a number
    /// where there was no number to return.</para>
    /// <para><b>Length.</b> It began <c>var len = Math.Min(a.Length, b.Length)</c> and scored the
    /// shared prefix, so a stale record of a different dimension produced a confident nonzero
    /// score from an incomparable pair — measured, a dim-16 record answered a dim-64 query at
    /// 0.8165. That is worse than the score-0.0 phantom hit #582 removed from the two
    /// BackgroundAgents stores, because it outranks real results. Unequal lengths are now
    /// unrankable.</para>
    /// <para><b>Zero magnitude.</b> <c>return 0</c> for a zero-norm vector is not a skip: the
    /// caller's threshold test is <c>score &lt; threshold</c>, so 0.0 survives a ScoreThreshold of
    /// 0.0. Saying so out of band is the only way the caller can tell.</para>
    /// <para><b>The epsilon.</b> <c>na &lt;= double.Epsilon</c> reads like a tolerance and is not
    /// one: <c>double.Epsilon</c> is 4.9e-324, so that was an exact-zero test in an epsilon's
    /// clothing. It is now written as one. Note the accumulator gets <c>a[i] * a[i]</c> — a float
    /// times a float, rounded to binary32 BEFORE it is widened — so the product flushes to zero
    /// once every component is below about 2^-75 (2.6e-23), far above the subnormal boundary.
    /// Testing the accumulation rather than the components is what makes that case fall out here
    /// too, and it is why an epsilon tuned to subnormals would have missed it.</para>
    /// <para><b>Not finite.</b> A fourth change, made after the three above shipped: the magnitude
    /// verdict is <see cref="IsUsableNorm"/>, which rejects +∞ and NaN alongside zero. See that
    /// method for what each of those did here before it existed.</para>
    /// </remarks>
    private static bool TryCosineSimilarity(ReadOnlySpan<float> a, ReadOnlySpan<float> b, out double similarity)
    {
        similarity = 0;
        if (a.Length != b.Length || a.Length == 0)
        {
            return false;
        }

        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }

        // The one guard by magnitude. A second `denom == 0` check would be part of the same test
        // spelled differently and was deliberately not kept: with both present, removing either
        // left every test green. `denom` cannot underflow on its own -- the accumulator sums
        // float32 products, whose smallest nonzero value is 2^-149, and the product of the two
        // square roots of that is 2^-149 again. Nor can `dot` be non-finite once both norms are:
        // finite norms mean every component is finite, and |a[i]*b[i]| <= max(a[i]^2, b[i]^2),
        // each of which is a representable float by assumption. A guard on `dot` would be a
        // branch no input can reach and therefore no test could pin.
        if (!IsUsableNorm(na) || !IsUsableNorm(nb))
        {
            return false;
        }

        similarity = dot / (Math.Sqrt(na) * Math.Sqrt(nb));
        return true;
    }

    /// <summary>
    /// Whether a vector can be ranked at all. Uses the identical accumulation AND the identical
    /// verdict (<see cref="IsUsableNorm"/>) as <see cref="TryCosineSimilarity"/> so the two always
    /// agree about which vectors have a magnitude; hoisted out of the loop so an unrankable query
    /// is refused once.
    /// </summary>
    private static bool IsRankable(ReadOnlySpan<float> v)
    {
        if (v.Length == 0)
        {
            return false;
        }

        double norm = 0;
        for (var i = 0; i < v.Length; i++)
        {
            norm += v[i] * v[i];
        }

        return IsUsableNorm(norm);
    }

    /// <summary>
    /// The single verdict on an accumulated squared magnitude. Both ends of the accumulator are
    /// unusable and both are reachable from ordinary-looking components, because
    /// <c>norm += v[i] * v[i]</c> rounds the PRODUCT to binary32 before widening it.
    ///
    /// <para><b>Zero.</b> The product flushes to zero once every component is below about 2^-75
    /// (2.6e-23), twenty-two orders of magnitude above subnormal — so an epsilon tuned to
    /// subnormals would miss the real cliff.</para>
    ///
    /// <para><b>Not finite.</b> The same product saturates to +∞ above about 1.84e19, and is NaN
    /// if any component is. Both pass <c>norm != 0</c> and are useless as a denominator. Measured
    /// on net10.0/Linux/x64 before this clause existed, with a two-record collection: a query of
    /// 1e20f gave <c>na = +∞</c> and a finite dot, so every record came back at score 0.0; a query
    /// of NaN came back as every record at score NaN; and a record whose embedding was NaN was
    /// returned as a hit at ScoreThreshold 0.0, 0.5 AND 0.99, because this collection's threshold
    /// filter is written <c>score &lt; threshold</c> and <c>NaN &lt; anything</c> is false. That
    /// last one is the sharpest of the three: an unrankable row that defeats every filter a caller
    /// can set.</para>
    ///
    /// <para>Kept identical to <c>Ashlar.BackgroundAgents.RAG.VectorMath.IsUsableNorm</c> by hand,
    /// for the reason given at the refusal above; both are pinned by tests.</para>
    /// </summary>
    private static bool IsUsableNorm(double norm) => norm != 0 && double.IsFinite(norm);

    private static ChunkRecord Clone(ChunkRecord r) => new()
    {
        Key = r.Key,
        SourceUri = r.SourceUri,
        Text = r.Text,
        TrustTier = r.TrustTier,
        CreatedAt = r.CreatedAt,
        UpdatedAt = r.UpdatedAt,
        Embedding = r.Embedding.ToArray(),
    };
}

internal static class AsyncEnumerableExtensions
{
    public static async IAsyncEnumerable<T> ToAsyncEnumerable<T>(
        this IEnumerable<T> source,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var item in source)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return item;
            await Task.Yield();
        }
    }
}
