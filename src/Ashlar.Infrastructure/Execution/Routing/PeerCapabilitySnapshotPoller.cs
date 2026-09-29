using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ashlar.Core.Application.Execution.Routing;
using Ashlar.Core.Application.Mesh.Ports;

namespace Ashlar.Infrastructure.Execution.Routing;

/// <summary>
/// Lightweight poller that keeps peer capability snapshots current for routing.
/// </summary>
internal sealed class PeerCapabilitySnapshotPoller : BackgroundService, IPeerCapabilitySnapshot
{
    private readonly IInstanceDiscovery _discovery;
    private readonly ILogger<PeerCapabilitySnapshotPoller> _logger;
    private readonly TimeSpan _pollInterval;
    private readonly object _gate = new();
    private IReadOnlyList<PeerExecutionCandidate> _candidates = Array.Empty<PeerExecutionCandidate>();
    private readonly string _capabilityId;
    private readonly PeerTrustPolicyResolver _trustPolicyResolver;

    /// <summary>Initializes a new peer capability snapshot poller.</summary>
    public PeerCapabilitySnapshotPoller(
        IInstanceDiscovery discovery,
        ILogger<PeerCapabilitySnapshotPoller> logger,
        IOptions<RunPodBrickConfig> config)
    {
        _discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        var configured = config?.Value?.PeerDiscoveryInterval ?? TimeSpan.FromSeconds(10);
        _pollInterval = configured <= TimeSpan.Zero ? TimeSpan.FromSeconds(5) : configured;
        _capabilityId = string.IsNullOrWhiteSpace(config?.Value?.PeerCapabilityId)
            ? "generation.capability-routing"
            : config!.Value.PeerCapabilityId;
        _trustPolicyResolver = new PeerTrustPolicyResolver(
            config?.Value?.PeerTrustPolicy,
            config?.Value?.TrustedPeerIdsCsv,
            config?.Value?.UntrustedPeerIdsCsv);
    }

    /// <summary>Current peer execution candidates from the latest capability snapshot.</summary>
    public IReadOnlyList<PeerExecutionCandidate> Candidates
    {
        get
        {
            lock (_gate)
            {
                return _candidates;
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var discovered = await _discovery.DiscoverAsync(stoppingToken).ConfigureAwait(false);
                var next = discovered.Select(MapPeer).ToArray();
                lock (_gate)
                {
                    _candidates = next;
                }

                _logger.LogDebug("peer-capabilities snapshot updated peers={PeerCount}", next.Length);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "peer-capabilities snapshot refresh failed");
            }

            try
            {
                await Task.Delay(_pollInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private PeerExecutionCandidate MapPeer(Ashlar.Core.Application.Mesh.Models.PeerInfo peer)
    {
        var discoveredTier = ParseTrustTier(peer.Capabilities, peer.TrustTier);
        var effectiveTier = _trustPolicyResolver.ResolveTier(peer with { TrustTier = discoveredTier });
        var vram = ParseLongCapability(peer.Capabilities, "vram");
        var queueDepth = ParseIntCapability(peer.Capabilities, "queue");
        var compute = ParseComputeClass(peer.Capabilities);
        var supportsGeneration = peer.Capabilities.Any(cap =>
            cap.Contains(_capabilityId, StringComparison.OrdinalIgnoreCase) ||
            cap.Contains("generation", StringComparison.OrdinalIgnoreCase) ||
            cap.Contains("ashlar-cli", StringComparison.OrdinalIgnoreCase));
        if (!supportsGeneration)
        {
            return new PeerExecutionCandidate
            {
                PeerId = peer.PeerId,
                Endpoint = string.Empty,
                TrustTier = effectiveTier
            };
        }

        if (!_trustPolicyResolver.IsAllowed(effectiveTier))
        {
            _logger.LogDebug(
                "peer-capabilities candidate blocked by trust policy policy={Policy} peer={PeerId} tier={Tier}",
                _trustPolicyResolver.Policy,
                peer.PeerId,
                effectiveTier);
            return new PeerExecutionCandidate
            {
                PeerId = peer.PeerId,
                Endpoint = string.Empty,
                AvailableVramBytes = vram,
                ComputeClass = compute,
                QueueDepth = queueDepth,
                TrustTier = effectiveTier,
                CapturedAt = DateTimeOffset.UtcNow
            };
        }

        return new PeerExecutionCandidate
        {
            PeerId = peer.PeerId,
            Endpoint = peer.Endpoint,
            AvailableVramBytes = vram,
            ComputeClass = compute,
            QueueDepth = queueDepth,
            TrustTier = effectiveTier,
            CapturedAt = DateTimeOffset.UtcNow
        };
    }

    private static long ParseLongCapability(IEnumerable<string> capabilities, string key)
    {
        foreach (var capability in capabilities)
        {
            if (capability.StartsWith($"{key}:", StringComparison.OrdinalIgnoreCase))
            {
                var value = capability[(key.Length + 1)..];
                if (long.TryParse(value, out var parsed))
                {
                    return parsed;
                }
            }
        }

        return 0;
    }

    private static int ParseIntCapability(IEnumerable<string> capabilities, string key)
    {
        foreach (var capability in capabilities)
        {
            if (capability.StartsWith($"{key}:", StringComparison.OrdinalIgnoreCase))
            {
                var value = capability[(key.Length + 1)..];
                if (int.TryParse(value, out var parsed))
                {
                    return parsed;
                }
            }
        }

        return 0;
    }

    private static GpuComputeClass ParseComputeClass(IEnumerable<string> capabilities)
    {
        foreach (var capability in capabilities)
        {
            if (capability.StartsWith("compute:", StringComparison.OrdinalIgnoreCase))
            {
                var text = capability["compute:".Length..];
                if (Enum.TryParse<GpuComputeClass>(text, true, out var parsed))
                {
                    return parsed;
                }
            }
        }

        return GpuComputeClass.None;
    }

    private static Ashlar.Core.Application.Mesh.Models.PeerTrustTier ParseTrustTier(
        IEnumerable<string> capabilities,
        Ashlar.Core.Application.Mesh.Models.PeerTrustTier defaultTier)
    {
        foreach (var capability in capabilities)
        {
            if (capability.StartsWith("trust:", StringComparison.OrdinalIgnoreCase))
            {
                var value = capability["trust:".Length..];
                if (Enum.TryParse<Ashlar.Core.Application.Mesh.Models.PeerTrustTier>(value, true, out var parsed))
                {
                    return parsed;
                }
            }
        }

        return defaultTier;
    }
}
