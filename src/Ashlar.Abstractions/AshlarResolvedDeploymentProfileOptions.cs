namespace Ashlar.Abstractions;

/// <summary>
/// The deployment profile <c>AddAshlar</c> resolved, handed to Infrastructure and the MEAI pipeline as options.
/// Those assemblies never read <c>ASHLAR_DEPLOYMENT_PROFILE</c> or the process-wide effective profile.
/// An unconfigured value is <see cref="Full"/>, which is not air-gapped and does not require loopback.
/// </summary>
public sealed class AshlarResolvedDeploymentProfileOptions
{
    /// <summary>Canonical name of the full profile, and the value when hosting has not registered one.</summary>
    public const string Full = "full";

    /// <summary>Canonical name of the air-gapped profile.</summary>
    public const string AirGapped = "air-gapped";

    /// <summary>Canonical name of the secure-workstation profile.</summary>
    public const string SecureWorkstation = "secure-workstation";

    /// <summary>
    /// Canonical profile name (<c>full</c>, <c>air-gapped</c>, <c>secure-workstation</c>, and the other names
    /// <c>AddAshlar</c> records).
    /// </summary>
    public string Profile { get; set; } = Full;

    /// <summary>True when <see cref="Profile"/> is <see cref="AirGapped"/>.</summary>
    public bool IsAirGapped => string.Equals(Profile, AirGapped, StringComparison.Ordinal);

    /// <summary>True when <see cref="Profile"/> is <see cref="SecureWorkstation"/>.</summary>
    public bool IsSecureWorkstation => string.Equals(Profile, SecureWorkstation, StringComparison.Ordinal);

    /// <summary>
    /// True on AirGapped and SecureWorkstation, where every inbound listener must bind loopback until PR 5.
    /// </summary>
    public bool RequiresLoopback => IsAirGapped || IsSecureWorkstation;
}
