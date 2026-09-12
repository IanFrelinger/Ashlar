using System.Collections;
using LiteDB;

namespace Ashlar.Infrastructure.Persistence;

/// <summary>
/// Runs a read-modify-write against one LiteDB file as a single atomic operation.
/// </summary>
/// <remarks>
/// <para><b>What Shared mode does not buy.</b> Since #594 every store composes its connection string
/// through <c>LiteDbConnectionString.ForSharedAccess</c>, so concurrent opens queue on a named mutex
/// instead of colliding. That mutex is taken and released per ENGINE OPERATION. A store method that
/// reads a document, derives a new value from it and writes the result therefore performs TWO
/// independent operations, and another writer — another thread, another instance, or the CLI in a
/// second process on the same <c>ASHLAR_STATE_DIR</c> — can commit in between. Both writers then
/// persist a value derived from the same snapshot and one of them is silently gone. Measured in the
/// Linux devtest container on <c>LiteDbUserKnowledgeLogStore.UpsertAsync</c>, 4 threads x 100 updates
/// to one id, counting the surviving <c>Version</c>: Shared kept 358 of 400 with ZERO exceptions
/// thrown. Nothing fails; the updates just are not there.</para>
///
/// <para><b>Why a transaction and not a compare-and-swap loop.</b> CAS needs a token on the document
/// to compare against, and only two of the affected documents have one
/// (<c>KnowledgeLogDoc.Version</c>, <c>MeshTaskDoc.LeaseToken</c>). <c>MeshFleetNodeDoc</c> has none,
/// so drain/admit/heartbeat would need a new field, a migration for every document already on disk,
/// and a retry loop in each caller — and a check-then-insert like
/// <c>LiteDbMeshTaskRegistry.CreateAsync</c> has no prior document to compare against at all, so CAS
/// cannot express it. <c>BeginTrans</c> covers all three shapes with the same three lines and changes
/// no schema. It is also the stronger guarantee: LiteDB's <c>SharedEngine</c> acquires the named
/// mutex on <c>BeginTrans</c> and holds it until <c>Commit</c> or <c>Rollback</c>, so the pair is
/// atomic against a SECOND PROCESS, which an in-process CAS retry is not.</para>
///
/// <para><b>The same call is what makes a batch cheap.</b> Because the mutex and the engine are
/// opened once for the whole transaction rather than once per operation, a loop of inserts inside one
/// of these costs one acquire instead of N — see <c>LiteDbDataDecisionAuditLog.FlushBuffer</c>, which
/// keeps its per-entry insert accounting and got 7x faster by doing nothing else.</para>
///
/// <para><b>Scope.</b> This makes the pair atomic only when BOTH halves are inside the body - not
/// when the member merely CALLS this helper somewhere. Reading the document, deciding, and then
/// opening a transaction round the write alone is the original lost update with a transaction
/// ornament on it, and it was measured to be genuinely raced: splitting
/// <c>LiteDbMeshTaskRegistry.UpdateAsync</c>'s read from its write turned three of the four
/// contended fleet facts red. <c>LiteDbAtomicWriteConventionTests</c> therefore checks the
/// BOUNDARY, not the spelling. A caller that reads through a store method, decides, and then calls
/// a second store method to write is a different shape again: the database is opened and closed
/// between the two calls, so no transaction can span it. Those sites are listed in that test's
/// remarks and are not closed by this helper.</para>
///
/// <para><b>Three shapes this helper refuses rather than accepts.</b> None is reachable from any
/// call site in the tree today; each is one ordinary edit away, and each fails SILENTLY - or, in the
/// third case, worse than silently - which is why they are rejected at the door rather than
/// documented.</para>
///
/// <para><i>Nesting on one database.</i> LiteDB 5.0.21 does not nest transactions. A second
/// <c>BeginTrans</c> on one database from one thread returns <c>false</c> and has ALREADY ended the
/// outer transaction: the outer <c>Commit</c> then returns <c>false</c> and every write made in BOTH
/// scopes is discarded, with nothing thrown on either side. Measured in the Linux devtest container
/// against LiteDB 5.0.21 on a <c>Connection=Shared</c> file - outer insert, then inner insert, then
/// a fresh <c>LiteDatabase</c> over the same file sees ZERO documents. An earlier revision of this
/// helper read that <c>false</c> as "an outer scope owns the commit" and joined it; joining is
/// strictly worse than leaving the pair unguarded, so it now throws.</para>
///
/// <para><i>Nesting across two databases on one thread</i> - the shape this deployment actually
/// exposes, and the one <c>BeginTrans</c>'s own answer CANNOT see. Two stores are constructed with
/// the same file path and a service holds both, so a body that consults its sibling store
/// synchronously opens a second <c>SharedEngine</c> over one file on one thread. Measured, same
/// container and version: outer <c>BeginTrans</c> true, inner <c>BeginTrans</c> TRUE, both
/// <c>Commit</c>s TRUE, and a fresh reader afterwards sees the outer write but NOT the inner one.
/// Nothing throws, and inspecting <c>Commit</c>'s return value would not have caught it either,
/// because <c>Commit</c> returned true for the write that vanished. It does not deadlock on the
/// sibling store's semaphore either - that semaphore is the other store's, and LiteDB's named mutex
/// is thread-reentrant, so the same thread walks through it; only a nested open from a DIFFERENT
/// thread blocks as designed. A thread-static depth count, checked before <c>BeginTrans</c>, is what
/// turns this into a refusal. An earlier revision of <c>IMeshTaskRegistry</c>'s remarks told
/// transform authors this case was covered by the semaphore or by the nesting check; neither clause
/// held.</para>
///
/// <para><i>A body that returns an un-materialised sequence.</i> The cursor is still open when
/// <c>Commit</c> runs, so <c>Commit</c> throws "Current transaction contains open cursors" - and
/// measured in the same container, <c>Rollback</c> then returns normally while the file's named
/// mutex stays held past <c>Dispose</c>, so the next open of it from any thread or process blocks
/// until this process exits. That is a hung database file rather than a lost update, which is why
/// this one is refused even though all seven shipped bodies were measured committing cleanly.</para>
///
/// <para><i>An async body.</i> <c>async () =&gt; { ... }</c> binds to <c>Func&lt;T&gt;</c> with
/// <c>T = Task</c>, so <c>Mutate</c> returns — and <c>Commit</c> runs — at the first <c>await</c>,
/// while the read-modify-write is still in flight. The write then lands outside the committed
/// transaction and an exception after the await is an unobserved task exception. Six of the seven
/// guarded store methods are already <c>async</c>, so an <c>await</c> inside one of these bodies is
/// the natural next edit. The <c>Func&lt;Task&gt;</c> and <c>Func&lt;Task&lt;T&gt;&gt;</c> overloads
/// below exist only to make that shape fail to COMPILE; the run-time <see cref="Task"/> check in
/// <see cref="Mutate{T}(ILiteDatabase, Func{T})"/> covers the spellings overload resolution cannot
/// see.</para>
///
/// <para>Measured on Linux (devtest container, net8.0 and net10.0). Not measured on Windows or macOS;
/// CI runs ubuntu, windows and macos, and no lane exercises this behaviourally on any of them.</para>
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
