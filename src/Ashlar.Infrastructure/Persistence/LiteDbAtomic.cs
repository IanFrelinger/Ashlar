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
/// <para><b>Scope.</b> This makes the pair atomic only when BOTH halves are inside the body. A caller
/// that reads through a store method, decides, and then calls a second store method to write is still
/// racing: the database is opened and closed between the two calls. Those sites are listed in
/// <c>LiteDbAtomicWriteConventionTests</c>' remarks and are not closed by this helper.</para>
///
/// <para><b>Two shapes this helper refuses rather than accepts.</b> Neither is reachable from any
/// call site in the tree today; both are one ordinary edit away, and both fail SILENTLY, which is why
/// they are rejected at the door rather than documented.</para>
///
/// <para><i>Nesting.</i> LiteDB 5.0.21 does not nest transactions. A second <c>BeginTrans</c> on one
/// database from one thread returns <c>false</c> and has ALREADY ended the outer transaction: the
/// outer <c>Commit</c> then returns <c>false</c> and every write made in BOTH scopes is discarded,
/// with nothing thrown on either side. Measured in the Linux devtest container against LiteDB 5.0.21
/// on a <c>Connection=Shared</c> file — outer insert, then inner insert, then a fresh
/// <c>LiteDatabase</c> over the same file sees ZERO documents. An earlier revision of this helper read
/// that <c>false</c> as "an outer scope owns the commit" and joined it; joining is strictly worse than
/// leaving the pair unguarded, so it now throws.</para>
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
    /// <summary>Why a nested <c>Mutate</c> is refused instead of joined.</summary>
    private const string NestedMessage =
        "LiteDbAtomic.Mutate is already inside a transaction on this ILiteDatabase. LiteDB 5.0.21 does "
        + "not nest: the inner BeginTrans has already ended the outer transaction, the outer Commit "
        + "will return false, and every write made in BOTH scopes is discarded without an exception. "
        + "Put the read and the write in ONE Mutate body rather than composing two guarded methods on "
        + "one open database.";

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
    /// This thread is already inside a transaction on <paramref name="db"/>.
    /// </exception>
    /// <exception cref="NotSupportedException">The body returned a <see cref="Task"/>.</exception>
    public static T Mutate<T>(ILiteDatabase db, Func<T> readModifyWrite)
    {
        if (db is null) throw new ArgumentNullException(nameof(db));
        if (readModifyWrite is null) throw new ArgumentNullException(nameof(readModifyWrite));

        // False means this thread is already inside a transaction on this database -- and LiteDB has
        // already ended that outer transaction by the time it says so, so there is nothing to join and
        // nothing to salvage. Throwing leaves the outer scope to roll back and tell its caller;
        // participating silently loses both scopes' writes.
        if (!db.BeginTrans())
            throw new InvalidOperationException(NestedMessage);

        try
        {
            var result = readModifyWrite();

            // Reached only for a Task the overloads below cannot see -- an explicit Mutate<Task>(...),
            // or a delegate held in a variable. Commit has not run yet, so the rollback in the catch
            // discards whatever the synchronous prefix of that body wrote.
            if (result is Task)
                throw new NotSupportedException(AsyncMessage);

            db.Commit();
            return result;
        }
        catch
        {
            Discard(db);
            throw;
        }
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
    /// A throw out of <c>Rollback</c> inside a catch block would replace the real exception with one
    /// about the cleanup, and the caller would be told the wrong thing about why its write did not
    /// land. The transaction is discarded either way when the database is disposed at the end of the
    /// store method, so there is nothing to recover here.
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
