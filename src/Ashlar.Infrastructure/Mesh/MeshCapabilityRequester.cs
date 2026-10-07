using System.Text.Json;
using Microsoft.Extensions.Logging;
using Ashlar.Core.Application.Mesh.Models;
using Ashlar.Core.Application.Mesh.Ports;
using Ashlar.Infrastructure.Execution.Routing;

namespace Ashlar.Infrastructure.Mesh;

/// <summary>
/// Capability requester that uses discovery, advertisement, and transport to request
/// capabilities from peers. Sends request via ILocalTransport and polls for response.
/// Returns null if no peers have the capability or no response within timeout.
/// </summary>
internal sealed class MeshCapabilityRequester : ICapabilityRequester
{
    private readonly ICapabilityAdvertisement _advertisement;
    private readonly ILocalTransport _transport;
    private readonly string _requesterPeerId;
    private readonly ILogger<MeshCapabilityRequester>? _logger;
    private readonly TimeSpan _receiveTimeout;
    private readonly int _pollIntervalMs;
    private readonly PeerTrustPolicyResolver _trustPolicy;

    /// <summary>Initializes a new mesh capability requester.</summary>
    public MeshCapabilityRequester(
        ICapabilityAdvertisement advertisement,
        ILocalTransport transport,
        string requesterPeerId,
        string? trustPolicy,
        string? trustedPeerIdsCsv,
        string? untrustedPeerIdsCsv,
        ILogger<MeshCapabilityRequester>? logger = null,
        TimeSpan? receiveTimeout = null,
        int pollIntervalMs = 100)
    {
        _advertisement = advertisement ?? throw new ArgumentNullException(nameof(advertisement));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _requesterPeerId = requesterPeerId ?? throw new ArgumentNullException(nameof(requesterPeerId));
        _logger = logger;
        _receiveTimeout = receiveTimeout ?? TimeSpan.FromSeconds(3);
        _pollIntervalMs = Math.Max(50, pollIntervalMs);
        _trustPolicy = new PeerTrustPolicyResolver(trustPolicy, trustedPeerIdsCsv, untrustedPeerIdsCsv);
    }

    /// <inheritdoc />
    public async Task<Artifact?> RequestAsync(string capability, ArtifactFormat preferredFormat, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _transport.ConnectAsync(cancellationToken);
        var peers = await _advertisement.FindPeersWithCapabilityAsync(capability, cancellationToken);
        peers = peers
            .Where(peer => _trustPolicy.IsAllowed(_trustPolicy.ResolveTier(peer)))
            .ToArray();
        if (peers.Count == 0)
        {
            _logger?.LogDebug("No peers with capability {Capability}", capability);
            return null;
        }

        var requestId = Guid.NewGuid().ToString("N");
        var request = new { capability, format = preferredFormat.ToString(), requestId, requesterId = _requesterPeerId };
        var requestBytes = JsonSerializer.SerializeToUtf8Bytes(request);

        foreach (var peer in peers)
        {
            try
            {
                await _transport.SendAsync(peer.PeerId, requestBytes, cancellationToken);
                var deadline = DateTime.UtcNow + _receiveTimeout;
                while (DateTime.UtcNow < deadline)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var responseBytes = await _transport.ReceiveAsync(cancellationToken);
                    if (responseBytes != null && responseBytes.Length > 0)
                    {
                        var artifact = TryParseResponse(responseBytes, requestId);
                        if (artifact != null)
                            return artifact;
                    }
                    await Task.Delay(_pollIntervalMs, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Request to peer {PeerId} failed", peer.PeerId);
            }
        }

        return null;
    }

    private static Artifact? TryParseResponse(byte[] responseBytes, string expectedRequestId)
    {
        try
        {
            var json = JsonDocument.Parse(responseBytes);
            var root = json.RootElement;
            if (root.TryGetProperty("requestId", out var rid) && rid.GetString() == expectedRequestId &&
                root.TryGetProperty("format", out var fmt) &&
                root.TryGetProperty("content", out var content))
            {
                var format = Enum.TryParse<ArtifactFormat>(fmt.GetString(), true, out var f) ? f : ArtifactFormat.Binary;
                var decoded = Convert.FromBase64String(content.GetString() ?? "");
                return new Artifact { Format = format, Content = decoded };
            }
        }
        catch
        {
            // Ignore parse errors
        }
        return null;
    }
}
