using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text.Json;
using Ashlar.Certification.Contracts;
using Ashlar.Core.Domain.Bricks;
using Ashlar.Core.Domain.Execution;
using Ashlar.Hosting;
using Ashlar.Infrastructure.Certification;
using Ashlar.Infrastructure.Execution;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NSec.Cryptography;

var assertions = new List<string>();
try
{
    if (args.Length != 2) throw new ArgumentException("Expected fixture directory and evidence directory.");
    var fixture = Path.GetFullPath(args[0]);
    var evidence = Path.GetFullPath(args[1]);
    Directory.CreateDirectory(evidence);
    Check("runtime-net10", Environment.Version.Major == 10);

    var sourcePath = Path.Combine(fixture, "DamageResolverBrick.cs");
    var witnessPath = Path.Combine(fixture, "damage-resolver.witness.json");
    var source = await File.ReadAllTextAsync(sourcePath);
    using var key = Key.Create(SignatureAlgorithm.Ed25519,
        new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
    var publicKey = Convert.ToBase64String(key.PublicKey.Export(KeyBlobFormat.RawPublicKey));
    var hmacKey = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
    var policy = CertificationTrustPolicy.FromTrustedKeys([publicKey]);
    var run = await BrickCertificationRun.ExecuteAsync(new BrickCertificationRunRequest
    {
        BrickProjectDirectory = fixture,
        WitnessSpecPath = witnessPath,
        RecordPath = Path.Combine(evidence, "deployment", "record.json"),
        Signer = new CertificationRecordSigner(hmacKey, Convert.ToBase64String(key.Export(KeyBlobFormat.RawPrivateKey)))
    });
    Check("actual-gate-admitted", run.Admitted,
        run.FailureCheck + ": " + run.Record.Reason);
    if (run.ArtifactPath is not { } artifactPath) throw new InvalidOperationException("Gate did not emit an artifact.");
    var artifact = await File.ReadAllBytesAsync(artifactPath);
    var artifactHash = BrickContentHasher.ComputeSha256(artifact);
    var originalRecord = await File.ReadAllTextAsync(run.RecordPath);
    var record = JsonSerializer.Deserialize<CertificationRecordData>(originalRecord,
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    var binding = record.Inputs.Single(input => input.Kind == CertificationInputKinds.GateEmittedArtifact);
    Check("gate-emitted-artifact-bound", binding.Hash == artifactHash);
    Check("pinned-signature-trusted", CertificationTrustVerifier.Verify(record, source, artifact, hmacKey, policy.Strict).Trusted);
    using var pe = new PEReader(new MemoryStream(artifact, writable: false));
    var metadata = pe.GetMetadataReader();
    var moduleId = metadata.GetGuid(metadata.GetModuleDefinition().Mvid);
    var deployment = new VerifiedBrickDeployment("damage-resolver", sourcePath, run.RecordPath, artifactPath);

    IHost BuildHost(CertificationTrustPolicy trust) => Host.CreateDefaultBuilder(Array.Empty<string>())
        .ConfigureLogging(logging => logging.ClearProviders())
        .ConfigureServices(services => services.AddAshlarVerifiedBricks([deployment], trust, hmacKey, options =>
        {
            options.DeploymentProfile = AshlarDeploymentProfile.System;
            options.ConfigPath = Path.Combine(evidence, "host-config.json");
            options.DisableObservationPipeline = true;
            options.RegisterBackgroundAgentHostedService = false;
        })).Build();

    using (var host = BuildHost(policy))
    {
        var registry = host.Services.GetRequiredService<IBrickRegistry>();
        Check("host-unavailable-before-start", IsUnavailable(registry));
        await host.StartAsync();
        var bricks = registry.GetAllBricks();
        Check("verified-only-registry", bricks.Count == 1 && bricks[0].Id == "damage-resolver"
            && registry.GetBrick("unverified") is null && host.Services.GetService<BrickRegistry>() is null);
        var brick = bricks[0];
        Check("signed-module-executed", brick.GetType().Assembly.ManifestModule.ModuleVersionId == moduleId);
        var input = new BrickInput(new Dictionary<string, object>
        {
            ["baseDamage"] = 100, ["critMultiplierPercent"] = 150, ["armor"] = 20, ["isCrit"] = true
        });
        var output = await brick.ExecuteAsync(input, ImplementationType.Deterministic, new Ashlar.Infrastructure.Execution.ExecutionContext());
        Check("damage-witness-output", output.Get<int>("finalDamage") == 130);
        await File.WriteAllBytesAsync(artifactPath, [0, 1, 2]);
        output = await brick.ExecuteAsync(input, ImplementationType.Deterministic, new Ashlar.Infrastructure.Execution.ExecutionContext());
        Check("deployed-dll-replacement-keeps-loaded-module", output.Get<int>("finalDamage") == 130
            && brick.GetType().Assembly.ManifestModule.ModuleVersionId == moduleId);
        await host.StopAsync();
        Check("host-unavailable-after-stop", IsUnavailable(registry));
    }

    async Task RefusesStartup(string name, CertificationTrustPolicy trust, string reason)
    {
        using var refused = BuildHost(trust);
        Exception? failure = null;
        try { await refused.StartAsync(); }
        catch (InvalidOperationException ex) { failure = ex; }
        Check(name, failure?.Message.Contains(reason, StringComparison.Ordinal) == true
            && IsUnavailable(refused.Services.GetRequiredService<IBrickRegistry>()), failure?.Message);
    }

    await RefusesStartup("tampered-artifact-refused", policy, "artifact-hash-mismatch");
    await File.WriteAllBytesAsync(artifactPath, artifact);
    await File.WriteAllTextAsync(sourcePath, source + "\n// changed after certification\n");
    await RefusesStartup("tampered-source-refused", policy, "content-hash-mismatch");
    await File.WriteAllTextAsync(sourcePath, source);
    using var foreign = Key.Create(SignatureAlgorithm.Ed25519);
    await RefusesStartup("foreign-signer-refused", CertificationTrustPolicy.FromTrustedKeys([
        Convert.ToBase64String(foreign.PublicKey.Export(KeyBlobFormat.RawPublicKey))]), "ed25519-key-not-trusted");
    var unpinnedRefused = false;
    try { using var ignored = BuildHost(CertificationTrustPolicy.Unpinned); }
    catch (CertificationTrustConfigurationException) { unpinnedRefused = true; }
    Check("unpinned-policy-refused", unpinnedRefused);

    await File.WriteAllTextAsync(Path.Combine(evidence, "result.json"), JsonSerializer.Serialize(new
    {
        Result = "PASS", Runtime = RuntimeInformation.FrameworkDescription, TargetFramework = "net10.0",
        GateOutcome = run.Outcome.ToString(), ArtifactSha256 = artifactHash, ModuleVersionId = moduleId,
        Assertions = assertions,
        Limitation = "This is a fresh .NET 10 gate artifact consumed from packages. It does not prove the same DLL runs on .NET 8."
    }, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"RESULT=PASS assertions={assertions.Count}");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex);
    Console.WriteLine("RESULT=FAIL");
    return 1;
}

void Check(string name, bool passed, string? detail = null)
{
    if (!passed) throw new InvalidOperationException("Assertion failed: " + name + ". " + detail);
    assertions.Add(name);
    Console.WriteLine("ASSERT " + name + " PASS");
}

static bool IsUnavailable(IBrickRegistry registry)
{
    try { registry.GetAllBricks(); return false; }
    catch (InvalidOperationException) { return true; }
}
