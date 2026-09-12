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
        foreach (var v in new[] { new float[64], Filled(64, 1e-23f), Filled(64, float.Epsilon), Filled(64, 1e-20f), Unit(64) })
        {
            var rankable = VectorMath.IsRankable(v);
            var scored = VectorMath.TryCosineSimilarity(v, Unit(64), out _);
            scored.Should().Be(rankable, "IsRankable and TryCosineSimilarity must agree about magnitude");
        }
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
