namespace Ashlar.Commercial.Fleet.Contracts.Models;

/// <summary>
/// What a transactional mesh task update did.
/// </summary>
/// <remarks>
/// Three outcomes rather than a <c>bool</c> because the caller has to tell them apart: a task that
/// is gone is a 404, a transform that declined is a 409, and only <see cref="Applied"/> means the
/// state the caller is about to report actually reached disk. Every call site before this type
/// existed discarded <c>UpdateAsync</c>'s <c>bool</c> and reported its own in-memory value as
/// persisted.
/// </remarks>
public enum MeshTaskUpdateOutcome
{
    /// <summary>The transform ran and its result was written.</summary>
    Applied = 0,

    /// <summary>No task with that id existed when the transaction opened.</summary>
    NotFound = 1,

    /// <summary>The transform declined: the document it saw did not satisfy the caller's precondition.</summary>
    PreconditionFailed = 2,
}

/// <summary>
/// The outcome of an <see cref="Ports.IMeshTaskRegistry.UpdateAsync"/> call and the state the store
/// ended up holding.
/// </summary>
/// <param name="Outcome">Which of the three things happened.</param>
/// <param name="State">
/// For <see cref="MeshTaskUpdateOutcome.Applied"/>, the state that was written — read this rather
/// than the value the caller composed, because the transform ran against the document the
/// transaction found, not against the caller's snapshot. For
/// <see cref="MeshTaskUpdateOutcome.PreconditionFailed"/>, the state that caused the refusal, so the
/// caller can say what it lost the race to. Null only for <see cref="MeshTaskUpdateOutcome.NotFound"/>.
/// </param>
public readonly record struct MeshTaskUpdateResult(MeshTaskUpdateOutcome Outcome, MeshTaskState? State)
{
    /// <summary>True when the write landed.</summary>
    public bool Applied => Outcome == MeshTaskUpdateOutcome.Applied;
}
