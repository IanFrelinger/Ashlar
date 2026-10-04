namespace Ashlar.Abstractions.Security.Egress;

/// <summary>
/// The ambient subject for egress decisions: whose high-water mark is the current label on this async flow.
/// </summary>
/// <remarks>
/// <para>With a frame active, a decision's current label is the frame's <see cref="HighWaterMark.Current"/>, read
/// when the decision is made, and its basis is <c>subject:&lt;id&gt;</c>. With none, the current label is
/// <see cref="SecurityLabel.SystemHigh"/> and the basis is <c>no-subject</c>: unlabelled data fails closed
/// upward, so only a destination inside the host boundary would be allowed.</para>
/// <para>The frame lives in an <see cref="AsyncLocal{T}"/>, so it flows into awaits and tasks started inside it
/// and never back out to the caller. Disposing the returned scope restores the frame that was active when it was
/// entered, also after an await. Once disposed, a frame counts nowhere: a task that captured it and outlives
/// the scope falls back to the enclosing live frame, or to <c>no-subject</c>. Disposing twice does nothing.</para>
/// <para>Entering a frame can only lower the current label from SystemHigh to what the mark says the subject has
/// read; a producer must therefore observe every read before the egress it governs.</para>
/// </remarks>
public static class EgressSubject
{
    internal const string NoSubjectBasis = "no-subject";
    internal const string SubjectBasisPrefix = "subject:";

    private static readonly AsyncLocal<Frame?> Active = new();

    /// <summary>
    /// Makes <paramref name="mark"/> the current label for egress decisions on this async flow until the returned
    /// scope is disposed.
    /// </summary>
    /// <param name="subjectId">Who the subject is, for the decision record (<c>subject:&lt;id&gt;</c>).</param>
    /// <param name="mark">The subject's high-water mark. Its current value is read at each decision.</param>
    /// <returns>A scope; disposing it restores the previous frame.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="subjectId"/> is empty or white space.</exception>
    public static IDisposable Enter(string subjectId, HighWaterMark mark)
    {
        SecurityGuard.ThrowIfNull(subjectId, nameof(subjectId));
        SecurityGuard.ThrowIfNull(mark, nameof(mark));
        if (string.IsNullOrWhiteSpace(subjectId))
            throw new ArgumentException("A subject id must not be empty or white space.", nameof(subjectId));

        var frame = new Frame(SubjectBasisPrefix + EgressDestinations.Bound(subjectId), mark, Active.Value);
        Active.Value = frame;
        return frame;
    }

    /// <summary>The current label on this async flow and where it came from.</summary>
    internal static (SecurityLabel Current, string Basis) Resolve()
    {
        var frame = Frame.Live(Active.Value);
        return frame is null
            ? (SecurityLabel.SystemHigh, NoSubjectBasis)
            : (frame.Mark.Current, frame.Basis);
    }

    private sealed class Frame : IDisposable
    {
        private readonly Frame? _previous;
        private int _disposed;

        internal Frame(string basis, HighWaterMark mark, Frame? previous)
        {
            Basis = basis;
            Mark = mark;
            _previous = previous;
        }

        internal string Basis { get; }

        internal HighWaterMark Mark { get; }

        private bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        // The nearest frame in the chain that has not been disposed.
        internal static Frame? Live(Frame? frame)
        {
            while (frame is not null && frame.IsDisposed)
                frame = frame._previous;
            return frame;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            // Only the flow this frame is current on changes. Anywhere else the frame simply stops counting.
            if (ReferenceEquals(Active.Value, this))
                Active.Value = Live(_previous);
        }
    }
}
