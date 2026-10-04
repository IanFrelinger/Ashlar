namespace Ashlar.Abstractions.Security;

/// <summary>
/// A subject's high-water mark for one session: the join of every label it has read, starting from a floor.
/// The star property (no write down) is evaluated against it.
/// </summary>
/// <remarks>
/// <para>The mark only rises. <see cref="Observe"/> joins a label in and nothing here lowers it; moving data
/// down is the job of a trusted downgrade, not of this type.</para>
/// <para><see cref="Observe"/> may be called from several threads: it joins atomically, so no observation is
/// lost and the mark never falls below what was read. A decision is still a snapshot: <see cref="CanWriteTo"/>
/// judges the mark as it stands when called, so a session must observe every read a write depends on before it
/// asks whether it may write.</para>
/// </remarks>
public sealed class HighWaterMark
{
    private SecurityLabel _current;

    /// <summary>Starts a mark at <see cref="SecurityLabel.Public"/>, the bottom of the lattice.</summary>
    public HighWaterMark()
        : this(SecurityLabel.Public)
    {
    }

    /// <summary>Starts a mark at <paramref name="floor"/>.</summary>
    /// <param name="floor">The label the session starts at.</param>
    public HighWaterMark(SecurityLabel floor)
    {
        SecurityGuard.ThrowIfNull(floor, nameof(floor));
        _current = floor;
    }

    /// <summary>The join of the floor and every label observed so far.</summary>
    public SecurityLabel Current => Volatile.Read(ref _current);

    /// <summary>Records that the subject has read data labelled <paramref name="label"/>.</summary>
    /// <param name="label">The label of the data read.</param>
    public void Observe(SecurityLabel label)
    {
        SecurityGuard.ThrowIfNull(label, nameof(label));

        // Labels are immutable, so a compare-and-swap loop makes the join atomic: a concurrent Observe can delay
        // this one but never overwrite it.
        var seen = Volatile.Read(ref _current);
        while (true)
        {
            var joined = seen.Join(label);
            if (joined.Equals(seen))
                return;

            var witnessed = Interlocked.CompareExchange(ref _current, joined, seen);
            if (ReferenceEquals(witnessed, seen))
                return;

            seen = witnessed;
        }
    }

    /// <summary>
    /// The star property against the current mark: <see cref="ReferenceMonitor.CanWrite"/> of
    /// <see cref="Current"/> and <paramref name="destination"/>.
    /// </summary>
    /// <param name="destination">The label of the destination being written to.</param>
    /// <returns>Allowed when <c>Current &lt;= destination</c>; otherwise refused with the reason.</returns>
    public AccessDecision CanWriteTo(SecurityLabel destination)
    {
        SecurityGuard.ThrowIfNull(destination, nameof(destination));
        return ReferenceMonitor.CanWrite(Current, destination);
    }
}
