using Ashlar.Certification.Contracts;
using Ashlar.Core.Domain.Bricks;
using Ashlar.Core.Domain.Execution;
using Ashlar.Hosting;
using Ashlar.Infrastructure.Certification;
using Ashlar.Infrastructure.Execution;
using Ashlar.Tests.Infrastructure.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSec.Cryptography;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

[Trait("Category", "Certification")]
[Trait("Category", "ProdStyle")]
[Collection("hot-swap-host")]
public sealed class VerifiedHostingProdStyleTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ashlar-verified-host-" + Guid.NewGuid());
    private const string HmacKey = "verified-host-test-key";

    [Fact(Timeout = TestTimeouts.Integration)]
    public async Task Actual_gate_deployment_is_the_only_registered_brick_and_tampering_blocks_startup()
    {
        Directory.CreateDirectory(_directory);
        var root = FindRepoRoot();
        var fixture = Path.Combine(root, "samples", "certified-brick-reuse", "Ashlar.Certified.DamageResolver");
        var sourcePath = Path.Combine(_directory, "DamageResolverBrick.cs");
        File.Copy(Path.Combine(fixture, "DamageResolverBrick.cs"), sourcePath);
        var witnessPath = Path.Combine(_directory, "witness.json");
        File.Copy(Path.Combine(fixture, "damage-resolver.witness.json"), witnessPath);
        await File.WriteAllTextAsync(Path.Combine(_directory, "Fixture.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");

        using var key = Key.Create(SignatureAlgorithm.Ed25519,
            new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
        var policy = CertificationTrustPolicy.FromTrustedKeys([
            Convert.ToBase64String(key.PublicKey.Export(KeyBlobFormat.RawPublicKey))]);
        var run = await BrickCertificationRun.ExecuteAsync(new BrickCertificationRunRequest
        {
            BrickProjectDirectory = _directory, WitnessSpecPath = witnessPath,
            RecordPath = Path.Combine(_directory, "deployment", "record.json"),
            Signer = new CertificationRecordSigner(HmacKey, Convert.ToBase64String(key.Export(KeyBlobFormat.RawPrivateKey)))
        });
        Assert.True(run.Admitted, run.FailureCheck + ": " + run.Record.Reason);
        Assert.NotNull(run.ArtifactPath);
        var deployment = new VerifiedBrickDeployment("damage-resolver", sourcePath, run.RecordPath, run.ArtifactPath);

        using var host = BuildHost(deployment, policy);
        var registry = host.Services.GetRequiredService<IBrickRegistry>();
        Assert.Throws<InvalidOperationException>(() => registry.GetAllBricks());
        Assert.Null(host.Services.GetService<BrickRegistry>());
        await host.StartAsync();
        var brick = Assert.Single(registry.GetAllBricks());
        Assert.Equal("damage-resolver", brick.Id);
        Assert.Null(registry.GetBrick("unverified"));
        await File.WriteAllBytesAsync(run.ArtifactPath, [0, 1, 2]);
        var input = new BrickInput(new Dictionary<string, object>
            { ["baseDamage"] = 100, ["critMultiplierPercent"] = 150, ["armor"] = 20, ["isCrit"] = true });
        var output = await brick.ExecuteAsync(input, ImplementationType.Deterministic,
            new Ashlar.Infrastructure.Execution.ExecutionContext());
        Assert.Equal(130, output.Get<int>("finalDamage"));
        await host.StopAsync();
        Assert.Throws<InvalidOperationException>(() => registry.GetAllBricks());

        using var refusedHost = BuildHost(deployment, policy);
        await Assert.ThrowsAsync<InvalidOperationException>(() => refusedHost.StartAsync());
        Assert.Throws<InvalidOperationException>(() => refusedHost.Services.GetRequiredService<IBrickRegistry>().GetAllBricks());
    }

    [Fact(Timeout = TestTimeouts.Integration)]
    public async Task Unpinned_configuration_and_duplicate_identities_fail_before_file_reads()
    {
        var missing = new VerifiedBrickDeployment("probe", "absent.cs", "absent.json", "absent.dll");
        Assert.Throws<CertificationTrustConfigurationException>(() => new ServiceCollection()
            .AddAshlarVerifiedBricks([missing], CertificationTrustPolicy.Unpinned));
        await Assert.ThrowsAsync<CertificationTrustConfigurationException>(() =>
            VerifiedBrickRegistry.LoadFromFilesAsync([missing], CertificationTrustPolicy.Unpinned));
        using var key = Key.Create(SignatureAlgorithm.Ed25519);
        var policy = CertificationTrustPolicy.FromTrustedKeys([
            Convert.ToBase64String(key.PublicKey.Export(KeyBlobFormat.RawPublicKey))]);
        await Assert.ThrowsAsync<ArgumentException>(() => VerifiedBrickRegistry.LoadFromFilesAsync(
            [missing, missing with { BrickId = "PROBE" }], policy));
    }

    private IHost BuildHost(VerifiedBrickDeployment deployment, CertificationTrustPolicy policy) =>
        Host.CreateDefaultBuilder(Array.Empty<string>()).ConfigureServices(services =>
        {
            services.AddSingleton<IBrickRegistry>(_ => throw new InvalidOperationException("Raw registry must never resolve."));
            services.AddAshlarVerifiedBricks([deployment], policy, HmacKey, options =>
            {
                options.DeploymentProfile = AshlarDeploymentProfile.System;
                options.ConfigPath = Path.Combine(_directory, "config.json");
                options.DisableObservationPipeline = true;
            });
        }).Build();

    [Fact(Timeout = TestTimeouts.Integration)]
    public async Task Later_raw_registry_override_cannot_silently_disable_verified_hosting()
    {
        using var key = Key.Create(SignatureAlgorithm.Ed25519);
        var policy = CertificationTrustPolicy.FromTrustedKeys([
            Convert.ToBase64String(key.PublicKey.Export(KeyBlobFormat.RawPublicKey))]);
        using var host = Host.CreateDefaultBuilder(Array.Empty<string>()).ConfigureServices(services =>
        {
            services.AddAshlarVerifiedBricks(
                [new VerifiedBrickDeployment("probe", "absent.cs", "absent.json", "absent.dll")], policy,
                configure: options => options.DeploymentProfile = AshlarDeploymentProfile.System);
            services.AddSingleton<IBrickRegistry>(new BrickRegistry([]));
        }).Build();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());
        Assert.Contains("replaced the verified brick registry", error.Message);
    }

    private static string FindRepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Ashlar.sln"))) return directory.FullName;
        throw new InvalidOperationException("Repository root not found.");
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
