using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ashlar.Core.Application.Certification.Models;
using Ashlar.Infrastructure.Certification.Composition;
using FluentAssertions;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>
/// Byte-level pin on the canonical signing payload for composition certification records.
/// <para>
/// These bytes are the message every composition admission signature is computed over, and
/// <c>CompositionCertificationGate</c> mints one for every admitted composition. Until this
/// suite existed the brick lane was pinned byte-for-byte and this lane was pinned by nothing:
/// its payload could change shape, order or spelling and the only thing that would notice is a
/// stored certificate failing to verify long after the change shipped.
/// </para>
/// <para>
/// The corpus lives in <c>composition-payloads.golden.json</c> beside the brick one, in the
/// same shape and with the same regeneration ritual, so a reader who knows one knows both.
/// Doubles are restricted to exactly representable values there for the reason that file
/// states.
/// </para>
/// </summary>
[Trait("Category", "Certification")]
public sealed class CompositionCanonicalPayloadGoldenTests
{
    private const string GoldenFileName = "composition-payloads.golden.json";

    private static readonly IReadOnlyDictionary<string, GoldenCase> Corpus = LoadCorpus();

    public static TheoryData<string> GoldenCaseNames
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var name in Corpus.Keys.OrderBy(n => n, StringComparer.Ordinal))
                data.Add(name);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(GoldenCaseNames))]
    public void CompositionCanonicalPayload_IsByteIdenticalToGolden(string caseName)
    {
        var golden = Corpus[caseName];

        var payload = CompositionCertificationRecordSigner.BuildPayload(golden.Record);

        // The string comparison comes first on purpose: it is the assertion that prints a
        // readable diff. The length and digest below turn "readable" into "byte-exact", so a
        // change that a string comparison could normalise away still fails.
        payload.Should().Be(
            golden.Payload,
            "the canonical payload for '{0}' backs every composition signature already written "
            + "over it; changing these bytes invalidates them",
            caseName);

        var bytes = Encoding.UTF8.GetBytes(payload);
        bytes.Length.Should().Be(golden.PayloadByteLength, "the signed message is bytes, not characters");
        Sha256Hex(bytes).Should().Be(
            golden.PayloadSha256,
            "SHA-256 of the UTF-8 canonical composition payload for '{0}'. Actual payload was: {1}",
            caseName,
            payload);
    }

    [Fact]
    public void GoldenCorpus_CoversAPopulatedAndAMinimalRecord()
    {
        // A theory over an empty or half-empty corpus passes without asserting anything, which
        // is the same failure mode as having no golden test at all. Assert the corpus itself.
        Corpus.Should().NotBeEmpty();
        Corpus.Values.Should().Contain(c => c.Record.CompositionEscapeRate != null, "a populated record is the shape the gate mints");
        Corpus.Values.Should().Contain(c => c.Record.CompositionEscapeRate == null, "a refusal record carries nulls and must still carry every name");
        Corpus.Values.Should().OnlyContain(
            c => c.Payload.Length > 2,
            "an empty object is never a legitimate canonical payload — every declared property is emitted whatever its value");
    }

    [Fact]
    public void Payload_ExcludesTheSignatureAndTheGate()
    {
        // Stated as its own assertion rather than left implicit in the byte comparison: the
        // populated fixture supplies both fields as sentinels, so if either ever entered the
        // payload the record would sign a value it does not cover today and every stored
        // signature would stop verifying.
        var payload = CompositionCertificationRecordSigner.BuildPayload(Corpus["composition-populated"].Record);

        payload.Should().NotContain("MUST-NOT-APPEAR");
    }

    [Fact]
    public void Sign_ComputesTheHmacOverExactlyTheGoldenBytes()
    {
        // Pins the binding between the golden bytes and the signature, without introducing a
        // second constant: the expected signature is derived from the golden payload string, so
        // this fails if Sign ever hashes anything other than what BuildPayload returned.
        const string key = "composition-canonical-payload-golden-test-hmac";
        var golden = Corpus["composition-populated"];
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(key));
        var expected = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(golden.Payload)));

        new CompositionCertificationRecordSigner(hmacKey: key).Sign(golden.Record).Should().Be(expected);
    }

    private static string Sha256Hex(byte[] bytes)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(bytes));
    }

    private static IReadOnlyDictionary<string, GoldenCase> LoadCorpus()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Tests", "Certification", GoldenFileName);
        if (!File.Exists(path))
            throw new FileNotFoundException($"Golden composition payload corpus not found at '{path}'.", path);

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var corpus = new Dictionary<string, GoldenCase>(StringComparer.Ordinal);
        foreach (var element in document.RootElement.GetProperty("cases").EnumerateArray())
        {
            var name = element.GetProperty("name").GetString()!;
            corpus[name] = new GoldenCase(
                JsonSerializer.Deserialize<CompositionCertificationRecord>(element.GetProperty("record").GetRawText(), options)!,
                element.GetProperty("payload").GetString()!,
                element.GetProperty("payloadSha256").GetString()!,
                element.GetProperty("payloadByteLength").GetInt32());
        }

        return corpus;
    }

    private sealed record GoldenCase(
        CompositionCertificationRecord Record,
        string Payload,
        string PayloadSha256,
        int PayloadByteLength);
}
