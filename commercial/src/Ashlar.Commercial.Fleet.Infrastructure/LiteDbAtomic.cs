using System.Collections;
using LiteDB;

namespace Ashlar.Commercial.Fleet.Infrastructure;

/// <summary>
/// The fleet assembly's half of the atomic read-modify-write helper. Same mechanism and the same
/// reasons as <c>Ashlar.Infrastructure.Persistence.LiteDbAtomic</c> — read that one for what the race
/// is and why a transaction rather than a compare-and-swap loop.
/// </summary>
/// <remarks>
/// <para>Why a second copy rather than a reference: the same trade <c>LiteDbDocumentMapper</c> makes
/// in this assembly. This is a lean fleet-node library whose only package dependencies are LiteDB and
/// the Microsoft.Extensions abstractions, while Ashlar.Infrastructure carries Roslyn, Docker.DotNet,
/// LlamaSharp, QuestPDF, Npgsql and SQLite; taking that reference to reach twenty lines would be a
/// worse trade than restating them. Unlike the mapper gate the two copies here share no state and
/// need none — a transaction is scoped to one <c>ILiteDatabase</c> on one thread.</para>
///
/// <para>The race this closes is worse here than anywhere else in the repository, because both fleet
/// registries are singletons over the SAME FILE and each carries its own <c>SemaphoreSlim</c>, which
/// cannot see the other's. A heartbeat that read a node document before an operator's drain committed
/// writes <c>Drained = false</c> back from its stale snapshot, and
/// <c>MeshTaskPlacementService</c> selects on exactly <c>Admitted &amp;&amp; !Drained</c> — so a lost
/// drain places new work on a node being taken out of service.</para>
///
/// <para>The refusals are the same three and for the same measured reasons: a nested <c>Mutate</c> on
/// one database throws because LiteDB 5.0.21's inner <c>BeginTrans</c> has already ended the outer
/// transaction and both scopes' writes are then discarded silently; a nested <c>Mutate</c> on a
/// SECOND database over the same file is refused by a thread-static depth count, because
/// <c>BeginTrans</c> returns true there and one of the two writes is discarded with both
/// <c>Commit</c>s reporting success; and an async body cannot compile because it would commit at its
/// first <c>await</c>. The second of those is the one that matters most on this side - both fleet
/// registries are constructed with the SAME PATH and <c>MeshTaskPlacementService</c> holds both, so a
/// transform that re-checks a node inside a task transaction is the natural edit and was measured to
/// lose a write. A body that returns an un-walked sequence is refused too, because the open cursor
/// makes <c>Commit</c> throw and the file's named mutex then stays held past <c>Dispose</c>. Read
/// the <c>Ashlar.Infrastructure</c> copy's remarks for the measurements.</para>
///
/// <para>Note what this does NOT reach on this side, and what has since been closed elsewhere. The
/// caller-side shape - a read through one store method, a decision, and a write through another, with
/// the database opened and closed in between - is not something any transaction can span, and
/// <c>CommercialFleetEndpoints.RegisterFleetNodeAsync</c> was the worst case of it: it read the node
/// through <c>IFleetNodeRegistry.GetAsync</c> and wrote the whole document back, so a <c>revoke</c>
/// landing in that window was reverted, 20 of 20 rounds. That is now closed at the PORT rather than
/// here - <c>RegisterOrUpdateAsync</c> no longer exists and the merge runs inside
/// <c>LiteDbFleetNodeRegistry.RegisterOrMergeAsync</c>'s own transaction. This helper still reaches
/// only what is inside one of its bodies, and only when BOTH halves are there; see
/// <c>LiteDbAtomicWriteConventionTests</c>' remarks for what remains open.</para>
/// </remarks>
public static class LiteDbAtomic
{
    /// <summary>
    /// How many <c>Mutate</c> transactions this THREAD has open, across every <c>ILiteDatabase</c>.
    /// </summary>
    /// <remarks>
    /// A LiteDB transaction is bound to the thread that began it, so thread-static is the right
    /// scope and needs no synchronisation of its own. It counts rather than flags only so that the
    /// decrement in <c>finally</c> cannot get out of step.
    /// </remarks>
    [ThreadStatic]
    private static int _openTransactions;

    /// <summary>Why a nested <c>Mutate</c> is refused instead of joined.</summary>
    private const string NestedMessage =
        "LiteDbAtomic.Mutate is already inside a transaction on this ILiteDatabase. LiteDB 5.0.21 does "
        + "not nest: the inner BeginTrans has already ended the outer transaction, the outer Commit "
        + "will return false, and every write made in BOTH scopes is discarded without an exception. "
        + "Put the read and the write in ONE Mutate body rather than composing two guarded methods on "
        + "one open database.";

    /// <summary>
    /// Why a transaction already open on THIS THREAD is refused, whichever database it belongs to.
    /// </summary>
    /// <remarks>
    /// <para>The check above only sees nesting on the SAME <c>ILiteDatabase</c>, and that is not the
    /// shape this deployment exposes. Two stores are constructed with the same file path and one
    /// service holds both, so a transform that consults its sibling store synchronously opens a
    /// SECOND <c>SharedEngine</c> over one file on one thread. Measured in the Linux devtest
    /// container, LiteDB 5.0.21, <c>Connection=Shared</c>, one file, one thread: the outer
    /// <c>BeginTrans</c> returned true, the inner <c>BeginTrans</c> on the second instance returned
    /// TRUE, both <c>Commit</c>s returned TRUE, and a fresh reader afterwards saw the outer write
    /// and the seed but NOT the inner one. Nothing threw, and checking <c>Commit</c>'s return value
    /// would not have caught it either, because <c>Commit</c> returned true for the write that was
    /// discarded. Nor does it deadlock on the sibling's semaphore: that semaphore belongs to the
    /// other store, and the .NET named mutex LiteDB queues on is thread-reentrant, so the same
    /// thread walks straight through it. Only a nested open from a DIFFERENT thread blocks as
    /// designed.</para>
    /// </remarks>
    private const string NestedOnThreadMessage =
        "LiteDbAtomic.Mutate is already inside a transaction on this THREAD, on a different "
        + "ILiteDatabase. If the two instances are over the same file they are two SharedEngines "
        + "whose named mutex is thread-reentrant: both BeginTrans and both Commit return true and "
        + "one of the two writes is silently discarded. If they are different files the pair was "
        + "never atomic to begin with. Either way, do not call one guarded store method from inside "
        + "another: compute what you need BEFORE the Mutate and close over the result.";

    /// <summary>Why a body that returns an un-materialised sequence is refused.</summary>
    /// <remarks>
    /// The third member of the family the two refusals above belong to, and it fails worse than
    /// either: an <c>IEnumerable</c> that has not been walked leaves a LiteDB cursor open, and
    /// <c>Commit</c> then throws "Current transaction contains open cursors". Measured in the Linux
    /// devtest container: <c>Rollback</c> in the catch below returns NORMALLY after that, the
    /// <c>using</c> disposes the database, and the <c>SharedEngine</c>'s named mutex for the file is
    /// never released - a fresh open from another thread never returned. A hung database file, not a
    /// lost update. None of the read shapes the shipped bodies actually use triggers it - all seven
    /// were measured committing cleanly and leaving the file openable - but returning
    /// <c>col.Find(...)</c>, or a <c>.Select(...)</c> off one, is the natural next edit, so it is
    /// rejected at the door like the other two. Materialise inside the body.
    /// </remarks>
    private const string LazySequenceMessage =
        "A LiteDbAtomic.Mutate body may not return an un-materialised sequence: the cursor is still "
        + "open when Commit runs, Commit throws 'Current transaction contains open cursors', and the "
        + "SharedEngine's named mutex for this file is then never released -- every later open of it, "
        + "including from another process, blocks until this one exits. Call .ToList() or .ToArray() "
        + "inside the body.";

    /// <summary>Why a failed <c>Commit</c> is re-thrown as something a caller must not retry.</summary>
    private const string CommitFailedMessage =
        "LiteDbAtomic.Mutate could not commit. Rollback was attempted and the write did not land, but "
        + "a failure inside Commit can leave LiteDB's named mutex for this file held after Dispose, in "
        + "which case every later open of it -- this process or another on the same state directory -- "
        + "blocks until this process exits. Treat this as fatal to the process, not as a retryable "
        + "write failure.";

    /// <summary>Why an async body is refused at compile time and again at run time.</summary>
    private const string AsyncMessage =
        "An async body cannot run inside a LiteDB transaction: the lambda returns at its first await, "
        + "so Commit runs while the read-modify-write is still in flight and the write lands outside "
        + "the transaction -- which is the lost update this helper exists to close. Make the body "
        + "synchronous, and do the awaiting outside Mutate.";

    /// <summary>
    /// Runs <paramref name="readModifyWrite"/> inside a transaction on <paramref name="db"/> and returns its result.
    /// </summary>
    /// <typeparam name="T">Result of the body.</typeparam>
    /// <param name="db">An open database. The transaction is bound to the calling thread.</param>
    /// <param name="readModifyWrite">The read and the write that must not be separable.</param>
    /// <returns>Whatever the body returned, after the transaction committed.</returns>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// This thread is already inside a transaction - on <paramref name="db"/> or on any other
    /// database - or <c>Commit</c> itself failed, in which case the file may be left unopenable.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// The body returned a <see cref="Task"/>, or a sequence it had not walked.
    /// </exception>
    public static T Mutate<T>(ILiteDatabase db, Func<T> readModifyWrite)
    {
        if (db is null) throw new ArgumentNullException(nameof(db));
        if (readModifyWrite is null) throw new ArgumentNullException(nameof(readModifyWrite));

        // Nesting across INSTANCES, which db.BeginTrans() below cannot see: two stores over one file
        // are two SharedEngines and the named mutex they share is thread-reentrant, so the inner
        // transaction is neither refused nor blocked and one of the two writes is discarded with both
        // Commits reporting success. Checked before BeginTrans, so the outer transaction is still
        // intact when this throws. See NestedOnThreadMessage for the measurement.
        if (_openTransactions > 0)
            throw new InvalidOperationException(NestedOnThreadMessage);

        // False means this thread is already inside a transaction on this database -- and LiteDB has
        // already ended that outer transaction by the time it says so, so there is nothing to join and
        // nothing to salvage. Throwing leaves the outer scope to roll back and tell its caller;
        // participating silently loses both scopes' writes.
        if (!db.BeginTrans())
            throw new InvalidOperationException(NestedMessage);

        _openTransactions++;
        var committing = false;
        try
        {
            var result = readModifyWrite();

            // Reached only for a Task the overloads below cannot see -- an explicit Mutate<Task>(...),
            // or a delegate held in a variable. Commit has not run yet, so the rollback in the catch
            // discards whatever the synchronous prefix of that body wrote.
            if (result is Task)
                throw new NotSupportedException(AsyncMessage);

            RefuseLazySequence(result);

            committing = true;
            db.Commit();
            return result;
        }
        catch (Exception ex) when (committing)
        {
            // Commit itself failed, which is NOT the same as the body failing: rollback returns
            // normally and the file's named mutex can stay held past Dispose. Re-thrown as something
            // that says so rather than surfacing as an ordinary write failure.
            Discard(db);
            throw new InvalidOperationException(CommitFailedMessage, ex);
        }
        catch
        {
            Discard(db);
            throw;
        }
        finally
        {
            _openTransactions--;
        }
    }

    /// <summary>
    /// Refuses a body whose result has not been walked, because the open cursor makes <c>Commit</c>
    /// throw and the file's named mutex is then never released.
    /// </summary>
    /// <param name="result">Whatever the body returned.</param>
    /// <exception cref="NotSupportedException">
    /// The result is a sequence that has not been materialised.
    /// </exception>
    /// <remarks>
    /// A materialised collection is fine and is the overwhelmingly common case, so the test is
    /// "enumerable but not a collection". <c>ICollection</c> covers lists, arrays and dictionaries;
    /// the generic interface walk covers <c>HashSet&lt;T&gt;</c> and the other generic-only
    /// collections. A string is enumerable and is obviously not a cursor.
    /// </remarks>
    private static void RefuseLazySequence(object? result)
    {
        if (result is null or string or ICollection)
            return;
        if (result is not IEnumerable)
            return;

        foreach (var contract in result.GetType().GetInterfaces())
        {
            if (contract.IsGenericType
                && contract.GetGenericTypeDefinition() == typeof(ICollection<>))
            {
                return;
            }
        }

        throw new NotSupportedException(LazySequenceMessage);
    }

    /// <summary>
    /// Never call this: an async body cannot run inside a LiteDB transaction. It exists so that
    /// <c>Mutate(db, async () =&gt; ...)</c> fails to compile instead of committing early.
    /// </summary>
    /// <typeparam name="T">Result of the body.</typeparam>
    /// <param name="db">Unused.</param>
    /// <param name="readModifyWrite">Unused.</param>
    /// <returns>Nothing; always throws.</returns>
    /// <exception cref="NotSupportedException">Always.</exception>
    [Obsolete(AsyncMessage, error: true)]
    public static T Mutate<T>(ILiteDatabase db, Func<Task<T>> readModifyWrite)
        => throw new NotSupportedException(AsyncMessage);

    /// <summary>
    /// Never call this: an async body cannot run inside a LiteDB transaction. It exists so that
    /// <c>Mutate(db, async () =&gt; ...)</c> fails to compile instead of committing early.
    /// </summary>
    /// <param name="db">Unused.</param>
    /// <param name="readModifyWrite">Unused.</param>
    /// <exception cref="NotSupportedException">Always.</exception>
    [Obsolete(AsyncMessage, error: true)]
    public static void Mutate(ILiteDatabase db, Func<Task> readModifyWrite)
        => throw new NotSupportedException(AsyncMessage);

    /// <summary>
    /// Runs <paramref name="readModifyWrite"/> inside a transaction on <paramref name="db"/>.
    /// </summary>
    /// <param name="db">An open database. The transaction is bound to the calling thread.</param>
    /// <param name="readModifyWrite">The read and the write that must not be separable.</param>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    public static void Mutate(ILiteDatabase db, Action readModifyWrite)
    {
        if (readModifyWrite is null) throw new ArgumentNullException(nameof(readModifyWrite));

        Mutate(db, () =>
        {
            readModifyWrite();
            return true;
        });
    }

    /// <summary>
    /// Rolls back, never masking the failure that caused the rollback.
    /// </summary>
    /// <remarks>
    /// <para>A throw out of <c>Rollback</c> inside a catch block would replace the real exception
    /// with one about the cleanup, and the caller would be told the wrong thing about why its write
    /// did not land. That is the whole reason this swallows.</para>
    ///
    /// <para><b>What an earlier revision of this comment claimed, and why it is false.</b> It said
    /// the transaction is discarded either way when the database is disposed, so there is nothing to
    /// recover. Measured in the Linux devtest container: when <c>Commit</c> ITSELF fails - a body
    /// that leaves a cursor open makes it throw "Current transaction contains open cursors" -
    /// <c>Rollback</c> returns normally, nothing is logged and nothing is even swallowed-and-noted,
    /// and after the <c>using</c> disposes the database the <c>SharedEngine</c>'s named mutex for
    /// the file is STILL HELD: a fresh open on another thread never returned. A failure here is
    /// therefore not equivalent to letting dispose handle it. The reachable cause is refused at the
    /// door by <see cref="RefuseLazySequence"/>, and a <c>Commit</c> that fails anyway is re-thrown
    /// as <see cref="InvalidOperationException"/> carrying <c>CommitFailedMessage</c>, which says
    /// the file may now be unopenable rather than pretending the write simply did not land. When the
    /// BODY throws, by contrast, rollback is clean - measured, the next writer on another thread
    /// succeeded in 16 ms.</para>
    /// </remarks>
    private static void Discard(ILiteDatabase db)
    {
        try
        {
            db.Rollback();
        }
        catch (LiteException)
        {
        }
        catch (InvalidOperationException)
        {
            // ObjectDisposedException derives from this one, so the pair is covered.
        }
    }
}
