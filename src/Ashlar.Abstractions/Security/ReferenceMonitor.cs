namespace Ashlar.Abstractions.Security;

/// <summary>
/// The reference monitor's two Bell-LaPadula decisions over <see cref="SecurityLabel"/>s: simple security
/// (no read up) and the star property (no write down).
/// </summary>
/// <remarks>
/// <para>Both are pure functions of their arguments. A refusal always carries an
/// <see cref="AccessDenialReason"/> and a <see cref="AccessDecision.Detail"/> naming what was missing; when more
/// than one rule fails, the reason reported is the first of <see cref="AccessDenialReason.SystemHighData"/>,
/// <see cref="AccessDenialReason.LevelTooLow"/>, <see cref="AccessDenialReason.MissingCompartment"/> and
/// <see cref="AccessDenialReason.MissingCaveat"/>.</para>
/// <para>This provides classification-style controls inside the runtime. It is not an accredited cross-domain
/// solution.</para>
/// </remarks>
public static class ReferenceMonitor
{
    /// <summary>
    /// Simple security (no read up): a subject holding <paramref name="clearance"/> may read
    /// <paramref name="data"/> only when the clearance dominates the data's label.
    /// </summary>
    /// <param name="clearance">The subject's clearance.</param>
    /// <param name="data">The label on the data to be read. Unlabelled data should be passed as
    /// <see cref="SecurityLabel.SystemHigh"/>.</param>
    /// <returns>Allowed when <c>data &lt;= clearance</c>; otherwise refused with the reason.</returns>
    public static AccessDecision CanRead(SecurityLabel clearance, SecurityLabel data)
    {
        SecurityGuard.ThrowIfNull(clearance, nameof(clearance));
        SecurityGuard.ThrowIfNull(data, nameof(data));
        return Decide(source: data, receiver: clearance, isRead: true);
    }

    /// <summary>
    /// The star property (no write down): a subject whose high-water mark is <paramref name="current"/> may write
    /// to <paramref name="destination"/> only when the destination's label dominates the high-water mark.
    /// </summary>
    /// <param name="current">The subject's high-water mark: the join of everything it has read this session
    /// (see <see cref="HighWaterMark"/>).</param>
    /// <param name="destination">The label of the destination being written to.</param>
    /// <returns>Allowed when <c>current &lt;= destination</c>; otherwise refused with the reason.</returns>
    public static AccessDecision CanWrite(SecurityLabel current, SecurityLabel destination)
    {
        SecurityGuard.ThrowIfNull(current, nameof(current));
        SecurityGuard.ThrowIfNull(destination, nameof(destination));
        return Decide(source: current, receiver: destination, isRead: false);
    }

    // Allowed iff `source` <= `receiver`: data <= clearance for a read, current <= destination for a write.
    private static AccessDecision Decide(SecurityLabel source, SecurityLabel receiver, bool isRead)
    {
        if (receiver.IsSystemHigh)
            return AccessDecision.Allow();

        var receiverName = isRead ? "clearance" : "destination";
        if (source.IsSystemHigh)
        {
            return AccessDecision.Deny(
                AccessDenialReason.SystemHighData,
                isRead
                    ? "the data is SystemHigh (unlabelled or unparseable), which only a SystemHigh clearance may read"
                    : "the subject has read SystemHigh data, which only a SystemHigh destination may receive");
        }

        if (source.Level > receiver.Level)
        {
            return AccessDecision.Deny(
                AccessDenialReason.LevelTooLow,
                receiverName + " level " + SecurityLabel.LevelName(receiver.Level) + " is below "
                + (isRead ? "data" : "current") + " level " + SecurityLabel.LevelName(source.Level));
        }

        var missingCompartments = SecurityLabel.Missing(source.CompartmentTokens, receiver.CompartmentTokens);
        if (missingCompartments.Length > 0)
        {
            return AccessDecision.Deny(
                AccessDenialReason.MissingCompartment,
                receiverName + " lacks compartment(s): " + SecurityLabel.JoinTokens(missingCompartments));
        }

        var missingCaveats = SecurityLabel.Missing(source.CaveatTokens, receiver.CaveatTokens);
        if (missingCaveats.Length > 0)
        {
            return AccessDecision.Deny(
                AccessDenialReason.MissingCaveat,
                receiverName + " lacks caveat(s): " + SecurityLabel.JoinTokens(missingCaveats));
        }

        return AccessDecision.Allow();
    }
}
