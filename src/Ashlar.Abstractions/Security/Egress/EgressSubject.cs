namespace Ashlar.Abstractions.Security.Egress;

/// <summary>
/// The ambient subject for egress decisions: whose high-water mark is the current label on this async flow.
/// </summary>
/// <remarks>
/// <para><b>Current label.</b> With a frame active, a decision's current label is the join of the
/// <see cref="HighWaterMark.Current"/> of every frame on this flow's chain: the innermost live frame and every frame
/// it was entered inside, live or disposed, up to a detachment. It is read when the decision is made, and its basis
/// is <c>subject:&lt;id&gt;</c> of the innermost live frame. With none, the current label is
/// <see cref="SecurityLabel.SystemHigh"/> and the basis is <c>no-subject</c>: unlabelled data fails closed upward, so
/// only a destination inside the host boundary would be allowed.</para>
/// <para><b>Monotone nesting.</b> A nested frame never decides below a frame it was entered inside. Disposing a
/// frame observes its mark into every frame it was entered inside, because what a subject read leaves with its
/// output. <see cref="Observe"/> joins a label into every frame on the chain at once, so an enclosing flow that
/// egresses while a nested one is still running already counts it.</para>
/// <para><b>Reads.</b> <see cref="BeginRead"/> scopes one read, such as one tool call. Unless the read completes
/// and reports a label for everything it returned, the scope observes <see cref="SecurityLabel.SystemHigh"/> when it
/// ends (<see cref="ReadScope"/>). <see cref="Observe"/> only raises: it never satisfies a read scope.</para>
/// <para>The frame lives in an <see cref="AsyncLocal{T}"/>, so it flows into awaits and tasks started inside it
/// and never back out to the caller. Disposing the returned scope on the flow where it is the innermost frame
/// restores the frame that was active when it was entered, also after an await, and also when that frame has ended
/// meanwhile, with one exception: if that frame was disposed out of order while this scope was the frame just inside
/// it on the disposing flow, the flow goes on past it, where in-order <c>using</c> blocks would have left it. So
/// frames that one flow disposes out of order build up no chain of disposed frames on it. Disposing twice does
/// nothing.</para>
/// <para><b>Disposed frames still count for the frames inside them.</b> A disposed frame is never the innermost one:
/// a flow whose innermost frame was disposed, such as a task that captured it and outlives the scope, decides at the
/// nearest live frame it was entered inside, into which its mark was observed, or at <c>no-subject</c>. But it still
/// counts for the frames entered inside it, and what they read still raises it. A frame disposed out of order counts
/// for the frame still running inside it. A parent frame whose <c>using</c> ends while a fire-and-forget task started
/// inside it is still running counts for every frame that task enters, then or later: the task stays inside the
/// parent, since only the flow that disposed the parent goes past it. Ending an enclosing scope first therefore never
/// lowers the current label of work it started.</para>
/// <para><b>Known limit (fail closed).</b> A task or thread keeps the frames it inherited, ended or not, for as long
/// as it runs: a long-running async loop or a dedicated thread started inside a frame counts that frame's mark in
/// every frame it enters, for its whole life. And disposing a frame changes only the flow it is disposed on: a frame
/// disposed on another flow, as by <c>await Task.Run(scope.Dispose)</c> or from a thread started without the
/// execution context, can leave the flow that entered it inside it, and repeating that grows that flow's chain.
/// Neither happens to a frame entered and disposed in a <c>using</c> block on one flow.</para>
/// <para>Entering a frame can lower the current label only from SystemHigh, where there is no subject, to the join
/// of the marks on the chain; a producer must therefore observe every read before the egress it governs.</para>
/// </remarks>
public static class EgressSubject
{
    internal const string NoSubjectBasis = "no-subject";
    internal const string SubjectBasisPrefix = "subject:";

    private static readonly AsyncLocal<Frame?> Active = new();

    /// <summary>
    /// Makes <paramref name="mark"/> part of the current label for egress decisions on this async flow until the
    /// returned scope is disposed. The current label is the join of <paramref name="mark"/> and the marks of every
    /// frame this one is entered inside, live or disposed, so a nested frame never decides below an enclosing one.
    /// </summary>
    /// <param name="subjectId">Who the subject is, for the decision record (<c>subject:&lt;id&gt;</c>).</param>
    /// <param name="mark">The subject's high-water mark. Its current value is read at each decision, and observed
    /// into every enclosing frame when the scope is disposed.</param>
    /// <returns>A scope; disposing it observes the mark into the enclosing frames and restores the previous
    /// frame.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="subjectId"/> is empty or white space.</exception>
    public static IDisposable Enter(string subjectId, HighWaterMark mark)
    {
        SecurityGuard.ThrowIfNull(subjectId, nameof(subjectId));
        SecurityGuard.ThrowIfNull(mark, nameof(mark));
        if (string.IsNullOrWhiteSpace(subjectId))
            throw new ArgumentException("A subject id must not be empty or white space.", nameof(subjectId));

        var frame = Frame.ForSubject(SubjectBasisPrefix + EgressDestinations.Bound(subjectId), mark, Active.Value);
        Active.Value = frame;
        return frame;
    }

    /// <summary>
    /// Records that the subject on this async flow has read data labelled <paramref name="label"/>: joins it into the
    /// mark of every frame on the chain, live or disposed, up to a detachment. With no frame it does nothing, since no
    /// subject is already the top.
    /// </summary>
    /// <remarks>It only raises. It never satisfies a <see cref="ReadScope"/>: a read that observes a label on a side
    /// value and returns something else still counts as unreported.</remarks>
    /// <param name="label">The label of the data read.</param>
    /// <exception cref="ArgumentNullException"><paramref name="label"/> is <see langword="null"/>.</exception>
    public static void Observe(SecurityLabel label)
    {
        SecurityGuard.ThrowIfNull(label, nameof(label));
        Frame.ObserveInto(Active.Value, label);
    }

    /// <summary>
    /// Starts a read scope for one read on this async flow, such as one tool call. When it ends it observes what the
    /// read reported, or <see cref="SecurityLabel.SystemHigh"/>, into every frame of the chain it was begun on.
    /// </summary>
    /// <returns>The scope. Call <see cref="ReadScope.Complete"/> when the read returns, and dispose it.</returns>
    public static ReadScope BeginRead() => new(Active.Value);

    /// <summary>
    /// Leaves every frame on this async flow until the returned scope is disposed: decisions made meanwhile, and in
    /// tasks started meanwhile, have no subject, so their current label is <see cref="SecurityLabel.SystemHigh"/>.
    /// </summary>
    /// <remarks>
    /// <para>For work handed to another component that must not be decided at the caller's mark: AgentBus subscriber
    /// dispatch. It only raises, since no subject is the top. A frame entered under it starts a chain of its own,
    /// which never reaches the caller's frames.</para>
    /// <para>Disposing restores the caller's frame on this flow only. A task started under it keeps no subject after
    /// it is disposed: it never falls back to the caller's frame.</para>
    /// </remarks>
    /// <returns>A scope; disposing it restores the previous frame on this flow.</returns>
    internal static IDisposable Detach()
    {
        var detachment = Frame.Detachment(Active.Value);
        Active.Value = detachment;
        return detachment;
    }

    /// <summary>The current label on this async flow and where it came from.</summary>
    internal static (SecurityLabel Current, string Basis) Resolve() => Frame.Resolve(Active.Value);

    /// <summary>A subject frame, or a detachment, which has no mark and ends the chain.</summary>
    internal sealed class Frame : IDisposable
    {
        private readonly Frame? _previous;
        private readonly Frame? _restore;
        private Frame? _unwoundBy;
        private int _disposed;

        private Frame(string basis, HighWaterMark? mark, Frame? previous, Frame? restore)
        {
            Basis = basis;
            Mark = mark;
            _previous = previous;
            _restore = restore;
        }

        internal string Basis { get; }

        /// <summary>The subject's mark, or <see langword="null"/> for a detachment.</summary>
        internal HighWaterMark? Mark { get; }

        private bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        // The frame a flow goes back to when this one, innermost there, is disposed: the frame it was entered under,
        // or for a detachment the caller's frame.
        private Frame? Outer => Mark is null ? _restore : _previous;

        internal static Frame ForSubject(string basis, HighWaterMark mark, Frame? previous) =>
            new(basis, mark, previous, restore: null);

        // A detachment has no enclosing frame, so a chain ends at it, live or disposed. It remembers the frame to
        // restore on the flow it detached.
        internal static Frame Detachment(Frame? restore) =>
            new(NoSubjectBasis, mark: null, previous: null, restore);

        // The nearest frame in the chain that has not been disposed.
        internal static Frame? Live(Frame? frame)
        {
            while (frame is not null && frame.IsDisposed)
                frame = frame._previous;
            return frame;
        }

        // The innermost live subject frame's mark joined with the mark of every subject frame it was entered inside,
        // live or disposed, up to the end of the chain or a detachment; with the innermost frame's basis. A frame
        // disposed above a live one still counts, so ending an enclosing scope first never declassifies a frame (or
        // a task) still running inside it. A disposed frame below the innermost live one has already observed its
        // mark into that one.
        internal static (SecurityLabel Current, string Basis) Resolve(Frame? chain)
        {
            var innermost = Live(chain);
            if (innermost?.Mark is null)
                return (SecurityLabel.SystemHigh, NoSubjectBasis);

            var current = innermost.Mark.Current;
            for (var frame = innermost._previous; frame?.Mark is not null; frame = frame._previous)
                current = current.Join(frame.Mark.Current);

            return (current, innermost.Basis);
        }

        // Joins label into the mark of every subject frame on the chain, live or disposed, up to the end of the chain
        // or a detachment. A disposed frame still counts for the live frames entered inside it (Resolve), so it is
        // raised too: a task that outlives its parent frame, or a frame disposed after it, still reaches every
        // sibling task started inside that parent.
        internal static void ObserveInto(Frame? chain, SecurityLabel label)
        {
            for (var frame = chain; frame?.Mark is not null; frame = frame._previous)
                frame.Mark.Observe(label);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            // What this subject read leaves with its output, so every enclosing subject has now read it too.
            if (Mark is not null)
                ObserveInto(_previous, Mark.Current);

            // Only the flow this frame is innermost on goes back, to where in-order using blocks would have left it.
            var active = Active.Value;
            if (ReferenceEquals(active, this))
            {
                Active.Value = Unwind(this);
                return;
            }

            // Disposed out of order on this flow: when the frame just inside this one on the flow ends, the flow goes
            // on past this one too. Nowhere else does it: the frame simply stops being the innermost one.
            for (var inside = active; inside is not null; inside = inside.Outer)
            {
                if (ReferenceEquals(inside.Outer, this))
                {
                    Volatile.Write(ref _unwoundBy, inside);
                    break;
                }
            }
        }

        // The frame a flow goes back to when `frame`, innermost there, is disposed: its outer frame, even when that one
        // was disposed meanwhile, so a task started inside a parent frame stays inside it after a frame of its own
        // ends and a frame it enters later still counts the parent's mark. The flow goes past an outer frame only when
        // it was disposed out of order on a flow where `frame` was the frame just inside it, as in-order using blocks
        // would have left that flow; so repeated out-of-order disposal on one flow keeps no chain of disposed frames.
        private static Frame? Unwind(Frame frame)
        {
            var outer = frame.Outer;
            while (outer is not null && ReferenceEquals(Volatile.Read(ref outer._unwoundBy), frame))
            {
                frame = outer;
                outer = frame.Outer;
            }

            return outer;
        }
    }
}
