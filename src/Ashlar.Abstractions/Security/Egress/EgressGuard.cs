namespace Ashlar.Abstractions.Security.Egress;

/// <summary>
/// The egress guard: resolves the mode, classifies the destination, reads the subject's current label, applies the
/// star property, and publishes the record.
/// </summary>
/// <remarks>
/// <para><b>Destination.</b> The first matching rule wins. A <c>unix</c> or <c>npipe</c> URI, a host that is
/// <c>localhost</c>, <c>*.localhost</c> or a loopback IP, or a name starting with <c>host:</c> is
/// <see cref="EgressDestinationClass.Host"/> (SystemHigh). Otherwise the family decides: <c>model.*</c> is
/// <see cref="EgressDestinationClass.ExternalModel"/> (Internal), <c>web-search</c> is
/// <see cref="EgressDestinationClass.WebSearch"/> (Confidential), the network and export families are
/// <see cref="EgressDestinationClass.NetworkExport"/> (Internal), and anything else is
/// <see cref="EgressDestinationClass.Unknown"/> (Public, failing closed to the bottom).</para>
/// <para><b>Current label.</b> With an <see cref="EgressSubject"/> frame active on the flow, the join of the
/// high-water mark of every frame on its chain, live or disposed, up to a detachment (the flow's own frame and every
/// frame it was entered inside), with the basis <c>subject:&lt;id&gt;</c> of the innermost live frame. With none, with
/// no live one, or under a detachment, <see cref="SecurityLabel.SystemHigh"/>, with the basis <c>no-subject</c>.</para>
/// <para><b>Decision.</b> <see cref="ReferenceMonitor.CanWrite"/>(current, destination label). With no subject,
/// destinations inside the host boundary are allowed and every other one is refused with
/// <see cref="AccessDenialReason.SystemHighData"/>.</para>
/// <para><b>Mode.</b> Resolved first, in its own step, by one resolver (SPEC-007 PR 4.6). A guard built with a
/// profile takes its profile and its override from its constructor only and never reads the environment. A guard
/// built without one (<see cref="ProcessDefault"/>) reads the profile once per decision, preferring the one
/// <c>AddAshlar</c> noted, and records that same value; its override is the process's (read once from the
/// environment, and raised to <c>enforce</c> if <c>AddAshlar</c> was asked to), or its constructor's when that is at
/// least as strict. Until SPEC-007 PR 4.11 every profile defaults to <c>report</c>. If resolving the mode throws, the
/// mode is <c>enforce</c> with the basis <c>fault</c>: it fails closed.</para>
/// <para><b>Decision and enforcement.</b> <see cref="Evaluate"/> never throws and never blocks: it records the
/// decision. HTTP and governed model routes act on <see cref="EgressDecision.Refused"/>. It does not consult or
/// change any other policy. If classifying throws, the record carries <see cref="EgressDecision.Fault"/> (the
/// exception's type name only) and <c>default(AccessDecision)</c>, and is still published.</para>
/// <para>Every instance decides by the same rules and publishes to the same <see cref="EgressDecisionLog"/>; an
/// instance differs only in the deployment profile and the mode override it starts from.</para>
/// <para>This provides classification-style controls inside the runtime. It is not an accredited cross-domain
/// solution.</para>
/// </remarks>
public sealed class EgressGuard : IEgressGuard
{
    internal const string DeploymentProfileVariable = "ASHLAR_DEPLOYMENT_PROFILE";
    internal const string FaultedDestinationBasis = "not classified: the evaluation faulted";
    internal const string FaultedCurrentBasis = "not resolved: the evaluation faulted";

    private static long _sequence;
    private static long _resolutionFaults;
    private static long _resolutionMissing;

    internal static long ResolutionFaults => Interlocked.Read(ref _resolutionFaults);
    internal static long ResolutionMissing => Interlocked.Read(ref _resolutionMissing);

    // A failed DI lookup must retain process enforcement. Diagnostics cannot stop client construction.
    internal static IEgressGuard ResolveForRoute(Func<IEgressGuard?> resolve, Action<bool, string?> diagnose)
    {
        string? fault = null;
        try
        {
            var guard = resolve();
            if (guard is not null)
                return guard;
        }
#pragma warning disable CA1031 // Host DI faults fall back to process policy and are counted and diagnosed.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            fault = ex.GetType().FullName ?? ex.GetType().Name;
        }

        if (fault is null)
            Interlocked.Increment(ref _resolutionMissing);
        else
            Interlocked.Increment(ref _resolutionFaults);
        try
        {
            diagnose(fault is not null, fault);
        }
#pragma warning disable CA1031 // Diagnostics must not change the fallback guard.
        catch (Exception)
#pragma warning restore CA1031
        {
            EgressDecisionLog.RecordSinkFault();
        }
        return ProcessDefault;
    }

    private readonly string? _deploymentProfile;
    private readonly string? _egressMode;

    /// <summary>
    /// Creates a guard.
    /// </summary>
    /// <param name="deploymentProfile">The deployment profile to decide under, for example <c>air-gapped</c>. When
    /// <see langword="null"/>, <c>ASHLAR_DEPLOYMENT_PROFILE</c> is read at every decision, preferring the profile
    /// <c>AddAshlar</c> resolved in this process, exactly as the remote-protocol option validators read it. A profile
    /// that is not one of the six fails closed to <c>enforce</c>.</param>
    /// <param name="egressMode">The mode override: <c>report</c> or <c>enforce</c> (trimmed, any case); any other
    /// non-blank value fails closed to <c>enforce</c>. A guard with a <paramref name="deploymentProfile"/> uses this
    /// override alone (none when <see langword="null"/>) and never reads the environment. A guard without one uses the
    /// process override (read once per process), and this override only when it is at least as strict, so it can
    /// raise that guard's mode but never lower it. HTTP and governed model routes honor enforcement; the full
    /// profile switch remains SPEC-007 PR 4.11.</param>
    public EgressGuard(string? deploymentProfile = null, string? egressMode = null)
    {
        _deploymentProfile = deploymentProfile;
        _egressMode = egressMode;
    }

    /// <summary>
    /// The guard used wherever none is given: it reads the deployment profile from the environment at every
    /// decision, and the mode override once per process.
    /// </summary>
    public static EgressGuard ProcessDefault { get; } = new();

    /// <inheritdoc />
    public EgressDecision Evaluate(EgressRequest request) => EvaluateCore(request, externalFault: null);

    // A host implementation can throw or violate the non-null return contract. The process mode determines
    // whether that failed evaluation stops the route; never substitute an ordinary allow decision for a fault.
    internal static EgressDecision EvaluateForRoute(IEgressGuard guard, EgressRequest request)
    {
        string fault;
        try
        {
            var decision = guard.Evaluate(request);
            if (decision is not null)
                return decision;
            fault = typeof(InvalidOperationException).FullName!;
        }
#pragma warning disable CA1031 // Host guard faults become recorded NoDecision results; enforcement happens outside this catch.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            fault = ex.GetType().FullName ?? ex.GetType().Name;
        }

        EgressGuardHandler.RecordGuardFault();
        return ProcessDefault.EvaluateCore(request, fault);
    }

    private EgressDecision EvaluateCore(EgressRequest? request, string? externalFault)
    {
        var sequence = Interlocked.Increment(ref _sequence);
        var at = DateTimeOffset.UtcNow;

        // The mode first, in a step of its own, so a fault while resolving it fails closed to enforce rather than
        // leaving a placeholder that would fail open (SPEC-007 §7). The profile is read once, here, and the record
        // below describes that same value, so a concurrent AddAshlar cannot give a ModeBasis and a Profile that
        // disagree.
        string? deploymentProfile = _deploymentProfile;
        var mode = EgressEnforcement.EnforceMode;
        var modeBasis = EgressEnforcement.FaultBasis;
        try
        {
            deploymentProfile = ReadProfile();
            (mode, modeBasis) = ResolveMode(deploymentProfile);
        }
#pragma warning disable CA1031 // Evaluate never throws by contract: a mode that cannot be resolved is enforce, basis fault.
        catch (Exception)
#pragma warning restore CA1031
        {
            mode = EgressEnforcement.EnforceMode;
            modeBasis = EgressEnforcement.FaultBasis;
        }

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
        string? fault = externalFault;

        try
        {
            (profile, enforces) = DescribeProfile(deploymentProfile);

#if NET6_0_OR_GREATER
            ArgumentNullException.ThrowIfNull(request);
#else
            if (request is null)
                throw new ArgumentNullException(nameof(request));
#endif
            family = EgressDestinations.Bound(request.Family);
            site = EgressDestinations.Bound(request.Site);

            (destination, destinationClass, destinationLabel, destinationBasis) =
                EgressDestinations.Classify(request.Family, request.Destination, request.DestinationName);

            (current, currentBasis) = EgressSubject.Resolve();

            access = externalFault is null ? ReferenceMonitor.CanWrite(current, destinationLabel) : default;
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

        // Q4 is a narrow file-export exception. Keep the real label/access decision in the record,
        // but report it for these explicitly selected CLI verbs. Faults never acquire an exception.
        if (fault is null && modeBasis != EgressEnforcement.FaultBasis
            && request?.Initiator == EgressInitiator.OperatorFileExport
            && request.Family == EgressFamilies.FileExport
            && request.Site is "EG-MESH-02" or "EG-MESH-08" or "EG-FILE-01" or "EG-FILE-02")
        {
            mode = EgressEnforcement.ReportMode;
            modeBasis = EgressEnforcement.OperatorVerbBasis;
        }

        string reference;
        try
        {
            reference = EgressEnforcement.NewReference();
        }
#pragma warning disable CA1031 // Evaluate never throws by contract: a reference that cannot be drawn is recorded as unavailable.
        catch (Exception)
#pragma warning restore CA1031
        {
            reference = EgressEnforcement.UnavailableReference;
        }

        var decision = new EgressDecision(
            sequence,
            at,
            mode,
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
            fault,
            modeBasis,
            reference);

        EgressDecisionLog.Publish(decision);
        return decision;
    }

    /// <summary>
    /// <see langword="true"/> when the profile this guard decides under, read as <see cref="Evaluate"/> reads it, is
    /// AirGapped or SecureWorkstation, or cannot be read (fail closed). For warnings only; it decides nothing.
    /// </summary>
    internal bool ProfileEnforcesByDefault()
    {
        try
        {
            return DescribeProfile(ReadProfile()).EnforcesByDefault;
        }
#pragma warning disable CA1031 // A profile that cannot be read is treated as the strictest, so the warning it gates is given.
        catch (Exception)
#pragma warning restore CA1031
        {
            return true;
        }
    }

    // A guard built with a profile uses it as given. Otherwise the profile is the effective value the remote-protocol
    // option validators read: the strictest one AddAshlar noted in this process, else the variable.
    private string? ReadProfile() =>
        _deploymentProfile ?? AshlarDeploymentProfileEnvironment.Effective(Environment.GetEnvironmentVariable(DeploymentProfileVariable));

    // A guard built with a profile decides from its constructor alone and never reads the environment (SPEC-007
    // PR 4.6, default D4). Otherwise the override is the process latch's, or the constructor's when that is at least
    // as strict: a guard that reads the process state cannot lower a process that enforces. A mode other than plain
    // report is announced on stderr once.
    private (string Mode, string ModeBasis) ResolveMode(string? profile)
    {
        EgressEnforcement.ModeResolutionProbe?.Invoke();

        if (_deploymentProfile is not null)
            return EgressEnforcement.ResolveMode(_deploymentProfile, _egressMode);

        var modeOverride = EgressEnforcement.ProcessOverride();
        var resolved = EgressEnforcement.ResolveMode(profile, modeOverride);
        if (_egressMode is not null)
        {
            var own = EgressEnforcement.ResolveMode(profile, _egressMode);
            var processIsStricter = IsEnforce(resolved.Mode) && !IsEnforce(own.Mode);
            if (!processIsStricter)
            {
                resolved = own;
                modeOverride = _egressMode;
            }
        }

        EgressEnforcement.AnnounceOnce(
            resolved.Mode,
            resolved.ModeBasis,
            EgressDestinations.Bound(profile),
            profileDefaulted: string.IsNullOrWhiteSpace(profile),
            overrideUnrecognised: EgressEnforcement.ParseOverride(modeOverride) == EgressEnforcement.OverrideKind.Unrecognised);
        return resolved;
    }

    // The record's profile and whether it would enforce once the switch lands, from the value ResolveMode used.
    private static (string Profile, bool EnforcesByDefault) DescribeProfile(string? profile) =>
        (EgressDestinations.Bound(profile),
            AshlarDeploymentProfileEnvironment.IsAirGapped(profile) || AshlarDeploymentProfileEnvironment.IsSecureWorkstation(profile));

    private static bool IsEnforce(string mode) =>
        string.Equals(mode, EgressEnforcement.EnforceMode, StringComparison.Ordinal);
}
