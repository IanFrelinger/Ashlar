namespace Ashlar.Abstractions.Security;

/// <summary>
/// The outcome of a <see cref="ReferenceMonitor"/> check: allowed, or refused with a reason a person can read.
/// </summary>
/// <remarks>
/// <para>Refusals are explained, never silent. A refused decision carries a <see cref="Reason"/> and a
/// <see cref="Detail"/> that names the offending level, compartment or caveat.</para>
/// <para><b>The default value fails closed.</b> <c>default(AccessDecision)</c> is not a decision anyone made,
/// so it reports <see cref="Allowed"/> as <see langword="false"/>, with <see cref="Reason"/> set to
/// <see cref="AccessDenialReason.None"/> and a <see cref="Detail"/> that says no decision was made. Decisions are
/// made only inside Ashlar.Abstractions, by the reference monitor. Test <see cref="Allowed"/>, never
/// <c>Reason == None</c>.</para>
/// </remarks>
public readonly struct AccessDecision : IEquatable<AccessDecision>
{
    private const string NoDecisionDetail = "no decision was made (default AccessDecision), so access is refused";

    private readonly string? _detail;

    private AccessDecision(bool allowed, AccessDenialReason reason, string detail)
    {
        Allowed = allowed;
        Reason = reason;
        _detail = detail;
    }

    /// <summary>
    /// <see langword="true"/> only when the reference monitor allowed the access. <see langword="false"/> for
    /// every refusal and for <c>default(AccessDecision)</c>.
    /// </summary>
    public bool Allowed { get; }

    /// <summary>
    /// Why the access was refused, or <see cref="AccessDenialReason.None"/> when it was allowed (or when this is
    /// <c>default(AccessDecision)</c>, which is refused).
    /// </summary>
    public AccessDenialReason Reason { get; }

    /// <summary>
    /// A sentence a person can read that explains a refusal and names the offending level, compartments or
    /// caveats (tokens comma-separated, in canonical order). Empty for an allowed decision.
    /// </summary>
    public string Detail => _detail ?? NoDecisionDetail;

    internal static AccessDecision Allow() => new(allowed: true, AccessDenialReason.None, string.Empty);

    internal static AccessDecision Deny(AccessDenialReason reason, string detail) => new(allowed: false, reason, detail);

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
        _ => "Unknown",
    };
}
