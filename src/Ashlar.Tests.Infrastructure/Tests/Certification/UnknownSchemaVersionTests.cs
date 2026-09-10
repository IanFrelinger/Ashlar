using Ashlar.Certification.Contracts;
using Ashlar.Core.Application.Certification.Models;
using Ashlar.Infrastructure.Certification;
using FluentAssertions;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>
/// An unknown certification schema version is an error, not a guess.
/// <para>
/// <c>BuildPayload</c> selects the canonical payload lane on the record's own
/// <c>SchemaVersion</c>. It used to select the v2 lane on "not null", so a record stamped 3, 7
/// or <c>int.MaxValue</c> was serialized in the v2 shape with that number emitted verbatim —
/// bytes whose meaning no code has ever defined — and, because a floor only says "at least
/// this new", cleared the floor of 2 that <see cref="CertificationVerifyOptions.Default"/> and
/// <see cref="CertificationVerifyOptions.Strict"/> apply. Only null (v1) and
/// <see cref="CertificationRecordData.TrustLoopSchemaVersion"/> (v2) select a lane; these tests
/// pin that every other version is refused at mint time and on every verification path, and
/// that the two known versions still route to the lanes they always did, byte for byte
/// (<see cref="CanonicalPayloadGoldenTests"/> carries the byte pin against an unchanged corpus).
/// </para>
/// </summary>
[Trait("Category", "Certification")]
public sealed class UnknownSchemaVersionTests
{
    private const string HmacKey = "unknown-schema-version-test-hmac";
    private const string BrickSource = "class SchemaVersionProbe { }";

    public static TheoryData<int> UnknownVersions => new() { 0, 1, 3, int.MaxValue, -1 };

    /// <summary>Unknown versions that clear Legacy's floor of 0; a negative one does not, and reports the floor.</summary>
    public static TheoryData<int> UnknownVersionsAboveZero => new() { 0, 1, 3, int.MaxValue };

    /// <summary>Versions that clear a floor of 2 and were therefore accepted by the floor alone.</summary>
    public static TheoryData<int> UnknownVersionsAboveTheFloor => new() { 3, int.MaxValue };

    /// <summary>Versions the floor already refuses; the floor must keep winning for them.</summary>
    public static TheoryData<int> UnknownVersionsBelowTheFloor => new() { 0, 1, -1 };

    [Fact]
    public void KnownVersions_AreExactlyNullAndTrustLoop()
    {
        CertificationRecordSigning.IsKnownSchemaVersion(null).Should().BeTrue("null is the legacy v1 lane");
        CertificationRecordSigning.IsKnownSchemaVersion(CertificationRecordData.TrustLoopSchemaVersion).Should().BeTrue("2 is the trust-loop v2 lane");
        CertificationRecordSigning.IsKnownSchemaVersion(CertificationRecordData.TrustLoopSchemaVersion + 1).Should().BeFalse();
    }

    [Fact]
    public void KnownVersions_StillRouteToTheirLanes()
    {
        // Lane routing for the two known versions is unchanged; the golden corpus pins the bytes.
        CertificationRecordSigning.BuildPayload(Record(null)).Should().StartWith("{\"status\":", "null selects the v1 shape");
        CertificationRecordSigning.BuildPayload(Record(CertificationRecordData.TrustLoopSchemaVersion))
            .Should().StartWith("{\"schemaVersion\":2,", "2 selects the v2 shape");
    }

    [Theory]
    [MemberData(nameof(UnknownVersions))]
    public void BuildPayload_RefusesAnUnknownVersion(int version)
    {
        Action act = () => _ = CertificationRecordSigning.BuildPayload(Record(version));

        act.Should().Throw<CanonicalPayloadException>("a version that selects no lane has no canonical bytes")
            .Which.Message.Should().Contain(version.ToString(), "the refusal has to say which version it refused");
    }

    [Theory]
    [MemberData(nameof(UnknownVersions))]
    public void Sign_RefusesAnUnknownVersion_AtMintTime(int version)
    {
        // The signing side propagates, the same as a degenerate payload: a loud failure at mint
        // time is the correct outcome for bytes nobody can vouch for.
        Action act = () => _ = CertificationRecordSigning.Sign(Record(version), HmacKey);

        act.Should().Throw<CanonicalPayloadException>();
    }

    [Theory]
    [MemberData(nameof(UnknownVersions))]
    public void VerifySignature_RefusesAnUnknownVersion_WithoutThrowing(int version)
    {
        // Verification refuses rather than throwing into its host, for both signature kinds.
        var record = Record(version) with
        {
            Signature = Convert.ToBase64String(new byte[32]),
            Ed25519Signature = Convert.ToBase64String(new byte[64]),
            Ed25519PublicKey = Convert.ToBase64String(new byte[32]),
        };

        CertificationRecordSigning.VerifySignature(record, HmacKey).Should().BeFalse();
        CertificationRecordEd25519.VerifySignature(record).Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(UnknownVersionsAboveZero))]
    public void TrustVerifier_ReportsAnUnknownVersion_UnderLegacy(int version)
    {
        // Legacy's floor is 0, which these clear, so the unknown-version check is the only
        // thing standing between this record and the signature comparison.
        var trust = CertificationTrustVerifier.Verify(Record(version), BrickSource, HmacKey, CertificationVerifyOptions.Legacy);

        trust.Trusted.Should().BeFalse();
        trust.FailureCode.Should().Be("schema-version-unknown");
    }

    [Fact]
    public void TrustVerifier_KeepsFloorPrecedence_EvenAtFloorZero()
    {
        // A negative version is below every floor, Legacy's 0 included, and the floor runs first.
        var trust = CertificationTrustVerifier.Verify(Record(-1), BrickSource, HmacKey, CertificationVerifyOptions.Legacy);

        trust.Trusted.Should().BeFalse();
        trust.FailureCode.Should().Be("schema-version-below-floor");
    }

    [Theory]
    [MemberData(nameof(UnknownVersionsAboveTheFloor))]
    public void TrustVerifier_ReportsAnUnknownVersion_ThatClearsTheFloor(int version)
    {
        // The regression being closed: these versions clear Default's floor of 2, so before
        // this check they reached the signature comparison in the v2 shape.
        var trust = CertificationTrustVerifier.Verify(Record(version), BrickSource, HmacKey, CertificationVerifyOptions.Default);

        trust.Trusted.Should().BeFalse();
        trust.FailureCode.Should().Be("schema-version-unknown");
    }

    [Theory]
    [MemberData(nameof(UnknownVersionsBelowTheFloor))]
    public void TrustVerifier_KeepsFloorPrecedence_ForAVersionBelowIt(int version)
    {
        // Floor first, unchanged: an explicit version below the floor reports the floor, so a
        // strict host's existing diagnostics do not change.
        var trust = CertificationTrustVerifier.Verify(Record(version), BrickSource, HmacKey, CertificationVerifyOptions.Default);

        trust.Trusted.Should().BeFalse();
        trust.FailureCode.Should().Be("schema-version-below-floor");
    }

    [Fact]
    public void UnknownVersion_IsRefusedBeforeTheSignatureIsExamined()
    {
        // Position pins the code: an unsigned record with an unknown version reports the version,
        // not record-unsigned, because the lane has to be established before anything about a
        // signature can be said.
        var trust = CertificationTrustVerifier.Verify(Record(3) with { Signed = false, Signature = null }, BrickSource, HmacKey, CertificationVerifyOptions.Legacy);

        trust.FailureCode.Should().Be("schema-version-unknown");
    }

    [Theory]
    [MemberData(nameof(UnknownVersions))]
    public void SecondTier_RefusesAnUnknownVersion(int version)
    {
        // CertificationRecordSigner.Verify gates the file store's re-verify-on-load; it returns
        // bool, so the diagnostic code is only available from CertificationTrustVerifier, but the
        // verdict has to be the same.
        var signer = new CertificationRecordSigner(HmacKey);
        var record = new CertificationRecord
        {
            Status = "PASS",
            Stage = "S0-S2",
            Admitted = true,
            Signed = true,
            Timestamp = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero),
            BrickId = "schema-version-probe",
            ContentHash = BrickContentHasher.ComputeSha256(BrickSource),
            SchemaVersion = version,
            Signature = Convert.ToBase64String(new byte[32]),
        };

        signer.Verify(record, CertificationVerifyOptions.Legacy).Should().BeFalse();
    }

    private static CertificationRecordData Record(int? version)
    {
        var record = new CertificationRecordData
        {
            Status = "PASS",
            Stage = "S0-S2",
            Admitted = true,
            Signed = true,
            Timestamp = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero),
            BrickId = "schema-version-probe",
            ContentHash = BrickContentHasher.ComputeSha256(BrickSource),
            EscapeRate = 0,
            TotalMutants = 1,
            SurvivingMutants = 0,
            KilledMutants = new[] { "m1" },
            SurvivingMutantIds = Array.Empty<string>(),
            Gate = "Ashlar.Infrastructure.Certification.CertificationGate",
            SchemaVersion = version,
        };

        // Signed under whichever lane the version selects, when it selects one. An unknown
        // version cannot be signed at all any more, so it carries a placeholder of the right
        // shape; every refusal asserted above happens before any comparison could reach it, and
        // the position test pins that it is the version, not the signature, being reported.
        return record with { Signature = SignWhenPossible(record) };
    }

    private static string SignWhenPossible(CertificationRecordData record) =>
        CertificationRecordSigning.IsKnownSchemaVersion(record.SchemaVersion)
            ? CertificationRecordSigning.Sign(record, HmacKey)
            : Convert.ToBase64String(new byte[32]);
}
