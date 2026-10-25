using Ashlar.Certification.Contracts;
using Ashlar.Core.Domain.Execution;
using Ashlar.Infrastructure.Certification;

namespace Ashlar.Samples.VerifiedBrickConsumer;

/// <summary>Sample facade over the packaged verified loader.</summary>
public sealed class VerifiedConsumer : IDisposable
{
    private readonly VerifiedBrick _brick;
    private VerifiedConsumer(VerifiedBrick brick) => _brick = brick;
    /// <summary>The signed hash of the loaded assembly bytes.</summary>
    public string ArtifactSha256 => _brick.ArtifactSha256;
    /// <summary>Verifies a pinned deployment and loads the same verified bytes.</summary>
    public static async Task<VerifiedConsumer> LoadFromFilesAsync(
        string expectedBrickId, string sourcePath, string recordPath, string assemblyPath,
        CertificationTrustPolicy policy, string? hmacKey = null, CancellationToken cancellationToken = default) =>
        new(await VerifiedBrick.LoadFromFilesAsync(expectedBrickId, sourcePath, recordPath, assemblyPath,
            policy, hmacKey, cancellationToken).ConfigureAwait(false));
    /// <summary>Executes the verified brick.</summary>
    public Task<BrickOutput> ExecuteAsync(BrickInput input, IExecutionContext context,
        CancellationToken cancellationToken = default) => _brick.ExecuteAsync(input, context, cancellationToken);
    /// <summary>Releases the verified deployment.</summary>
    public void Dispose() => _brick.Dispose();
}
