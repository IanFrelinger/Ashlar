namespace Ashlar.Abstractions.Security;

/// <summary>
/// A subject's high-water mark for one session: the join of every label it has read, starting from a floor.
/// The star property (no write down) is evaluated against it.
/// </summary>
/// <remarks>
/// <para>The mark only rises. <see cref="Observe"/> joins a label in and nothing here lowers it; moving data
/// down is the job of a trusted downgrade, not of this type.</para>
/// <para>This type is not thread-safe. A session that reads on several threads must serialise calls to
/// <see cref="Observe"/> and <see cref="CanWriteTo"/>, or keep one mark per thread and join them. An
/// unsynchronised <see cref="Observe"/> can be lost, leaving the mark below what was read and permitting a write
/// down.</para>
/// </remarks>
public sealed class HighWaterMark
{
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
        Current = floor;
    }

    /// <summary>The join of the floor and every label observed so far.</summary>
    public SecurityLabel Current { get; private set; }

    /// <summary>Records that the subject has read data labelled <paramref name="label"/>.</summary>
    /// <param name="label">The label of the data read.</param>
    public void Observe(SecurityLabel label)
    {
        SecurityGuard.ThrowIfNull(label, nameof(label));
        Current = Current.Join(label);
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
