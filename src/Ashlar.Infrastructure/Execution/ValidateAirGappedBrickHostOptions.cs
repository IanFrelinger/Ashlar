using Microsoft.Extensions.Options;
using Ashlar.Abstractions;

namespace Ashlar.Infrastructure.Execution;

/// <summary>
/// Refuses a non-empty remote brick catalog on AirGapped. Full and the other profiles are unchanged.
/// Missing profile options are Full.
/// </summary>
public sealed class ValidateAirGappedBrickHostOptions : IValidateOptions<BrickHostOptions>
{
    private readonly IOptions<AshlarResolvedDeploymentProfileOptions> _profile;

    /// <summary>Creates the validator.</summary>
    public ValidateAirGappedBrickHostOptions(IOptions<AshlarResolvedDeploymentProfileOptions> profile)
    {
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
    }

    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, BrickHostOptions options)
    {
        if (!_profile.Value.IsAirGapped)
            return ValidateOptionsResult.Success;

        var urls = options.RemoteCatalogBaseUrls ?? Array.Empty<string>();
        if (!urls.Any(static url => !string.IsNullOrWhiteSpace(url)))
            return ValidateOptionsResult.Success;

        return ValidateOptionsResult.Fail(
            "BrickHost:RemoteCatalogBaseUrls must be empty under the AirGapped deployment profile. " +
            "Remote brick catalogs are a network path.");
    }
}
