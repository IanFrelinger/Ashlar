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
        // owns the commit.
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
