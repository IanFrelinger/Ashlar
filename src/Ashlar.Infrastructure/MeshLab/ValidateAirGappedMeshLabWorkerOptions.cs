using Microsoft.Extensions.Options;
using Ashlar.Abstractions;

namespace Ashlar.Infrastructure.MeshLab;

/// <summary>
/// Refuses the mesh-lab worker executor on AirGapped. The option stays off by default everywhere,
/// and enabling it on any other profile is unchanged.
/// </summary>
public sealed class ValidateAirGappedMeshLabWorkerOptions : IValidateOptions<MeshLabWorkerExecutorOptions>
{
    private readonly IOptions<AshlarResolvedDeploymentProfileOptions> _profile;

    /// <summary>Creates the validator.</summary>
    public ValidateAirGappedMeshLabWorkerOptions(IOptions<AshlarResolvedDeploymentProfileOptions> profile)
    {
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
    }

    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, MeshLabWorkerExecutorOptions options)
    {
        if (_profile.Value.IsAirGapped && options.Enabled)
        {
            return ValidateOptionsResult.Fail(
                "Ashlar:MeshLab:WorkerExecutor:Enabled=true is not permitted under the AirGapped deployment profile. " +
                "The worker calls a remote director.");
        }

        return ValidateOptionsResult.Success;
    }
}
