using System.Text.Json;
using Ashlar.Certification.Contracts;
using FluentAssertions;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>
/// The canonical payload must never be signed, or accepted, unless it is the shape its lane
/// declares.
/// <para>
/// The payload is produced by reflection-based <c>System.Text.Json</c>. That is not a
/// guarantee: reflection-based serialization does not survive trimming or ahead-of-time
/// publishing, and under those publish modes the payload can come out as the empty object
/// <c>{}</c> — no exception, no warning. Those bytes are the message every signature is
/// computed over, so an empty or short payload does not weaken a signature, it moves the
/// signature onto something that is not the record.
/// </para>
/// <para>
/// The guard is exercised here directly rather than through <c>BuildPayload</c> because the
/// publish configurations that make the serializer degrade cannot be reproduced inside a test
/// host — the assembly under test is loaded untrimmed by definition. What the golden corpus in
/// <see cref="CanonicalPayloadGoldenTests"/> proves alongside it is the other half: that the
/// declared shape is the shape the serializer really emits, so the guard rejects nothing
/// legitimate.
/// </para>
/// </summary>
[Trait("Category", "Certification")]
public sealed class CanonicalPayloadGuardTests
{
    private const string MinimalV1Payload =
        "{\"status\":\"FAIL\",\"stage\":\"S0\",\"admitted\":false,\"signed\":false," +
        "\"timestamp\":\"2026-01-02T03:04:05.0000000Z\",\"brickId\":\"guard-brick\"," +
        "\"contentHash\":null,\"escapeRate\":null,\"totalMutants\":null,\"survivingMutants\":null," +
        "\"killedMutants\":[],\"survivingMutantIds\":[],\"reason\":null}";

    private const string MinimalV2Payload =
        "{\"schemaVersion\":2,\"status\":\"FAIL\",\"stage\":\"load\",\"admitted\":false,\"signed\":false," +
        "\"timestamp\":\"2026-01-02T03:04:05.0000000Z\",\"brickId\":\"guard-brick\"," +
        "\"contentHash\":null,\"escapeRate\":null,\"totalMutants\":null,\"survivingMutants\":null," +
        "\"killedMutants\":[],\"survivingMutantIds\":[],\"reason\":null,\"gate\":null," +
        "\"gatesPassed\":[],\"inputs\":[],\"proposer\":null,\"attempts\":[],\"ed25519PublicKey\":null}";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmptyObject_IsRefused(bool versioned)
    {
        Action act = () => _ = CertificationRecordSigning.EnsureCanonical("{}", versioned);

        act.Should().Throw<CanonicalPayloadException>(
                "an empty payload signs nothing about the record it claims to cover")
            .WithMessage("*not its declared shape*")
            .Which.Message.Should().Contain(versioned ? "v2" : "v1", "the refusal has to say which lane it refused");
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("\"\"")]
    [InlineData("0")]
    public void NonObjectPayload_IsRefused(string payload)
    {
        Action act = () => _ = CertificationRecordSigning.EnsureCanonical(payload, versioned: true);

        act.Should().Throw<CanonicalPayloadException>();
    }

    [Fact]
    public void UnparseablePayload_IsRefused()
    {
        Action act = () => _ = CertificationRecordSigning.EnsureCanonical("{\"status\":", versioned: false);

        act.Should().Throw<CanonicalPayloadException>().Which.InnerException.Should().BeAssignableTo<JsonException>();
    }

    [Fact]
    public void MinimalPayloads_AreAccepted()
    {
        // The negative signal is the property-NAME set, never size: no ignore policy is
        // configured, so a record with nothing but its required members still emits every
        // declared property with a null value. A guard that keyed on length or emptiness would
        // refuse these, which are entirely legitimate certificates.
        CertificationRecordSigning.EnsureCanonical(MinimalV1Payload, versioned: false).Should().Be(MinimalV1Payload);
        CertificationRecordSigning.EnsureCanonical(MinimalV2Payload, versioned: true).Should().Be(MinimalV2Payload);
    }

    [Fact]
    public void DroppedProperty_IsRefused()
    {
        var truncated = MinimalV1Payload.Replace(",\"reason\":null", string.Empty);

        Action act = () => _ = CertificationRecordSigning.EnsureCanonical(truncated, versioned: false);

        act.Should().Throw<CanonicalPayloadException>("a partially serialized payload covers less of the record than the signature claims")
            .Which.Message.Should().Contain("13").And.Contain("reason");
    }

    [Fact]
    public void ReorderedProperties_AreRefused()
    {
        // Reordering keeps every name and every value, so nothing about the record is lost —
        // but the bytes change, which silently invalidates every signature already written.
        var reordered = MinimalV1Payload
            .Replace("{\"status\":\"FAIL\",\"stage\":\"S0\"", "{\"stage\":\"S0\",\"status\":\"FAIL\"");

        Action act = () => _ = CertificationRecordSigning.EnsureCanonical(reordered, versioned: false);

        act.Should().Throw<CanonicalPayloadException>();
    }

    [Fact]
    public void ExtraProperty_IsRefused()
    {
        var extended = MinimalV2Payload.Replace("\"ed25519PublicKey\":null}", "\"ed25519PublicKey\":null,\"extra\":1}");

        Action act = () => _ = CertificationRecordSigning.EnsureCanonical(extended, versioned: true);

        act.Should().Throw<CanonicalPayloadException>();
    }

    [Fact]
    public void WrongLane_IsRefused()
    {
        // The schema version selects the payload, so a v1 body checked as v2 (or the reverse)
        // is exactly the shape mismatch a downgraded or upgraded record would produce.
        Action actV1AsV2 = () => _ = CertificationRecordSigning.EnsureCanonical(MinimalV1Payload, versioned: true);
        Action actV2AsV1 = () => _ = CertificationRecordSigning.EnsureCanonical(MinimalV2Payload, versioned: false);

        actV1AsV2.Should().Throw<CanonicalPayloadException>();
        actV2AsV1.Should().Throw<CanonicalPayloadException>();
    }

    public static TheoryData<string, string> DegenerateNestedShapes => new()
    {
        { "gatesPassed", "\"gatesPassed\":[{}]" },
        { "inputs", "\"inputs\":[{}]" },
        { "proposer", "\"proposer\":{}" },
        { "attempts", "\"attempts\":[{}]" },
    };

    [Theory]
    [MemberData(nameof(DegenerateNestedShapes))]
    public void DegenerateNestedShape_IsRefused(string member, string replacement)
    {
        // The trust-loop evidence lives in these nested shapes. A payload that keeps all twenty
        // top-level names while emptying them out would still look plausible from the outside.
        var payload = MinimalV2Payload.Replace(NestedOriginal(member), replacement);
        payload.Should().NotBe(MinimalV2Payload, "the fixture must actually have been modified");

        Action act = () => _ = CertificationRecordSigning.EnsureCanonical(payload, versioned: true);

        act.Should().Throw<CanonicalPayloadException>().Which.Message.Should().Contain(member);
    }

    [Fact]
    public void ProposerParameters_AreValuesRatherThanShape()
    {
        // Parameter keys are caller-supplied and emitted verbatim, so they are content, not
        // structure. Treating them as structure would make the guard refuse legitimate records.
        var withParameters = MinimalV2Payload.Replace(
            "\"proposer\":null",
            "\"proposer\":{\"identity\":\"agent://p\",\"parameters\":{\"Anything\":\"1\",\"at all\":\"2\"},\"seed\":null}");
        var withoutParameters = MinimalV2Payload.Replace(
            "\"proposer\":null",
            "\"proposer\":{\"identity\":\"agent://p\",\"parameters\":{},\"seed\":null}");

        CertificationRecordSigning.EnsureCanonical(withParameters, versioned: true).Should().Be(withParameters);
        CertificationRecordSigning.EnsureCanonical(withoutParameters, versioned: true).Should().Be(withoutParameters);
    }

    [Fact]
    public void GuardedPayload_IsReturnedUnchanged()
    {
        // Byte-neutrality on the legitimate path, asserted where the guard runs. The golden
        // corpus asserts the same thing against every pinned record.
        var record = new CertificationRecordData
        {
            Status = "PASS",
            Stage = "S0-S2",
            Admitted = true,
            Signed = true,
            Timestamp = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
            BrickId = "guard-brick",
            SchemaVersion = CertificationRecordData.TrustLoopSchemaVersion,
        };

        var payload = CertificationRecordSigning.BuildPayload(record);

        CertificationRecordSigning.EnsureCanonical(payload, versioned: true).Should().BeSameAs(payload);
    }

    private static string NestedOriginal(string member) => member switch
    {
        "gatesPassed" => "\"gatesPassed\":[]",
        "inputs" => "\"inputs\":[]",
        "proposer" => "\"proposer\":null",
        "attempts" => "\"attempts\":[]",
        _ => throw new ArgumentOutOfRangeException(nameof(member), member, "unknown nested member"),
    };
}
