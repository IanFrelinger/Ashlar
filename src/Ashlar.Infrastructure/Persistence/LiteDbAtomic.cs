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
/// <para>Measured on Linux (devtest container, net8.0 and net10.0). Not measured on Windows or macOS;
/// CI runs ubuntu, windows and macos, and no lane exercises this behaviourally on any of them.</para>
/// </remarks>
public static class LiteDbAtomic
{
    /// <summary>
    /// Runs <paramref name="readModifyWrite"/> inside a transaction on <paramref name="db"/> and returns its result.
    /// </summary>
    /// <typeparam name="T">Result of the body.</typeparam>
    /// <param name="db">An open database. The transaction is bound to the calling thread.</param>
    /// <param name="readModifyWrite">The read and the write that must not be separable.</param>
    /// <returns>Whatever the body returned, after the transaction committed.</returns>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    public static T Mutate<T>(ILiteDatabase db, Func<T> readModifyWrite)
    {
        if (db is null) throw new ArgumentNullException(nameof(db));
        if (readModifyWrite is null) throw new ArgumentNullException(nameof(readModifyWrite));

        // False means this thread is already inside a transaction on this database, so an outer scope
        // owns the commit. Committing anyway would end the outer scope's transaction early, which is
        // the one way this helper could make things worse than leaving the pair unguarded.
        var owned = db.BeginTrans();
        try
        {
            var result = readModifyWrite();
            if (owned) db.Commit();
            return result;
        }
        catch
        {
            if (owned) Discard(db);
            throw;
        }
    }

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
