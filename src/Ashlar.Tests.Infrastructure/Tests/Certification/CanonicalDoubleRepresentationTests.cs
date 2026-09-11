using System.Globalization;
using System.Text.Json;
using Ashlar.Certification.Contracts;
using Ashlar.Core.Application.Certification.Models;
using Ashlar.Infrastructure.Certification.Composition;
using FluentAssertions;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>
/// The one decimal form every target must produce for a double, the three decisions behind it, and
/// the one class it does not close.
/// <para>
/// The canonical payload is the message every certification signature is computed over, so the
/// decimal form of a double in it has to be the same text on every target the package ships for
/// and in every publish mode. It is not the same text if it is delegated: the netstandard2.0
/// asset formats 1/3 as <c>0.33333333333333331</c> where net8.0 and net10.0 format it
/// <c>0.3333333333333333</c>, and it drops the sign of negative zero where they keep it. The
/// golden corpora pin the form for the records they hold; this suite pins the reasons, which a
/// corpus entry cannot state — round-trip fidelity across the whole range, the deliberate
/// collapse of both zeros, the refusal of values JSON has no number for, and independence from
/// the ambient culture.
/// </para>
/// <para>
/// It also pins the one class the chosen form does NOT make identical everywhere. 17 digits is a
/// WIDTH, and a width does not say how to break an exact tie at the last digit: the targets break
/// one in opposite directions, and the tie test below carries the worked case and both known
/// texts so that residual cannot widen unnoticed.
/// </para>
/// </summary>
[Trait("Category", "Certification")]
public sealed class CanonicalDoubleRepresentationTests
{
    private const string HmacKey = "canonical-double-representation-test-hmac";
    private const string BrickSource = "class CanonicalDoubleProbe { }";

    /// <summary>
    /// Every value whose decimal form was measured to differ between targets, plus the ends of
    /// the range. The expected text is 17 significant digits on the invariant culture — a width
    /// at which a binary64 always survives the round trip, rather than a shortest form whose
    /// length depends on the formatter that produced it.
    /// <para>
    /// The last row is the exception, and has to be read with
    /// <see cref="AnExactTieAtTheSeventeenthDigit_IsTheOneFormTheTargetsStillRoundDifferently"/>:
    /// its expected text is what net8.0 and net10.0 produce, and the netstandard2.0 asset under
    /// Mono produces the neighbouring digit. Every other row is the same text on all three.
    /// </para>
    /// </summary>
    public static TheoryData<double, string> CanonicalForms => new()
    {
        { 1.0 / 3.0, "0.33333333333333331" },
        { 0.007, "0.0070000000000000001" },
        { 1e-7, "9.9999999999999995E-08" },
        { 0.1, "0.10000000000000001" },
        { 1e23, "9.9999999999999992E+22" },
        { double.Epsilon, "4.9406564584124654E-324" },
        { double.MaxValue, "1.7976931348623157E+308" },
        { double.MinValue, "-1.7976931348623157E+308" },
        { 0.5, "0.5" },
        { 0.25, "0.25" },
        { 0.0, "0" },
        { -0.0, "0" },
        { TieAtTheSeventeenthDigit, TieFormOnDotNet },
    };

    [Theory]
    [MemberData(nameof(CanonicalForms))]
    public void EscapeRate_IsWrittenInTheCanonicalDecimalForm(double value, string expected)
    {
        var payload = CertificationRecordSigning.BuildPayload(Minimal() with { EscapeRate = value });

        // The trailing separator is part of the assertion: escapeRate is always followed by
        // totalMutants, and without the comma the expected "0" of a zero row is a substring of
        // every other form in this table too.
        payload.Should().Contain(
            "\"escapeRate\":" + expected + ",",
            "the decimal form of a double is chosen by the emitter, not by whichever formatter "
            + "the target happens to ship");
    }

    [Theory]
    [MemberData(nameof(CanonicalForms))]
    public void DurationSeconds_IsWrittenInTheSameCanonicalDecimalForm(double value, string expected)
    {
        // The second double-bearing field on the v2 lane, and the one no production minter
        // populates today — which is the whole reason to pin it here rather than wait.
        var record = Minimal() with
        {
            Attempts = new[]
            {
                new CertificationAttempt { Index = 1, Outcome = "pass", DurationSeconds = value },
            },
        };

        // durationSeconds closes its attempt object, so the brace plays the separator's part.
        CertificationRecordSigning.BuildPayload(record).Should().Contain("\"durationSeconds\":" + expected + "}");
    }

    [Theory]
    [MemberData(nameof(CanonicalForms))]
    public void CompositionEscapeRate_IsWrittenInTheSameCanonicalDecimalForm(double value, string expected)
    {
        // The composition emitter is a second hand-written copy in a different assembly. Two
        // copies of one decision drift unless something asserts they are the same decision.
        CompositionCertificationRecordSigner.BuildPayload(MinimalComposition() with { CompositionEscapeRate = value })
            .Should().Contain("\"compositionEscapeRate\":" + expected);
    }

    /// <summary>The same values, for the assertions that have no use for the expected text.</summary>
    public static TheoryData<double> CanonicalValues => new()
    {
        1.0 / 3.0, 0.007, 1e-7, 0.1, 1e23, double.Epsilon,
        double.MaxValue, double.MinValue, 0.5, 0.25, 0.0, -0.0,
        TieAtTheSeventeenthDigit,
    };

    /// <summary>
    /// 0.5 + 2^-18: exactly representable, inside <c>escapeRate</c>'s documented 0.0-1.0 range,
    /// and lying precisely halfway between two 17-digit decimals.
    /// </summary>
    private const double TieAtTheSeventeenthDigit = 0.500003814697265625;

    /// <summary>What net8.0 and net10.0 write for it: the tie broken to even.</summary>
    private const string TieFormOnDotNet = "0.50000381469726562";

    /// <summary>
    /// What the netstandard2.0 asset writes for it under Mono 6.12: the tie broken away from
    /// zero. Measured, not derived — and the reason this suite carries a tie case at all.
    /// </summary>
    private const string TieFormOnMono = "0.50000381469726563";

    [Fact]
    public void AnExactTieAtTheSeventeenthDigit_IsTheOneFormTheTargetsStillRoundDifferently()
    {
        // A width is not a rounding rule. Asking for 17 significant digits fixes how many digits
        // every target writes; it leaves the target's own formatter to decide which way to break
        // a value sitting exactly between two of them, and the targets decide differently.
        // Pinned here rather than in a golden corpus on purpose: no producer in this tree can
        // reach such a value (escapeRate is survivors over mutants, durationSeconds a measured
        // elapsed time), so a corpus case would only turn the netstandard2.0 probe red over a
        // divergence nothing signs. This suite runs on net8.0 and net10.0 in CI, so a change in
        // what THIS side does is caught; what the other side does is pinned as text below.
        CertificationRecordSigning.BuildPayload(Minimal() with { EscapeRate = TieAtTheSeventeenthDigit })
            .Should().Contain("\"escapeRate\":" + TieFormOnDotNet + ",");
        CompositionCertificationRecordSigner.BuildPayload(
                MinimalComposition() with { CompositionEscapeRate = TieAtTheSeventeenthDigit })
            .Should().Contain("\"compositionEscapeRate\":" + TieFormOnDotNet);

        // The two forms are neighbours at the last digit and nowhere else, which is what makes
        // this a tie rather than a disagreement about the value.
        TieFormOnMono.Should().HaveLength(TieFormOnDotNet.Length);
        TieFormOnMono.Substring(0, TieFormOnMono.Length - 1).Should().Be(
            TieFormOnDotNet.Substring(0, TieFormOnDotNet.Length - 1));
        TieFormOnMono.Should().NotBe(TieFormOnDotNet);

        // And both texts read back as the identical bit pattern. The residual is in the BYTES -
        // which is the whole of the claim, because the bytes back a signature - and not in the
        // value a reader recovers.
        foreach (var form in new[] { TieFormOnDotNet, TieFormOnMono })
        {
            BitConverter.DoubleToInt64Bits(double.Parse(form, CultureInfo.InvariantCulture))
                .Should().Be(
                    BitConverter.DoubleToInt64Bits(TieAtTheSeventeenthDigit),
                    "both forms name the same binary64, so a record written on either target is "
                    + "read back as the same value even where the text differs");
        }
    }

    [Theory]
    [MemberData(nameof(CanonicalValues))]
    public void EveryEmittedDouble_ReParsesToTheBitPatternItWasWrittenFrom(double value)
    {
        // Round-trip fidelity is the constraint that disqualified every fixed-width format: F17
        // of double.Epsilon is a string of zeros, and the value is simply gone. A form that
        // cannot be read back is not a form these bytes may take.
        var payload = CertificationRecordSigning.BuildPayload(Minimal() with { EscapeRate = value });

        using var document = JsonDocument.Parse(payload);
        var read = document.RootElement.GetProperty("escapeRate").GetDouble();

        BitConverter.DoubleToInt64Bits(read).Should().Be(
            BitConverter.DoubleToInt64Bits(value == 0d ? 0d : value),
            "17 significant digits is the width at which a binary64 always survives the round trip");
    }

    [Fact]
    public void BothZeros_CollapseOntoOneCanonicalForm()
    {
        // Deliberate, and the one decision here that looks like a bug to a reader who has not
        // measured it. Emitting "-0" makes the writers agree and leaves the readers disagreeing:
        // the netstandard2.0 asset parses -0 back as +0, so it would re-emit 0 and compute
        // different bytes from the same record file. Collapsing gives up only the sign bit of a
        // zero — -0.0 == 0.0 — and is stable through emit, read and re-emit everywhere.
        var negative = CertificationRecordSigning.BuildPayload(Minimal() with { EscapeRate = -0.0 });
        var positive = CertificationRecordSigning.BuildPayload(Minimal() with { EscapeRate = 0.0 });

        negative.Should().Be(positive);
        negative.Should().Contain("\"escapeRate\":0").And.NotContain("\"escapeRate\":-0");

        var compositionNegative = CompositionCertificationRecordSigner.BuildPayload(
            MinimalComposition() with { CompositionEscapeRate = -0.0 });
        compositionNegative.Should().Be(CompositionCertificationRecordSigner.BuildPayload(
            MinimalComposition() with { CompositionEscapeRate = 0.0 }));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void NonFiniteDouble_IsRefusedAsACanonicalPayloadFault(double value)
    {
        // JSON has no number for these, so refusal IS the canonical answer. What this pins is the
        // vehicle: Utf8JsonWriter refuses them with an ArgumentException, which VerifySignature
        // does not catch and therefore throws into its host — a verifier that throws has not
        // answered the question it was asked.
        Action act = () => _ = CertificationRecordSigning.BuildPayload(Minimal() with { EscapeRate = value });

        act.Should().Throw<CanonicalPayloadException>().WithMessage("*no number for NaN or infinity*");
    }

    [Fact]
    public void NonFiniteDouble_RefusesVerificationInsteadOfThrowingIntoTheHost()
    {
        var bound = Minimal() with
        {
            ContentHash = BrickContentHasher.ComputeSha256(BrickSource),
            Signature = "not-the-point",
            EscapeRate = double.PositiveInfinity,
        };

        CertificationRecordSigning.VerifySignature(bound, HmacKey).Should().BeFalse();

        var result = CertificationTrustVerifier.Verify(bound, BrickSource, HmacKey, CertificationVerifyOptions.Strict);
        result.Trusted.Should().BeFalse();
        result.FailureCode.Should().Be(
            "payload-not-canonical",
            "an unwritable payload is a canonical-payload fault, not the catch-all "
            + "payload-unbuildable that an escaping ArgumentException produces");
    }

    [Fact]
    public void NonFiniteCompositionDouble_IsRefusedTheSameWay()
    {
        Action act = () => _ = CompositionCertificationRecordSigner.BuildPayload(
            MinimalComposition() with { CompositionEscapeRate = double.NaN });

        act.Should().Throw<CanonicalPayloadException>().WithMessage("*no number for NaN or infinity*");
    }

    [Fact]
    public void TheDecimalSeparator_DoesNotFollowTheAmbientCulture()
    {
        // A host that sets a comma-decimal culture would otherwise sign "escapeRate":0,5 — bytes
        // that are not JSON at all, and that no other host would reproduce. The culture is built
        // by hand rather than looked up by name so this holds under InvariantGlobalization too.
        var comma = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        comma.NumberFormat.NumberDecimalSeparator = ",";
        comma.NumberFormat.NumberGroupSeparator = ".";

        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = comma;
            CertificationRecordSigning.BuildPayload(Minimal() with { EscapeRate = 0.5 })
                .Should().Contain("\"escapeRate\":0.5");
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    private static CertificationRecordData Minimal() => new()
    {
        Status = "PASS",
        Stage = "S0-S2",
        Admitted = true,
        Signed = true,
        Timestamp = new DateTimeOffset(2026, 8, 12, 10, 0, 0, TimeSpan.Zero),
        BrickId = "canonical-double-brick",
        SchemaVersion = CertificationRecordData.TrustLoopSchemaVersion,
    };

    private static CompositionCertificationRecord MinimalComposition() => new()
    {
        Status = "PASS",
        Stage = "S0-S2",
        Admitted = true,
        Signed = true,
        Timestamp = new DateTimeOffset(2026, 8, 12, 10, 0, 0, TimeSpan.Zero),
        CompositionId = "canonical-double-composition",
    };
}
