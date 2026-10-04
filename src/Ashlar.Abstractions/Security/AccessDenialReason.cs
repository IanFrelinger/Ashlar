namespace Ashlar.Abstractions.Security;

/// <summary>
/// Why the <see cref="ReferenceMonitor"/> refused an access. Every refusal names one.
/// </summary>
/// <remarks>
/// When more than one rule fails, the reason reported is the first in this order:
/// <see cref="SystemHighData"/>, <see cref="LevelTooLow"/>, <see cref="MissingCompartment"/>,
/// <see cref="MissingCaveat"/>.
/// </remarks>
public enum AccessDenialReason
{
    /// <summary>Not refused.</summary>
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
    /// The data is <see cref="SecurityLabel.SystemHigh"/> (unlabelled or unparseable, so treated as the top
    /// element), and only a <see cref="SecurityLabel.SystemHigh"/> receiver may take it.
    /// </summary>
    SystemHighData = 4,
}
