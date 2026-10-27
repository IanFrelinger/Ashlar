using System.Diagnostics.CodeAnalysis;

namespace Ashlar.Abstractions.Security.Egress;

/// <summary>
/// Why <see cref="PrivacyRule"/> refused a send (SPEC-010 §3.7). Every refusal names one; <see cref="None"/> means
/// allowed.
/// </summary>
/// <remarks>
/// <para>The rule checks its steps in the order of SPEC-010 §3.3 and reports the first that refuses:
/// <see cref="LocalOnlyData"/>, then <see cref="UnknownDestination"/>, then <see cref="LabelForbidsDestination"/>.
/// That is not the order of the values.</para>
/// <para>The numeric values are stable and new reasons are only appended, so a stored value keeps its meaning.</para>
/// </remarks>
[Experimental(SecurityExperimental.DiagnosticId, UrlFormat = SecurityExperimental.UrlFormat)]
public enum EgressRefusalReason
{
    /// <summary>
    /// Not refused. Only an allowed decision carries it: <c>Reason == None</c> holds exactly when
    /// <see cref="PrivacyDecision.Allowed"/> is <see langword="true"/>.
    /// </summary>
    None = 0,

    /// <summary>
    /// The read set holds <c>local-only</c>, and the destination is outside the host boundary. Unlabelled and
    /// unparseable data count as <c>local-only</c>.
    /// </summary>
    LocalOnlyData = 1,

    /// <summary>
    /// The read set holds the label that forbids the destination's class: <c>no-external-model</c> for
    /// <see cref="EgressDestinationClass.ExternalModel"/>, <c>no-web</c> for <see cref="EgressDestinationClass.WebSearch"/>
    /// or <c>no-network-export</c> for <see cref="EgressDestinationClass.NetworkExport"/>.
    /// <see cref="PrivacyDecision.ForbiddenBy"/> names it.
    /// </summary>
    LabelForbidsDestination = 2,

    /// <summary>
    /// The destination is in no known class (<see cref="EgressDestinationClass.Unknown"/>, or a value this rule does
    /// not know), and such a destination receives nothing.
    /// </summary>
    UnknownDestination = 3,

    /// <summary>
    /// No decision was made: what <c>default(PrivacyDecision)</c> reports, for example from an uninitialised field or
    /// an evaluation that faulted. It is a refusal, so an unset decision never reads as a grant.
    /// </summary>
    NoDecision = 4,
}
