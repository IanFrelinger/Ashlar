using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Runtime.Versioning;
using System.Text.Json;
using Ashlar.Certification.Contracts;
using Ashlar.Core.Domain.Execution;
using Ashlar.Infrastructure.Certification;
using Ashlar.Samples.VerifiedBrickConsumer;
using Ashlar.VerifiedArtifactPortability;
using NSec.Cryptography;

internal static class Program
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length != 3 || !int.TryParse(args[1], out var expectedMajor)
                || expectedMajor is not (8 or 9 or 10))
                throw new ArgumentException("Usage: Consumer <deployment-directory> <runtime-major:8|9|10> <receipt-path>");

            Console.WriteLine($"runtime: {RuntimeInformation.FrameworkDescription}; {RuntimeInformation.OSDescription}; {RuntimeInformation.ProcessArchitecture}");
            Require(Environment.Version.Major == expectedMajor && Environment.Version.Minor == 0,
                $"runtime-mismatch: expected {expectedMajor}.0.x, got {Environment.Version}");
            Require(Tfm(typeof(Program).Assembly) == $".NETCoreApp,Version=v{expectedMajor}.0",
                "The probe executable targets a different runtime from this matrix leg.");
            var assetMajor = expectedMajor == 9 ? 8 : expectedMajor;
            var consumerTfm = Tfm(typeof(VerifiedConsumer).Assembly);
            var contractsTfm = Tfm(typeof(BrickInput).Assembly);
            Require(consumerTfm == $".NETCoreApp,Version=v{assetMajor}.0", "Unexpected consumer asset selected.");
            Require(contractsTfm == $".NETCoreApp,Version=v{assetMajor}.0", "Unexpected brick contracts asset selected.");
            Console.WriteLine($"assets: consumer={consumerTfm}; contracts={contractsTfm}");

            var deployment = Path.GetFullPath(args[0]);
            var manifest = JsonSerializer.Deserialize<DeploymentManifest>(
                await File.ReadAllTextAsync(Path.Combine(deployment, "manifest.json")), Json)
                ?? throw new InvalidOperationException("Missing manifest.");
            Require(manifest.BrickId == ProbeConstants.BrickId && manifest.BrickTypeName == ProbeConstants.TypeName,
                "Unexpected fixture identity.");
            Require(manifest.BuildTargetFramework == "net8.0", "Fixture must be built against the net8.0 reference assemblies.");
            var artifactPath = Path.Combine(deployment, CertifiedArtifactExporter.ArtifactFileName);
            var artifact = await File.ReadAllBytesAsync(artifactPath);
            Require(BrickContentHasher.ComputeSha256(artifact) == manifest.AssemblySha256, "Downloaded artifact hash differs from producer.");

            // The manifest is the CI producer's test trust configuration. Production operators
            // must obtain signer pins independently of the untrusted deployment being verified.
            var policy = CertificationTrustPolicy.FromTrustedKeys([manifest.TrustedPublicKey]);
            var checks = new List<string> { "runtime-and-assets", "producer-hash" };
            var scratch = Path.Combine(Path.GetTempPath(), "ashlar-portability-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(scratch);
            try
            {
                foreach (var name in new[] { "Brick.cs", "record.json", CertifiedArtifactExporter.ArtifactFileName })
                    File.Copy(Path.Combine(deployment, name), Path.Combine(scratch, name));
                var sourcePath = Path.Combine(scratch, "Brick.cs");
                var recordPath = Path.Combine(scratch, "record.json");
                var assemblyPath = Path.Combine(scratch, CertifiedArtifactExporter.ArtifactFileName);
                var source = await File.ReadAllTextAsync(sourcePath);
                var recordJson = await File.ReadAllTextAsync(recordPath);
                var record = JsonSerializer.Deserialize<CertificationRecordData>(recordJson, Json)
                    ?? throw new InvalidOperationException("Missing record.");

                Task<VerifiedConsumer> Load(CertificationTrustPolicy? trust = null) =>
                    VerifiedConsumer.LoadFromFilesAsync(manifest.BrickId, sourcePath, recordPath,
                        assemblyPath, trust ?? policy, ProbeConstants.HmacKey);

                var before = Assemblies();
                using (var consumer = await Load())
                {
                    var loaded = Assemblies().Single(a => !before.Contains(a)
                        && a.GetType(manifest.BrickTypeName, throwOnError: false) is not null);
                    Require(loaded.ManifestModule.ModuleVersionId == manifest.ModuleVersionId, "Loaded module differs from producer MVID.");
                    Require(consumer.ArtifactSha256 == manifest.AssemblySha256, "Consumer did not load the producer's signed hash.");
                    checks.Add("loaded-module-identity");
                    await Execute(consumer);
                    checks.Add("execute");
                    await File.WriteAllBytesAsync(assemblyPath, [0, 1, 2]);
                    await Execute(consumer);
                    checks.Add("execute-after-dll-replacement");
                }

                await Refused(() => Load(), "artifact-hash-mismatch");
                checks.Add("tampered-artifact-refused");
                await File.WriteAllBytesAsync(assemblyPath, artifact);
                await File.WriteAllTextAsync(sourcePath, source + " // changed");
                await Refused(() => Load(), "content-hash-mismatch");
                checks.Add("tampered-source-refused");
                await File.WriteAllTextAsync(sourcePath, source);

                await File.WriteAllTextAsync(recordPath, JsonSerializer.Serialize(record with { Ed25519Signature = null }, Json));
                await Refused(() => Load(), "ed25519-signature-required");
                checks.Add("missing-signature-refused");
                var signature = Convert.FromBase64String(record.Ed25519Signature!);
                signature[0] ^= 1;
                await File.WriteAllTextAsync(recordPath, JsonSerializer.Serialize(record with
                    { Ed25519Signature = Convert.ToBase64String(signature) }, Json));
                await Refused(() => Load(), "ed25519-signature-invalid");
                checks.Add("invalid-signature-refused");
                await File.WriteAllTextAsync(recordPath, recordJson);

                using var foreignKey = Key.Create(SignatureAlgorithm.Ed25519);
                var foreignPolicy = CertificationTrustPolicy.FromTrustedKeys([
                    Convert.ToBase64String(foreignKey.PublicKey.Export(KeyBlobFormat.RawPublicKey))]);
                await Refused(() => Load(foreignPolicy), "ed25519-key-not-trusted");
                checks.Add("foreign-signer-refused");
            }
            finally
            {
                Directory.Delete(scratch, recursive: true);
            }

            Require(BrickContentHasher.ComputeSha256(await File.ReadAllBytesAsync(artifactPath)) == manifest.AssemblySha256,
                "The original deployment artifact was modified during the probe.");
            var platform = OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsLinux() ? "Linux" :
                OperatingSystem.IsMacOS() ? "macOS" : throw new PlatformNotSupportedException();
            var receipt = new
            {
                runtimeMajor = Environment.Version.Major,
                runtimeVersion = Environment.Version.ToString(),
                platform,
                operatingSystem = RuntimeInformation.OSDescription,
                architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                manifest.AssemblySha256,
                manifest.ModuleVersionId,
                manifest.BuildTargetFramework,
                consumerTargetFramework = consumerTfm,
                contractsTargetFramework = contractsTfm,
                checks
            };
            await File.WriteAllTextAsync(args[2], JsonSerializer.Serialize(receipt, Json));
            Console.WriteLine($"PASS: {checks.Count} checks; sha256={manifest.AssemblySha256}; mvid={manifest.ModuleVersionId}");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("FAIL: " + exception);
            return 1;
        }
    }

    private static async Task Execute(VerifiedConsumer consumer)
    {
        var input = new BrickInput();
        input.Set("value", 41);
        var output = await consumer.ExecuteAsync(input, new Ashlar.Infrastructure.Execution.ExecutionContext());
        Require(output.Get<string>("marker") == "certified" && output.Get<int>("result") == 42,
            "Verified brick returned an unexpected result.");
    }

    private static async Task Refused(Func<Task<VerifiedConsumer>> load, string expectedCode)
    {
        var before = Assemblies();
        try
        {
            using var accepted = await load();
            throw new InvalidOperationException("Unexpectedly accepted invalid deployment.");
        }
        catch (InvalidOperationException exception) when (exception.Message.StartsWith(expectedCode + ":", StringComparison.Ordinal))
        {
            Require(!Assemblies().Any(a => !before.Contains(a)
                && a.GetType(ProbeConstants.TypeName, throwOnError: false) is not null),
                "Rejected artifact was loaded before refusal.");
            Console.WriteLine("refused: " + expectedCode);
        }
    }

    private static HashSet<Assembly> Assemblies() => AssemblyLoadContext.All.SelectMany(c => c.Assemblies).ToHashSet();
    private static string? Tfm(Assembly assembly) => assembly.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName;
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
