using Ashlar.Core.Application.Adaptation.Models;
using Ashlar.Core.Application.Adaptation.Ports;
using Ashlar.Core.Domain.Bricks;

namespace Ashlar.Infrastructure.Adaptation;

/// <summary>
/// Decomposes bricks into editable BrickManifest.
/// </summary>
internal sealed class BrickDecomposer : IBrickDecomposer
{
    /// <inheritdoc />
    public Task<BrickManifest> DecomposeAsync(DomainBrick brick, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var manifest = new BrickManifest
        {
            Id = brick.Id,
            Name = brick.Name,
            Version = brick.Version,
            Description = brick.Description,
            Category = brick.Category,
            Interface = brick.Interface,
            ImplementationTypeName = brick.GetType().AssemblyQualifiedName,
        };

        return Task.FromResult(manifest);
    }
}
