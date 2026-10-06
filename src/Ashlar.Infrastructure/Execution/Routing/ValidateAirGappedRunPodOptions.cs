using Microsoft.Extensions.Options;
using Ashlar.Abstractions;
using Ashlar.Core.Application.Execution.Routing;

namespace Ashlar.Infrastructure.Execution.Routing;

/// <summary>
/// Refuses peer-network routing on AirGapped. The flag stays available on every other profile.
/// </summary>
public sealed class ValidateAirGappedRunPodOptions : IValidateOptions<RunPodBrickConfig>
{
    private readonly IOptions<AshlarResolvedDeploymentProfileOptions> _profile;

    /// <summary>Creates the validator.</summary>
    public ValidateAirGappedRunPodOptions(IOptions<AshlarResolvedDeploymentProfileOptions> profile)
    {
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
    }

    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, RunPodBrickConfig options)
    {
        if (_profile.Value.IsAirGapped && options.EnablePeerNetworkRouting)
        {
            return ValidateOptionsResult.Fail(
                "Ashlar:RunPod:EnablePeerNetworkRouting=true is not permitted under the AirGapped deployment profile. " +
                "Peer-network execution is a remote path.");
        }

        return ValidateOptionsResult.Success;
    }
}
