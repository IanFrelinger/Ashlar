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
/// <para>The refusals are the same two and for the same measured reasons: a nested <c>Mutate</c>
/// throws because LiteDB 5.0.21's inner <c>BeginTrans</c> has already ended the outer transaction and
/// both scopes' writes are then discarded silently, and an async body cannot compile because it would
/// commit at its first <c>await</c>. Read the <c>Ashlar.Infrastructure</c> copy's remarks for the
/// measurements.</para>
///
/// <para>Note what this does NOT reach on this side. <c>CommercialFleetEndpoints</c>'
/// <c>RegisterFleetNodeAsync</c> reads the node through <c>IFleetNodeRegistry.GetAsync</c> and then
/// writes the WHOLE document back through <c>RegisterOrUpdateAsync</c>, on a database this store
/// opened and closed in between — so a <c>revoke</c> landing in that window is still reverted. That is
/// the caller-side shape no transaction can span; see <c>LiteDbAtomicWriteConventionTests</c>'
/// remarks for the full list.</para>
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
