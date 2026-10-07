using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ashlar.Abstractions;
using Ashlar.Core.Application.Execution.Routing;
using Ashlar.Core.Application.NodeCapabilityRuntime.Ports;

namespace Ashlar.Infrastructure.Execution.Routing;

/// <summary>
/// NCR-driven capability router for local vs RunPod execution.
/// </summary>
public sealed class NcrCapabilityRouter : ICapabilityRouter
{
    private readonly INCRCapabilitySnapshot _snapshot;
    private readonly IPeerCapabilitySnapshot _peerSnapshot;
    private readonly IPeerExecutor _peerExecutor;
    private readonly ILocalExecutor _localExecutor;
    private readonly RunPodBrick _runPodBrick;
    private readonly IOptions<RunPodBrickConfig> _config;
    private readonly ILogger<NcrCapabilityRouter> _logger;
    private readonly IOptions<AshlarResolvedDeploymentProfileOptions>? _profile;
    private readonly PeerTrustPolicyResolver _peerTrustResolver;

    /// <summary>Initializes a new ncr capability router.</summary>
    public NcrCapabilityRouter(
        INCRCapabilitySnapshot snapshot,
        IPeerCapabilitySnapshot peerSnapshot,
        IPeerExecutor peerExecutor,
        ILocalExecutor localExecutor,
        RunPodBrick runPodBrick,
        IOptions<RunPodBrickConfig> config,
        ILogger<NcrCapabilityRouter> logger,
        IOptions<AshlarResolvedDeploymentProfileOptions>? deploymentProfile = null)
    {
        _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        _peerSnapshot = peerSnapshot ?? throw new ArgumentNullException(nameof(peerSnapshot));
        _peerExecutor = peerExecutor ?? throw new ArgumentNullException(nameof(peerExecutor));
        _localExecutor = localExecutor ?? throw new ArgumentNullException(nameof(localExecutor));
        _runPodBrick = runPodBrick ?? throw new ArgumentNullException(nameof(runPodBrick));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _profile = deploymentProfile;
        _peerTrustResolver = new PeerTrustPolicyResolver(
            _config.Value.PeerTrustPolicy,
            _config.Value.TrustedPeerIdsCsv,
            _config.Value.UntrustedPeerIdsCsv);
    }

    private bool IsAirGapped => _profile?.Value.IsAirGapped == true;

    /// <summary>Resolve execution target.</summary>
    public ExecutionTarget ResolveExecutionTarget(JobRequirements requirements)
    {
        if (requirements.RemoteExecutionPreference == RemoteExecutionPreference.PeerNetworkOnly)
        {
            if (IsAirGapped)
            {
                const string refused =
                    "AirGapped: peer-network-only execution is unavailable; remote peers are not used on this profile.";
                _logger.LogInformation("capability-routing decision=refused reason={Reason}", refused);
                throw new InvalidOperationException(refused);
            }

            return ResolveRemoteTarget(requirements, "Explicit peer-network execution requested.");
        }

        if (!IsAirGapped && requirements.RemoteExecutionPreference == RemoteExecutionPreference.PreferPeerNetwork)
        {
            var peerRoutingEnabled = _config.Value.EnablePeerNetworkRouting;
            var hasEligiblePeers = _peerSnapshot.Candidates.Any(peer => IsPeerEligible(peer, requirements));
            if (peerRoutingEnabled && hasEligiblePeers)
            {
                return ResolveRemoteTarget(requirements, "Preferred peer-network execution requested.");
            }
        }

        var remoteReason = ResolveRemoteReason(requirements);
        if (remoteReason is null)
        {
            const string localReason = "Local capabilities satisfy VRAM, compute class, and queue-depth checks.";
            _logger.LogInformation("capability-routing decision=local reason={Reason}", localReason);
            return new ExecutionTarget.Local(_localExecutor, localReason);
        }

        if (IsAirGapped)
        {
            var airGappedReason = $"AirGapped: remote execution unavailable; running locally ({remoteReason})";
            _logger.LogInformation("capability-routing decision=local reason={Reason}", airGappedReason);
            return new ExecutionTarget.Local(_localExecutor, airGappedReason);
        }

        return ResolveRemoteTarget(requirements, remoteReason);
    }

    private ExecutionTarget ResolveRemoteTarget(JobRequirements requirements, string baseReason)
    {
        var optionPreference = _config.Value.PreferPeerNetworkOverCloud
            ? RemoteExecutionPreference.PreferPeerNetwork
            : RemoteExecutionPreference.CloudOnly;

        var preference = requirements.RemoteExecutionPreference == RemoteExecutionPreference.UseSystemDefault
            ? optionPreference
            : requirements.RemoteExecutionPreference;

        var peerRoutingEnabled = _config.Value.EnablePeerNetworkRouting;
        var eligiblePeers = _peerSnapshot.Candidates.Where(peer => IsPeerEligible(peer, requirements)).ToArray();
        var peerCount = eligiblePeers.Length;
        var hasPeers = peerCount > 0;

        if (preference == RemoteExecutionPreference.PeerNetworkOnly)
        {
            if (peerRoutingEnabled)
            {
                var reason = hasPeers
                    ? $"{baseReason} Peer-network-only requested."
                    : $"{baseReason} Peer-network-only requested but no eligible peers found.";
                _logger.LogInformation(
                    "capability-routing decision=remote-peer reason={Reason} peers={PeerCount}",
                    reason,
                    peerCount);
                return new ExecutionTarget.Remote(_peerExecutor, reason);
            }

            var disabledReason = $"{baseReason} Peer-network-only requested but peer routing is disabled.";
            _logger.LogInformation("capability-routing decision=remote-peer reason={Reason}", disabledReason);
            return new ExecutionTarget.Remote(_peerExecutor, disabledReason);
        }

        if (peerRoutingEnabled &&
            preference == RemoteExecutionPreference.PreferPeerNetwork &&
            hasPeers)
        {
            var peerReason = $"{baseReason} Routing to peer Ashlar network (peers={peerCount}).";
            _logger.LogInformation("capability-routing decision=remote-peer reason={Reason}", peerReason);
            return new ExecutionTarget.Remote(_peerExecutor, peerReason);
        }

        if (peerRoutingEnabled &&
            preference == RemoteExecutionPreference.PreferPeerNetwork &&
            !hasPeers)
        {
            _logger.LogInformation(
                "capability-routing peer-preferred but no peers available; falling back to cloud provider. reason={Reason}",
                baseReason);
        }

        _logger.LogInformation("capability-routing decision=remote-cloud reason={Reason}", baseReason);
        return new ExecutionTarget.Remote(_runPodBrick, baseReason);
    }

    private string? ResolveRemoteReason(JobRequirements requirements)
    {
        if (requirements.IsOvernightOrBackground)
        {
            return "Overnight/background job forces remote execution.";
        }

        if (_snapshot.AvailableVramBytes < requirements.MinimumVramBytes)
        {
            return $"Insufficient VRAM: available={_snapshot.AvailableVramBytes}, required={requirements.MinimumVramBytes}.";
        }

        if (_snapshot.ComputeClass < requirements.ComputeClass)
        {
            return $"Insufficient compute class: available={_snapshot.ComputeClass}, required={requirements.ComputeClass}.";
        }

        var threshold = Math.Max(0, _config.Value.QueueDepthThreshold);
        if (_snapshot.CurrentQueueDepth > threshold)
        {
            return $"Local queue depth threshold exceeded: depth={_snapshot.CurrentQueueDepth}, threshold={threshold}.";
        }

        return null;
    }

    private bool IsPeerEligible(PeerExecutionCandidate peer, JobRequirements requirements)
    {
        if (!_peerTrustResolver.IsAllowed(peer))
        {
            _logger.LogWarning(
                "capability-routing peer skipped: trust tier not allowed peerId={PeerId} tier={TrustTier} policy={PeerTrustPolicy}",
                peer.PeerId,
                peer.TrustTier,
                _peerTrustResolver.Policy);
            return false;
        }

        if (string.IsNullOrWhiteSpace(peer.Endpoint))
        {
            return false;
        }

        if (peer.AvailableVramBytes > 0 && peer.AvailableVramBytes < requirements.MinimumVramBytes)
        {
            return false;
        }

        if (peer.ComputeClass != GpuComputeClass.None && peer.ComputeClass < requirements.ComputeClass)
        {
            return false;
        }

        var queueThreshold = Math.Max(0, _config.Value.QueueDepthThreshold);
        if (peer.QueueDepth > queueThreshold)
        {
            return false;
        }

        return true;
    }
}
