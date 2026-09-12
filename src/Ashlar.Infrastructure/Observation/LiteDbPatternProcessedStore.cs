using LiteDB;
using Ashlar.Core.Application.Observation.Ports;
using Ashlar.Core.Application.Persistence;
using Ashlar.Infrastructure.Persistence;

namespace Ashlar.Infrastructure.Observation;

/// <summary>
/// LiteDB-backed store for processed pattern IDs. Uses same DB file as pattern store.
/// </summary>
/// <remarks>
/// The claim is a unique index on <c>PatternId</c>, so the single winner is decided by the database
/// and holds against a second PROCESS — which is the case that matters, because <c>ashlar improve</c>
/// runs a second loop over the same state directory and no in-process lock reaches it. Measured in
/// the Linux devtest container, 8 threads over 8 separate <c>LiteDatabase</c> instances on one file
/// all claiming one key: 1 winner, 7 refused, 1 row.
/// </remarks>
public sealed class LiteDbPatternProcessedStore : IPatternProcessedStore
{
    private const string CollectionName = "processed_patterns";

    /// <summary>
    /// LiteDB 5.0.21 raises this for an insert that would duplicate a unique index key. Restated
    /// here rather than reached through the library so the comparison says what it is testing.
    /// </summary>
    private const int DuplicateKeyErrorCode = 110;

    private static readonly string PatternIdField = nameof(ProcessedDoc.PatternId);

    private readonly string _connectionString;
    private readonly object _indexGate = new();
    private bool _indexesReady;

    /// <summary>Initializes a new lite db pattern processed store.</summary>
    /// <param name="pathOrConnectionString">File path or connection string.</param>
    public LiteDbPatternProcessedStore(string pathOrConnectionString)
    {
        if (string.IsNullOrWhiteSpace(pathOrConnectionString))
            throw new ArgumentNullException(nameof(pathOrConnectionString));
        _connectionString = LiteDbConnectionString.ForSharedAccess(pathOrConnectionString, nameof(pathOrConnectionString));
        LiteDbDocumentMapper.EnsureMapped<ProcessedDoc>();
    }

    /// <summary>Claims the pattern; false when it was already claimed.</summary>
    /// <param name="patternId">Pattern id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True when this call took the claim.</returns>
    /// <exception cref="ArgumentException">The id is null or whitespace.</exception>
    /// <remarks>
    /// The insert IS the claim: the unique index refuses the second one. There is deliberately no
    /// read in front of it — a probe would put the check-then-act straight back, one layer lower.
    /// </remarks>
    public Task<bool> TryClaimAsync(string patternId, CancellationToken cancellationToken = default)
    {
        LiteDbDocumentMapper.EnsureMapped<ProcessedDoc>();
        cancellationToken.ThrowIfCancellationRequested();

        // Under a unique index an empty string indexes as the SAME key as null, so a blank id would
        // claim "the blank pattern" once and then hard-throw for every later one. Refuse it here,
        // where the caller can see why, rather than at the index.
        var key = NormalizeKey(patternId, nameof(patternId));

        using var db = new LiteDatabase(_connectionString);
        var col = db.GetCollection<ProcessedDoc>(CollectionName);
        EnsureIndexes(db, col);

        try
        {
            col.Insert(new ProcessedDoc { PatternId = key, ProcessedAt = DateTimeOffset.UtcNow });
            return Task.FromResult(true);
        }
        catch (LiteException ex) when (ex.ErrorCode == DuplicateKeyErrorCode)
        {
            return Task.FromResult(false);
        }
    }

    /// <summary>Is processed asynchronously.</summary>
    /// <param name="patternId">Pattern id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True when a claim exists.</returns>
    public Task<bool> IsProcessedAsync(string patternId, CancellationToken cancellationToken = default)
    {
        LiteDbDocumentMapper.EnsureMapped<ProcessedDoc>();
        cancellationToken.ThrowIfCancellationRequested();
        using var db = new LiteDatabase(_connectionString);
        var col = db.GetCollection<ProcessedDoc>(CollectionName);
        // The index is declared by the write path, not here. A read has no business declaring one,
        // and this call was the LINQ-expression form -- the one that races on the BsonMapper.
        // Query.EQ below is already the string-based API and never reaches the expression visitor.
        var doc = col.FindOne(Query.EQ(PatternIdField, patternId));
        return Task.FromResult(doc != null);
    }

    /// <summary>
    /// Declares the unique index once per store, migrating a database that already carries the old
    /// non-unique one.
    /// </summary>
    /// <param name="db">The open database, for the transaction.</param>
    /// <param name="col">The collection, bound by the caller.</param>
    /// <remarks>
    /// <para><b>Why this is a migration and not a flag.</b> LiteDB 5.0.21's <c>EnsureIndex</c> is a
    /// NO-OP when an index of that name already exists, whatever the unique flag: measured, it
    /// returns <c>false</c> and changes nothing, and a duplicate insert then succeeds. Every
    /// database already on disk carries a NON-unique <c>PatternId</c> index, because the shipped
    /// write path declared one on its first write. So adding <c>unique: true</c> on its own is green
    /// on any test that starts from a temp file and completely inert in production — sections 4 and
    /// 5 of <c>docs/HowGatesGoQuiet.md</c>, a check that measures nothing while looking like it
    /// worked. <c>Tests/Persistence/LiteDbPatternProcessedStoreClaimTests</c> carries a fact that
    /// starts from a SEEDED non-unique database precisely so the flag-only version cannot pass.</para>
    ///
    /// <para><b>Why the de-duplication is not optional.</b> Dropping the index and then declaring it
    /// unique over a collection that still holds duplicates throws, and the collection is left with
    /// NO index at all — measured, after which duplicates flow more freely than before. Swallowing
    /// that throw would be strictly worse than doing nothing. The whole sequence therefore runs
    /// inside one <c>LiteDbAtomic.Mutate</c>: <c>EnsureIndex</c> participates in the transaction, so
    /// a failed migration leaves the database exactly as it was rather than index-less.</para>
    ///
    /// <para>Idempotent, and safe to run concurrently from separate processes: measured at 8
    /// concurrent whole-migrations over one file, all 8 succeeded and uniqueness was enforced
    /// afterwards. A second run removes 0 rows and leaves the index in place.</para>
    ///
    /// <para>The two problems the old comment here described are unchanged and still apply: the
    /// string overload avoids <c>EnsureIndex(x =&gt; x.Field)</c>, whose expression visitor is not
    /// safe to drive from several threads at once, and the flag skips the work after the first
    /// success.</para>
    /// </remarks>
    private void EnsureIndexes(ILiteDatabase db, ILiteCollection<ProcessedDoc> col)
    {
        if (Volatile.Read(ref _indexesReady)) return;

        lock (_indexGate)
        {
            if (_indexesReady) return;

            LiteDbAtomic.Mutate(db, () =>
            {
                col.DropIndex(PatternIdField);

                var keep = new HashSet<string>(StringComparer.Ordinal);
                var doomed = new List<BsonValue>();
                foreach (var doc in col.FindAll()
                             .OrderBy(d => d.ProcessedAt)
                             .ThenBy(d => d.Id.ToString(), StringComparer.Ordinal))
                {
                    if (keep.Add(doc.PatternId ?? string.Empty))
                        continue;
                    doomed.Add(new BsonValue(doc.Id));
                }

                foreach (var id in doomed)
                    col.Delete(id);

                col.EnsureIndex(PatternIdField, unique: true);
                return true;
            });

            Volatile.Write(ref _indexesReady, true);
        }
    }

    /// <summary>Trims a pattern id and refuses a blank one.</summary>
    /// <param name="patternId">Raw id.</param>
    /// <param name="parameterName">Name to report.</param>
    /// <returns>The trimmed id.</returns>
    private static string NormalizeKey(string patternId, string parameterName)
        => string.IsNullOrWhiteSpace(patternId)
            ? throw new ArgumentException(
                "a pattern id is required: under the unique index an empty id and a null id are the "
                + "SAME key, so a blank one would claim once and then throw for every later blank.",
                parameterName)
            : patternId.Trim();

    private sealed class ProcessedDoc
    {
        /// <summary>Id.</summary>
        [BsonId]
        public ObjectId Id { get; set; } = ObjectId.NewObjectId();
        /// <summary>Pattern id.</summary>
        public string PatternId { get; set; } = string.Empty;
        /// <summary>Processed at.</summary>
        public DateTimeOffset ProcessedAt { get; set; }
    }
}
