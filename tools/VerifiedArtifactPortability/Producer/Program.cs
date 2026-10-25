using System.Security.Cryptography;
using System.Text.Json;
using Ashlar.Certification.Contracts;
using Ashlar.Core.Application.Certification.Models;
using Ashlar.Infrastructure.Certification;
using Ashlar.VerifiedArtifactPortability;
using Mono.Cecil;
using NSec.Cryptography;

if (args.Length != 3)
{
    Console.Error.WriteLine("Usage: Producer <fixture-dll> <fixture-source> <new-deployment-dir>");
    return 2;
}

var directory = Path.GetFullPath(args[2]);
if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any())
{
    throw new InvalidOperationException("Refusing to overwrite a nonempty deployment directory.");
}

var assemblyBytes = await File.ReadAllBytesAsync(args[0]);
var source = await File.ReadAllTextAsync(args[1]);
using var module = ModuleDefinition.ReadModule(new MemoryStream(assemblyBytes, writable: false));
var framework = module.Assembly.CustomAttributes.SingleOrDefault(
    attribute => attribute.AttributeType.FullName == "System.Runtime.Versioning.TargetFrameworkAttribute");
if (framework?.ConstructorArguments.Single().Value is not string frameworkName
    || frameworkName != ".NETCoreApp,Version=v8.0")
{
    throw new InvalidOperationException("The portable fixture must be built for net8.0.");
}

var runtimeReference = module.AssemblyReferences.SingleOrDefault(reference => reference.Name == "System.Runtime");
if (runtimeReference?.Version.Major != 8
    || module.AssemblyReferences.Any(reference => reference.Name == "System.Private.CoreLib"))
{
    throw new InvalidOperationException("The fixture must reference the .NET 8 reference-assembly surface.");
}

var type = module.GetType(ProbeConstants.TypeName);
if (type is null || !type.IsClass || type.IsAbstract || type.HasGenericParameters
    || type.BaseType?.FullName != "Ashlar.Core.Domain.Bricks.Brick")
{
    throw new InvalidOperationException("The fixture must contain the expected concrete brick type.");
}

IlImportFence.Inspect(assemblyBytes);

// This is a synthetic certification fixture for testing the consumer, not evidence
// that a production certification gate admitted the MSBuild-produced assembly.
var artifact = new GateEmittedArtifact
{
    AssemblyBytes = assemblyBytes,
    AssemblySha256 = BrickContentHasher.ComputeSha256(assemblyBytes),
    SourceSha256 = BrickContentHasher.ComputeSha256(source),
    BrickTypeName = ProbeConstants.TypeName,
    CompileOptionsBlob = "fixture-build=MSBuild;targetFramework=net8.0;language=12.0;allowUnsafe=false;checkOverflow=false;optimization=Release;nullable=disable",
    CompilerVersion = "synthetic-portability-fixture-msbuild"
};

using var key = Key.Create(SignatureAlgorithm.Ed25519,
    new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
var publicKey = Convert.ToBase64String(key.PublicKey.Export(KeyBlobFormat.RawPublicKey));
var unsigned = new CertificationRecordData
{
    Status = "PASS",
    Stage = "S0-S2",
    Admitted = true,
    Signed = true,
    Timestamp = DateTimeOffset.UtcNow,
    BrickId = ProbeConstants.BrickId,
    ContentHash = artifact.SourceSha256,
    Gate = "synthetic-portability-test-only",
    SchemaVersion = CertificationRecordData.TrustLoopSchemaVersion,
    Inputs =
    [
        new CertificationInput
        {
            Kind = CertificationInputKinds.GateEmittedArtifact,
            Id = artifact.BrickTypeName,
            Hash = artifact.AssemblySha256
        },
        CertifierIdentity.ToInput()
    ],
    Ed25519PublicKey = publicKey
};
var privateKey = key.Export(KeyBlobFormat.RawPrivateKey);
CertificationRecordData record;
try
{
    record = unsigned with
    {
        Signature = CertificationRecordSigning.Sign(unsigned, ProbeConstants.HmacKey),
        Ed25519Signature = CertificationRecordEd25519.Sign(unsigned, privateKey)
    };
}
finally
{
    CryptographicOperations.ZeroMemory(privateKey);
}

var manifest = new DeploymentManifest
{
    BrickId = ProbeConstants.BrickId,
    BrickTypeName = artifact.BrickTypeName,
    AssemblySha256 = artifact.AssemblySha256,
    ModuleVersionId = module.Mvid,
    TrustedPublicKey = publicKey,
    BuildTargetFramework = "net8.0"
};

Directory.CreateDirectory(directory);
await File.WriteAllTextAsync(Path.Combine(directory, "Brick.cs"), source);
await CertifiedArtifactExporter.WriteAsync(Path.Combine(directory, "record.json"), record, artifact);
await File.WriteAllTextAsync(Path.Combine(directory, "manifest.json"),
    JsonSerializer.Serialize(manifest, new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    }));
Console.WriteLine($"fixture target: {manifest.BuildTargetFramework}");
Console.WriteLine($"artifact sha256: {manifest.AssemblySha256}");
Console.WriteLine($"artifact mvid: {manifest.ModuleVersionId}");
Console.WriteLine("PASS: produced one synthetic signed deployment for every consumer.");
return 0;
