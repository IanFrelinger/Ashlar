using Ashlar.Certification.Contracts;
using Ashlar.Core.Domain.Execution;

namespace Ashlar.Infrastructure.Certification;

/// <summary>A fixed registry containing only pinned, verified deployment bytes.</summary>
public sealed class VerifiedBrickRegistry : IBrickRegistry, IDisposable
{
    private readonly Dictionary<string, VerifiedBrick> _bricks;
    private readonly object _lifecycle = new();
    private bool _disposed;
    private VerifiedBrickRegistry(Dictionary<string, VerifiedBrick> bricks) => _bricks = bricks;

    /// <summary>Loads all deployments or disposes the partial registry on any refusal.</summary>
    public static async Task<VerifiedBrickRegistry> LoadFromFilesAsync(
        IEnumerable<VerifiedBrickDeployment> deployments, CertificationTrustPolicy policy,
        string? hmacKey = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (!policy.Strict.PinningEnabled)
            throw new CertificationTrustConfigurationException("Verified deployments require pinned Ed25519 keys.");
        ArgumentNullException.ThrowIfNull(deployments);
        cancellationToken.ThrowIfCancellationRequested();
        var entries = deployments.ToArray();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            ArgumentNullException.ThrowIfNull(entry);
            ArgumentException.ThrowIfNullOrWhiteSpace(entry.BrickId);
            if (!ids.Add(entry.BrickId))
                throw new ArgumentException("Duplicate verified brick identity: " + entry.BrickId, nameof(deployments));
        }
        var loaded = new Dictionary<string, VerifiedBrick>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var entry in entries)
                loaded.Add(entry.BrickId, await VerifiedBrick.LoadFromFilesAsync(entry.BrickId, entry.SourcePath,
                    entry.RecordPath, entry.AssemblyPath, policy, hmacKey, cancellationToken).ConfigureAwait(false));
            return new VerifiedBrickRegistry(loaded);
        }
        catch
        {
            foreach (var brick in loaded.Values) brick.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public DomainBrick? GetBrick(string id)
    {
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _bricks.TryGetValue(id, out var brick) ? brick.Brick : null;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<DomainBrick> GetAllBricks()
    {
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _bricks.Values.Select(brick => brick.Brick).ToArray();
        }
    }

    /// <summary>Releases every owned deployment.</summary>
    public void Dispose()
    {
        lock (_lifecycle)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var brick in _bricks.Values) brick.Dispose();
            _bricks.Clear();
        }
    }
}
