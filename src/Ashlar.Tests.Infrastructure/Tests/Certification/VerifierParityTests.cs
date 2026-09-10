using System.Text.Json;
using Ashlar.Certification.Contracts;
using FluentAssertions;
using NSec.Cryptography;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>
/// Verifier parity across target frameworks: the same record bytes must reach the same verdict
/// on every target, and a target that cannot evaluate a check must refuse rather than skip it.
/// <para>
/// <c>Ashlar.Certification.Contracts</c> ships netstandard2.0 alongside net8.0 and net10.0, and
/// the netstandard2.0 asset has no Ed25519 implementation. These tests pin the net8.0+ half of
/// the parity claim: a record that carries an Ed25519 signature which does not verify is refused
/// under EVERY options instance, <see cref="CertificationVerifyOptions.Legacy"/> included,
/// because the refusal keys on the signature's presence and not on any option. The
/// netstandard2.0 half is measured by <c>scripts/ns20-canonical-bytes-probe.sh</c>, which
/// executes the shipped netstandard2.0 asset under Mono against the same
/// <c>canonical-payloads.golden.json</c> the corpus theories below read, expecting
/// <c>ed25519-signature-unverifiable</c> where this side expects
/// <c>ed25519-signature-invalid</c>, and TRUSTED for the HMAC-only records on both. One corpus,
/// two readers: what the netstandard2.0 asset trusts is a subset of what net8.0 trusts, never a
/// contradiction, and that is asserted from both sides against the same bytes.
/// </para>
/// </summary>
[Trait("Category", "Certification")]
public sealed class VerifierParityTests
{
    private const string HmacKey = "verifier-parity-test-hmac";
    private const string BrickSource = "class ParityProbe { }";
    private const string GoldenFileName = "canonical-payloads.golden.json";

    private static readonly IReadOnlyDictionary<string, CertificationRecordData> Corpus = LoadCorpus();

    /// <summary>
    /// Options that do not require an Ed25519 signature, by name. A floor on its own is the same
    /// gap as Legacy spelled by hand, which is why it is probed alongside the preset: the refusal
    /// must key on the record, not on which preset happened to be chosen.
    /// </summary>
    public static TheoryData<string> LenientOptionNames => new() { "legacy", "floor-only" };

    [Theory]
    [MemberData(nameof(LenientOptionNames))]
    public void PresentSignatureThatDoesNotVerify_IsRefused_WhenNoSignatureWasRequired(string optionsName)
    {
        var signed = DualSign(BoundV2Record(), CreateEd25519Key());
        var corrupted = signed with { Ed25519Signature = "not-base64!" };

        CertificationRecordSigning.VerifySignature(corrupted, HmacKey).Should().BeTrue(
            "the Ed25519 signature is outside the HMAC payload, so the HMAC alone cannot notice it changed");
        var trust = CertificationTrustVerifier.Verify(corrupted, BrickSource, HmacKey, Options(optionsName));

        trust.Trusted.Should().BeFalse($"a present signature is evaluated under '{optionsName}' options whether or not one was required");
        trust.FailureCode.Should().Be("ed25519-signature-invalid");
    }

    [Fact]
    public void WellFormedSignatureThatDoesNotVerify_IsRefused_UnderLegacy()
    {
        // Not-Base64 is the cheap failure. A 64-byte signature that simply is not over these
        // bytes has to be refused by the signature math itself.
        var signed = DualSign(BoundV2Record(), CreateEd25519Key());
        var wrong = signed with { Ed25519Signature = Convert.ToBase64String(new byte[64]) };

        var trust = CertificationTrustVerifier.Verify(wrong, BrickSource, HmacKey, CertificationVerifyOptions.Legacy);

        trust.Trusted.Should().BeFalse();
        trust.FailureCode.Should().Be("ed25519-signature-invalid");
    }

    [Fact]
    public void SignatureWithoutPublicKey_IsRefused_UnderLegacy()
    {
        var record = BoundV2Record() with { Ed25519Signature = Convert.ToBase64String(new byte[64]) };
        var signed = record with { Signature = CertificationRecordSigning.Sign(record, HmacKey) };

        var trust = CertificationTrustVerifier.Verify(signed, BrickSource, HmacKey, CertificationVerifyOptions.Legacy);

        trust.Trusted.Should().BeFalse();
        trust.FailureCode.Should().Be("ed25519-key-missing", "a signature with no key needs no cryptography to refuse, so every target refuses it the same way");
    }

    [Fact]
    public void PresentSignature_IsEvaluatedBeforeTheContentHash()
    {
        // Position pins the failure code, not only the verdict: a record with both a bad
        // signature and a bad content binding reports the signature on every target, because
        // the netstandard2.0 refusal sits at the same point in the sequence as this one.
        var signed = DualSign(BoundV2Record(), CreateEd25519Key());
        var corrupted = signed with { Ed25519Signature = "not-base64!" };

        var trust = CertificationTrustVerifier.Verify(corrupted, "class SomethingElse { }", HmacKey, CertificationVerifyOptions.Legacy);

        trust.Trusted.Should().BeFalse();
        trust.FailureCode.Should().Be("ed25519-signature-invalid");
    }

    [Fact]
    public void HmacOnlyRecord_StaysTrusted_UnderLegacy()
    {
        // The positive control. Records with no Ed25519 signature keep today's behaviour on
        // every target; the HMAC-only shape is the one every target can evaluate completely.
        var signed = DualSign(BoundV2Record(), CreateEd25519Key());
        var hmacOnly = signed with { Ed25519Signature = null, Ed25519PublicKey = null, Signature = null };
        hmacOnly = hmacOnly with { Signature = CertificationRecordSigning.Sign(hmacOnly, HmacKey) };

        var trust = CertificationTrustVerifier.Verify(hmacOnly, BrickSource, HmacKey, CertificationVerifyOptions.Legacy);

        trust.Trusted.Should().BeTrue($"{trust.FailureCode}: {trust.Reason}");
    }

    // ---------- The golden corpus, read from both sides ----------

    public static TheoryData<string> GoldenCasesCarryingAnEd25519Signature => Names(c => !string.IsNullOrWhiteSpace(c.Ed25519Signature));

    public static TheoryData<string> GoldenAdmittedCases => Names(IsAdmittedPass);

    [Fact]
    public void GoldenCorpus_CoversBothParitySides()
    {
        // A theory over an empty selection passes without asserting anything, which is the same
        // failure mode as having no parity test at all. Assert the selections themselves.
        Corpus.Values.Should().Contain(c => IsAdmittedPass(c) && !string.IsNullOrWhiteSpace(c.Ed25519Signature),
            "the corpus must carry an admitted record whose Ed25519 signature is present but cannot verify");
        Corpus.Values.Should().Contain(c => IsAdmittedPass(c) && string.IsNullOrWhiteSpace(c.Ed25519Signature),
            "the corpus must carry an admitted HMAC-only record as the positive control");
    }

    [Theory]
    [MemberData(nameof(GoldenCasesCarryingAnEd25519Signature))]
    public void GoldenRecordCarryingAnEd25519Signature_IsRefused_UnderLegacy(string caseName)
    {
        // The corpus placeholders are, by design, not signatures over anything. The probe runs
        // this exact record through the netstandard2.0 asset and expects
        // ed25519-signature-unverifiable; here the signature math runs and says invalid. Both
        // refuse, so neither target trusts a record the other would not.
        var bound = Bind(Corpus[caseName]);

        var trust = CertificationTrustVerifier.Verify(bound, BrickSource, HmacKey, CertificationVerifyOptions.Legacy);

        trust.Trusted.Should().BeFalse();
        trust.FailureCode.Should().Be("ed25519-signature-invalid");
    }

    [Theory]
    [MemberData(nameof(GoldenAdmittedCases))]
    public void GoldenRecordWithEd25519Stripped_IsTrusted_UnderLegacy(string caseName)
    {
        // The same corpus record as an HMAC-only record: trusted here and trusted on the
        // netstandard2.0 asset, so the parity claim is a subset, not an empty set.
        var hmacOnly = Bind(Corpus[caseName] with { Ed25519Signature = null, Ed25519PublicKey = null });

        var trust = CertificationTrustVerifier.Verify(hmacOnly, BrickSource, HmacKey, CertificationVerifyOptions.Legacy);

        trust.Trusted.Should().BeTrue($"{trust.FailureCode}: {trust.Reason}");
    }

    // ---------- helpers ----------

    private static CertificationVerifyOptions Options(string name) => name switch
    {
        "legacy" => CertificationVerifyOptions.Legacy,
        "floor-only" => new CertificationVerifyOptions { MinimumSchemaVersion = CertificationRecordData.TrustLoopSchemaVersion },
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "unknown options name"),
    };

    private static bool IsAdmittedPass(CertificationRecordData record) =>
        record.Admitted && record.Signed && string.Equals(record.Status, "PASS", StringComparison.Ordinal);

    private static TheoryData<string> Names(Func<CertificationRecordData, bool> select)
    {
        var data = new TheoryData<string>();
        foreach (var pair in Corpus.Where(p => IsAdmittedPass(p.Value) && select(p.Value)).OrderBy(p => p.Key, StringComparer.Ordinal))
            data.Add(pair.Key);
        return data;
    }

    /// <summary>Binds a corpus record to <see cref="BrickSource"/> and re-signs the HMAC, leaving the Ed25519 fields as the corpus has them.</summary>
    private static CertificationRecordData Bind(CertificationRecordData record)
    {
        var bound = record with { ContentHash = BrickContentHasher.ComputeSha256(BrickSource), Signature = null };
        return bound with { Signature = CertificationRecordSigning.Sign(bound, HmacKey) };
    }

    private static CertificationRecordData BoundV2Record() => new()
    {
        Status = "PASS",
        Stage = "S0-S2",
        Admitted = true,
        Signed = true,
        Timestamp = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero),
        BrickId = "parity-brick",
        ContentHash = BrickContentHasher.ComputeSha256(BrickSource),
        EscapeRate = 0,
        TotalMutants = 1,
        SurvivingMutants = 0,
        KilledMutants = new[] { "m1" },
        SurvivingMutantIds = Array.Empty<string>(),
        Gate = "Ashlar.Infrastructure.Certification.CertificationGate",
        SchemaVersion = CertificationRecordData.TrustLoopSchemaVersion,
        Inputs = new[] { new CertificationInput { Kind = "witness", Id = "parity-brick", Hash = "witness-hash" } },
    };

    private static CertificationRecordData DualSign(CertificationRecordData record, byte[] privateKey)
    {
        var withKey = record with { Ed25519PublicKey = CertificationRecordEd25519.DerivePublicKeyBase64(privateKey) };
        return withKey with
        {
            Signature = CertificationRecordSigning.Sign(withKey, HmacKey),
            Ed25519Signature = CertificationRecordEd25519.Sign(withKey, privateKey),
        };
    }

    private static byte[] CreateEd25519Key()
    {
        using var key = Key.Create(
            SignatureAlgorithm.Ed25519,
            new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
        return key.Export(KeyBlobFormat.RawPrivateKey);
    }

    private static IReadOnlyDictionary<string, CertificationRecordData> LoadCorpus()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Tests", "Certification", GoldenFileName);
        if (!File.Exists(path))
            throw new FileNotFoundException($"Golden canonical payload corpus not found at '{path}'.", path);

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var corpus = new Dictionary<string, CertificationRecordData>(StringComparer.Ordinal);
        foreach (var element in document.RootElement.GetProperty("cases").EnumerateArray())
        {
            corpus[element.GetProperty("name").GetString()!] =
                JsonSerializer.Deserialize<CertificationRecordData>(element.GetProperty("record").GetRawText(), options)!;
        }

        return corpus;
    }
}
