namespace Ashlar.Abstractions.Security.Egress;

/// <summary>
/// The record of one egress decision: what was asked, how the destination was classified, what the subject had
/// read, and whether the star property (no write down) would allow it.
/// </summary>
/// <remarks>
/// <para><b>What a record never contains:</b> the payload or request body, headers, or a URL's userinfo, path,
/// query or fragment. <see cref="Destination"/> is <c>scheme://host[:port]</c> or a name. One exception: a name
/// that starts with <c>file:</c> (in any case) is a file path, not a URL, so it is recorded as written: it holds
/// whatever its text holds, even when it is URL-shaped (<c>file://127.0.0.1/E$/out.nxpkg</c>), a userinfo, query or
/// fragment included. Ashlar's own file sites pass <c>file:</c> plus a local path. Caller-supplied text is bounded in
/// length and its control and format characters are replaced.</para>
/// <para><see cref="Access"/>'s <see cref="AccessDecision.Detail"/> is for operators and audit logs; do not hand
/// it to the refused subject.</para>
/// <para>Records are built only by <see cref="EgressGuard"/>; they are immutable.</para>
/// </remarks>
public sealed class EgressDecision
{
    internal EgressDecision(
        long sequence,
        DateTimeOffset at,
        string mode,
        string family,
        string site,
        string destination,
        EgressDestinationClass destinationClass,
        SecurityLabel destinationLabel,
        string destinationBasis,
        SecurityLabel current,
        string currentBasis,
        AccessDecision access,
        string profile,
        bool profileEnforcesByDefault,
        string? fault,
        string modeBasis,
        string reference)
    {
        Sequence = sequence;
        At = at;
        Mode = mode;
        Family = family;
        Site = site;
        Destination = destination;
        DestinationClass = destinationClass;
        DestinationLabel = destinationLabel;
        DestinationBasis = destinationBasis;
        Current = current;
        CurrentBasis = currentBasis;
        Access = access;
        Profile = profile;
        ProfileEnforcesByDefault = profileEnforcesByDefault;
        Fault = fault;
        ModeBasis = modeBasis;
        Ref = reference;
        Refused = string.Equals(mode, EgressEnforcement.EnforceMode, StringComparison.Ordinal) && !access.Allowed;
    }

    /// <summary>A process-wide sequence number, unique and increasing across every guard in the process.</summary>
    public long Sequence { get; }

    /// <summary>When the decision was made (UTC).</summary>
    public DateTimeOffset At { get; }

    /// <summary>
    /// The mode the decision was made in: <c>report</c> or <c>enforce</c>. <see cref="ModeBasis"/> says what decided
    /// it. Until SPEC-007 PR 4.11 every profile defaults to <c>report</c>. HTTP and governed model routes enforce
    /// refusals; explicit sites are converted separately in PR 4.9. A decision alone does not prove a send stopped.
    /// </summary>
    public string Mode { get; }

    /// <summary>The request's family (see <see cref="EgressFamilies"/>).</summary>
    public string Family { get; }

    /// <summary>The request's site id.</summary>
    public string Site { get; }

    /// <summary>
    /// The destination: <c>scheme://host[:port]</c> for a URI, otherwise the request's name (a URL-shaped name
    /// whose authority cannot be read without guessing is <c>scheme://&lt;unparsed&gt;</c>). Never a userinfo,
    /// path, query or fragment, except for a name that starts with <c>file:</c> (in any case): that name is a file
    /// path, recorded as written and bounded, so it holds whatever its text holds, a userinfo included.
    /// </summary>
    public string Destination { get; }

    /// <summary>The destination's class.</summary>
    public EgressDestinationClass DestinationClass { get; }

    /// <summary>The destination's label, which follows from its class.</summary>
    public SecurityLabel DestinationLabel { get; }

    /// <summary>Why the destination has that label, in words.</summary>
    public string DestinationBasis { get; }

    /// <summary>
    /// The subject's current label: what it has read. <see cref="SecurityLabel.SystemHigh"/> when no
    /// <see cref="EgressSubject"/> is active, because unlabelled data fails closed upward.
    /// </summary>
    public SecurityLabel Current { get; }

    /// <summary>
    /// Where <see cref="Current"/> came from: <c>subject:&lt;id&gt;</c> or <c>no-subject</c>; or, when the evaluation
    /// faulted before the current label was read, <c>not resolved: the evaluation faulted</c> (and <see cref="Current"/>
    /// is <see cref="SecurityLabel.SystemHigh"/>).
    /// </summary>
    public string CurrentBasis { get; }

    /// <summary>
    /// <see cref="ReferenceMonitor.CanWrite"/> of <see cref="Current"/> and <see cref="DestinationLabel"/>: would
    /// allow or would refuse, with the reason. <c>default(AccessDecision)</c> (refused, NoDecision) when
    /// <see cref="Fault"/> is set.
    /// </summary>
    public AccessDecision Access { get; }

    /// <summary>
    /// The deployment profile the decision was made under, and the one <see cref="Mode"/> was resolved from: the
    /// guard's configured profile, or else the effective <c>ASHLAR_DEPLOYMENT_PROFILE</c> text (the strictest profile
    /// <c>AddAshlar</c> noted, else the variable; empty when none is set), read once per decision.
    /// </summary>
    public string Profile { get; }

    /// <summary>
    /// <see langword="true"/> when the profile is one that will enforce egress decisions by default once SPEC-007
    /// PR 4.11 switches it on (AirGapped, SecureWorkstation): the same predicate that today keeps remote MCP and A2A
    /// off those profiles. It does not set the mode; <see cref="Mode"/> does.
    /// </summary>
    public bool ProfileEnforcesByDefault { get; }

    /// <summary>
    /// <see langword="null"/>, or the full type name of the exception that stopped the evaluation. Only the type
    /// name is recorded, never the message. When set, <see cref="Access"/> is <c>default(AccessDecision)</c>.
    /// </summary>
    public string? Fault { get; }

    /// <summary>
    /// What decided <see cref="Mode"/>: <c>profile:&lt;profile&gt;</c> (the profile's default, with the canonical
    /// profile name), <c>profile:unrecognised</c>, <c>override</c> (the process's mode override,
    /// <c>AshlarHostingOptions.EgressMode</c> or the guard's constructor), <c>override-ignored</c>, or <c>fault</c>
    /// (resolving the mode faulted, so it is <c>enforce</c>). Later PRs add <c>break-glass</c>, <c>host-opt-out</c> and
    /// <c>operator-verb</c>.
    /// </summary>
    public string ModeBasis { get; }

    /// <summary>
    /// What a route must do with the egress: <see langword="true"/> when <see cref="Mode"/> is <c>enforce</c> and
    /// <see cref="Access"/> does not allow it, a fault included. Routes honor it before sending.
    /// </summary>
    public bool Refused { get; }

    /// <summary>Whether a route must refuse this decision; an alias of <see cref="Refused"/>.</summary>
    public bool Refuses => Refused;

    /// <summary>Throws a redacted policy refusal when enforcing, otherwise returns this same decision.</summary>
    /// <exception cref="EgressRefusedException">The decision requires the route to stop.</exception>
    public EgressDecision ThrowIfRefused()
    {
        if (Refused)
            throw new EgressRefusedException(this);
        return this;
    }

    /// <summary>
    /// A random 64-bit reference for this decision, as 16 lowercase hex digits (<c>unavailable</c> if none could be
    /// drawn). It joins what a refused party is shown to the operator's record. Unlike <see cref="Sequence"/>, it says
    /// nothing about how many other decisions the process made.
    /// </summary>
    public string Ref { get; }
}
