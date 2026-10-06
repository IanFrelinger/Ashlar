namespace Ashlar.Infrastructure.Deployment;

/// <summary>
/// The deployment profile a composition resolved, as Hosting registers it from <c>AddAshlar</c> (SPEC-007 PR 4.10,
/// default D35). It is how the profile reaches Infrastructure and the application hosts: they never read the process
/// profile, or <c>ASHLAR_DEPLOYMENT_PROFILE</c>, themselves.
/// </summary>
/// <remarks>
/// <para><b>The strictest profile noted in the process</b> (default D5). <c>AddAshlar</c> registers the profile the
/// process has noted, which its own call has just noted into, so after <c>AddAshlar(AirGapped)</c> a later
/// <c>AddAshlar</c> that resolves a weaker profile still registers AirGapped, as the egress guard it composes does.</para>
/// <para><b>Immutable, and never bound from configuration.</b> It is a plain singleton, not an options type, so no
/// <c>Configure</c> call, appsettings file or command-line argument can lower it. A composition that never runs
/// <c>AddAshlar</c> has none, and every consumer then behaves as it did before this type existed; the egress guard
/// stays the backstop there.</para>
/// </remarks>
public sealed class ResolvedDeploymentProfile
{
    /// <summary>Takes the profile name.</summary>
    /// <param name="profile">The profile, canonical (<c>air-gapped</c>, <c>secure-workstation</c>, <c>full</c>, ...)
    /// or any spelling <c>ASHLAR_DEPLOYMENT_PROFILE</c> accepts.</param>
    public ResolvedDeploymentProfile(string profile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profile);
        Profile = profile.Trim();
        var folded = Profile.ToUpperInvariant()
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal);
        IsAirGapped = folded is "AIRGAPPED";
        IsSecureWorkstation = folded is "SECUREWORKSTATION" or "WORKSTATION";
    }

    /// <summary>The profile as given.</summary>
    public string Profile { get; }

    /// <summary><see langword="true"/> on the AirGapped profile.</summary>
    public bool IsAirGapped { get; }

    /// <summary><see langword="true"/> on the SecureWorkstation profile.</summary>
    public bool IsSecureWorkstation { get; }

    /// <summary>
    /// <see langword="true"/> on AirGapped and SecureWorkstation, where every Ashlar inbound listener must bind
    /// loopback (owner decision Q6).
    /// </summary>
    public bool RequiresLoopbackInbound => IsAirGapped || IsSecureWorkstation;

    /// <summary>The name an operator message uses: <c>AirGapped</c>, <c>SecureWorkstation</c>, or the profile as given.</summary>
    public string DisplayName => IsAirGapped ? "AirGapped" : IsSecureWorkstation ? "SecureWorkstation" : Profile;

    /// <inheritdoc />
    public override string ToString() => DisplayName;
}
