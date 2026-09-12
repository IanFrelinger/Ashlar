namespace Ashlar.BackgroundAgents.RAG;

/// <summary>
/// Helpers for vector similarity (e.g. cosine similarity).
/// </summary>
public static class VectorMath
{
    /// <summary>
    /// Cosine similarity between two vectors (-1.0 to 1.0; 0.0 to 1.0 for non-negative vectors).
    /// Returns 0 if the vectors are incomparable or either one has an unusable magnitude.
    /// </summary>
    /// <remarks>
    /// <para><b>Prefer <see cref="TryCosineSimilarity"/> in a search loop.</b> This overload collapses
    /// three different states into the single value 0.0: <i>incomparable</i> (the lengths differ),
    /// <i>undefined</i> (one of the vectors has no usable magnitude, so the angle between them does
    /// not exist), and <i>genuinely orthogonal</i> (a real, correctly computed score of zero). A
    /// caller filtering on <c>score &gt;= minScore</c> cannot tell them apart, and at the common
    /// minScore of 0.0 all three read as a match — which is how a query nobody can rank came back as
    /// every document in the corpus. <see cref="TryCosineSimilarity"/> separates the first two from
    /// the third by returning <see langword="false"/> instead of a score.</para>
    /// </remarks>
    public static double CosineSimilarity(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
        => TryCosineSimilarity(a, b, out var similarity) ? similarity : 0;

    /// <summary>
    /// Cosine similarity between two vectors, distinguishing "no score exists" from "the score is 0".
    /// </summary>
    /// <param name="a">First vector.</param>
    /// <param name="b">Second vector.</param>
    /// <param name="similarity">The similarity, clamped to [-1, 1]. Meaningless when this returns false.</param>
    /// <returns>
    /// <see langword="false"/> when no similarity exists between these two vectors — they are empty,
    /// they differ in length, or at least one of them has no usable magnitude (zero, not a number,
    /// or overflowed). <see langword="true"/> with a usable score otherwise, including a legitimate
    /// score of exactly 0.0 for two orthogonal vectors.
    /// </returns>
    /// <remarks>
    /// <para>Magnitude is tested on the accumulated norm, not on the inputs, and both ends of that
    /// accumulator are unusable — see <see cref="IsUsableNorm"/> for why, and for the measurements.</para>
    /// </remarks>
    public static bool TryCosineSimilarity(ReadOnlySpan<float> a, ReadOnlySpan<float> b, out double similarity)
    {
        similarity = 0;
        if (a.Length != b.Length || a.Length == 0)
            return false;

        double dot = 0, normA = 0, normB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        // The one guard, and the only place this method decides a pair cannot be ranked by
        // magnitude. A `denom == 0` check below would be a second spelling of part of the same
        // test and was deliberately not kept: with both present, removing either one left every
        // test green, so neither was pinned by anything. `denom` cannot underflow independently
        // here -- the accumulator sums float32 products, so its smallest nonzero value is 2^-149
        // (1.4e-45), whose square root is 3.7e-23 and whose product with another such root is
        // 1.4e-45 again. Zero norms are the only route to a zero denominator.
        //
        // The norms are also the only route to a non-finite SCORE, which is why no separate check
        // on `dot` or on `similarity` follows this one. If both norms are finite then every
        // `a[i] * a[i]` and `b[i] * b[i]` was a finite binary32 product, so every component is
        // finite and |a[i] * b[i]| <= max(a[i]^2, b[i]^2) -- itself a representable float -- and
        // the sum of those is bounded by (normA + normB) / 2, a finite double. A guard on `dot`
        // would therefore be a branch no input can take, i.e. one no test could pin, which is the
        // same mistake as the `denom` check above. That argument is measured rather than asserted:
        // TryCosineSimilarity_WhenItReturnsTrue_TheScoreIsFiniteAndInRange sweeps both vectors
        // over the extremes of binary32 and fails if any admitted pair scores NaN.
        if (!IsUsableNorm(normA) || !IsUsableNorm(normB))
            return false;

        similarity = Math.Clamp(dot / (Math.Sqrt(normA) * Math.Sqrt(normB)), -1.0, 1.0);
        return true;
    }

    /// <summary>
    /// Whether a vector can be ranked at all: false when it is empty or its magnitude accumulates
    /// to something no ratio can be taken against.
    /// </summary>
    /// <remarks>
    /// Hoisted out of the scoring loop so a store can refuse an unrankable QUERY once, before any
    /// I/O, rather than discovering it per candidate. It uses the identical accumulation and the
    /// identical verdict — <see cref="IsUsableNorm"/> — as <see cref="TryCosineSimilarity"/>, so
    /// the two cannot drift into disagreeing about which vectors have a magnitude.
    /// </remarks>
    public static bool IsRankable(ReadOnlySpan<float> v)
    {
        if (v.Length == 0)
            return false;

        double norm = 0;
        for (var i = 0; i < v.Length; i++)
            norm += v[i] * v[i];

        return IsUsableNorm(norm);
    }

    /// <summary>
    /// The single verdict on an accumulated squared magnitude, shared by <see cref="IsRankable"/>
    /// and <see cref="TryCosineSimilarity"/> so the hoisted check and the per-candidate check
    /// cannot disagree.
    /// </summary>
    /// <remarks>
    /// <para><b>The test is on the accumulated norm, not on the inputs, and that is not a
    /// detail.</b> <c>norm += v[i] * v[i]</c> multiplies two <see langword="float"/>s, so the
    /// PRODUCT is rounded to binary32 before it is widened into the <see langword="double"/>
    /// accumulator. That rounding is what makes both ends of this range reachable from components
    /// that look perfectly ordinary.</para>
    /// <para><b>Zero (<c>norm != 0</c>).</b> A float32 product flushes to zero once every component
    /// is below about 2^-75 (2.6e-23), roughly twenty-two orders of magnitude above the subnormal
    /// boundary. Measured on net10.0 Linux/x64: a vector of 1e-23f scored 0 against a unit vector
    /// and was admitted at minScore 0.0, while a vector of 1e-20f scored 0.125 correctly. So an
    /// epsilon-on-the-inputs guard tuned to subnormals would miss the real cliff entirely, and so
    /// would a guard written against <c>== 0f</c> components.</para>
    /// <para><b>Not finite (<c>double.IsFinite</c>).</b> The same product saturates to +∞ once
    /// components exceed about 1.84e19 (the square root of <c>float.MaxValue</c>), and it is NaN if
    /// any component is. Both give a norm that passes <c>norm != 0</c> while being useless as the
    /// denominator of a ratio, and each fails in its own direction rather than loudly. Measured on
    /// net10.0 Linux/x64 with a two-document corpus, at the commit that added the zero guard and
    /// before this clause existed: a query of 1e20f gave <c>normA = +∞</c> against a finite dot, so
    /// <c>dot / ∞</c> was exactly 0.0 and both <c>InMemoryVectorStore</c> and
    /// <c>SqliteVectorStore</c> returned BOTH documents at score 0 at minScore 0.0 — the phantom hit
    /// the zero guard exists to remove, rebuilt out of the other end of the same accumulator. A
    /// query of NaN failed more quietly: those two stores returned zero hits and threw nothing,
    /// because <c>NaN &gt;= minScore</c> is false, so the score filter ate every row and the caller
    /// got exactly the indistinguishable-from-an-empty-corpus answer <see cref="UnrankableQuery"/>
    /// exists to prevent. In the other direction a NaN-embedded ROW survived every filter tried
    /// (thresholds 0.0, 0.5 and 0.99) in the sibling <c>InProcessChunkCollection</c>, whose skip is
    /// written <c>score &lt; threshold</c> — false for NaN at any threshold.</para>
    /// <para><b>Reachability</b>, since both bundled generators L2-normalize: a host-supplied
    /// <c>IEmbeddingGenerator</c> (both registrations are <c>TryAddSingleton</c>, i.e. the
    /// documented override point), a direct call to the public <c>IVectorStore</c> surface, or a
    /// <c>rag_vectors.embedding</c> blob whose bytes are not a valid float array —
    /// <c>SqliteVectorStore.BlobToFloatArray</c> reinterprets raw bytes with no validation, and
    /// 0xFFFFFFFF decodes to NaN.</para>
    /// </remarks>
    private static bool IsUsableNorm(double norm) => norm != 0 && double.IsFinite(norm);

    /// <summary>
    /// The refusal both <see cref="IVectorStore"/> implementations in this assembly raise for a
    /// query embedding that <see cref="IsRankable"/> rejects. One wording, one place, so the two
    /// stores cannot drift into explaining the same condition differently.
    /// </summary>
    /// <remarks>
    /// <para><b>Why refuse rather than return nothing.</b> An empty result means "the corpus holds
    /// nothing close enough". A query with no usable magnitude means "this question cannot be ranked
    /// against anything at all" — every document is exactly as far from it as every other. Answering
    /// the second with the first hands the caller a confident, wrong, unfalsifiable "no results",
    /// which is the failure shape docs/HowGatesGoQuiet.md exists to name. A skipped DOCUMENT is the
    /// opposite case and is handled the opposite way: one unrankable row must not take the rest of
    /// a good query down with it, so stores drop those silently, exactly as they drop a row of the
    /// wrong dimension.</para>
    /// <para>One message for all three magnitude states, naming each, because the caller who has to
    /// act on it needs to know which one it was: a zero norm is a text problem the user can fix by
    /// retyping the query, while NaN or an overflow is a malformed embedding and retyping will not
    /// help.</para>
    /// </remarks>
    internal static ArgumentException UnrankableQuery(string paramName)
        => new(
            "The query embedding cannot be ranked against anything: its magnitude is zero, not a "
            + "number, or too large to represent, so cosine similarity is undefined for it and "
            + "every document in the store is equally (un)close to it. This is NOT the same as "
            + "'nothing matched' — returning an empty result here would be indistinguishable from "
            + "an empty corpus. A ZERO magnitude usually means the embedding generator found no "
            + "tokens in the query: an empty string, whitespace, or punctuation only (\"!!!\", "
            + "\"...\"). A NaN or OVERFLOWING magnitude means the embedding itself is malformed — a "
            + "generator that emitted NaN or components above ~1.8e19, or stored bytes reinterpreted "
            + "as floats. Supply a query whose embedding has a finite, nonzero magnitude.",
            paramName);

    /// <summary>
    /// Normalize vector in place to unit length.
    /// </summary>
    public static void NormalizeInPlace(Span<float> v)
    {
        double norm = 0;
        foreach (var x in v)
            norm += x * x;
        norm = Math.Sqrt(norm);
        if (norm == 0) return;
        for (var i = 0; i < v.Length; i++)
            v[i] = (float)(v[i] / norm);
    }
}
