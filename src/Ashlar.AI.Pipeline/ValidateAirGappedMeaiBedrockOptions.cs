using Microsoft.Extensions.Options;
using Ashlar.Abstractions;

namespace Ashlar.AI.Pipeline;

/// <summary>
/// Refuses Bedrock on AirGapped. Enabling it on any other profile is unchanged.
/// </summary>
public sealed class ValidateAirGappedMeaiBedrockOptions : IValidateOptions<MeaiPipelineOptions>
{
    private readonly IOptions<AshlarResolvedDeploymentProfileOptions> _profile;

    /// <summary>Creates the validator.</summary>
    public ValidateAirGappedMeaiBedrockOptions(IOptions<AshlarResolvedDeploymentProfileOptions> profile)
    {
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
    }

    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, MeaiPipelineOptions options)
    {
        if (_profile.Value.IsAirGapped && options.Bedrock.Enabled)
        {
            return ValidateOptionsResult.Fail(
                "Ashlar:Meai:Bedrock:Enabled=true is not permitted under the AirGapped deployment profile. " +
                "Bedrock is a cloud model path.");
        }

        return ValidateOptionsResult.Success;
    }
}
