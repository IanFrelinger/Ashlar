namespace Ashlar.VerifiedArtifactPortability;

// Test evidence shared by the producer and every consumer. The public key pins this
// run's synthetic test signer; this manifest is not an operator trust configuration.
internal sealed record DeploymentManifest
{
    public required string BrickId { get; init; }
    public required string BrickTypeName { get; init; }
    public required string AssemblySha256 { get; init; }
    public required Guid ModuleVersionId { get; init; }
    public required string TrustedPublicKey { get; init; }
    public required string BuildTargetFramework { get; init; }
}

internal static class ProbeConstants
{
    public const string BrickId = "verified-portability-probe";
    public const string TypeName = "Ashlar.PortabilityFixture.PortableBrick";
    public const string HmacKey = "portability-test-key-not-for-production";
}
