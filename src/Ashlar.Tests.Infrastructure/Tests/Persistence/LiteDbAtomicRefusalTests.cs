using FluentAssertions;
using LiteDB;
using Ashlar.Core.Application.Persistence;
using Ashlar.Infrastructure.Persistence;
using Ashlar.Tests.Infrastructure.Helpers;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Persistence;

/// <summary>
/// The shapes <c>LiteDbAtomic.Mutate</c> refuses at the door, and the measurements that say why
/// refusing beats documenting.
/// </summary>
/// <remarks>
/// <para>Each of these was silent before. A nested transaction across two database INSTANCES over
/// one file reported success on both commits and lost one write; a body returning an un-walked
/// sequence made <c>Commit</c> throw and left the file's named mutex held past <c>Dispose</c>, so
/// the database became unopenable by any thread or process. Neither showed up as an exception a
/// caller could act on, which is the only reason a refusal is the right answer rather than a
/// paragraph of remarks.</para>
///
/// <para>Every fact here carries the positive control in the same method: the same helper, one step
/// away from the refused shape, must still work. A helper that threw on everything would satisfy
/// the refusals on their own.</para>
///
/// <para><b>Not measured beyond Linux.</b> Ran in the devtest container on net8.0 and net10.0. The
/// nesting behaviour in particular is a property of the .NET named mutex being thread-reentrant,
/// which is not a platform-specific guarantee - but this suite does not assert that it behaves the
/// same way on Windows or macOS, and neither does any lane.</para>
/// </remarks>
public sealed class LiteDbAtomicRefusalTests : TempDirTestBase
{
    private const string CollectionName = "atomic_refusals";

    /// <summary>Initializes a new lite db atomic refusal tests.</summary>
    public LiteDbAtomicRefusalTests()
        : base("ashlar-atomic-refusal")
    {
    }

    /// <summary>
    /// A second <c>Mutate</c> on the same thread is refused even when it is a DIFFERENT database
    /// instance over the same file - the case <c>BeginTrans</c>'s own answer cannot see.
    /// </summary>
    /// <remarks>
    /// Measured without the guard, Linux devtest container, LiteDB 5.0.21, one file, one thread:
    /// the inner <c>BeginTrans</c> returned TRUE, both <c>Commit</c>s returned TRUE, and a fresh
    /// reader afterwards saw the outer write and the seed but not the inner one. Both fleet
    /// registries are constructed with the same path and one service holds both, so a transform
    /// that consults its sibling registry is the edit this refuses.
    /// </remarks>
    [Fact]
    public void A_nested_Mutate_on_a_second_database_over_one_file_is_refused()
    {
        var path = Path.Combine(TempDir, $"nested-sibling-{Guid.NewGuid():N}.db");
        var connection = LiteDbConnectionString.ForSharedAccess(path);

        using var outer = new LiteDatabase(connection);
        var col = outer.GetCollection(CollectionName);

        var act = () => LiteDbAtomic.Mutate(outer, () =>
        {
            col.Insert(new BsonDocument { ["_id"] = "outer" });

            using var sibling = new LiteDatabase(connection);
            var siblingCol = sibling.GetCollection(CollectionName);
            return LiteDbAtomic.Mutate(sibling, () =>
            {
                siblingCol.Insert(new BsonDocument { ["_id"] = "inner" });
                return true;
            });
        });

        act.Should().Throw<InvalidOperationException>(
                "the same thread is already inside a transaction. Both engines take a named mutex "
                + "that is thread-reentrant, so nothing below this line would have blocked or "
                + "returned false - one of the two writes would simply not be on disk, with both "
                + "Commits reporting success.")
            .WithMessage("*already inside a transaction on this THREAD*");

        Ids(connection).Should().BeEmpty(
            "the outer transaction is rolled back with the refusal, so neither write lands. A "
            + "surviving 'outer' with no 'inner' is the silent loss this refusal exists to stop "
            + "being possible.");
    }

    /// <summary>
    /// Two <c>Mutate</c> calls in SEQUENCE on one thread both land - the positive control for the
    /// refusal above.
    /// </summary>
    /// <remarks>
    /// The depth count is decremented in a <c>finally</c>, so a count that leaked would make every
    /// second guarded store call in a process throw. That is a worse failure than the one being
    /// fixed, which is why it is asserted rather than reasoned about.
    /// </remarks>
    [Fact]
    public void Sequential_Mutate_calls_on_one_thread_both_land()
    {
        var path = Path.Combine(TempDir, $"sequential-{Guid.NewGuid():N}.db");
        var connection = LiteDbConnectionString.ForSharedAccess(path);

        for (var i = 0; i < 3; i++)
        {
            using var db = new LiteDatabase(connection);
            var col = db.GetCollection(CollectionName);
            var id = $"row-{i}";
            LiteDbAtomic.Mutate(db, () => col.Insert(new BsonDocument { ["_id"] = id }));
        }

        Ids(connection).Should().BeEquivalentTo(
            ["row-0", "row-1", "row-2"],
            "the positive control: the depth count must not leak past a completed transaction, or "
            + "the second guarded call anywhere in a process throws");
    }

    /// <summary>
    /// A <c>Mutate</c> whose refused nesting is on a different FILE is also refused, and says why.
    /// </summary>
    /// <remarks>
    /// Two different files are not atomic together in any case, so joining them under one
    /// transaction would be a guarantee nobody can keep. Refusing is the honest answer and the
    /// message says so; nothing in the tree does this today.
    /// </remarks>
    [Fact]
    public void A_nested_Mutate_on_a_different_file_is_refused_too()
    {
        var first = LiteDbConnectionString.ForSharedAccess(
            Path.Combine(TempDir, $"first-{Guid.NewGuid():N}.db"));
        var second = LiteDbConnectionString.ForSharedAccess(
            Path.Combine(TempDir, $"second-{Guid.NewGuid():N}.db"));

        using var outer = new LiteDatabase(first);
        var outerCol = outer.GetCollection(CollectionName);

        var act = () => LiteDbAtomic.Mutate(outer, () =>
        {
            outerCol.Insert(new BsonDocument { ["_id"] = "outer" });

            using var other = new LiteDatabase(second);
            var otherCol = other.GetCollection(CollectionName);
            return LiteDbAtomic.Mutate(other, () =>
            {
                otherCol.Insert(new BsonDocument { ["_id"] = "inner" });
                return true;
            });
        });

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*different files the pair was never atomic*");

        Ids(first).Should().BeEmpty("the outer scope rolled back");
        Ids(second).Should().BeEmpty("and the inner one never committed");
    }

    /// <summary>
    /// A body that returns a sequence it has not walked is refused, because the open cursor makes
    /// <c>Commit</c> throw and the file's named mutex is then never released.
    /// </summary>
    /// <remarks>
    /// Measured without the refusal: <c>Commit</c> threw <c>"Current transaction contains open
    /// cursors"</c>, <c>Rollback</c> in the catch returned normally, and after the <c>using</c>
    /// disposed the database a fresh <c>LiteDatabase</c> on another thread never returned. A hung
    /// database file, not a lost update - which is why this one is refused even though none of the
    /// shipped bodies triggers it.
    /// </remarks>
    [Fact]
    public void A_body_that_returns_an_unwalked_sequence_is_refused()
    {
        var path = Path.Combine(TempDir, $"lazy-{Guid.NewGuid():N}.db");
        var connection = LiteDbConnectionString.ForSharedAccess(path);

        using var db = new LiteDatabase(connection);
        var col = db.GetCollection(CollectionName);
        col.Insert(new BsonDocument { ["_id"] = "seed" });

        var act = () => LiteDbAtomic.Mutate(db, () =>
        {
            col.Insert(new BsonDocument { ["_id"] = "written" });
            return col.FindAll().Select(d => d["_id"]);
        });

        act.Should().Throw<NotSupportedException>(
                "the cursor is still open when Commit runs; measured, that throws and leaves this "
                + "file unopenable by any thread or process until this one exits")
            .WithMessage("*un-materialised sequence*");

        Ids(connection).Should().BeEquivalentTo(
            ["seed"],
            "the refusal happens before Commit, so the write is rolled back rather than half-landed");
    }

    /// <summary>
    /// A body returning a MATERIALISED collection is accepted - the positive control for the
    /// refusal above, and the shape the real store bodies are one edit away from.
    /// </summary>
    [Fact]
    public void A_body_that_returns_a_materialised_collection_is_accepted()
    {
        var path = Path.Combine(TempDir, $"materialised-{Guid.NewGuid():N}.db");
        var connection = LiteDbConnectionString.ForSharedAccess(path);

        using var db = new LiteDatabase(connection);
        var col = db.GetCollection(CollectionName);

        var written = LiteDbAtomic.Mutate(db, () =>
        {
            col.Insert(new BsonDocument { ["_id"] = "kept" });
            return col.FindAll().Select(d => d["_id"].AsString).ToList();
        });

        written.Should().BeEquivalentTo(
            ["kept"],
            "the positive control: .ToList() is the whole remedy the refusal asks for, and a check "
            + "that refused every enumerable result would make several of the shipped store bodies "
            + "unwritable");
        Ids(connection).Should().BeEquivalentTo(["kept"]);
    }

    /// <summary>
    /// When the body throws, the transaction is discarded and the file is immediately usable again.
    /// </summary>
    /// <remarks>
    /// The contrast that makes the <c>Commit</c>-failure path worth distinguishing: an ordinary body
    /// failure is clean, so a caller CAN retry it, and only a failure inside <c>Commit</c> is the
    /// one that may have left the file's mutex held. Asserted on another thread because a mutex
    /// held by this one would be re-entered rather than blocking, and the assertion would pass while
    /// measuring nothing.
    /// </remarks>
    [Fact]
    public void A_body_that_throws_rolls_back_and_leaves_the_file_usable()
    {
        var path = Path.Combine(TempDir, $"body-throws-{Guid.NewGuid():N}.db");
        var connection = LiteDbConnectionString.ForSharedAccess(path);

        using (var db = new LiteDatabase(connection))
        {
            var col = db.GetCollection(CollectionName);

            // Explicitly typed: a block lambda whose every path throws converts to Func<bool> AND
            // to Func<Task<bool>>, so leaving it inferred binds to the [Obsolete(error)] async
            // overload and the file will not compile. Which is the compile-time refusal doing its
            // job, but it is not what this fact is about.
            Func<bool> bodyThatThrows = () =>
            {
                col.Insert(new BsonDocument { ["_id"] = "doomed" });
                throw new InvalidTimeZoneException("the body failed");
            };

            var act = () => LiteDbAtomic.Mutate(db, bodyThatThrows);

            act.Should().Throw<InvalidTimeZoneException>(
                "the body's own exception reaches the caller rather than one about the cleanup");
        }

        // A real thread rather than a Task: LiteDB's named mutex is thread-reentrant, so a reader on
        // THIS thread would walk straight through a mutex that was still held and the assertion
        // would pass having measured nothing. Bounded, and the bound is reported as itself.
        List<string>? read = null;
        Exception? failure = null;
        var reader = new Thread(() =>
        {
            try { read = Ids(connection); }
            catch (Exception ex) { failure = ex; }
        })
        {
            IsBackground = true,
        };

        reader.Start();
        reader.Join(TimeSpan.FromSeconds(30)).Should().BeTrue(
            "a reader on ANOTHER thread must be able to open the file. This is the assertion that "
            + "distinguishes a clean rollback from the Commit-failure path, where the named mutex "
            + "stays held past Dispose and this wait never returns.");

        failure.Should().BeNull("the reader opened the file cleanly");
        read.Should().BeEmpty("and the doomed write is gone");
    }

    private static List<string> Ids(string connectionString)
    {
        using var db = new LiteDatabase(connectionString);
        return db.GetCollection(CollectionName).FindAll().Select(d => d["_id"].AsString).ToList();
    }
}
