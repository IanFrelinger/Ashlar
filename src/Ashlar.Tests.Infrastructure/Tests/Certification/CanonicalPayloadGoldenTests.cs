using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ashlar.Certification.Contracts;
using FluentAssertions;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>
/// Byte-level pin on the canonical signing payload for every reachable payload version.
/// <para>
/// These bytes are the message every HMAC and Ed25519 certification signature is computed
/// over, so their stability is the whole basis on which a signature written yesterday still
/// verifies today. They are written field by field rather than serialized from an object
/// graph, so their order and their names are a property of this repository — but their
/// encoding is still <c>Utf8JsonWriter</c>'s, and the package ships three target frameworks
/// against three different <c>System.Text.Json</c> builds. "The bytes did not change" stays an
/// assertion rather than something that can be reasoned about. This suite makes it one.
/// </para>
/// <para>
/// The corpus lives in <c>canonical-payloads.golden.json</c> rather than in this file because
/// <c>scripts/ns20-canonical-bytes-probe.sh</c> (the netstandard2.0 asset under Mono) and
/// <c>scripts/portability/net9-probe.sh</c> (the net8.0 asset on the 9.0 runtime) read the
/// same file to check those assets against the same constants. Independently typed copies
/// would drift, and the cross-target equality claim would quietly evaporate with them.
/// </para>
/// </summary>
[Trait("Category", "Certification")]
public sealed class CanonicalPayloadGoldenTests
{
    private const string GoldenFileName = "canonical-payloads.golden.json";

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
    public void CanonicalPayload_IsByteIdenticalToGolden(string caseName)
    {
        var golden = Corpus[caseName];

        var payload = CertificationRecordSigning.BuildPayload(golden.Record);

        // The string comparison comes first on purpose: it is the assertion that prints a
        // readable diff. The length and digest below turn "readable" into "byte-exact", so a
        // change that a string comparison could normalise away still fails.
        payload.Should().Be(
            golden.Payload,
            "the canonical payload for '{0}' backs every signature already written over it; "
            + "changing these bytes invalidates them",
            caseName);

        var bytes = Encoding.UTF8.GetBytes(payload);
        bytes.Length.Should().Be(golden.PayloadByteLength, "the signed message is bytes, not characters");
        Sha256Hex(bytes).Should().Be(
            golden.PayloadSha256,
            "SHA-256 of the UTF-8 canonical payload for '{0}'; this is the same constant "
            + "scripts/ns20-canonical-bytes-probe.sh checks the netstandard2.0 asset against. Actual payload was: {1}",
            caseName,
            payload);
    }

    [Fact]
    public void GoldenCorpus_CoversBothPayloadVersions()
    {
        // A theory over an empty or half-empty corpus passes without asserting anything, which
        // is the same failure mode as having no golden test at all. Assert the corpus itself.
        Corpus.Should().NotBeEmpty();
        Corpus.Values.Should().Contain(c => c.Record.SchemaVersion == null, "the v1 lane is reachable through the published package API");
        Corpus.Values.Should().Contain(c => c.Record.SchemaVersion != null, "v2 is the shape every live minter emits");
        Corpus.Values.Should().OnlyContain(
            c => c.Payload.Length > 2,
            "an empty object is never a legitimate canonical payload — every declared property is emitted whatever its value");
    }

    [Fact]
    public void Sign_ComputesTheHmacOverExactlyTheGoldenBytes()
    {
        // Pins the binding between the golden bytes and the signature, without introducing a
        // second constant: the expected signature is derived from the golden payload string,
        // so this fails if Sign ever hashes anything other than what BuildPayload returned.
        const string key = "canonical-payload-golden-test-hmac";
        var golden = Corpus["v2-populated"];
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(key));
        var expected = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(golden.Payload)));

        CertificationRecordSigning.Sign(golden.Record, key).Should().Be(expected);
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
            throw new FileNotFoundException($"Golden canonical payload corpus not found at '{path}'.", path);

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var corpus = new Dictionary<string, GoldenCase>(StringComparer.Ordinal);
        foreach (var element in document.RootElement.GetProperty("cases").EnumerateArray())
        {
            var name = element.GetProperty("name").GetString()!;
            corpus[name] = new GoldenCase(
                JsonSerializer.Deserialize<CertificationRecordData>(element.GetProperty("record").GetRawText(), options)!,
                element.GetProperty("payload").GetString()!,
                element.GetProperty("payloadSha256").GetString()!,
                element.GetProperty("payloadByteLength").GetInt32());
        }

        return corpus;
    }

    private sealed record GoldenCase(
        CertificationRecordData Record,
        string Payload,
        string PayloadSha256,
        int PayloadByteLength);
}
