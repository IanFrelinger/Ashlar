namespace Ashlar.BackgroundAgents.RAG;

/// <summary>
/// Helpers for vector similarity (e.g. cosine similarity).
/// </summary>
public static class VectorMath
{
    /// <summary>
    /// Cosine similarity between two vectors (-1.0 to 1.0; 0.0 to 1.0 for non-negative vectors).
    /// Returns 0 if the vectors are incomparable or either one has a zero magnitude.
    /// </summary>
    /// <remarks>
    /// <para><b>Prefer <see cref="TryCosineSimilarity"/> in a search loop.</b> This overload collapses
    /// three different states into the single value 0.0: <i>incomparable</i> (the lengths differ),
    /// <i>undefined</i> (one of the vectors has zero magnitude, so the angle between them does not
    /// exist), and <i>genuinely orthogonal</i> (a real, correctly computed score of zero). A caller
    /// filtering on <c>score &gt;= minScore</c> cannot tell them apart, and at the common minScore of
    /// 0.0 all three read as a match — which is how a query nobody can rank came back as every
    /// document in the corpus. <see cref="TryCosineSimilarity"/> separates the first two from the
    /// third by returning <see langword="false"/> instead of a score.</para>
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
    /// they differ in length, or at least one of them has a zero magnitude. <see langword="true"/>
    /// with a usable score otherwise, including a legitimate score of exactly 0.0 for two orthogonal
    /// vectors.
    /// </returns>
    /// <remarks>
    /// <para><b>Zero magnitude is tested on the accumulated norm, not on the inputs, and that is not
    /// a detail.</b> <c>normA += a[i] * a[i]</c> multiplies two <see langword="float"/>s, so the
    /// PRODUCT is rounded to binary32 before it is widened into the <see langword="double"/>
    /// accumulator — and a float32 product flushes to zero once every component is below about
    /// 2^-75 (2.6e-23), roughly twenty-two orders of magnitude above the subnormal boundary.
    /// Measured on net10.0 Linux/x64: a vector of 1e-23f scored 0 against a unit vector and was
    /// admitted at minScore 0.0, while a vector of 1e-20f scored 0.125 correctly. So an
    /// epsilon-on-the-inputs guard tuned to subnormals would miss the real cliff entirely, and so
    /// would a guard written against <c>== 0f</c> components. Testing the same accumulation the
    /// scorer uses catches the exact-zero case and the float32-underflow case in one comparison,
    /// because on this code path they are the same case.</para>
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
        // magnitude. A `denom == 0` check below would be a second spelling of the same test and
        // was deliberately not kept: with both present, removing either one left every test
        // green, so neither was pinned by anything. `denom` cannot underflow independently here
        // -- the accumulator sums float32 products, so its smallest nonzero value is 2^-149
        // (1.4e-45), whose square root is 3.7e-23 and whose product with another such root is
        // 1.4e-45 again. Zero norms are the only route to a zero denominator.
        if (normA == 0 || normB == 0)
            return false;

        similarity = Math.Clamp(dot / (Math.Sqrt(normA) * Math.Sqrt(normB)), -1.0, 1.0);
        return true;
    }

    /// <summary>
    /// Whether a vector can be ranked at all: false when it is empty or its magnitude accumulates
    /// to zero.
    /// </summary>
    /// <remarks>
    /// Hoisted out of the scoring loop so a store can refuse an unrankable QUERY once, before any
    /// I/O, rather than discovering it per candidate. It uses the identical accumulation to
    /// <see cref="TryCosineSimilarity"/> — see that method's remarks for why the accumulation and
    /// not the components is the thing to test — so the two always agree about which vectors have
    /// no magnitude.
    /// </remarks>
    public static bool IsRankable(ReadOnlySpan<float> v)
    {
        if (v.Length == 0)
            return false;

        double norm = 0;
        for (var i = 0; i < v.Length; i++)
            norm += v[i] * v[i];

        return norm != 0;
    }

    /// <summary>
    /// The refusal both <see cref="IVectorStore"/> implementations in this assembly raise for a
    /// query embedding that <see cref="IsRankable"/> rejects. One wording, one place, so the two
    /// stores cannot drift into explaining the same condition differently.
    /// </summary>
    /// <remarks>
    /// <para><b>Why refuse rather than return nothing.</b> An empty result means "the corpus holds
    /// nothing close enough". A zero-magnitude query means "this question cannot be ranked against
    /// anything at all" — every document is exactly as far from it as every other. Answering the
    /// second with the first hands the caller a confident, wrong, unfalsifiable "no results", which
    /// is the failure shape docs/HowGatesGoQuiet.md exists to name. A skipped DOCUMENT is the
    /// opposite case and is handled the opposite way: one unrankable row must not take the rest of
    /// a good query down with it, so stores drop those silently, exactly as they drop a row of the
    /// wrong dimension.</para>
    /// </remarks>
    internal static ArgumentException UnrankableQuery(string paramName)
        => new(
            "The query embedding has zero magnitude, so it cannot be ranked against anything: "
            + "cosine similarity is undefined for it and every document in the store is equally "
            + "(un)close to it. This is NOT the same as 'nothing matched' — returning an empty "
            + "result here would be indistinguishable from an empty corpus. The usual cause is a "
            + "query the embedding generator found no tokens in: an empty string, whitespace, or "
            + "punctuation only (\"!!!\", \"...\"). Supply a query containing at least one token.",
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
