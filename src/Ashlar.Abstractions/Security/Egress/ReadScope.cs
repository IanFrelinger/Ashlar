namespace Ashlar.Abstractions.Security.Egress;

/// <summary>
/// One read by the subject on an async flow, such as one tool call. When the scope ends, what the read returned is
/// observed into every live frame of the chain it was begun on. Start one with <see cref="EgressSubject.BeginRead"/>.
/// </summary>
/// <remarks>
/// <para>How the scope ends decides what it observes:</para>
/// <list type="bullet">
/// <item><description><b>Completed and reported:</b> the join of the labels passed to <see cref="Report"/>, and
/// nothing else. Reporting <see cref="SecurityLabel.Public"/>, the bottom, says the read returned nothing labelled
/// above it ("read nothing") and raises no mark.</description></item>
/// <item><description><b>Completed without a report:</b> <see cref="SecurityLabel.SystemHigh"/>. An unreported read
/// is unlabelled data, which fails closed to the top (SPEC-007 §7).</description></item>
/// <item><description><b>Disposed without <see cref="Complete"/></b>, which is how a scope ends when the read throws:
/// <see cref="SecurityLabel.SystemHigh"/>, whatever was reported. A read can fetch data and then throw, and the
/// exception's message can carry it.</description></item>
/// </list>
/// <para>Only <see cref="Report"/> satisfies a scope. <see cref="EgressSubject.Observe"/> raises the frames but never
/// reports a read, so code that can reach it cannot launder a result by observing a low label on a side value. The
/// scope is not ambient: only code holding it can report, and its holder decides whose reports it accepts, such as
/// those of a reader that labels everything its result carries.</para>
/// <para>A scope observes only when it is disposed, so dispose it on every path, with a <c>using</c> block that
/// encloses the read. A report made after the scope ended is observed into the frames directly, so it is never lost.
/// Disposing twice does nothing. <see cref="Report"/>, <see cref="Complete"/> and <see cref="Dispose"/> may be called from
/// any thread.</para>
/// </remarks>
public sealed class ReadScope : IDisposable
{
    private readonly EgressSubject.Frame? _chain;
    private SecurityLabel? _reported;
    private int _completed;
    private int _ended;

    internal ReadScope(EgressSubject.Frame? chain)
    {
        _chain = chain;
    }

    /// <summary>
    /// Reports that the read returned data labelled <paramref name="label"/>. Several reports join. Call it with a
    /// label for everything the read returned; <see cref="SecurityLabel.Public"/> reports that it read nothing.
    /// </summary>
    /// <param name="label">The label of the data read.</param>
    /// <exception cref="ArgumentNullException"><paramref name="label"/> is <see langword="null"/>.</exception>
    public void Report(SecurityLabel label)
    {
        SecurityGuard.ThrowIfNull(label, nameof(label));

        // Labels are immutable, so a compare-and-swap loop joins concurrent reports without losing one.
        var seen = Volatile.Read(ref _reported);
        while (true)
        {
            var joined = seen is null ? label : seen.Join(label);
            var witnessed = Interlocked.CompareExchange(ref _reported, joined, seen);
            if (ReferenceEquals(witnessed, seen))
                break;

            seen = witnessed;
        }

        // A scope that has already ended observed what it had; this report goes to the frames itself.
        if (Volatile.Read(ref _ended) != 0)
            EgressSubject.Frame.ObserveInto(_chain, label);
    }

    /// <summary>
    /// Marks the read as returned normally. Without it, disposing the scope observes
    /// <see cref="SecurityLabel.SystemHigh"/>, as for a read that threw.
    /// </summary>
    public void Complete() => Interlocked.Exchange(ref _completed, 1);

    /// <summary>
    /// Ends the scope: observes the reported labels if the read completed and reported, and otherwise
    /// <see cref="SecurityLabel.SystemHigh"/>, into every live frame of the chain the scope was begun on.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _ended, 1) != 0)
            return;

        var reported = Volatile.Read(ref _reported);
        var read = Volatile.Read(ref _completed) != 0 && reported is not null ? reported : SecurityLabel.SystemHigh;
        EgressSubject.Frame.ObserveInto(_chain, read);
    }
}
