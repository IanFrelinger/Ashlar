using FluentAssertions;
using Ashlar.Certification.Contracts;
using Ashlar.Core.Application.Certification.Models;
using Ashlar.Core.Domain.Bricks;
using Ashlar.Core.Domain.Execution;
using Ashlar.Infrastructure.Certification;
using Ashlar.Tests.Infrastructure.Certification.Fixtures;
using NSec.Cryptography;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>
/// Trust-loop certificate schema (v2) and Ed25519 dual-write tests: legacy (v1)
/// records keep their exact payload bytes, v2 records sign the extended payload
/// (including <c>Gate</c>), and the Ed25519 signature is enforced whenever present.
/// </summary>
[Trait("Category", "Certification")]
public sealed class TrustLoopRecordSchemaTests
{
    private const string HmacKey = "trust-loop-schema-test-hmac";
    private const string BrickSource = "class TrustLoopProbe { }";

    // ---------- Legacy (v1) compatibility ----------

    [Fact]
    public void LegacyRecord_PayloadBytes_AreUnchanged()
    {
        var record = BuildLegacyRecord();

        var payload = CertificationRecordSigning.BuildPayload(record);

        payload.Should().Be(
            "{\"status\":\"PASS\",\"stage\":\"S0-S2\",\"admitted\":true,\"signed\":true," +
            "\"timestamp\":\"2026-01-02T03:04:05.0000000Z\",\"brickId\":\"legacy-brick\"," +
            "\"contentHash\":\"hash-abc\",\"escapeRate\":0,\"totalMutants\":3,\"survivingMutants\":0," +
            "\"killedMutants\":[\"m1\",\"m2\"],\"survivingMutantIds\":[],\"reason\":null}",
            "pre-trust-loop sidecar signatures are only valid if the v1 payload stays byte-identical");
    }

    [Fact]
    public void LegacyRecord_SignsAndVerifies()
    {
        var record = BuildLegacyRecord() with { ContentHash = BrickContentHasher.ComputeSha256(BrickSource) };
        var signed = record with { Signature = CertificationRecordSigning.Sign(record, HmacKey) };

        var trust = CertificationTrustVerifier.Verify(signed, BrickSource, HmacKey, CertificationVerifyOptions.Legacy);

        trust.Trusted.Should().BeTrue($"{trust.FailureCode}: {trust.Reason}");
    }

    [Fact]
    public void LegacyRecord_GateTamper_DoesNotInvalidateSignature()
    {
        // Documents the known v1 limitation the v2 payload closes: Gate is outside
        // the legacy payload, so v1 records cannot detect Gate tampering.
        var record = BuildLegacyRecord();
        var signed = record with { Signature = CertificationRecordSigning.Sign(record, HmacKey) };

        var tampered = signed with { Gate = "forged-gate" };

        CertificationRecordSigning.VerifySignature(tampered, HmacKey).Should().BeTrue(
            "v1 payloads do not cover Gate; this pins the legacy behavior the v2 schema fixes");
    }

    [Fact]
    public void LegacyJson_WithoutTrustLoopFields_DeserializesToLegacyDefaults()
    {
        const string json =
            "{\"status\":\"PASS\",\"stage\":\"S0-S2\",\"admitted\":true,\"signed\":true," +
            "\"timestamp\":\"2026-01-02T03:04:05+00:00\",\"brickId\":\"legacy-brick\"}";

        var record = System.Text.Json.JsonSerializer.Deserialize<CertificationRecordData>(
            json,
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        record.SchemaVersion.Should().BeNull();
        record.GatesPassed.Should().BeEmpty();
        record.Inputs.Should().BeEmpty();
        record.Proposer.Should().BeNull();
        record.Attempts.Should().BeEmpty();
        record.Ed25519Signature.Should().BeNull();
    }

    // ---------- v2 payload coverage ----------

    public static TheoryData<string, Func<CertificationRecordData, CertificationRecordData>> TrustLoopFieldTampers => new()
    {
        { "gate", r => r with { Gate = "forged-gate" } },
        { "schema-version", r => r with { SchemaVersion = null } },
        { "gates-passed", r => r with { GatesPassed = new[] { new CertificationGatePass { Name = "forged-gate-pass" } } } },
        { "inputs", r => r with { Inputs = new[] { new CertificationInput { Kind = "witness", Id = "x", Hash = "forged" } } } },
        { "proposer", r => r with { Proposer = new CertificationProposer { Identity = "agent://forged" } } },
        { "attempts", r => r with { Attempts = new[] { new CertificationAttempt { Index = 1, Outcome = "pass" } } } },
    };

    [Theory]
    [MemberData(nameof(TrustLoopFieldTampers))]
    public void V2Record_TamperingTrustLoopField_InvalidatesSignature(
        string field,
        Func<CertificationRecordData, CertificationRecordData> tamper)
    {
        var record = BuildV2Record();
        var signed = record with { Signature = CertificationRecordSigning.Sign(record, HmacKey) };

        var tampered = tamper(signed);

        CertificationRecordSigning.VerifySignature(tampered, HmacKey).Should().BeFalse(
            $"the v2 payload must cover '{field}'");
    }

    [Fact]
    public void V1Record_UpgradedToV2WithoutResigning_IsInvalid()
    {
        var record = BuildLegacyRecord();
        var signed = record with { Signature = CertificationRecordSigning.Sign(record, HmacKey) };

        var upgraded = signed with { SchemaVersion = CertificationRecordData.TrustLoopSchemaVersion };

        CertificationRecordSigning.VerifySignature(upgraded, HmacKey).Should().BeFalse(
            "changing the schema version switches the payload, so the old signature must not carry over");
    }

    [Fact]
    public void V2Record_HmacOnly_Verifies()
    {
        var record = BuildV2Record() with { ContentHash = BrickContentHasher.ComputeSha256(BrickSource) };
        var signed = record with { Signature = CertificationRecordSigning.Sign(record, HmacKey) };

        var trust = CertificationTrustVerifier.Verify(signed, BrickSource, HmacKey, CertificationVerifyOptions.Legacy);

        trust.Trusted.Should().BeTrue(
            $"dual-write is a transition window; HMAC-only v2 records are valid under Legacy ({trust.FailureCode}: {trust.Reason})");
    }

    // ---------- Ed25519 dual-write ----------

    [Fact]
    public void DualSignedRecord_Verifies()
    {
        var (privateKey, publicKey) = CreateEd25519Key();
        var signed = DualSign(BuildV2Record() with { ContentHash = BrickContentHasher.ComputeSha256(BrickSource) }, privateKey);

        signed.Ed25519PublicKey.Should().Be(publicKey);
        var trust = CertificationTrustVerifier.Verify(signed, BrickSource, HmacKey);

        trust.Trusted.Should().BeTrue($"{trust.FailureCode}: {trust.Reason}");
    }

    [Fact]
    public void DualSignedRecord_SignatureFromDifferentKey_IsRejected()
    {
        // The HMAC payload cannot cover the Ed25519 signature bytes themselves, so a
        // swapped signature passes the HMAC check; the Ed25519 check must catch it.
        var (privateKey, _) = CreateEd25519Key();
        var (otherPrivateKey, _) = CreateEd25519Key();
        var signed = DualSign(BuildV2Record() with { ContentHash = BrickContentHasher.ComputeSha256(BrickSource) }, privateKey);

        var swapped = signed with
        {
            Ed25519Signature = CertificationRecordEd25519.Sign(signed with { Signature = null, Ed25519Signature = null }, Convert.FromBase64String(otherPrivateKey))
        };

        CertificationRecordSigning.VerifySignature(swapped, HmacKey).Should().BeTrue(
            "the swapped signature is outside the HMAC payload, which is exactly why the Ed25519 check must run");
        var trust = CertificationTrustVerifier.Verify(swapped, BrickSource, HmacKey);
        trust.Trusted.Should().BeFalse();
        trust.FailureCode.Should().Be("ed25519-signature-invalid");
    }

    [Fact]
    public void DualSignedRecord_GarbageSignature_IsRejected()
    {
        var (privateKey, _) = CreateEd25519Key();
        var signed = DualSign(BuildV2Record() with { ContentHash = BrickContentHasher.ComputeSha256(BrickSource) }, privateKey);

        var trust = CertificationTrustVerifier.Verify(signed with { Ed25519Signature = "not-base64!" }, BrickSource, HmacKey);

        trust.Trusted.Should().BeFalse();
        trust.FailureCode.Should().Be("ed25519-signature-invalid");
    }

    [Fact]
    public void Ed25519SignatureWithoutPublicKey_IsRejected()
    {
        var record = BuildV2Record() with
        {
            ContentHash = BrickContentHasher.ComputeSha256(BrickSource),
            Ed25519Signature = Convert.ToBase64String(new byte[64])
        };
        var signed = record with { Signature = CertificationRecordSigning.Sign(record, HmacKey) };

        var trust = CertificationTrustVerifier.Verify(signed, BrickSource, HmacKey);

        trust.Trusted.Should().BeFalse();
        trust.FailureCode.Should().Be("ed25519-key-missing");
    }

    [Fact]
    public void DualSignedRecord_PublicKeySubstitution_BreaksHmac()
    {
        var (privateKey, _) = CreateEd25519Key();
        var (_, otherPublicKey) = CreateEd25519Key();
        var signed = DualSign(BuildV2Record() with { ContentHash = BrickContentHasher.ComputeSha256(BrickSource) }, privateKey);

        var trust = CertificationTrustVerifier.Verify(signed with { Ed25519PublicKey = otherPublicKey }, BrickSource, HmacKey);

        trust.Trusted.Should().BeFalse("the minter public key is part of the HMAC payload");
        trust.FailureCode.Should().Be("signature-invalid");
    }

    [Fact]
    public void ResolvePrivateKey_MalformedConfiguredKey_Throws()
    {
        var actNotBase64 = () => CertificationRecordEd25519.ResolvePrivateKey("not-base64!");
        var actWrongLength = () => CertificationRecordEd25519.ResolvePrivateKey(Convert.ToBase64String(new byte[16]));

        actNotBase64.Should().Throw<FormatException>("a configured but garbage key must fail loudly, not degrade to HMAC-only");
        actWrongLength.Should().Throw<FormatException>();
    }

    // ---------- Durable store round-trip ----------

    [Fact]
    public void FileStore_RoundTripsDualSignedV2Record_AndRefusesDiskTamper()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"ashlar-trust-loop-store-{Guid.NewGuid():N}");
        try
        {
            var (privateKey, _) = CreateEd25519Key();
            var signer = new CertificationRecordSigner(HmacKey, privateKey);
            var store = new FileCertificationRecordStore(directory, signer);

            var record = signer.SignRecord(BuildV2DomainRecord());
            store.Save(record);

            var fetched = new FileCertificationRecordStore(directory, signer).Get(record.BrickId);
            fetched.Should().NotBeNull("a dual-signed v2 record must survive the file store round-trip");
            var roundTripped = fetched!;
            roundTripped.SchemaVersion.Should().Be(CertificationRecordData.TrustLoopSchemaVersion);
            roundTripped.GatesPassed.Select(g => g.Name).Should().Equal("correctness-witness", "mutation-gate");
            roundTripped.Proposer!.Identity.Should().Be("agent://proposer-1");
            roundTripped.Ed25519Signature.Should().Be(record.Ed25519Signature);

            var path = Path.Combine(directory, $"{record.BrickId}.json");
            File.WriteAllText(path, File.ReadAllText(path).Replace(
                "Ashlar.Infrastructure.Certification.CertificationGate", "forged-gate"));

            new FileCertificationRecordStore(directory, signer).Get(record.BrickId).Should().BeNull(
                "v2 records sign the Gate field, so on-disk Gate tampering must fail verification");
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    // ---------- The real minter dual-writes ----------

    [Fact]
    public async Task Gate_Admit_RecordsAdditionalContextInputs_UnderTheSignature()
    {
        var signer = new CertificationRecordSigner(HmacKey);
        var gate = new CertificationGate(signer);
        var contextInput = new CertificationInput
        {
            Kind = "context",
            Id = "mutation-probe-brick",
            Hash = "assembled-context-hash"
        };
        var request = new CertificationRequest
        {
            Brick = new MutationProbeBrick(),
            Witness = StrongWitness,
            SourceCode = MutationProbeBrickSource.Code,
            ProjectPath = CreateCleanProjectFile(),
            CompilationReferences = CompilationReferences(),
            BrickTypeName = typeof(MutationProbeBrick).FullName,
            AdditionalInputs = new[] { contextInput }
        };

        var decision = await gate.CertifyAsync(request);

        decision.Admitted.Should().BeTrue();
        var data = CertificationRecordMapper.ToData(decision.Record);
        data.Inputs[0].Kind.Should().Be("witness", "the witness input always comes first");
        data.Inputs.Should().ContainSingle(i =>
            i.Kind == "context" && i.Id == "mutation-probe-brick" && i.Hash == "assembled-context-hash");
        data.Inputs.Should().Contain(i => i.Kind == CertificationInputKinds.CertifierIdentity);
        data.Inputs.Should().Contain(i => i.Kind == CertificationInputKinds.CompileOptions);
        data.Inputs.Should().Contain(i => i.Kind == CertificationInputKinds.ExecutionMode);

        var trust = CertificationTrustVerifier.Verify(data, MutationProbeBrickSource.Code, HmacKey, CertificationVerifyOptions.Legacy);
        trust.Trusted.Should().BeTrue($"{trust.FailureCode}: {trust.Reason}");

        var tampered = data with
        {
            Inputs = data.Inputs
                .Select(i => i.Kind == "context" ? i with { Hash = "forged-context-hash" } : i)
                .ToArray()
        };
        CertificationRecordSigning.VerifySignature(tampered, HmacKey).Should().BeFalse(
            "the v2 payload covers context inputs, so tampering the recorded context hash must invalidate the signature");
    }

    [Fact]
    public async Task Gate_Admit_MintsDualSignedTrustLoopCertificate()
    {
        var (privateKey, publicKey) = CreateEd25519Key();
        var signer = new CertificationRecordSigner(HmacKey, privateKey);
        var gate = new CertificationGate(signer);
        var request = new CertificationRequest
        {
            Brick = new MutationProbeBrick(),
            Witness = StrongWitness,
            SourceCode = MutationProbeBrickSource.Code,
            ProjectPath = CreateCleanProjectFile(),
            CompilationReferences = CompilationReferences(),
            BrickTypeName = typeof(MutationProbeBrick).FullName
        };

        var decision = await gate.CertifyAsync(request);

        decision.Admitted.Should().BeTrue();
        var record = decision.Record;
        record.SchemaVersion.Should().Be(CertificationRecordData.LabelSchemaVersion);
        record.HighestDataLabel.Should().Be("SystemHigh");
        record.GatesPassed.Select(g => g.Name).Should().Equal(
            "analyzer-gate", "correctness-witness", "mutation-gate", "determinism", "dependency-graph");
        record.GatesPassed[0].Configuration.Should()
            .Contain("analyzerAssemblyVersion=").And.Contain("severityFloor=Warning").And.Contain("diagnosticsEvaluated=",
                "A1.5: the analyzer gate records versions, floor, and evaluated-diagnostic count");
        record.Inputs.Should().ContainSingle(i =>
            i.Kind == "witness" && i.Id == "mutation-probe-brick" && !string.IsNullOrWhiteSpace(i.Hash));
        record.Ed25519PublicKey.Should().Be(publicKey);
        record.Ed25519Signature.Should().NotBeNullOrWhiteSpace();
        signer.Verify(record).Should().BeTrue();

        var trust = CertificationTrustVerifier.Verify(
            CertificationRecordMapper.ToData(record), MutationProbeBrickSource.Code, HmacKey);
        trust.Trusted.Should().BeTrue($"{trust.FailureCode}: {trust.Reason}");
    }

    [Fact]
    public async Task Gate_CorrectnessFailure_RecordsNoGatesPassed()
    {
        var gate = new CertificationGate(new CertificationRecordSigner(HmacKey));
        var request = new CertificationRequest
        {
            Brick = new BadWitnessBrick(),
            Witness = StrongWitness with { BrickId = "bad-witness-brick" },
            SourceCode = "namespace X; class Y {}",
            ProjectPath = CreateCleanProjectFile(),
            CompilationReferences = CompilationReferences(),
            BrickTypeName = typeof(BadWitnessBrick).FullName
        };

        var decision = await gate.CertifyAsync(request);

        decision.Admitted.Should().BeFalse();
        decision.FailureCheck.Should().Be("correctness");
        decision.Record.SchemaVersion.Should().Be(CertificationRecordData.LabelSchemaVersion);
        decision.Record.HighestDataLabel.Should().Be("SystemHigh");
        decision.Record.GatesPassed.Select(g => g.Name).Should().Equal(
            new[] { "analyzer-gate" },
            "the analyzer fence runs before correctness, so it is the only gate passed before a correctness failure (R2.4)");
        decision.Record.Signature.Should().BeNull("FAIL records are never signed");
    }

    // ---------- helpers ----------

    private static readonly string ProbeLog =
        "2024-01-01 INFO Started\n2024-01-01 ERROR First failure: connection reset\n2024-01-01 WARN Retrying\n2024-01-01 ERROR Second failure: timeout";

    private static readonly WitnessSpec StrongWitness = new(
        "mutation-probe-brick",
        [
            new WitnessCase(
                new Dictionary<string, object> { ["logText"] = ProbeLog },
                new Dictionary<string, object>
                {
                    ["errorCount"] = 2,
                    ["firstErrorMessage"] = "First failure: connection reset"
                })
        ]);

    private static CertificationRecordData BuildLegacyRecord() => new()
    {
        Status = "PASS",
        Stage = "S0-S2",
        Admitted = true,
        Signed = true,
        Timestamp = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
        BrickId = "legacy-brick",
        ContentHash = "hash-abc",
        EscapeRate = 0,
        TotalMutants = 3,
        SurvivingMutants = 0,
        KilledMutants = new[] { "m2", "m1" },
        SurvivingMutantIds = Array.Empty<string>(),
        Gate = "Ashlar.Infrastructure.Certification.CertificationGate"
    };

    private static CertificationRecordData BuildV2Record() => new()
    {
        Status = "PASS",
        Stage = "S0-S2",
        Admitted = true,
        Signed = true,
        Timestamp = new DateTimeOffset(2026, 8, 12, 10, 0, 0, TimeSpan.Zero),
        BrickId = "trust-loop-brick",
        ContentHash = "hash-abc",
        EscapeRate = 0,
        TotalMutants = 2,
        SurvivingMutants = 0,
        KilledMutants = new[] { "m1", "m2" },
        SurvivingMutantIds = Array.Empty<string>(),
        Gate = "Ashlar.Infrastructure.Certification.CertificationGate",
        SchemaVersion = CertificationRecordData.TrustLoopSchemaVersion,
        GatesPassed = new[]
        {
            new CertificationGatePass { Name = "correctness-witness", Version = "1" },
            new CertificationGatePass { Name = "mutation-gate", Version = "1", Configuration = "escapeRateThreshold=0" }
        },
        Inputs = new[] { new CertificationInput { Kind = "witness", Id = "trust-loop-brick", Hash = "witness-hash" } },
        Proposer = new CertificationProposer
        {
            Identity = "agent://proposer-1",
            Parameters = new Dictionary<string, string> { ["temperature"] = "0" },
            Seed = "42"
        },
        Attempts = new[]
        {
            new CertificationAttempt { Index = 1, Outcome = "fail", FailureCategory = "mutation" },
            new CertificationAttempt { Index = 2, Outcome = "pass" }
        }
    };

    private static CertificationRecord BuildV2DomainRecord() => new()
    {
        Status = "PASS",
        Stage = "S0-S2",
        Admitted = true,
        Signed = true,
        Timestamp = new DateTimeOffset(2026, 8, 12, 10, 0, 0, TimeSpan.Zero),
        BrickId = "trust-loop-store-brick",
        ContentHash = "hash-abc",
        Gate = "Ashlar.Infrastructure.Certification.CertificationGate",
        SchemaVersion = CertificationRecordData.TrustLoopSchemaVersion,
        GatesPassed = new[]
        {
            new CertificationGatePass { Name = "correctness-witness", Version = "1" },
            new CertificationGatePass { Name = "mutation-gate", Version = "1" }
        },
        Inputs = new[] { new CertificationInput { Kind = "witness", Id = "trust-loop-store-brick", Hash = "witness-hash" } },
        Proposer = new CertificationProposer { Identity = "agent://proposer-1" },
        Attempts = new[] { new CertificationAttempt { Index = 1, Outcome = "pass" } }
    };

    private static CertificationRecordData DualSign(CertificationRecordData record, string privateKeyBase64)
    {
        var withKey = record with { Ed25519PublicKey = CertificationRecordEd25519.DerivePublicKeyBase64(Convert.FromBase64String(privateKeyBase64)) };
        return withKey with
        {
            Signature = CertificationRecordSigning.Sign(withKey, HmacKey),
            Ed25519Signature = CertificationRecordEd25519.Sign(withKey, Convert.FromBase64String(privateKeyBase64))
        };
    }

    private static (string PrivateKeyBase64, string PublicKeyBase64) CreateEd25519Key()
    {
        using var key = Key.Create(
            SignatureAlgorithm.Ed25519,
            new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
        return (
            Convert.ToBase64String(key.Export(KeyBlobFormat.RawPrivateKey)),
            Convert.ToBase64String(key.PublicKey.Export(KeyBlobFormat.RawPublicKey)));
    }

    private static List<string> CompilationReferences() =>
    [
        typeof(DomainBrick).Assembly.Location,
        typeof(BrickInput).Assembly.Location,
        typeof(MutationProbeBrick).Assembly.Location
    ];

    private static string CreateCleanProjectFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ashlar-cert-clean-{Guid.NewGuid():N}.csproj");
        File.WriteAllText(path, """
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <PackageReference Include="Ashlar.Brick.Contracts" Version="0.1.0" />
    <PackageReference Include="Ashlar.Authoring" Version="0.1.0" />
  </ItemGroup>
</Project>
""");
        return path;
    }
}
