using Ashlar.Certification.Contracts;
using FluentAssertions;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

[Trait("Category", "Certification")]
public sealed class LabelledCertificationRecordTests
{
    private const string HmacKey = "label-schema-test-key";
    private static readonly byte[] PrivateKey = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();

    private static CertificationRecordData Record(string? label = "Secret") => new()
    {
        SchemaVersion = CertificationRecordData.LabelSchemaVersion,
        HighestDataLabel = label,
        Status = "PASS", Stage = "label-gate", Admitted = true, Signed = true,
        Timestamp = new DateTimeOffset(2026, 10, 10, 0, 0, 0, TimeSpan.Zero),
        BrickId = "labelled-skill", ContentHash = "bound-content-hash",
        Ed25519PublicKey = CertificationRecordEd25519.DerivePublicKeyBase64(PrivateKey)
    };

    [Theory]
    [InlineData("Public")]
    [InlineData("Internal")]
    [InlineData("Confidential")]
    [InlineData("Secret")]
    [InlineData("TopSecret")]
    [InlineData("SystemHigh")]
    public void Both_signatures_bind_the_declared_label(string label)
    {
        var record = Record(label);
        var signed = record with
        {
            Signature = CertificationRecordSigning.Sign(record, HmacKey),
            Ed25519Signature = CertificationRecordEd25519.Sign(record, PrivateKey)
        };
        CertificationRecordSigning.VerifySignature(signed, HmacKey).Should().BeTrue();
        CertificationRecordEd25519.VerifySignature(signed).Should().BeTrue();
        var tampered = signed with { HighestDataLabel = label == "Public" ? "SystemHigh" : "Public" };
        CertificationRecordSigning.VerifySignature(tampered, HmacKey).Should().BeFalse();
        CertificationRecordEd25519.VerifySignature(tampered).Should().BeFalse();
        var downgraded = signed with { SchemaVersion = 2 };
        CertificationRecordSigning.VerifySignature(downgraded, HmacKey).Should().BeFalse();
        CertificationRecordEd25519.VerifySignature(downgraded).Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("secret")]
    [InlineData("SystemHigh ")]
    [InlineData("Unknown")]
    [InlineData("Secret//C:A")]
    public void V3_refuses_missing_or_noncanonical_level_declarations(string? label)
    {
        Action mint = () => CertificationRecordSigning.Sign(Record(label), HmacKey);
        mint.Should().Throw<CanonicalPayloadException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData(2)]
    public void Older_lanes_cannot_carry_unsigned_label_claims(int? schema)
    {
        var legacy = Record() with { SchemaVersion = schema, HighestDataLabel = null };
        var signed = legacy with { Signature = CertificationRecordSigning.Sign(legacy, HmacKey) };
        CertificationRecordSigning.VerifySignature(signed, HmacKey).Should().BeTrue();
        CertificationRecordSigning.VerifySignature(signed with { HighestDataLabel = "Public" }, HmacKey).Should().BeFalse();
    }

    [Fact]
    public void Domain_mapper_preserves_the_label_bound_by_the_signature()
    {
        var model = new Ashlar.Core.Application.Certification.Models.CertificationRecord
        {
            SchemaVersion = 3, HighestDataLabel = "SystemHigh", Status = "PASS", Stage = "label-gate",
            Admitted = true, Signed = true, Timestamp = DateTimeOffset.UnixEpoch, BrickId = "mapper-probe"
        };
        var dto = Ashlar.Core.Application.Certification.CertificationRecordMapper.ToData(model);
        dto.HighestDataLabel.Should().Be("SystemHigh");
        CertificationRecordSigning.BuildPayload(dto).Should().EndWith("\"highestDataLabel\":\"SystemHigh\"}");
    }
}
