using FluentAssertions;
using Ashlar.BackgroundAgents.RAG;
using Xunit;

namespace Ashlar.Tests.BackgroundAgents.RAG;

/// <summary>
/// Pins the distinction <c>CosineSimilarity</c> could not make: "there is no score" versus "the
/// score is 0". Three states used to arrive as the same <c>double</c> — incomparable lengths,
/// undefined (zero magnitude), and genuinely orthogonal — and a caller filtering on
/// <c>score &gt;= minScore</c> could not separate them, so at the common minScore of 0.0 all three
/// read as a match.
///
/// <para>The orthogonal case is the one that makes this awkward and is therefore the one that has
/// to be pinned hardest: it is a real, correct score of exactly 0.0, and any guard written as
/// <c>score &gt; 0</c> or <c>Math.Abs(score) &gt; eps</c> would silently discard it. That is why
/// the fix separates the states out of band instead of filtering on the value.</para>
/// </summary>
public class VectorMathTests
{
    private static float[] Unit(int dim)
    {
        var v = new float[dim];
        v[0] = 1f;
        return v;
    }

    private static float[] Filled(int dim, float value)
    {
        var v = new float[dim];
        Array.Fill(v, value);
        return v;
    }

    [Fact]
    public void TryCosineSimilarity_LengthMismatch_IsNotRankable()
    {
        VectorMath.TryCosineSimilarity(Unit(16), Unit(32), out _).Should().BeFalse();
    }

    [Fact]
    public void TryCosineSimilarity_ZeroVector_IsNotRankable()
    {
        VectorMath.TryCosineSimilarity(new float[64], Unit(64), out _).Should().BeFalse();
        VectorMath.TryCosineSimilarity(Unit(64), new float[64], out _).Should().BeFalse();
    }

    /// <summary>
    /// The boundary, and the reason an epsilon would have been the wrong guard. 1e-23f is a
    /// perfectly ordinary binary32 value — about twenty-two orders of magnitude above the
    /// subnormal floor — but <c>normA += a[i] * a[i]</c> multiplies two floats, so the product is
    /// rounded to binary32 and flushes to zero BEFORE it is widened into the double accumulator.
    /// The vector therefore has no magnitude as far as the scorer is concerned, and scored 0.0
    /// against everything.
    ///
    /// <para>A test written only against an exactly-zero vector passes against a guard that
    /// inspects the input components, and would not catch this. This case is the one that fails
    /// such a guard.</para>
    /// </summary>
    [Fact]
    public void TryCosineSimilarity_Float32UnderflowVector_IsNotRankable()
    {
        // Preconditions, so a future reader can see the mechanism rather than trust the constant.
        const float T = 1e-23f;
        T.Should().NotBe(0f, "1e-23f is a normal binary32 value, not a zero literal");
        ((float)(T * T)).Should().Be(0f, "the float32 PRODUCT is what underflows, not the input");
        ((double)T * T).Should().BeGreaterThan(0d, "widening the operands first would not underflow");

        VectorMath.IsRankable(Filled(64, T)).Should().BeFalse();
        VectorMath.TryCosineSimilarity(Filled(64, T), Unit(64), out _).Should().BeFalse();
    }

    /// <summary>
    /// Positive control for the case above: one step back from the cliff the vector is rankable
    /// and scores correctly. Without this, a guard that rejected every small vector — or every
    /// vector — would pass the underflow test.
    /// </summary>
    [Fact]
    public void TryCosineSimilarity_SmallButRepresentableVector_IsRankableAndScoresCorrectly()
    {
        const float T = 1e-20f;

        VectorMath.IsRankable(Filled(64, T)).Should().BeTrue();
        VectorMath.TryCosineSimilarity(Filled(64, T), Unit(64), out var score).Should().BeTrue();

        // 64 equal components against a unit vector: cos = 1 / sqrt(64) = 0.125.
        score.Should().BeApproximately(0.125, 1e-6);
    }

    /// <summary>
    /// The state that must NOT be confused with the two above. Two orthogonal unit vectors have a
    /// real similarity of exactly 0.0, and a store is right to return such a document at
    /// minScore 0.0.
    /// </summary>
    [Fact]
    public void TryCosineSimilarity_OrthogonalVectors_AreRankableWithScoreExactlyZero()
    {
        var a = new float[4] { 1f, 0f, 0f, 0f };
        var b = new float[4] { 0f, 1f, 0f, 0f };

        VectorMath.TryCosineSimilarity(a, b, out var score).Should().BeTrue(
            "orthogonal is a real answer, not a missing one");
        score.Should().Be(0d);
    }

    [Fact]
    public void IsRankable_AgreesWithTryCosineSimilarity_OnEveryMagnitudeCase()
    {
        // Both must decide "has magnitude" the same way, because IsRankable is hoisted out of
        // the loop that TryCosineSimilarity runs inside. If they ever disagree, a store refuses
        // queries it can score or scores queries it should refuse.
        var cases = new[]
        {
            new float[64],
            Filled(64, 1e-23f),
            Filled(64, float.Epsilon),
            Filled(64, 1e-20f),
            Unit(64),
            // The other end of the same accumulator, added with the finiteness clause.
            Filled(64, 1e19f),
            Filled(64, 1e20f),
            Filled(64, float.MaxValue),
            Filled(64, float.NaN),
            Filled(64, float.PositiveInfinity),
            Filled(64, float.NegativeInfinity),
        };

        foreach (var v in cases)
        {
            var rankable = VectorMath.IsRankable(v);
            var scored = VectorMath.TryCosineSimilarity(v, Unit(64), out _);
            scored.Should().Be(rankable, "IsRankable and TryCosineSimilarity must agree about magnitude");
        }
    }

    /// <summary>
    /// The mirror of <see cref="TryCosineSimilarity_Float32UnderflowVector_IsNotRankable"/>, at the
    /// top of the same accumulator instead of the bottom. <c>normA += a[i] * a[i]</c> rounds the
    /// PRODUCT to binary32, so it saturates to +∞ once components pass √float.MaxValue (~1.84e19) —
    /// and an infinite norm passed the <c>norm != 0</c> guard while being useless as the denominator
    /// of a ratio.
    ///
    /// <para>Measured on net10.0/Linux/x64 with the zero guard in place and this clause absent:
    /// <c>IsRankable(1e20f) = True</c>, <c>TryCosineSimilarity(1e20f, unit)</c> returned true with a
    /// score of exactly 0.0 (a finite dot over an infinite denominator), and
    /// <c>InMemoryVectorStore</c> answered such a query with BOTH documents at score 0 at
    /// minScore 0.0 — the same phantom hit the zero guard exists to remove, rebuilt from the other
    /// direction.</para>
    /// </summary>
    [Fact]
    public void TryCosineSimilarity_Float32OverflowVector_IsNotRankable()
    {
        // Preconditions, so a future reader can see the mechanism rather than trust the constant.
        const float T = 1e20f;
        float.IsFinite(T).Should().BeTrue("1e20f is an ordinary binary32 value, not an infinity literal");
        float.IsPositiveInfinity((float)(T * T)).Should().BeTrue("the float32 PRODUCT is what overflows, not the input");
        ((double)T * T).Should().BeLessThan(double.MaxValue, "widening the operands first would not overflow");

        VectorMath.IsRankable(Filled(64, T)).Should().BeFalse();
        VectorMath.TryCosineSimilarity(Filled(64, T), Unit(64), out _).Should().BeFalse();
        VectorMath.TryCosineSimilarity(Unit(64), Filled(64, T), out _).Should().BeFalse();
    }

    /// <summary>
    /// Positive control for the case above, and the reason the guard cannot simply be "reject big
    /// vectors": one step back from the cliff a large vector is rankable and scores correctly.
    /// 1e19f squares to 1e38, still inside binary32, so the accumulation stays finite.
    /// </summary>
    [Fact]
    public void TryCosineSimilarity_LargeButRepresentableVector_IsRankableAndScoresCorrectly()
    {
        const float T = 1e19f;
        float.IsFinite((float)(T * T)).Should().BeTrue("1e19f squared is 1e38, inside binary32");

        VectorMath.IsRankable(Filled(64, T)).Should().BeTrue();
        VectorMath.TryCosineSimilarity(Filled(64, T), Unit(64), out var score).Should().BeTrue();

        // 64 equal components against a unit vector: cos = 1 / sqrt(64) = 0.125, the same answer
        // the 1e-20f case gives, because cosine does not care about scale.
        score.Should().BeApproximately(0.125, 1e-6);
    }

    /// <summary>
    /// NaN is the quietest of the three unusable magnitudes, and it fails in the opposite direction
    /// from the other two. A NaN norm passes <c>norm != 0</c> (NaN compares unequal to everything,
    /// including zero), so the pair was admitted with a score of NaN — and NaN then loses every
    /// comparison a caller can write. Measured before this clause: a NaN QUERY made
    /// <c>InMemoryVectorStore</c> and <c>SqliteVectorStore</c> return zero hits and throw nothing,
    /// because <c>NaN &gt;= minScore</c> is false, which is precisely the
    /// indistinguishable-from-an-empty-corpus answer the refusal exists to prevent; a NaN ROW in
    /// <c>InProcessChunkCollection</c> was returned as a hit at every threshold, because
    /// <c>NaN &lt; threshold</c> is false.
    /// </summary>
    [Fact]
    public void TryCosineSimilarity_NaNVector_IsNotRankable()
    {
        double.IsNaN(double.NaN).Should().BeTrue();
        (double.NaN != 0).Should().BeTrue("this is why `norm != 0` alone admitted a NaN magnitude");

        VectorMath.IsRankable(Filled(64, float.NaN)).Should().BeFalse();
        VectorMath.TryCosineSimilarity(Filled(64, float.NaN), Unit(64), out _).Should().BeFalse();
        VectorMath.TryCosineSimilarity(Unit(64), Filled(64, float.NaN), out _).Should().BeFalse();

        // A single bad component is enough: the accumulator is contaminated by one NaN.
        var oneBad = Unit(64);
        oneBad[7] = float.NaN;
        VectorMath.IsRankable(oneBad).Should().BeFalse();
    }

    [Fact]
    public void TryCosineSimilarity_InfiniteComponents_AreNotRankable()
    {
        VectorMath.IsRankable(Filled(64, float.PositiveInfinity)).Should().BeFalse();
        VectorMath.IsRankable(Filled(64, float.NegativeInfinity)).Should().BeFalse();
        VectorMath.TryCosineSimilarity(Filled(64, float.PositiveInfinity), Unit(64), out _).Should().BeFalse();
    }

    /// <summary>
    /// The contract, swept rather than argued. <c>TryCosineSimilarity</c> guards the two NORMS and
    /// deliberately does not guard <c>dot</c> or the final quotient, on the reasoning that finite
    /// norms force a finite dot. A guard nobody can reach is a guard no test can pin, so that
    /// reasoning is measured here instead of being written down and trusted: every pair the method
    /// ADMITS must carry a finite score inside [-1, 1].
    ///
    /// <para>The admitted-count assertion is the positive control. Without it this test is
    /// satisfied by a method that refuses everything, which is exactly the shape a too-broad guard
    /// would take.</para>
    /// </summary>
    [Fact]
    public void TryCosineSimilarity_WhenItReturnsTrue_TheScoreIsFiniteAndInRange()
    {
        float[] extremes =
        [
            0f, 1f, -1f, 1e-23f, -1e-23f, 1e-20f, 1e19f, -1e19f, 1e20f,
            float.MaxValue, float.MinValue, float.Epsilon,
            float.NaN, float.PositiveInfinity, float.NegativeInfinity,
        ];

        var admitted = 0;
        foreach (var x in extremes)
        {
            foreach (var y in extremes)
            {
                // Mixed shapes so the dot product is not simply a scaled copy of either norm, and
                // so the extreme component never cancels itself out of the numerator.
                var a = new[] { x, 1f, 0f, 1f };
                var b = new[] { y, 0f, 1f, 1f };

                if (!VectorMath.TryCosineSimilarity(a, b, out var score))
                    continue;

                admitted++;
                double.IsFinite(score).Should().BeTrue(
                    "an admitted pair must have a real score (x={0}, y={1}, score={2})", x, y, score);
                score.Should().BeInRange(-1d, 1d,
                    "cosine similarity is bounded (x={0}, y={1})", x, y);
            }
        }

        admitted.Should().BeGreaterThan(0, "a method that refuses every pair would pass this vacuously");
    }

    /// <summary>
    /// The old entry point keeps its documented behaviour — 0 for every unrankable pair — because
    /// it is public API. This pins that it is still the lossy one, so nobody reads the refactor as
    /// having fixed it in place.
    /// </summary>
    [Fact]
    public void CosineSimilarity_StillCollapsesUnrankablePairsToZero()
    {
        VectorMath.CosineSimilarity(Unit(16), Unit(32)).Should().Be(0d);
        VectorMath.CosineSimilarity(new float[64], Unit(64)).Should().Be(0d);
        VectorMath.CosineSimilarity(Filled(64, 1e-23f), Unit(64)).Should().Be(0d);
    }
}
