using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Ashlar.Core.Application.Mesh.Models;
using Ashlar.Core.Application.Mesh.Ports;

namespace Ashlar.Infrastructure.Mesh;

/// <summary>
/// Fulfills capability requests by polling the transport inbox and invoking registered handlers.
/// Phase E: Local IPC mesh - validates artifact transfer between instances.
/// </summary>
internal sealed class MeshCapabilityFulfiller : ICapabilityFulfiller
{
    private readonly ILocalTransport _transport;
    private readonly ILogger<MeshCapabilityFulfiller>? _logger;
    private readonly ConcurrentDictionary<string, Func<ArtifactFormat, CancellationToken, Task<Artifact>>> _handlers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Initializes a new mesh capability fulfiller.</summary>
    public MeshCapabilityFulfiller(ILocalTransport transport, ILogger<MeshCapabilityFulfiller>? logger = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _logger = logger;
    }

    /// <inheritdoc />
    public void RegisterHandler(string capability, Func<ArtifactFormat, CancellationToken, Task<Artifact>> handler)
    {
        _handlers[capability] = handler ?? throw new ArgumentNullException(nameof(handler));
    }

    /// <inheritdoc />
    public async Task<bool> ProcessOneAsync(CancellationToken cancellationToken = default)
    {
        await _transport.ConnectAsync(cancellationToken);
        var requestBytes = await _transport.ReceiveAsync(cancellationToken);
        if (requestBytes == null || requestBytes.Length == 0)
            return false;

        try
        {
            var doc = JsonDocument.Parse(requestBytes);
            var root = doc.RootElement;
            if (!root.TryGetProperty("capability", out var capEl) ||
                !root.TryGetProperty("requesterId", out var reqEl) ||
                !root.TryGetProperty("requestId", out var ridEl) ||
                !root.TryGetProperty("format", out var fmtEl))
                return false;

            var capability = capEl.GetString() ?? "";
            var requesterId = reqEl.GetString() ?? "";
            var requestId = ridEl.GetString() ?? "";
            var format = Enum.TryParse<ArtifactFormat>(fmtEl.GetString(), true, out var f) ? f : ArtifactFormat.Binary;

            if (!_handlers.TryGetValue(capability, out var handler))
            {
                _logger?.LogDebug("No handler for capability {Capability}", capability);
                return false;
            }

            var artifact = await handler(format, cancellationToken);
            var response = new
            {
                requestId,
                format = artifact.Format.ToString(),
                content = Convert.ToBase64String(artifact.Content),
            };
            var responseBytes = JsonSerializer.SerializeToUtf8Bytes(response);
            await _transport.SendAsync(requesterId, responseBytes, cancellationToken);
            _logger?.LogDebug("Fulfilled capability {Capability} for requester {RequesterId}", capability, requesterId);
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Failed to process mesh request");
            return false;
        }
    }
}
