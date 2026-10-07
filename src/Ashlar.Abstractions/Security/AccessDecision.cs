namespace Ashlar.Abstractions.Security;

/// <summary>
/// The outcome of a <see cref="ReferenceMonitor"/> check: allowed, or refused with a reason a person can read.
/// </summary>
/// <remarks>
/// <para>Refusals are explained, never silent. A refused decision carries a <see cref="Reason"/> and a
/// <see cref="Detail"/> that names the offending level, compartment or caveat. <see cref="Reason"/> is
/// <see cref="AccessDenialReason.None"/> exactly when <see cref="Allowed"/> is <see langword="true"/>.</para>
/// <para><b>The default value fails closed.</b> <c>default(AccessDecision)</c> is not a decision anyone made,
/// so it reports <see cref="Allowed"/> as <see langword="false"/>, <see cref="Reason"/> as
/// <see cref="AccessDenialReason.NoDecision"/> and a <see cref="Detail"/> that says no decision was made.</para>
/// <para>Decisions are constructed only inside Ashlar.Abstractions (and the assemblies it grants internals to),
/// and every refusal must name a reason and a detail.</para>
/// </remarks>
public readonly struct AccessDecision : IEquatable<AccessDecision>
{
    private const string NoDecisionDetail = "no decision was made (default AccessDecision), so access is refused";

    private readonly AccessDenialReason _reason;
    private readonly string? _detail;

    private AccessDecision(bool allowed, AccessDenialReason reason, string detail)
    {
        Allowed = allowed;
        _reason = reason;
        _detail = detail;
    }

    /// <summary>
    /// <see langword="true"/> only when the reference monitor allowed the access. <see langword="false"/> for
    /// every refusal and for <c>default(AccessDecision)</c>.
    /// </summary>
    public bool Allowed { get; }

    /// <summary>
    /// Why the access was refused: the first failing rule in the order <see cref="AccessDenialReason.SystemHighData"/>,
    /// <see cref="AccessDenialReason.LevelTooLow"/>, <see cref="AccessDenialReason.MissingCompartment"/>,
    /// <see cref="AccessDenialReason.MissingCaveat"/>; <see cref="AccessDenialReason.NoDecision"/> for
    /// <c>default(AccessDecision)</c>; <see cref="AccessDenialReason.None"/> only when allowed.
    /// </summary>
    public AccessDenialReason Reason => _detail is null ? AccessDenialReason.NoDecision : _reason;

    /// <summary>
    /// A sentence a person can read that explains a refusal and names the offending level, compartments or
    /// caveats (tokens comma-separated, in canonical order). Empty for an allowed decision.
    /// </summary>
    /// <remarks>
    /// <para>The wording is for people and may change; do not parse it. <see cref="Reason"/> is the stable,
    /// machine-readable part.</para>
    /// <para>A read refusal names the data's level and the compartments and caveats the subject is not cleared for,
    /// which can be sensitive in themselves. The detail (and <see cref="ToString"/>) is for audit logs and operators;
    /// do not hand it to the refused subject.</para>
    /// </remarks>
    public string Detail => _detail ?? NoDecisionDetail;

    internal static AccessDecision Allow() => new(allowed: true, AccessDenialReason.None, string.Empty);

    internal static AccessDecision Deny(AccessDenialReason reason, string detail)
    {
        if (reason is AccessDenialReason.None or AccessDenialReason.NoDecision)
            throw new ArgumentOutOfRangeException(nameof(reason), "A refusal needs a reason other than None or NoDecision.");
        if (string.IsNullOrEmpty(detail))
            throw new ArgumentException("A refusal needs a detail a person can read.", nameof(detail));

        return new(allowed: false, reason, detail);
    }

    /// <inheritdoc />
    public bool Equals(AccessDecision other) =>
        Allowed == other.Allowed
        && Reason == other.Reason
        && string.Equals(Detail, other.Detail, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is AccessDecision other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        unchecked
        {
            var hash = Allowed ? 1 : 0;
            hash = (hash * 397) ^ (int)Reason;
            return (hash * 397) ^ StringComparer.Ordinal.GetHashCode(Detail);
        }
    }

    /// <summary>
    /// <c>allowed</c>, or <c>refused (Reason): Detail</c>.
    /// </summary>
    public override string ToString() =>
        Allowed ? "allowed" : "refused (" + ReasonName(Reason) + "): " + Detail;

    /// <summary>Structural equality.</summary>
    public static bool operator ==(AccessDecision left, AccessDecision right) => left.Equals(right);

    /// <summary>Structural inequality.</summary>
    public static bool operator !=(AccessDecision left, AccessDecision right) => !left.Equals(right);

    private static string ReasonName(AccessDenialReason reason) => reason switch
    {
        AccessDenialReason.None => "None",
        AccessDenialReason.LevelTooLow => "LevelTooLow",
        AccessDenialReason.MissingCompartment => "MissingCompartment",
        AccessDenialReason.MissingCaveat => "MissingCaveat",
        AccessDenialReason.SystemHighData => "SystemHighData",
        AccessDenialReason.NoDecision => "NoDecision",
        _ => "Unknown",
    };
}
