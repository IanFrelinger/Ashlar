using System.Runtime.Loader;
using System.Text.Json;
using Ashlar.Certification.Contracts;
using Ashlar.Core.Application.Certification.Models;
using Ashlar.Core.Domain.Execution;
using Ashlar.Infrastructure.Certification;
using Ashlar.Samples.VerifiedBrickConsumer;
using Ashlar.Tests.Infrastructure.Helpers;
using Mono.Cecil;
using NSec.Cryptography;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

[Trait("Category", "Certification")]
[Trait("Category", "ProdStyle")]
[Collection("hot-swap-host")]
public sealed class VerifiedConsumerProdStyleTests : IDisposable
{
    private const string HmacKey = "verified-consumer-test-key";
    private const string BrickId = "verified-consumer-probe";
    private const string TypeName = "Ashlar.ConsumerProbe.ProbeBrick";
    private const string Source = """
        using Ashlar.Core.Domain.Bricks;
        using Ashlar.Core.Domain.Execution;
        namespace Ashlar.ConsumerProbe;
        public sealed class ProbeBrick : DomainBrick
        {
            public ProbeBrick() { Id = "verified-consumer-probe"; Name = "Probe"; Description = "Consumer fixture"; }
            public override Task<BrickOutput> ExecuteAsync(BrickInput input, ImplementationType implementation,
                IExecutionContext context, CancellationToken cancellationToken = default)
            {
                var output = new BrickOutput { Summary = "certified" };
                output.Set("marker", "certified");
                return Task.FromResult(output);
            }
        }
        """;
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ashlar-verified-consumer-" + Guid.NewGuid());
    private string SourcePath => Path.Combine(_directory, "Brick.cs");
    private string RecordPath => Path.Combine(_directory, "record.json");
    private string AssemblyPath => Path.Combine(_directory, "gate-emitted-brick.dll");

    [Fact(Timeout = TestTimeouts.Integration)]
    public async Task Executes_the_signed_module_even_after_deployed_DLL_is_replaced()
    {
        var (artifact, _, policy) = await Deploy();
        var before = AssemblyLoadContext.All.SelectMany(c => c.Assemblies).ToHashSet();
        using var consumer = await Load(policy);
        using var pe = ModuleDefinition.ReadModule(new MemoryStream(artifact.AssemblyBytes));
        var loaded = Assert.Single(AssemblyLoadContext.All.SelectMany(c => c.Assemblies),
            a => !before.Contains(a) && a.GetType(TypeName) is not null);
        Assert.Equal(pe.Mvid, loaded.ManifestModule.ModuleVersionId);
        Assert.Equal(artifact.AssemblySha256, consumer.ArtifactSha256);

        await File.WriteAllBytesAsync(AssemblyPath, [0, 1, 2]);
        var result = await consumer.ExecuteAsync(new BrickInput(), new Ashlar.Infrastructure.Execution.ExecutionContext());
        Assert.Equal("certified", result.Get<string>("marker"));
    }

    [Fact(Timeout = TestTimeouts.Integration)]
    public async Task Unpinned_policy_is_rejected_before_reading_any_deployment_files()
    {
        await Assert.ThrowsAsync<CertificationTrustConfigurationException>(() => Load(CertificationTrustPolicy.Unpinned));
    }

    [Fact(Timeout = TestTimeouts.Integration)]
    public async Task Foreign_signer_is_refused()
    {
        await Deploy();
        using var key = CreateKey();
        var foreign = CertificationTrustPolicy.FromTrustedKeys([Convert.ToBase64String(key.PublicKey.Export(KeyBlobFormat.RawPublicKey))]);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Load(foreign));
        Assert.Contains("ed25519-key-not-trusted", ex.Message);
    }

    [Theory(Timeout = TestTimeouts.Integration)]
    [InlineData("source", "content-hash-mismatch")]
    [InlineData("artifact", "artifact-hash-mismatch")]
    [InlineData("signature", "ed25519-signature-required")]
    [InlineData("invalid-signature", "ed25519-signature-invalid")]
    [InlineData("identity", "brick-id-mismatch")]
    public async Task Tampered_deployment_is_refused_before_execution(string part, string failureCode)
    {
        var (_, record, policy) = await Deploy();
        if (part == "source") await File.AppendAllTextAsync(SourcePath, " // changed");
        if (part == "artifact") await File.WriteAllBytesAsync(AssemblyPath, [0, 1, 2]);
        if (part == "signature") await File.WriteAllTextAsync(RecordPath, JsonSerializer.Serialize(record with { Ed25519Signature = null }));
        if (part == "invalid-signature")
        {
            var signature = Convert.FromBase64String(record.Ed25519Signature!);
            signature[0] ^= 1;
            await File.WriteAllTextAsync(RecordPath, JsonSerializer.Serialize(record with
            {
                Ed25519Signature = Convert.ToBase64String(signature)
            }));
        }
        if (part == "identity") await File.WriteAllTextAsync(RecordPath, JsonSerializer.Serialize(record with { BrickId = "other" }));
        var before = AssemblyLoadContext.All.SelectMany(c => c.Assemblies).ToHashSet();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Load(policy));
        Assert.Contains(failureCode, ex.Message);
        Assert.DoesNotContain(AssemblyLoadContext.All.SelectMany(c => c.Assemblies),
            a => !before.Contains(a) && a.GetType(TypeName) is not null);
    }

    [Fact(Timeout = TestTimeouts.Integration)]
    public async Task Validly_signed_record_for_another_identity_is_refused()
    {
        var (artifact, record, policy) = await Deploy(record => record with { BrickId = "other" });
        Assert.True(CertificationTrustVerifier.Verify(record, Source, artifact.AssemblyBytes, HmacKey, policy.Strict).Trusted);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Load(policy));
        Assert.Contains("brick-id-mismatch", ex.Message);
    }

    [Fact(Timeout = TestTimeouts.Integration)]
    public async Task Loaded_brick_must_match_the_expected_identity()
    {
        var (_, _, policy) = await Deploy(source: Source.Replace(BrickId, "other"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Load(policy));
        Assert.Contains("brick-instance-id-mismatch", ex.Message);
    }

    [Theory(Timeout = TestTimeouts.Integration)]
    [InlineData("", "artifact-binding-invalid")]
    [InlineData("Ashlar.ConsumerProbe.MissingBrick", "artifact-type-invalid")]
    public async Task Signed_artifact_binding_must_name_a_brick(string typeName, string failureCode)
    {
        var (_, _, policy) = await Deploy(record => record with
        {
            Inputs = record.Inputs.Select(input => input.Kind == CertificationInputKinds.GateEmittedArtifact
                ? input with { Id = typeName } : input).ToArray()
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Load(policy));
        Assert.Contains(failureCode, ex.Message);
    }

    [Fact(Timeout = TestTimeouts.Integration)]
    public async Task Ambiguous_signed_artifact_bindings_are_refused()
    {
        var (_, _, policy) = await Deploy(record => record with
        {
            Inputs = [.. record.Inputs, record.Inputs.Single(input => input.Kind == CertificationInputKinds.GateEmittedArtifact)]
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Load(policy));
        Assert.Contains("artifact-binding-invalid", ex.Message);
    }

    [Fact(Timeout = TestTimeouts.Integration)]
    public async Task Signed_artifact_with_forbidden_import_is_refused_before_loading()
    {
        var (_, _, policy) = await Deploy(source: Source.Replace("Summary = \"certified\"", "Summary = System.Environment.MachineName"));
        var before = AssemblyLoadContext.All.SelectMany(c => c.Assemblies).ToHashSet();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Load(policy));
        Assert.Contains("il-import fence", ex.Message);
        Assert.DoesNotContain(AssemblyLoadContext.All.SelectMany(c => c.Assemblies),
            a => !before.Contains(a) && a.GetType(TypeName) is not null);
    }

    [Fact(Timeout = TestTimeouts.Integration)]
    public async Task Disposed_consumer_refuses_execution()
    {
        var (_, _, policy) = await Deploy();
        using var consumer = await Load(policy);
        consumer.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => consumer.ExecuteAsync(
            new BrickInput(), new Ashlar.Infrastructure.Execution.ExecutionContext()));
    }

    private Task<VerifiedConsumer> Load(CertificationTrustPolicy policy) => VerifiedConsumer.LoadFromFilesAsync(
        BrickId, SourcePath, RecordPath, AssemblyPath, policy, HmacKey);

    private async Task<(GateEmittedArtifact Artifact, CertificationRecordData Record, CertificationTrustPolicy Policy)> Deploy(
        Func<CertificationRecordData, CertificationRecordData>? customizeRecord = null,
        string source = Source)
    {
        Directory.CreateDirectory(_directory);
        var artifact = GateEmittedArtifactCompiler.Compile(source, BrickCertificationProjectLoader.DefaultCompilationReferences());
        using var key = CreateKey();
        var publicKey = Convert.ToBase64String(key.PublicKey.Export(KeyBlobFormat.RawPublicKey));
        var unsigned = new CertificationRecordData
        {
            Status = "PASS", Stage = "S0-S2", Admitted = true, Signed = true,
            Timestamp = DateTimeOffset.UtcNow, BrickId = BrickId, ContentHash = BrickContentHasher.ComputeSha256(source),
            Gate = "consumer-test", SchemaVersion = CertificationRecordData.TrustLoopSchemaVersion,
            Inputs = [new CertificationInput { Kind = CertificationInputKinds.GateEmittedArtifact,
                Id = artifact.BrickTypeName, Hash = artifact.AssemblySha256 }, CertifierIdentity.ToInput()],
            Ed25519PublicKey = publicKey
        };
        unsigned = customizeRecord?.Invoke(unsigned) ?? unsigned;
        var record = unsigned with
        {
            Signature = CertificationRecordSigning.Sign(unsigned, HmacKey),
            Ed25519Signature = CertificationRecordEd25519.Sign(unsigned, key.Export(KeyBlobFormat.RawPrivateKey))
        };
        await File.WriteAllTextAsync(SourcePath, source);
        await CertifiedArtifactExporter.WriteAsync(RecordPath, record, artifact);
        return (artifact, record, CertificationTrustPolicy.FromTrustedKeys([publicKey]));
    }

    private static Key CreateKey() => Key.Create(SignatureAlgorithm.Ed25519,
        new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
