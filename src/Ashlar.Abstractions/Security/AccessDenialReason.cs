namespace Ashlar.Abstractions.Security;

/// <summary>
/// Why an access was refused. Every refusal names one; <see cref="None"/> means allowed.
/// </summary>
/// <remarks>
/// <para>When more than one rule fails, the reference monitor reports the first in this order:
/// <see cref="SystemHighData"/>, <see cref="LevelTooLow"/>, <see cref="MissingCompartment"/>,
/// <see cref="MissingCaveat"/>.</para>
/// <para>The numeric values are stable and new reasons are only appended, so a stored value keeps its
/// meaning.</para>
/// </remarks>
public enum AccessDenialReason
{
    /// <summary>
    /// Not refused. Only an allowed decision carries it: <c>Reason == None</c> holds exactly when
    /// <see cref="AccessDecision.Allowed"/> is <see langword="true"/>.
    /// </summary>
    None = 0,

    /// <summary>
    /// The receiving side's level (the clearance for a read, the destination for a write) is below the
    /// level of the data.
    /// </summary>
    LevelTooLow = 1,

    /// <summary>
    /// The receiving side lacks a compartment the data carries. <see cref="AccessDecision.Detail"/> names it.
    /// </summary>
    MissingCompartment = 2,

    /// <summary>
    /// The receiving side lacks a caveat the data carries. <see cref="AccessDecision.Detail"/> names it.
    /// </summary>
    MissingCaveat = 3,

    /// <summary>
    /// The data (for a write, the subject's current label) is <see cref="SecurityLabel.SystemHigh"/>, for example
    /// because it was unlabelled or unparseable, and only a <see cref="SecurityLabel.SystemHigh"/> receiver may
    /// take it.
    /// </summary>
    SystemHighData = 4,

    /// <summary>
    /// No decision was made: what <c>default(AccessDecision)</c> reports, for example from an uninitialised
    /// field. It is a refusal, so an unset decision never reads as a grant.
    /// </summary>
    NoDecision = 5,
}
