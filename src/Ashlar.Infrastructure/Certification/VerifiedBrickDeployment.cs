namespace Ashlar.Infrastructure.Certification;

/// <summary>Host-owned paths and expected identity for one certified deployment.</summary>
/// <param name="BrickId">The exact expected brick identity.</param>
/// <param name="SourcePath">The source whose hash is signed.</param>
/// <param name="RecordPath">The signed certification record.</param>
/// <param name="AssemblyPath">The gate-emitted assembly whose hash is signed.</param>
public sealed record VerifiedBrickDeployment(string BrickId, string SourcePath, string RecordPath, string AssemblyPath);
