using System.Collections;
using Ashlar.Certification.Contracts;
using FluentAssertions;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>
/// The decisions the canonical payload writer makes that the golden corpus cannot state.
/// <para>
/// <see cref="CanonicalPayloadGoldenTests"/> pins the bytes for five records, which is what
/// makes the payload's shape, order and escaping a fact rather than an intention. It cannot
/// cover inputs a corpus entry is not allowed to hold — a repeated dictionary key, a null
/// inside a list declared non-null, a timestamp carrying a non-zero offset — and those are
/// exactly the inputs where a writer and a serializer could quietly disagree. Each is asserted
/// here against the behaviour the payload is documented to have.
/// </para>
/// </summary>
[Trait("Category", "Certification")]
public sealed class CanonicalPayloadEmitterTests
{
    private const string HmacKey = "canonical-payload-emitter-test-hmac";
    private const string BrickSource = "class CanonicalPayloadEmitterProbe { }";

    private static CertificationRecordData Minimal(int? schemaVersion = CertificationRecordData.TrustLoopSchemaVersion) =>
        new()
        {
            Status = "PASS",
            Stage = "S0-S2",
            Admitted = true,
            Signed = true,
            Timestamp = new DateTimeOffset(2026, 8, 12, 10, 0, 0, TimeSpan.Zero),
            BrickId = "emitter-brick",
            SchemaVersion = schemaVersion,
        };

    [Fact]
    public void Timestamp_IsTheRoundTripUtcString_NotTheWriterOwnDateTimeForm()
    {
        // The hazard this pins: Utf8JsonWriter has a DateTimeOffset overload that looks like the
        // obvious call and produces different bytes — "+00:00" instead of "Z", and trailing
        // fractional zeros trimmed. The offset here is deliberately not UTC, so the conversion
        // to UtcDateTime is pinned alongside the format.
        var record = Minimal() with { Timestamp = new DateTimeOffset(2026, 8, 12, 12, 0, 0, TimeSpan.FromHours(2)) };

        var payload = CertificationRecordSigning.BuildPayload(record);

        payload.Should().Contain("\"timestamp\":\"2026-08-12T10:00:00.0000000Z\"");
        payload.Should().NotContain("+00:00");
    }

    [Fact]
    public void NullInsideAMutantIdList_IsWrittenAsTheLiteralNull()
    {
        // The list element type is non-nullable, but nothing enforces that for a caller of the
        // published package, so the payload has to have an answer. It is the same answer the
        // rest of the payload gives for an absent value: the literal null, in ordinal sort
        // position, never a dropped element — dropping one would change the bytes without
        // changing the record.
        var record = Minimal() with { KilledMutants = new[] { "m-1", null!, "m-0" } };

        var payload = CertificationRecordSigning.BuildPayload(record);

        payload.Should().Contain("\"killedMutants\":[null,\"m-0\",\"m-1\"]");
    }

    [Fact]
    public void EmptyProposerParameters_AreWrittenAsAnEmptyObject()
    {
        // A proposer with no parameters is not the same record as no proposer at all, and the
        // two must not share a signature: one is an empty object, the other the literal null.
        var withEmpty = Minimal() with { Proposer = new CertificationProposer { Identity = "agent://p" } };

        CertificationRecordSigning.BuildPayload(withEmpty).Should().Contain("\"parameters\":{}");
        CertificationRecordSigning.BuildPayload(Minimal()).Should().Contain("\"proposer\":null");
    }

    [Fact]
    public void ProposerParameterKeys_AreEmittedVerbatimInOrdinalOrder()
    {
        // No key policy is configured, so parameter keys are content rather than shape: they are
        // not camel-cased, and their order is ordinal, which puts uppercase before lowercase.
        var record = Minimal() with
        {
            Proposer = new CertificationProposer
            {
                Identity = "agent://p",
                Parameters = new Dictionary<string, string>
                {
                    ["temperature"] = "0",
                    ["IterationLimit"] = "3",
                    ["beta"] = "x",
                },
            },
        };

        CertificationRecordSigning.BuildPayload(record).Should()
            .Contain("\"parameters\":{\"IterationLimit\":\"3\",\"beta\":\"x\",\"temperature\":\"0\"}");
    }

    [Fact]
    public void RepeatedProposerParameterKey_IsRefusedRatherThanWrittenTwice()
    {
        // A JSON writer does not reject a duplicate property name, so a payload carrying one
        // would be signed happily and would describe no single record. Only a custom
        // IReadOnlyDictionary can produce it — a Dictionary cannot — which is why it is asserted
        // here rather than left to a corpus entry.
        var record = Minimal() with
        {
            Proposer = new CertificationProposer
            {
                Identity = "agent://p",
                Parameters = new RepeatedKeyParameters(
                    new KeyValuePair<string, string>("seed", "1"),
                    new KeyValuePair<string, string>("seed", "2")),
            },
        };

        Action act = () => _ = CertificationRecordSigning.BuildPayload(record);

        act.Should().Throw<CanonicalPayloadException>().WithMessage("*same parameter key more than once*");
    }

    [Fact]
    public void RepeatedProposerParameterKey_RefusesVerificationInsteadOfThrowingIntoTheHost()
    {
        // The refusal contract, on the input above. Signing propagates, because a loud failure
        // at mint time is the correct outcome; every verification path answers instead.
        var bound = Minimal() with
        {
            ContentHash = BrickContentHasher.ComputeSha256(BrickSource),
            Signature = "not-the-point",
            Proposer = new CertificationProposer
            {
                Identity = "agent://p",
                Parameters = new RepeatedKeyParameters(
                    new KeyValuePair<string, string>("seed", "1"),
                    new KeyValuePair<string, string>("seed", "2")),
            },
        };

        CertificationRecordSigning.VerifySignature(bound, HmacKey).Should().BeFalse();

        var result = CertificationTrustVerifier.Verify(bound, BrickSource, HmacKey, CertificationVerifyOptions.Strict);
        result.Trusted.Should().BeFalse();
        result.FailureCode.Should().Be("payload-not-canonical");
    }

    [Fact]
    public void BothLanes_EmitEveryDeclaredNameForARecordCarryingAlmostNothing()
    {
        // The property-name sequence is an invariant of the lane rather than of the record, and
        // that is what lets the shape guard define a degenerate payload without guessing at
        // size. Asserted on the emptiest record either lane accepts.
        var v1 = CertificationRecordSigning.BuildPayload(Minimal(schemaVersion: null));
        var v2 = CertificationRecordSigning.BuildPayload(Minimal());

        CountProperties(v1).Should().Be(13);
        CountProperties(v2).Should().Be(20);
    }

    private static int CountProperties(string payload)
    {
        using var document = System.Text.Json.JsonDocument.Parse(payload);
        var count = 0;
        foreach (var _ in document.RootElement.EnumerateObject())
            count++;
        return count;
    }

    // A dictionary that yields the same key twice. Dictionary<,> cannot, which is the only
    // reason this exists.
    private sealed class RepeatedKeyParameters : IReadOnlyDictionary<string, string>
    {
        private readonly KeyValuePair<string, string>[] _entries;

        public RepeatedKeyParameters(params KeyValuePair<string, string>[] entries) => _entries = entries;

        public int Count => _entries.Length;

        public IEnumerable<string> Keys => _entries.Select(e => e.Key);

        public IEnumerable<string> Values => _entries.Select(e => e.Value);

        public string this[string key] => _entries.First(e => e.Key == key).Value;

        public bool ContainsKey(string key) => _entries.Any(e => e.Key == key);

        public bool TryGetValue(string key, out string value)
        {
            foreach (var entry in _entries)
            {
                if (entry.Key == key)
                {
                    value = entry.Value;
                    return true;
                }
            }

            value = null!;
            return false;
        }

        public IEnumerator<KeyValuePair<string, string>> GetEnumerator() =>
            ((IEnumerable<KeyValuePair<string, string>>)_entries).GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
