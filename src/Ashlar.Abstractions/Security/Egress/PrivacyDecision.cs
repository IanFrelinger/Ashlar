using System.Diagnostics.CodeAnalysis;

namespace Ashlar.Abstractions.Security.Egress;

/// <summary>
/// The outcome of <see cref="PrivacyRule.Decide"/>: allowed, or refused with a reason a person can read.
/// </summary>
/// <remarks>
/// <para><see cref="Reason"/> is <see cref="EgressRefusalReason.None"/> exactly when <see cref="Allowed"/> is
/// <see langword="true"/>. A refusal for <see cref="EgressRefusalReason.LabelForbidsDestination"/> names the label in
/// <see cref="ForbiddenBy"/>; every other decision leaves it <see langword="null"/>.</para>
/// <para><b>The default value fails closed.</b> <c>default(PrivacyDecision)</c> is not a decision anyone made, so it
/// reports <see cref="Allowed"/> as <see langword="false"/> and <see cref="Reason"/> as
/// <see cref="EgressRefusalReason.NoDecision"/>.</para>
/// <para>Decisions are constructed only inside Ashlar.Abstractions (and the assemblies it grants internals to).</para>
/// </remarks>
[Experimental(SecurityExperimental.DiagnosticId, UrlFormat = SecurityExperimental.UrlFormat)]
public readonly struct PrivacyDecision : IEquatable<PrivacyDecision>
{
    private readonly EgressRefusalReason _reason;
    private readonly PrivacyLabel? _forbiddenBy;
    private readonly bool _decided;

    private PrivacyDecision(EgressRefusalReason reason, PrivacyLabel? forbiddenBy)
    {
        _reason = reason;
        _forbiddenBy = forbiddenBy;
        _decided = true;
    }

    /// <summary>
    /// <see langword="true"/> only when <see cref="PrivacyRule"/> allowed the send. <see langword="false"/> for every
    /// refusal and for <c>default(PrivacyDecision)</c>.
    /// </summary>
    public bool Allowed => Reason == EgressRefusalReason.None;

    /// <summary>
    /// Why the send was refused: the first refusing step of the rule; <see cref="EgressRefusalReason.NoDecision"/> for
    /// <c>default(PrivacyDecision)</c>; <see cref="EgressRefusalReason.None"/> only when allowed.
    /// </summary>
    public EgressRefusalReason Reason => _decided ? _reason : EgressRefusalReason.NoDecision;

    /// <summary>
    /// The label that forbids the destination, for a <see cref="EgressRefusalReason.LabelForbidsDestination"/> refusal;
    /// otherwise <see langword="null"/>.
    /// </summary>
    public PrivacyLabel? ForbiddenBy => _forbiddenBy;

    internal static PrivacyDecision Allow() => new(EgressRefusalReason.None, forbiddenBy: null);

    internal static PrivacyDecision Refuse(EgressRefusalReason reason)
    {
        if (reason is not (EgressRefusalReason.LocalOnlyData or EgressRefusalReason.UnknownDestination))
        {
            throw new ArgumentOutOfRangeException(
                nameof(reason), "Refuse takes LocalOnlyData or UnknownDestination; a forbidding label goes through Forbid.");
        }

        return new(reason, forbiddenBy: null);
    }

    internal static PrivacyDecision Forbid(PrivacyLabel label)
    {
        if (label is not (PrivacyLabel.NoExternalModel or PrivacyLabel.NoWeb or PrivacyLabel.NoNetworkExport))
            throw new ArgumentOutOfRangeException(nameof(label), "Only no-external-model, no-web and no-network-export forbid a class.");

        return new(EgressRefusalReason.LabelForbidsDestination, label);
    }

    /// <inheritdoc />
    public bool Equals(PrivacyDecision other) => Reason == other.Reason && ForbiddenBy == other.ForbiddenBy;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is PrivacyDecision other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        unchecked
        {
            return ((int)Reason * 397) ^ (ForbiddenBy is { } label ? (int)label + 1 : 0);
        }
    }

    /// <summary>
    /// <c>allowed</c>, or <c>refused (Reason): </c> and a sentence that names the label or the destination at fault.
    /// The wording is for people and may change; <see cref="Reason"/> and <see cref="ForbiddenBy"/> are the stable,
    /// machine-readable parts.
    /// </summary>
    public override string ToString() => Reason switch
    {
        EgressRefusalReason.None => "allowed",
        EgressRefusalReason.LocalOnlyData =>
            "refused (LocalOnlyData): the read set holds local-only, which nothing outside the host boundary may receive",
        EgressRefusalReason.LabelForbidsDestination =>
            "refused (LabelForbidsDestination): the read set holds " + LabelText(ForbiddenBy)
            + ", which forbids " + ForbiddenClass(ForbiddenBy) + " destinations",
        EgressRefusalReason.UnknownDestination =>
            "refused (UnknownDestination): the destination is in no known class, and such a destination receives nothing",
        EgressRefusalReason.NoDecision =>
            "refused (NoDecision): no decision was made (default PrivacyDecision), so the send is refused",
        _ => "refused",
    };

    /// <summary>Structural equality.</summary>
    public static bool operator ==(PrivacyDecision left, PrivacyDecision right) => left.Equals(right);

    /// <summary>Structural inequality.</summary>
    public static bool operator !=(PrivacyDecision left, PrivacyDecision right) => !left.Equals(right);

    private static string LabelText(PrivacyLabel? label) =>
        label is { } value ? PrivacyLabelSet.TextOf(value) : "no label";

    private static string ForbiddenClass(PrivacyLabel? label) => label switch
    {
        PrivacyLabel.NoExternalModel => nameof(EgressDestinationClass.ExternalModel),
        PrivacyLabel.NoWeb => nameof(EgressDestinationClass.WebSearch),
        PrivacyLabel.NoNetworkExport => nameof(EgressDestinationClass.NetworkExport),
        _ => "no",
    };
}
