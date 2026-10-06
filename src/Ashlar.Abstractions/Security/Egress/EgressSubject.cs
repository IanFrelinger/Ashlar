namespace Ashlar.Abstractions.Security.Egress;

/// <summary>
/// The ambient subject for egress decisions: whose high-water mark is the current label on this async flow.
/// </summary>
/// <remarks>
/// <para><b>Current label.</b> With a frame active, a decision's current label is the join of the
/// <see cref="HighWaterMark.Current"/> of every frame on this flow's chain, live or disposed, up to a detachment: the
/// flow's own frame and every frame it was entered inside. It is read when the decision is made, and its basis is
/// <c>subject:&lt;id&gt;</c> of the innermost live frame. With no frame, with no live frame on the chain, or under a
/// detachment, the current label is <see cref="SecurityLabel.SystemHigh"/> and the basis is <c>no-subject</c>:
/// unlabelled data fails closed upward, so only a destination inside the host boundary would be allowed.</para>
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
/// meanwhile, with one exception: a frame that this same flow disposed out of order while this scope was the frame
/// just inside it, which the flow goes on past, to where in-order <c>using</c> blocks would have left it. A task the
/// flow starts after that out-of-order dispose does the same; no other flow does. So frames that one flow disposes
/// out of order build up no chain of disposed frames on it. Disposing twice does nothing.</para>
/// <para><b>Disposed frames still count.</b> A disposed frame is never the innermost one, so it never names a
/// decision: a flow whose own frame was disposed, such as a task that captured it and outlives the scope, decides
/// with the basis of the nearest live frame it was entered inside, or at <c>no-subject</c> if there is none. But a
/// disposed frame's mark still counts, as it is when the decision is made, on every flow still inside it, and what
/// such a flow reads still raises it. A frame disposed out of order counts for the frame still running inside it. A
/// flow goes past a disposed frame only if that flow disposed it, or is a task started there afterwards. Every other
/// flow stays inside it, with no frame of its own and in every frame it enters then or later, and reads its mark when
/// it decides, also what the mark rose to after the frame ended: a fire-and-forget task started inside a parent frame
/// whose <c>using</c> ends first, a task handed a child scope to end, and the flow that entered a frame another flow
/// disposed. Ending an enclosing scope first therefore never lowers the current label of work it started. A frame
/// counts as live until its mark has been observed into the frames it was entered inside, also while it is being
/// disposed, so a decision made meanwhile names that frame, never an enclosing one that does not hold its mark
/// yet.</para>
/// <para><b>Known limit (fail closed).</b> A task or thread keeps the frames it inherited, ended or not, for as long
/// as it runs: a long-running async loop or a dedicated thread started inside a frame counts that frame's mark, as it
/// rises, in every decision it makes, for its whole life. And disposing a frame takes out of it only the flow it is
/// disposed on and the tasks that flow starts afterwards: a frame disposed on another flow, as by
/// <c>await Task.Run(scope.Dispose)</c>, by a background task, or from a thread started without the execution
/// context, leaves the flow that entered it inside it for good, and repeating that grows that flow's chain. Neither
/// happens to a frame entered and disposed in a <c>using</c> block on one flow.</para>
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
    /// <para>Disposing it restores the caller's frame on the flow where it is the innermost frame, as for a frame:
    /// the flow that detached, or a task started under it that was handed the scope, which then decides at the
    /// caller's mark (what it read under the detachment never reached that mark, as on the flow that detached). If a
    /// frame entered under it is still innermost on the disposing flow, that flow goes back to the caller's frame when
    /// that frame ends, as in-order <c>using</c> blocks would, and so does a task it starts after the dispose. Every
    /// other flow it is still active on keeps no subject and never falls back to the caller's frame: a task started
    /// under it that does not dispose it, also when the detachment ends before a frame entered under it, and the flow
    /// that detached when another flow disposed it.</para>
    /// </remarks>
    /// <returns>A scope; disposing it restores the caller's frame on the flow where it is innermost.</returns>
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
        private const int Entered = 0;
        private const int Disposing = 1;
        private const int Disposed = 2;

        private readonly Frame? _previous;
        private readonly Frame? _restore;
        private int _state;

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

        // Disposed only once its mark has been observed outward: a frame being disposed still counts as live, so a
        // decision made meanwhile names it as the innermost live frame, never an enclosing frame that does not hold
        // its mark yet. (The label does not depend on it: Resolve reads a disposed frame's mark too.)
        private bool IsDisposed => Volatile.Read(ref _state) == Disposed;

        // The frame a flow goes back to when this one, innermost there, is disposed: the frame it was entered under,
        // or for a detachment the caller's frame.
        private Frame? Outer => Mark is null ? _restore : _previous;

        internal static Frame ForSubject(string basis, HighWaterMark mark, Frame? previous) =>
            new(basis, mark, previous, restore: null);

        // A detachment has no enclosing frame, so a chain ends at it, live or disposed. It remembers the caller's
        // frame, which a flow goes back to when it disposes the detachment where that is its innermost frame.
        internal static Frame Detachment(Frame? restore) =>
            new(NoSubjectBasis, mark: null, previous: null, restore);

        // The nearest frame in the chain that has not been disposed.
        internal static Frame? Live(Frame? frame)
        {
            while (frame is not null && frame.IsDisposed)
                frame = frame._previous;
            return frame;
        }

        // The join of the mark of every subject frame on the chain, live or disposed, from its head to the end of the
        // chain or a detachment, with the basis of the innermost live frame; no subject if there is no live frame or it
        // is a detachment. A disposed frame is read as its mark is now, not as it was observed into the frames around
        // it when it ended: a flow leaves a frame only by disposing it as its own head, so a flow still inside it reads
        // it, also with no live frame of its own above the nearest live one, so ending an enclosing scope first never
        // declassifies a frame or a task still running inside it, and what the ended frame's mark rises to afterwards
        // still counts there.
        internal static (SecurityLabel Current, string Basis) Resolve(Frame? chain)
        {
            var innermost = Live(chain);
            if (innermost?.Mark is null)
                return (SecurityLabel.SystemHigh, NoSubjectBasis);

            // Every frame from the chain's head to the innermost live one is a subject frame (Live would have stopped
            // at a detachment), so the walk reaches it and goes on below it.
            var current = SecurityLabel.Public;
            for (var frame = chain; frame?.Mark is not null; frame = frame._previous)
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
            if (Interlocked.CompareExchange(ref _state, Disposing, Entered) != Entered)
                return;

            // What this subject read leaves with its output, so every enclosing subject has now read it too. Only then
            // is the frame disposed, so no decision names a frame outside it before its mark has left.
            if (Mark is not null)
                ObserveInto(_previous, Mark.Current);

            Volatile.Write(ref _state, Disposed);

            // A flow leaves a frame only by disposing the frame that is its own head, and goes back to exactly the
            // frame that one was entered under (a detachment: the caller's frame), disposed or not. Anywhere else the
            // frame stays on every chain that holds it and keeps counting.
            if (ReferenceEquals(Active.Value, this))
                Active.Value = Outer;
        }
    }
}
