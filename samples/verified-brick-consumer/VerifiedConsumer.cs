using System.Runtime.Loader;
using System.Text.Json;
using Ashlar.Certification.Contracts;
using Ashlar.Core.Domain.Bricks;
using Ashlar.Core.Domain.Execution;
using Ashlar.Infrastructure.Certification;
using DomainBrick = Ashlar.Core.Domain.Bricks.Brick;

namespace Ashlar.Samples.VerifiedBrickConsumer;

/// <summary>Loads and executes the deployed assembly covered by a pinned certification record.</summary>
public sealed class VerifiedConsumer : IDisposable
{
    private AssemblyLoadContext? _loadContext;
    private DomainBrick? _brick;

    private VerifiedConsumer(AssemblyLoadContext loadContext, DomainBrick brick, string artifactSha256)
    {
        _loadContext = loadContext;
        _brick = brick;
        ArtifactSha256 = artifactSha256;
    }

    /// <summary>The signed hash of the assembly bytes loaded by this consumer.</summary>
    public string ArtifactSha256 { get; }

    /// <summary>
    /// Requires signer pinning before reading deployment files, verifies source and artifact,
    /// then loads those same in-memory bytes and resolves the type named by the signed record.
    /// </summary>
    public static async Task<VerifiedConsumer> LoadFromFilesAsync(
        string expectedBrickId,
        string sourcePath,
        string recordPath,
        string assemblyPath,
        CertificationTrustPolicy policy,
        string? hmacKey = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (!policy.Strict.PinningEnabled)
        {
            throw new CertificationTrustConfigurationException(
                "The verified consumer requires at least one trusted Ed25519 public key.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(expectedBrickId);
        var source = await File.ReadAllTextAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        var json = await File.ReadAllTextAsync(recordPath, cancellationToken).ConfigureAwait(false);
        var record = JsonSerializer.Deserialize<CertificationRecordData>(
            json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("certification-record-invalid: record JSON is null.");
        if (!string.Equals(record.BrickId, expectedBrickId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("brick-id-mismatch: record names a different brick.");
        }

        var assemblyBytes = await File.ReadAllBytesAsync(assemblyPath, cancellationToken).ConfigureAwait(false);
        var trust = CertificationTrustVerifier.Verify(record, source, assemblyBytes, hmacKey, policy.Strict);
        if (!trust.Trusted)
        {
            throw new InvalidOperationException($"{trust.FailureCode}: {trust.Reason}");
        }

        var bindings = record.Inputs
            .Where(input => string.Equals(input.Kind, CertificationInputKinds.GateEmittedArtifact, StringComparison.Ordinal))
            .ToArray();
        if (bindings.Length != 1 || string.IsNullOrWhiteSpace(bindings[0].Id))
        {
            throw new InvalidOperationException("artifact-binding-invalid: expected one artifact with a type name.");
        }

        // Inspect before loading: even a module initializer must not run before the import fence.
        IlImportFence.Inspect(assemblyBytes);
        cancellationToken.ThrowIfCancellationRequested();
        var loadContext = new AssemblyLoadContext($"VerifiedConsumer_{Guid.NewGuid():N}", isCollectible: true);
        try
        {
            // Dependencies resolve through the default context so the host and brick share contracts.
            // Never re-read the DLL after verification, or compile a replacement from the source.
            using var stream = new MemoryStream(assemblyBytes, writable: false);
            var assembly = loadContext.LoadFromStream(stream);
            var type = assembly.GetType(bindings[0].Id, throwOnError: false);
            if (type is null || type.Assembly != assembly || !typeof(DomainBrick).IsAssignableFrom(type)
                || type.IsAbstract || type.ContainsGenericParameters)
            {
                throw new InvalidOperationException("artifact-type-invalid: record must name a concrete brick in the verified assembly.");
            }

            var brick = (DomainBrick)Activator.CreateInstance(type)!;
            if (!string.Equals(brick.Id, expectedBrickId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("brick-instance-id-mismatch: loaded brick has a different identity.");
            }

            return new VerifiedConsumer(loadContext, brick, bindings[0].Hash);
        }
        catch
        {
            loadContext.Unload();
            throw;
        }
    }

    /// <summary>Executes the verified brick using its deterministic implementation.</summary>
    public Task<BrickOutput> ExecuteAsync(
        BrickInput input,
        IExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var brick = _brick;
        ObjectDisposedException.ThrowIf(brick is null, this);
        return brick.ExecuteAsync(input, ImplementationType.Deterministic, context, cancellationToken);
    }

    /// <summary>Releases the brick and requests unloading of its collectible assembly context.</summary>
    public void Dispose()
    {
        _brick = null;
        Interlocked.Exchange(ref _loadContext, null)?.Unload();
    }
}
