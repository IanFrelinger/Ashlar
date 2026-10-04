namespace Ashlar.Abstractions.Security;

/// <summary>
/// The ordered classification level of a <see cref="SecurityLabel"/>:
/// <c>Public &lt; Internal &lt; Confidential &lt; Secret &lt; TopSecret</c>.
/// </summary>
/// <remarks>
/// The names and numeric values (0 to 4) match the primitive levels in
/// <c>Ashlar.BackgroundAgents.DataSensitivity.DataSensitivityLevels</c> (their <c>SensitivityValue</c>),
/// which this model generalises; a cert-gate parity test holds the two together. The zero value is
/// <see cref="Public"/>, the bottom of the order.
/// </remarks>
public enum SecurityLevel
{
    /// <summary>Public data: the lowest level.</summary>
    Public = 0,

    /// <summary>Internal use only.</summary>
    Internal = 1,

    /// <summary>Confidential: restricted access.</summary>
    Confidential = 2,

    /// <summary>Secret: highly restricted.</summary>
    Secret = 3,

    /// <summary>Top secret: the highest level.</summary>
    TopSecret = 4,
}
