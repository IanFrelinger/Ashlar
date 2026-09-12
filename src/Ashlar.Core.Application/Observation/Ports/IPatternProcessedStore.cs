namespace Ashlar.Core.Application.Observation.Ports;

/// <summary>
/// Tracks which patterns have been processed by the self-improvement loop.
/// Prevents re-triggering improvement on the same pattern indefinitely.
/// </summary>
public interface IPatternProcessedStore
{
    /// <summary>
    /// Claims a pattern for this cycle. Returns false when someone already holds the claim.
    /// </summary>
    /// <param name="patternId">Pattern id. Must not be null or whitespace.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True when this call is the one that claimed it.</returns>
    /// <remarks>
    /// <para><b>Why this replaced <c>MarkProcessedAsync</c>.</b> The loop used to ask
    /// <c>IsProcessedAsync</c>, do the work, and mark afterwards. The work between those two calls is
    /// a source edit, a solution-wide regression run and a promotion, and
    /// <c>BackgroundAgentRegistry</c> says in its own remarks that the scheduler may start a cycle
    /// while the previous one is still running. Two cycles both read "not processed", both edit the
    /// same file, both run the suite against a tree the other is editing, and both promote. A
    /// check-then-act cannot be fixed by making the mark safer; the claim has to happen BEFORE the
    /// work and its failure has to be a return value, which is why this is a port change and not a
    /// store change.</para>
    ///
    /// <para>An implementation must make the claim atomic against another PROCESS, not only another
    /// thread: <c>ashlar improve</c> builds a second loop over the same state directory. The LiteDB
    /// implementation gets that from a unique index, so the losing insert is refused by the database
    /// rather than by a lock this process happens to hold.</para>
    ///
    /// <para>A claim is not released. A pattern whose cycle throws part way stays claimed and is not
    /// retried, where before it was retried forever; that is the deliberate trade, and the alternative
    /// — releasing on failure — hands the same pattern back to the cycle that is already failing on it.</para>
    /// </remarks>
    Task<bool> TryClaimAsync(string patternId, CancellationToken cancellationToken = default);

    /// <summary>Returns true if the pattern has been processed.</summary>
    /// <param name="patternId">Pattern id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True when a claim exists.</returns>
    /// <remarks>
    /// For reporting and for tests. Do NOT use it as the guard in front of work — that is exactly
    /// the check-then-act <see cref="TryClaimAsync"/> exists to replace.
    /// </remarks>
    Task<bool> IsProcessedAsync(string patternId, CancellationToken cancellationToken = default);
}
