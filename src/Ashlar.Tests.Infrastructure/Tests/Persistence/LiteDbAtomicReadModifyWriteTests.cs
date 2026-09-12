using FluentAssertions;
using LiteDB;
using Ashlar.Core.Application.Persistence;
using Ashlar.Core.Application.Trust.Models;
using Ashlar.Infrastructure.Trust;
using Ashlar.Tests.Infrastructure.Helpers;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Persistence;

/// <summary>
/// An update that a store accepted must still be in the document afterwards.
/// </summary>
/// <remarks>
/// <para>The behavioural half of <c>LiteDbAtomicWriteConventionTests</c>. That one reads text and can
/// only prove the transaction is spelled; this one proves it does what it is for.</para>
///
/// <para><b>What Shared mode left open.</b> #594 stopped two overlapping OPENS from colliding, and
/// <c>LiteDbStoreSharedModeConcurrencyTests</c> counts the writes that survive. It counts INSERTS,
/// though, and an insert carries its whole value with it. <c>UpsertAsync</c> does not: it reads the
/// stored document, derives <c>Version + 1</c> from it and writes that back, so it is two engine
/// operations and Shared's named mutex is released between them. Two upserts that overlap both read
/// <c>Version = N</c> and both write <c>N + 1</c>, so one update is gone. Measured in the Linux
/// devtest container at 4 threads x 100 updates to ONE id, counting the stored <c>Version</c>: Direct
/// arrived at 219 of 400 and Shared at 358 of 400, both with ZERO exceptions thrown.</para>
///
/// <para><b>Why the assertion is a number and not an exception.</b> Same reason as the Shared-mode
/// suite next to it, only more so: this race has no exception on ANY platform. Nothing is refused,
/// nothing is logged, the increments simply are not there. A test that expected a throw would be
/// green everywhere and assert nothing.</para>
///
/// <para><b>Why the version counter and not a document count.</b> The count is right in both modes —
/// there is one document either way. Only the value it converged on says whether the updates were
/// serialised, which is why this test is about a single id and not about throughput.</para>
///
/// <para><b>No store instance lock to hide behind.</b> <c>LiteDbUserKnowledgeLogStore</c> has no lock
/// over its read/write pair at all — its only gate guards the EnsureIndexes flag — so a single
/// instance races itself here. That is also how it ships: the store is a singleton behind
/// <c>GET /knowledge/query</c> and its upserts and deletes run on HTTP request threads.</para>
///
/// <para>No <c>Task.Delay</c> anywhere: the threads are real threads released by a
/// <see cref="Barrier"/>, which is the only synchronisation a race test is allowed to need.</para>
/// </remarks>
public sealed class LiteDbAtomicReadModifyWriteTests : TempDirTestBase
{
    private const int Writers = 4;
    private const int UpdatesPerWriter = 100;
    private const string CollectionName = "user_knowledge_log";
    private const string EntryId = "one-entry-many-writers";

    /// <summary>Initializes a new lite db atomic read modify write tests.</summary>
    public LiteDbAtomicReadModifyWriteTests()
        : base("ashlar-litedb-rmw")
    {
    }

    /// <summary>
    /// xUnit1031 rejects a blocking wait written inside a test method, and an async test would not
    /// produce overlapping real threads — so the body lives one call down, the same workaround
    /// <c>LiteDbMapperConcurrencyTests</c> uses.
    /// </summary>
    [Fact]
    public void UserKnowledgeLog_keeps_every_update_when_four_threads_race_on_one_id()
        => RaceFourWritersOnOneId();

    private void RaceFourWritersOnOneId()
    {
        var path = Path.Combine(TempDir, $"knowledge-{Guid.NewGuid():N}.db");

        // Seed once, single-threaded, so every write the racers make is an INCREMENT of a document
        // that already exists. Without this the first few writes would take the entry's own version
        // and the expected total would depend on how many of them got there first.
        var seed = new LiteDbUserKnowledgeLogStore(path);
        seed.UpsertAsync(Entry()).GetAwaiter().GetResult();
        StoredVersion(path).Should().Be(0, "the seed stores the entry's own version verbatim");

        // Two instances, because two singletons over one file is what ships — and because a second
        // PROCESS on the same ASHLAR_STATE_DIR is the case no in-process lock could ever cover.
        var a = new LiteDbUserKnowledgeLogStore(path);
        var b = new LiteDbUserKnowledgeLogStore(path);

        RunConcurrently(
            () => a.UpsertAsync(Entry()).GetAwaiter().GetResult(),
            () => b.UpsertAsync(Entry()).GetAwaiter().GetResult(),
            () => a.UpsertAsync(Entry()).GetAwaiter().GetResult(),
            () => b.UpsertAsync(Entry()).GetAwaiter().GetResult());

        StoredVersion(path).Should().Be(
            Writers * UpdatesPerWriter,
            "every accepted upsert increments the stored Version by one, so {0} of them must leave it "
            + "at {0}. A lower number is not a slow test or a flaky one — it is updates that were "
            + "accepted, reported success, and are not on disk. Shared mode alone reaches roughly 358 "
            + "here because it releases its mutex between the read and the write.",
            Writers * UpdatesPerWriter);
    }

    private static UserKnowledgeLogEntry Entry() => new()
    {
        Id = EntryId,
        DataType = "inferred-preferences",
        Content = "content",
        Version = 0,
    };

    private static void RunConcurrently(params Action[] writers)
    {
        writers.Should().HaveCount(Writers);

        var ready = new Barrier(writers.Length);
        var threads = writers.Select(writer => Task.Factory.StartNew(
            () =>
            {
                // Start together: the lost update only happens while the pairs actually interleave.
                ready.SignalAndWait();
                for (var i = 0; i < UpdatesPerWriter; i++)
                    writer();
            },
            TaskCreationOptions.LongRunning)).ToArray();

        WaitFor(threads);
    }

    private static void WaitFor(Task[] threads) => Task.WaitAll(threads);

    /// <summary>
    /// Reads the stored version without going through the store's document type or its mapper, so a
    /// mapper fault cannot read as a lost update.
    /// </summary>
    private static int StoredVersion(string path)
    {
        using var db = new LiteDatabase(LiteDbConnectionString.ForSharedAccess(path));
        var doc = db.GetCollection(CollectionName).FindById(new BsonValue(EntryId));
        Assert.NotNull(doc);
        return doc["Version"].AsInt32;
    }
}
