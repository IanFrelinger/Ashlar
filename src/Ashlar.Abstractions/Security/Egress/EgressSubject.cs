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
/// <para><b>Leaving a frame.</b> The frame lives in an <see cref="AsyncLocal{T}"/>, so it flows into awaits and tasks
/// started inside it and never back out to the caller. A flow leaves a frame only by disposing the frame that is its
/// own innermost one, and then goes back to exactly the frame that one was entered under, also after an await and also
/// when that frame has ended meanwhile. Disposing a frame anywhere else moves no flow: every flow inside it stays inside
/// it. Disposing twice does nothing. So <c>using</c> blocks on one flow end where they began.</para>
/// <para><b>Disposed frames still count.</b> A disposed frame is never the innermost one, so it never names a
/// decision: a flow whose own frame was disposed, such as a task that captured it and outlives the scope, decides
/// with the basis of the nearest live frame it was entered inside, or at <c>no-subject</c> if there is none. But a
/// disposed frame's mark still counts, as it is when the decision is made, on every flow still inside it, and what
/// such a flow reads still raises it. A frame disposed out of order counts for the frame still running inside it.
/// Every flow that did not leave a frame by disposing it as its own innermost one stays inside it, with no frame of
/// its own and in every frame it enters then or later, and reads its mark when it decides, also what the mark rose to
/// after the frame ended: a fire-and-forget task started inside a parent frame whose <c>using</c> ends first, a task
/// handed a child scope to end, the flow that entered a frame another flow disposed, and a flow that disposed its own
/// frames out of order. Ending an enclosing scope first therefore never lowers the current label of work it started. A
/// frame counts as live until its mark has been observed into the frames it was entered inside, also while it is being
/// disposed, so a decision made meanwhile names that frame, never an enclosing one that does not hold its mark
/// yet.</para>
/// <para><b>Known limits (fail closed).</b> (a) A flow that disposes its frames out of order stays inside the outer
/// frame it disposed: a frame it enters later joins that frame's mark, and repeated in a loop the flow's chain grows by
/// one frame per repetition, which every later decision on the flow walks. That costs availability, not
/// confidentiality. (b) Anything such a flow reads later raises the mark of the frame it stayed inside, which is the
/// subject's shared <see cref="HighWaterMark"/>, so unrelated later work can raise the label of another session of
/// that subject. The same holds for a task or thread, which keeps the frames it inherited, ended or not, for as long as
/// it runs, and for a flow whose frame was disposed on another flow, as by <c>await Task.Run(scope.Dispose)</c>, by a
/// background task, or from a thread started without the execution context. None of this happens to a frame entered
/// and disposed in a <c>using</c> block on the flow that entered it.</para>
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
    /// <returns>A scope; disposing it observes the mark into the enclosing frames and, on the flow where it is the
    /// innermost frame, restores the frame it was entered under.</returns>
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
    /// Runs <paramref name="start"/> with no subject on this async flow: decisions made while it runs, and in tasks it
    /// starts, have no subject, so their current label is <see cref="SecurityLabel.SystemHigh"/>. When it returns or
    /// throws, this flow's frame is exactly the one it was before the call.
    /// </summary>
    /// <remarks>
    /// <para>For work handed to another component that must not be decided at the caller's mark: AgentBus subscriber
    /// dispatch. It only raises, since no subject is the top. A frame entered inside it starts a chain of its own, which
    /// never reaches the caller's frames.</para>
    /// <para>No caller holds the detachment, so none can end it out of order, on another flow, or by handing it to a
    /// task: the calling flow leaves it exactly when <paramref name="start"/> returns. Work <paramref name="start"/>
    /// starts, such as a task, keeps it for its whole life, also after the caller's frame ends. A frame
    /// <paramref name="start"/> enters and leaves undisposed is dropped from the calling flow on return, as in-order
    /// <c>using</c> blocks around the frame and the detachment would leave the caller: a frame entered under a detachment
    /// never reaches the caller. Disposed later, it moves no flow but one where it is the innermost frame, such as a task
    /// started inside it, and its mark reaches only the frames it was entered inside, up to the detachment.</para>
    /// </remarks>
    /// <param name="start">The work, run synchronously on this flow.</param>
    /// <exception cref="ArgumentNullException"><paramref name="start"/> is <see langword="null"/>.</exception>
    internal static void RunDetached(Action start)
    {
        SecurityGuard.ThrowIfNull(start, nameof(start));

        var caller = Active.Value;
        Active.Value = Frame.Detachment();
        try
        {
            start();
        }
        finally
        {
            Active.Value = caller;
        }
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
        private int _state;

        private Frame(string basis, HighWaterMark? mark, Frame? previous)
        {
            Basis = basis;
            Mark = mark;
            _previous = previous;
        }

        internal string Basis { get; }

        /// <summary>The subject's mark, or <see langword="null"/> for a detachment.</summary>
        internal HighWaterMark? Mark { get; }

        // Disposed only once its mark has been observed outward: a frame being disposed still counts as live, so a
        // decision made meanwhile names it as the innermost live frame, never an enclosing frame that does not hold
        // its mark yet. (The label does not depend on it: Resolve reads a disposed frame's mark too.)
        private bool IsDisposed => Volatile.Read(ref _state) == Disposed;

        internal static Frame ForSubject(string basis, HighWaterMark mark, Frame? previous) =>
            new(basis, mark, previous);

        // A detachment has no enclosing frame and no mark, so every chain walk ends at it. Only RunDetached makes one,
        // and no caller ever holds it, so it is never disposed: RunDetached itself puts the caller's frame back.
        internal static Frame Detachment() =>
            new(NoSubjectBasis, mark: null, previous: null);

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
            // frame that one was entered under, disposed or not. Anywhere else the frame stays on every chain that holds
            // it and keeps counting.
            if (ReferenceEquals(Active.Value, this))
                Active.Value = _previous;
        }
    }
}
