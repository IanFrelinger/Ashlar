using System.Diagnostics.CodeAnalysis;

namespace Ashlar.Abstractions.Security.Egress;

/// <summary>
/// The privacy rule for one send (SPEC-010 §3.3): may a subject whose read set is R send to a destination of class D?
/// </summary>
/// <remarks>
/// <para>The steps run in this order, and the first that decides is the decision:</para>
/// <list type="number">
/// <item><description>D is <see cref="EgressDestinationClass.Host"/>: allowed.</description></item>
/// <item><description>R holds <c>local-only</c>: refused with <see cref="EgressRefusalReason.LocalOnlyData"/>.</description></item>
/// <item><description>D is <see cref="EgressDestinationClass.Unknown"/>, or a class value this rule does not know:
/// refused with <see cref="EgressRefusalReason.UnknownDestination"/>.</description></item>
/// <item><description>R holds the label that forbids D (<c>no-external-model</c> for
/// <see cref="EgressDestinationClass.ExternalModel"/>, <c>no-web</c> for <see cref="EgressDestinationClass.WebSearch"/>,
/// <c>no-network-export</c> for <see cref="EgressDestinationClass.NetworkExport"/>): refused with
/// <see cref="EgressRefusalReason.LabelForbidsDestination"/>, naming the label in
/// <see cref="PrivacyDecision.ForbiddenBy"/>.</description></item>
/// <item><description>Otherwise: allowed.</description></item>
/// </list>
/// <para>It is a pure function of its arguments and decides only when it is called. Routing every send through it is
/// the job of the egress guard. It is a privacy check at the egress boundary: it decides where labelled data may be
/// sent, and it says nothing about what an agent's text output carries.</para>
/// </remarks>
[Experimental(SecurityExperimental.DiagnosticId, UrlFormat = SecurityExperimental.UrlFormat)]
public static class PrivacyRule
{
    /// <summary>Decides a send from a subject whose read set is <paramref name="readSet"/> to a destination of class
    /// <paramref name="destination"/>.</summary>
    /// <param name="readSet">What the subject has read (<see cref="ReadSet.Current"/>). Unlabelled data should be passed
    /// as <see cref="PrivacyLabelSet.LocalOnly"/>.</param>
    /// <param name="destination">The class of the destination.</param>
    /// <returns>Allowed, or refused with the reason of the first step that refuses.</returns>
    public static PrivacyDecision Decide(PrivacyLabelSet readSet, EgressDestinationClass destination)
    {
        SecurityGuard.ThrowIfNull(readSet, nameof(readSet));

        // 1. Inside the host boundary.
        if (destination == EgressDestinationClass.Host)
            return PrivacyDecision.Allow();

        // 2. local-only data reaches nothing outside the host boundary.
        if (readSet.Contains(PrivacyLabel.LocalOnly))
            return PrivacyDecision.Refuse(EgressRefusalReason.LocalOnlyData);

        // 3. A destination in no known class receives nothing; a class value this rule does not know counts as Unknown.
        if (!TryGetForbiddingLabel(destination, out var forbidding))
            return PrivacyDecision.Refuse(EgressRefusalReason.UnknownDestination);

        // 4. The label that forbids the destination's class.
        if (readSet.Contains(forbidding))
            return PrivacyDecision.Forbid(forbidding);

        // 5.
        return PrivacyDecision.Allow();
    }

    // The label that forbids a class, for the three classes a label can forbid. False for Unknown and for any value
    // that is not a defined class.
    private static bool TryGetForbiddingLabel(EgressDestinationClass destination, out PrivacyLabel label)
    {
        switch (destination)
        {
            case EgressDestinationClass.ExternalModel:
                label = PrivacyLabel.NoExternalModel;
                return true;
            case EgressDestinationClass.WebSearch:
                label = PrivacyLabel.NoWeb;
                return true;
            case EgressDestinationClass.NetworkExport:
                label = PrivacyLabel.NoNetworkExport;
                return true;
            default:
                label = PrivacyLabel.LocalOnly;
                return false;
        }
    }
}
