using Ashlar.Core.Application.Mesh.Ports;

namespace Ashlar.Infrastructure.Mesh;

/// <summary>
/// File-based local transport. Uses a shared directory for peer inboxes.
/// SendAsync writes to peer's inbox; ReceiveAsync reads from our inbox.
/// Works for same-machine communication when peers use the same mesh base path.
/// </summary>
internal sealed class FileBasedLocalTransport : ILocalTransport
{
    private readonly string _meshBasePath;
    private readonly string _peerId;
    private bool _connected;

    /// <summary>Initializes a new file based local transport.</summary>
    public FileBasedLocalTransport(string meshBasePath, string peerId)
    {
        _meshBasePath = meshBasePath ?? throw new ArgumentNullException(nameof(meshBasePath));
        _peerId = peerId ?? throw new ArgumentNullException(nameof(peerId));
    }

    /// <inheritdoc />
    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var inbox = GetInboxPath(_peerId);
        Directory.CreateDirectory(inbox);
        _connected = true;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task SendAsync(string peerId, byte[] message, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_connected)
            throw new InvalidOperationException("Transport not connected. Call ConnectAsync first.");
        var inbox = GetInboxPath(peerId);
        Directory.CreateDirectory(inbox);
        var fileName = $"{_peerId}-{Guid.NewGuid():N}.bin";
        var filePath = Path.Combine(inbox, fileName);
        File.WriteAllBytes(filePath, message);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<byte[]?> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_connected)
            return Task.FromResult<byte[]?>(null);
        var inbox = GetInboxPath(_peerId);
        if (!Directory.Exists(inbox))
            return Task.FromResult<byte[]?>(null);
        var files = Directory.GetFiles(inbox, "*.bin").OrderBy(f => File.GetCreationTimeUtc(f)).ToArray();
        foreach (var file in files)
        {
            try
            {
                var bytes = File.ReadAllBytes(file);
                File.Delete(file);
                return Task.FromResult<byte[]?>(bytes);
            }
            catch
            {
                // Skip locked or invalid files
            }
        }
        return Task.FromResult<byte[]?>(null);
    }

    private string GetInboxPath(string peerId) => Path.Combine(_meshBasePath, peerId, "inbox");
}
