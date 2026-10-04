namespace Ashlar.Abstractions.Security.Egress;

/// <summary>
/// The report-only egress guard: classifies the destination, reads the subject's current label, applies the star
/// property, and publishes the record.
/// </summary>
/// <remarks>
/// <para><b>Destination.</b> The first matching rule wins. A <c>unix</c> or <c>npipe</c> URI, a host that is
/// <c>localhost</c>, <c>*.localhost</c> or a loopback IP, or a name starting with <c>host:</c> is
/// <see cref="EgressDestinationClass.Host"/> (SystemHigh). Otherwise the family decides: <c>model.*</c> is
/// <see cref="EgressDestinationClass.ExternalModel"/> (Internal), <c>web-search</c> is
/// <see cref="EgressDestinationClass.WebSearch"/> (Confidential), the network and export families are
/// <see cref="EgressDestinationClass.NetworkExport"/> (Internal), and anything else is
/// <see cref="EgressDestinationClass.Unknown"/> (Public, failing closed to the bottom).</para>
/// <para><b>Current label.</b> The active <see cref="EgressSubject"/> frame's high-water mark, or
/// <see cref="SecurityLabel.SystemHigh"/> (<c>no-subject</c>) when none is active.</para>
/// <para><b>Decision.</b> <see cref="ReferenceMonitor.CanWrite"/>(current, destination label). With no subject,
/// destinations inside the host boundary are allowed and every other one is refused with
/// <see cref="AccessDenialReason.SystemHighData"/>.</para>
/// <para><b>Report-only.</b> <see cref="Evaluate"/> never throws, never refuses and never blocks: it only records.
/// It does not consult or change any other policy. If evaluating throws, the record carries
/// <see cref="EgressDecision.Fault"/> (the exception's type name only) and <c>default(AccessDecision)</c>, and is
/// still published.</para>
/// <para>Every instance decides by the same rules and publishes to the same <see cref="EgressDecisionLog"/>; an
/// instance differs only in the deployment profile it reports.</para>
/// <para>This provides classification-style controls inside the runtime. It is not an accredited cross-domain
/// solution.</para>
/// </remarks>
public sealed class EgressGuard : IEgressGuard
{
    internal const string ReportMode = "report";
    internal const string DeploymentProfileVariable = "ASHLAR_DEPLOYMENT_PROFILE";
    internal const string FaultedDestinationBasis = "not classified: the evaluation faulted";
    internal const string FaultedCurrentBasis = "not resolved: the evaluation faulted";

    private static long _sequence;

    private readonly string? _deploymentProfile;

    /// <summary>
    /// Creates a guard.
    /// </summary>
    /// <param name="deploymentProfile">The deployment profile to report, for example <c>air-gapped</c>. When
    /// <see langword="null"/>, <c>ASHLAR_DEPLOYMENT_PROFILE</c> is read at every decision, preferring the profile
    /// <c>AddAshlar</c> resolved in this process, exactly as the remote-protocol option validators read it.</param>
    public EgressGuard(string? deploymentProfile = null)
    {
        _deploymentProfile = deploymentProfile;
    }

    /// <summary>
    /// The guard used wherever none is given: it reads the deployment profile from the environment at every
    /// decision.
    /// </summary>
    public static EgressGuard ProcessDefault { get; } = new();

    /// <inheritdoc />
    public EgressDecision Evaluate(EgressRequest request)
    {
        var sequence = Interlocked.Increment(ref _sequence);
        var at = DateTimeOffset.UtcNow;

        // Fail-closed placeholders. A stage that faults leaves its fields as they are here: the destination at the
        // bottom (Public), the current label at the top (SystemHigh), and no decision.
        var profile = string.Empty;
        var enforces = false;
        var family = string.Empty;
        var site = string.Empty;
        var destination = EgressDestinations.UnknownDestination;
        var destinationClass = EgressDestinationClass.Unknown;
        var destinationLabel = SecurityLabel.Public;
        var destinationBasis = FaultedDestinationBasis;
        var current = SecurityLabel.SystemHigh;
        var currentBasis = FaultedCurrentBasis;
        var access = default(AccessDecision);
        string? fault = null;

        try
        {
            (profile, enforces) = ResolveProfile();

            SecurityGuard.ThrowIfNull(request, nameof(request));
            family = EgressDestinations.Bound(request.Family);
            site = EgressDestinations.Bound(request.Site);

            (destination, destinationClass, destinationLabel, destinationBasis) =
                EgressDestinations.Classify(request.Family, request.Destination, request.DestinationName);

            (current, currentBasis) = EgressSubject.Resolve();

            access = ReferenceMonitor.CanWrite(current, destinationLabel);
        }
#pragma warning disable CA1031 // Evaluate never throws by contract: any failure becomes the record's Fault and the caller proceeds.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            // The type name only: an exception message can carry the destination's path, query or credentials.
            var type = ex.GetType();
            fault = type.FullName ?? type.Name;
            access = default;
        }

        var decision = new EgressDecision(
            sequence,
            at,
            ReportMode,
            family,
            site,
            destination,
            destinationClass,
            destinationLabel,
            destinationBasis,
            current,
            currentBasis,
            access,
            profile,
            enforces,
            fault);

        EgressDecisionLog.Publish(decision);
        return decision;
    }

    // A configured profile is reported as given. Otherwise the environment is read now, through the same effective
    // value (the profile AddAshlar resolved, else the variable) that the remote-protocol option validators use.
    private (string Profile, bool EnforcesByDefault) ResolveProfile()
    {
        if (_deploymentProfile is not null)
        {
            var enforces = AshlarDeploymentProfileEnvironment.IsAirGapped(_deploymentProfile)
                || AshlarDeploymentProfileEnvironment.IsSecureWorkstation(_deploymentProfile);
            return (EgressDestinations.Bound(_deploymentProfile), enforces);
        }

        var raw = Environment.GetEnvironmentVariable(DeploymentProfileVariable);
        return (
            EgressDestinations.Bound(AshlarDeploymentProfileEnvironment.Effective(raw)),
            AshlarDeploymentProfileEnvironment.ForbidsRemoteProtocolEgress(raw));
    }
}
