using FluentAssertions;
using Ashlar.Core.Domain.Bricks;
using Ashlar.Core.Domain.Execution;
using Ashlar.Infrastructure.Execution;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Execution;

/// <summary>
/// Pins the semantic-cache key shared by <c>BehaviorExecutor</c> and <c>ClusterExecutor</c>.
///
/// <para><b>What the key used to be, and why the first test below is the load-bearing one.</b>
/// Both executors carried a byte-identical private <c>ComputeCacheKey</c> that serialized
/// <c>BrickInput.ToDictionary()</c> and appended <c>string.GetHashCode()</c>. That had three
/// problems; only two of them can be tested here, and the third is the reason to mention the
/// limits out loud.</para>
///
/// <para><i>Order.</i> <c>ToDictionary()</c> returns the live insertion-ordered dictionary, so the
/// same logical input built by calling <c>Set</c> in a different order serialized differently and
/// missed. Measured inside a single process launch: <c>{path, mode}</c> hashed 1350354404 while
/// <c>{mode, path}</c> hashed 453566733. This is testable in-process and is what
/// <see cref="Key_does_not_depend_on_the_order_the_input_was_assembled_in"/> asserts.</para>
///
/// <para><i>Width.</i> The key ended in a bare 32-bit int, so two different inputs to the same
/// brick collide at roughly 65k distinct inputs by the birthday bound — and a collision does not
/// miss, it returns the OTHER input's <c>BrickOutput</c> as a hit. That is a wrong answer inside a
/// single process. It cannot be tested by producing a collision on demand (the hash is randomized,
/// so a golden colliding pair does not exist), so
/// <see cref="Key_carries_a_full_width_digest_not_a_32_bit_int"/> asserts the property that
/// removes the hazard instead of the hazard itself.</para>
///
/// <para><i>Process independence.</i> <c>string.GetHashCode()</c> is randomized per process —
/// measured, five distinct values over five launches. No in-process test can observe that, which
/// is exactly the trap <c>docs/HowGatesGoQuiet.md</c> names and which
/// <c>InfrastructureGapCoverageTests</c> still demonstrates by re-deriving the value under test in
/// the same process. <see cref="Key_matches_a_constant_computed_in_a_different_process"/> handles
/// it the only way that works: a golden constant produced elsewhere and committed to source.</para>
/// </summary>
public sealed class SemanticCacheKeyTests
{
    private static BrickInput Input(params (string Key, object Value)[] pairs)
    {
        var input = new BrickInput();
        foreach (var (key, value) in pairs)
            input.Set(key, value);
        return input;
    }

    [Fact]
    public void Key_does_not_depend_on_the_order_the_input_was_assembled_in()
    {
        var forward = SemanticCacheKey.For("brick-a", Input(("path", "/x"), ("mode", "fast")), ImplementationType.Deterministic);
        var reversed = SemanticCacheKey.For("brick-a", Input(("mode", "fast"), ("path", "/x")), ImplementationType.Deterministic);

        forward.Should().Be(reversed,
            "the same logical input must hit the cache however it was assembled");
    }

    /// <summary>
    /// Positive control for the test above. Order-independence is trivially satisfied by a key
    /// that ignores the input altogether, so the key must still separate inputs that genuinely
    /// differ — by value, by brick, and by implementation.
    /// </summary>
    [Fact]
    public void Key_still_separates_inputs_that_actually_differ()
    {
        var baseline = SemanticCacheKey.For("brick-a", Input(("path", "/x"), ("mode", "fast")), ImplementationType.Deterministic);

        SemanticCacheKey.For("brick-a", Input(("path", "/y"), ("mode", "fast")), ImplementationType.Deterministic)
            .Should().NotBe(baseline, "a different value is a different input");
        SemanticCacheKey.For("brick-a", Input(("path", "/x")), ImplementationType.Deterministic)
            .Should().NotBe(baseline, "a missing entry is a different input");
        SemanticCacheKey.For("brick-b", Input(("path", "/x"), ("mode", "fast")), ImplementationType.Deterministic)
            .Should().NotBe(baseline, "a different brick is a different execution");
        SemanticCacheKey.For("brick-a", Input(("path", "/x"), ("mode", "fast")), ImplementationType.Agentic)
            .Should().NotBe(baseline, "a different implementation is a different execution");
    }

    /// <summary>
    /// The width property, asserted because the collision itself cannot be produced on demand.
    /// A SHA-256 digest in Base64 is 44 characters; the old key's last segment was the decimal
    /// form of an <c>int</c>, at most 11 characters including a sign.
    /// </summary>
    [Fact]
    public void Key_carries_a_full_width_digest_not_a_32_bit_int()
    {
        var key = SemanticCacheKey.For("brick-a", Input(("path", "/x")), ImplementationType.Deterministic);

        var digest = key.Split(':').Last();
        digest.Should().HaveLength(44, "a Base64 SHA-256 digest is 44 characters");
        int.TryParse(digest, out _).Should().BeFalse(
            "a key that parses as an int is a 32-bit key, and a 32-bit key returns another "
            + "input's BrickOutput on collision");
    }

    /// <summary>
    /// The one assertion an in-process test cannot otherwise make. This constant was produced by
    /// a SEPARATE process and committed here; a test that recomputed the expected value in this
    /// process would pass identically before and after the fix and would be worth nothing — which
    /// is precisely what <c>InfrastructureGapCoverageTests.InMemoryCompositionCache_stores_and_retrieves</c>
    /// does today and why it is not coverage for that class's hashing.
    ///
    /// <para>If this constant ever has to change, the key derivation changed, and every entry in
    /// every persistent semantic cache became unreachable. That is a decision, not a fixup:
    /// change the constant only alongside the reason.</para>
    /// </summary>
    [Fact]
    public void Key_matches_a_constant_computed_in_a_different_process()
    {
        SemanticCacheKey.For("brick-a", Input(("path", "/x"), ("mode", "fast")), ImplementationType.Deterministic)
            .Should().Be("brick-a:Deterministic:gMetnECRaN2+2qGfe0dBvQOo7oUjliZQK2xRjjIMSHE=");
    }
}
