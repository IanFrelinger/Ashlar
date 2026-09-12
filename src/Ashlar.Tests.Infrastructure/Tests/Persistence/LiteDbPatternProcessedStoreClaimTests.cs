using FluentAssertions;
using LiteDB;
using Ashlar.Core.Application.Persistence;
using Ashlar.Infrastructure.Observation;
using Ashlar.Tests.Infrastructure.Helpers;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Persistence;

/// <summary>
/// One pattern is claimed once, by one cycle, across threads AND across processes.
/// </summary>
/// <remarks>
/// <para><b>What was wrong.</b> <c>SelfImprovementLoop</c> asked <c>IsProcessedAsync</c>, then did
/// the work — a source edit, a solution-wide regression run and a promotion — and marked the
/// pattern afterwards at each of eight exits. Two overlapping cycles both read "not processed",
/// both edited the same file, both ran the suite against a tree the other was editing, and both
/// promoted. <c>BackgroundAgentRegistry.TrackedExecuteAgentAsync</c> says in its own remarks that
/// the scheduler may start a cycle while the previous one is running, and <c>ashlar improve</c>
/// starts a second loop in a whole second process on the same state directory.</para>
///
/// <para><b>Why a unique index and not a lock.</b> The second process is the case that matters and
/// no in-process lock reaches it. The index makes the database pick the winner.</para>
///
/// <para><b>Why a migration and not a flag — the fact that matters most here.</b> LiteDB 5.0.21's
/// <c>EnsureIndex</c> is a no-op when an index of that name already exists, whatever the unique
/// flag. Every store already on disk carries a NON-unique <c>PatternId</c> index, because the
/// shipped write path declared one on its first write. A one-line <c>unique: true</c> is therefore
/// green on every test that starts from a fresh temp file and completely inert in production:
/// <c>docs/HowGatesGoQuiet.md</c> sections 4 and 5, a check that measures nothing while looking like
/// it worked. <see cref="A_store_that_already_has_the_old_non_unique_index_still_claims_once"/>
/// starts from a SEEDED database with the old index and duplicate rows, and is the fact the
/// flag-only version cannot pass.</para>
///
/// <para>Counts and stored rows, not exception shapes — except where the exception IS the mechanism
/// (a duplicate-key refusal), and even there the assertion is on the number of winners and the
/// number of rows, never on a throw reaching the caller.</para>
///
/// <para>Measured on Linux (devtest container). This file runs in <c>kernel-coverage</c>, which is
/// ubuntu-only and not a required check; nothing here is a claim about Windows or macOS.</para>
/// </remarks>
public sealed class LiteDbPatternProcessedStoreClaimTests : TempDirTestBase
{
    private const string CollectionName = "processed_patterns";
    private const string PatternIdField = "PatternId";
    private const int Racers = 8;

    /// <summary>Initializes a new lite db pattern processed store claim tests.</summary>
    public LiteDbPatternProcessedStoreClaimTests()
        : base("ashlar-pattern-claim")
    {
    }

    /// <summary>
    /// The positive control and the basic refusal in one fact: distinct ids all claim, and a repeat
    /// of any of them does not.
    /// </summary>
    /// <remarks>
    /// The positive half is not decoration. Every other fact here counts refusals, and a store that
    /// refused everything would satisfy all of them.
    /// </remarks>
    [Fact]
    public async Task A_fresh_id_claims_and_a_repeat_does_not()
    {
        var path = Path.Combine(TempDir, $"claim-{Guid.NewGuid():N}.db");
        var store = new LiteDbPatternProcessedStore(path);

        foreach (var id in new[] { "p1", "p2", "p3" })
            (await store.TryClaimAsync(id)).Should().BeTrue("{0} has never been claimed", id);

        foreach (var id in new[] { "p1", "p2", "p3" })
            (await store.TryClaimAsync(id)).Should().BeFalse("{0} is already claimed", id);

        (await store.IsProcessedAsync("p2")).Should().BeTrue();
        (await store.IsProcessedAsync("never-seen")).Should().BeFalse();
        RawCount(path).Should().Be(3, "three ids, three rows, however many times they were asked for");
    }

    /// <summary>
    /// A database written by the shipped store — old non-unique index, duplicate rows already on
    /// disk — must end up claiming once, and must not be left without an index.
    /// </summary>
    /// <remarks>
    /// <para>This is the whole reason the change is a migration. Seeded through raw LiteDB rather
    /// than through the store, because the store can no longer produce this state and the point is
    /// that a deployed one already has.</para>
    ///
    /// <para>It also pins the two halves a careless migration gets wrong. Dropping the index and
    /// declaring it unique WITHOUT de-duplicating throws and leaves the collection with no index at
    /// all — measured, after which duplicates flow more freely than before — so the row count is
    /// asserted as well as the refusal. And the surviving row must be the EARLIEST claim, because
    /// collapsing to a later one would move the recorded processing time backwards in the audit.</para>
    /// </remarks>
    [Fact]
    public async Task A_store_that_already_has_the_old_non_unique_index_still_claims_once()
    {
        var path = Path.Combine(TempDir, $"legacy-{Guid.NewGuid():N}.db");
        var firstSeen = DateTimeOffset.UtcNow.AddDays(-3);

        using (var db = new LiteDatabase(LiteDbConnectionString.ForSharedAccess(path)))
        {
            var col = db.GetCollection(CollectionName);
            col.EnsureIndex(PatternIdField).Should().BeTrue(
                "the seed is the shipped shape: a NON-unique index, declared by the old write path");

            // Three rows for one id and two for another, exactly what eight un-guarded exits and a
            // check-then-act produced.
            col.Insert(Legacy("dup", firstSeen));
            col.Insert(Legacy("dup", firstSeen.AddHours(1)));
            col.Insert(Legacy("dup", firstSeen.AddHours(2)));
            col.Insert(Legacy("other", firstSeen.AddHours(3)));
            col.Insert(Legacy("other", firstSeen.AddHours(4)));
            col.Insert(Legacy("unique-already", firstSeen.AddHours(5)));
        }

        RawCount(path).Should().Be(6, "the seed really did write duplicates");

        var store = new LiteDbPatternProcessedStore(path);

        (await store.TryClaimAsync("dup")).Should().BeFalse(
            "the pattern is on disk three times over; it is claimed, however many rows say so");
        (await store.TryClaimAsync("fresh-after-migration")).Should().BeTrue(
            "the positive control for the migrated store: a NEW id must still be claimable");
        (await store.TryClaimAsync("fresh-after-migration")).Should().BeFalse(
            "and the unique index must actually be enforcing afterwards - a migration that drops "
            + "the index and then fails to declare the unique one leaves the collection with NO "
            + "index, which is strictly worse than doing nothing");

        RawCount(path).Should().Be(
            4,
            "three ids collapse to one row each, plus the one claimed after the migration. A count "
            + "of 6 means the unique flag was declared over a collection that still held duplicates "
            + "- LiteDB makes that a no-op when an index of the name already exists, whatever the "
            + "flag, so the store would be green here and inert in production.");

        StoredProcessedAt(path, "dup").Should().BeCloseTo(
            firstSeen,
            TimeSpan.FromSeconds(1),
            "the surviving row is the EARLIEST claim; collapsing to a later one moves the recorded "
            + "processing time forwards and rewrites the audit trail");
    }

    /// <summary>
    /// A legacy database whose duplicate rows differ only in CASE migrates, because the
    /// de-duplication compares keys the way the index does.
    /// </summary>
    /// <remarks>
    /// <para>The first version of the migration de-duplicated with <c>StringComparer.Ordinal</c>
    /// and then declared an index that compares through the database collation - measured on this
    /// file as LCID 127 with <c>CompareOptions.IgnoreCase</c>. Rows differing only in case
    /// therefore survived the de-duplication, <c>EnsureIndex(unique)</c> raised duplicate-key error
    /// 110 inside the transaction, and because the store's ready flag is only set AFTER the
    /// transaction returns, every later claim re-ran the migration and threw again. Measured
    /// against the real store: <c>TryClaimAsync("brand-new-pattern")</c> threw on attempts one, two
    /// and three, and <c>SelfImprovementLoop</c> has no catch at that call site, so the improvement
    /// cycle died rather than getting a refusal it could act on.</para>
    ///
    /// <para>Reachability is not theoretical: <c>PatternDetector</c> mints lower-case GUIDs, but
    /// <c>MeshKnowledgeImportService</c> stores a peer's <c>PatternId</c> verbatim with no
    /// normalization, and <c>SelfImprovementLoop</c> claims whatever the pattern store returns.</para>
    /// </remarks>
    [Fact]
    public async Task A_legacy_store_whose_duplicates_differ_only_in_case_still_migrates()
    {
        var path = Path.Combine(TempDir, $"legacy-case-{Guid.NewGuid():N}.db");
        var firstSeen = DateTimeOffset.UtcNow.AddDays(-3);

        using (var db = new LiteDatabase(LiteDbConnectionString.ForSharedAccess(path)))
        {
            var col = db.GetCollection(CollectionName);
            col.EnsureIndex(PatternIdField).Should().BeTrue(
                "the seed is the shipped shape: a NON-unique index, declared by the old write path");

            // One id in two casings. Under the collation these are ONE index key, which is what an
            // Ordinal de-duplication cannot see.
            col.Insert(Legacy("AbC123", firstSeen));
            col.Insert(Legacy("abc123", firstSeen.AddHours(1)));
            col.Insert(Legacy("plain", firstSeen.AddHours(2)));
        }

        RawCount(path).Should().Be(3, "the seed really did write both casings");

        var store = new LiteDbPatternProcessedStore(path);

        (await store.TryClaimAsync("brand-new-pattern")).Should().BeTrue(
            "the migration must complete. With an Ordinal de-duplication this call THREW "
            + "LiteException 110 - and so did every call after it, because the ready flag is only "
            + "set once the migration returns.");

        RawCount(path).Should().Be(
            3,
            "AbC123 and abc123 collapse to one row, plain survives, and brand-new-pattern is "
            + "added: 2 + 1. A count of 4 means nothing was de-duplicated.");

        (await store.TryClaimAsync("ABC123")).Should().BeFalse(
            "and the surviving row still answers for every casing of that id");
        StoredProcessedAt(path, "abc123").Should().BeCloseTo(
            firstSeen,
            TimeSpan.FromSeconds(1),
            "the EARLIEST of the two casings survives, as for any other duplicate");
    }

    /// <summary>
    /// A legacy row with no <c>PatternId</c> field at all is left alone.
    /// </summary>
    /// <remarks>
    /// The same comparer mismatch in the opposite direction. The typed document's property
    /// initializer maps an absent field onto <c>string.Empty</c>, so an Ordinal de-duplication over
    /// mapped documents treated an absent field and an empty string as one key and deleted one of
    /// the rows. Measured: <c>Compare(BsonValue.Null, "")</c> is -1, and a unique index accepts
    /// both - so that deletion was data loss with no defect to justify it, and it left a valid
    /// index behind, so nothing noticed.
    /// </remarks>
    [Fact]
    public async Task A_legacy_row_with_no_pattern_id_field_is_not_deleted_by_the_migration()
    {
        var path = Path.Combine(TempDir, $"legacy-missing-{Guid.NewGuid():N}.db");

        using (var db = new LiteDatabase(LiteDbConnectionString.ForSharedAccess(path)))
        {
            var col = db.GetCollection(CollectionName);
            col.EnsureIndex(PatternIdField);

            // Both rows are needed, and that is the point: an Ordinal de-duplication over MAPPED
            // documents sees one key for these two, because the typed document's property
            // initializer turns an absent field into string.Empty. Seeding only the absent one
            // leaves nothing to collide with and the fact passes in both states - which is what
            // the first version of this test did.
            col.Insert(new BsonDocument
            {
                ["ProcessedAt"] = DateTime.UtcNow.AddDays(-2),
            });
            col.Insert(Legacy(string.Empty, DateTimeOffset.UtcNow.AddDays(-1)));
            col.Insert(Legacy("real", DateTimeOffset.UtcNow.AddDays(-1)));
        }

        RawCount(path).Should().Be(3);

        var store = new LiteDbPatternProcessedStore(path);
        (await store.TryClaimAsync("after-migration")).Should().BeTrue();

        RawCount(path).Should().Be(
            4,
            "an absent PatternId field indexes as BsonValue.Null, which the collation orders "
            + "strictly BEFORE the empty string (measured: Compare(Null, \"\") == -1), so a unique "
            + "index accepts both and neither is a duplicate of anything. A count of 3 means the "
            + "migration deleted one of them - silently, and leaving a valid index behind.");
    }

    /// <summary>
    /// Two ids differing only in case are ONE claim, and that is a property of the store rather
    /// than a change this made.
    /// </summary>
    /// <remarks>
    /// <para>The index compares through the database collation, so the second casing is refused.
    /// Pinned here because a caller reading "the database decides the winner" would reasonably
    /// assume the key is the id it passed, and because the alternative - making the key exact -
    /// would mean changing the collation of a shared database file, which is a far wider change
    /// than a concurrency fix.</para>
    ///
    /// <para>It is not a narrowing. The previous shape answered <c>IsProcessedAsync</c> with
    /// <c>Query.EQ</c> over the same field and the same collation, and measured on a store built
    /// that way, <c>IsProcessedAsync("pattern-a")</c> already returned true for a stored
    /// <c>Pattern-A</c>. The claim is exactly as coarse as the check it replaced.</para>
    /// </remarks>
    [Fact]
    public async Task Two_ids_that_differ_only_in_case_are_one_claim()
    {
        var path = Path.Combine(TempDir, $"case-{Guid.NewGuid():N}.db");
        var store = new LiteDbPatternProcessedStore(path);

        (await store.TryClaimAsync("Pattern-A")).Should().BeTrue();
        (await store.TryClaimAsync("pattern-a")).Should().BeFalse(
            "the unique index compares through the database collation (LCID 127, IgnoreCase), so "
            + "these are one key. If this ever returns true the index has stopped being unique, or "
            + "the collation changed - both of which matter more than the case question itself.");

        (await store.IsProcessedAsync("PATTERN-A")).Should().BeTrue(
            "and the read answers on the same key, which is what it did before the claim existed");

        (await store.TryClaimAsync("pattern-b")).Should().BeTrue(
            "the positive control: a genuinely different id must still claim, or this fact is "
            + "satisfied by a store that refuses every second call");

        RawCount(path).Should().Be(2);
    }

    /// <summary>
    /// Eight threads over eight separate database instances, all claiming one id: one winner.
    /// </summary>
    /// <remarks>
    /// Eight instances rather than one, for the reason the whole change exists: the second claimant
    /// is another PROCESS, and an in-process lock cannot see it. Asserted on the number of winners
    /// and the number of rows — the duplicate-key refusal is the mechanism, not the claim.
    /// </remarks>
    [Fact]
    public void Eight_racing_claims_of_one_pattern_leave_one_winner() => RaceOneKey();

    private void RaceOneKey()
    {
        var path = Path.Combine(TempDir, $"race-{Guid.NewGuid():N}.db");
        var winners = 0;
        var refusals = 0;

        var stores = Enumerable.Range(0, Racers)
            .Select(_ => new LiteDbPatternProcessedStore(path))
            .ToArray();

        RunConcurrently(stores.Select(store => new Action(() =>
        {
            if (store.TryClaimAsync("one-pattern").GetAwaiter().GetResult())
                Interlocked.Increment(ref winners);
            else
                Interlocked.Increment(ref refusals);
        })).ToArray());

        winners.Should().Be(
            1,
            "the claim is what decides which cycle edits the source file, runs the regression suite "
            + "and promotes. Two winners is two of those, against a tree each is editing.");
        refusals.Should().Be(Racers - 1, "every other racer must have been told no, not thrown at");
        RawCount(path).Should().Be(1, "one claim, one row");
    }

    /// <summary>
    /// A blank id is refused by the store, not by the index.
    /// </summary>
    /// <remarks>
    /// Measured: under a unique index an empty string indexes as the SAME key as null, so without
    /// this the first blank id would claim "the blank pattern" and every later one would throw a
    /// duplicate-key <c>LiteException</c> out of a caller that has no catch for it.
    /// </remarks>
    [Fact]
    public async Task A_blank_pattern_id_is_refused_before_it_reaches_the_index()
    {
        var path = Path.Combine(TempDir, $"blank-{Guid.NewGuid():N}.db");
        var store = new LiteDbPatternProcessedStore(path);

        foreach (var blank in new[] { "", "   ", "\t" })
        {
            var attempt = () => store.TryClaimAsync(blank);
            await attempt.Should().ThrowAsync<ArgumentException>();
        }

        RawCount(path).Should().Be(0, "a refused id writes no row");
        (await store.TryClaimAsync(" trimmed ")).Should().BeTrue();
        (await store.TryClaimAsync("trimmed")).Should().BeFalse(
            "ids are trimmed, so a stray space cannot buy a second claim on one pattern");
    }

    private static BsonDocument Legacy(string patternId, DateTimeOffset processedAt) => new()
    {
        ["_id"] = ObjectId.NewObjectId(),
        [PatternIdField] = patternId,
        ["ProcessedAt"] = processedAt.UtcDateTime,
    };

    private static void RunConcurrently(params Action[] racers)
    {
        var ready = new Barrier(racers.Length);
        var threads = racers.Select(racer => Task.Factory.StartNew(
            () =>
            {
                // Real threads released together. A Task.Delay would be a guess about timing rather
                // than a synchronisation primitive.
                ready.SignalAndWait();
                racer();
            },
            TaskCreationOptions.LongRunning)).ToArray();

        Task.WaitAll(threads);
    }

    /// <summary>Counts rows without the store's document type or its mapper.</summary>
    private static long RawCount(string path)
    {
        using var db = new LiteDatabase(LiteDbConnectionString.ForSharedAccess(path));
        return db.GetCollection(CollectionName).LongCount();
    }

    /// <summary>Reads the surviving row's timestamp for one id, raw.</summary>
    private static DateTimeOffset StoredProcessedAt(string path, string patternId)
    {
        using var db = new LiteDatabase(LiteDbConnectionString.ForSharedAccess(path));
        var doc = db.GetCollection(CollectionName).FindOne(Query.EQ(PatternIdField, patternId));
        Assert.NotNull(doc);
        return new DateTimeOffset(DateTime.SpecifyKind(doc["ProcessedAt"].AsDateTime, DateTimeKind.Utc));
    }
}
