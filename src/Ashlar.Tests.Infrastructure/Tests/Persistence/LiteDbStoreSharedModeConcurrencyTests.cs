using FluentAssertions;
using LiteDB;
using Ashlar.Core.Application.Adaptation.Models;
using Ashlar.Core.Application.Copilot.Models;
using Ashlar.Core.Application.Observation.Models;
using Ashlar.Core.Application.Persistence;
using Ashlar.Core.Application.SelfContext.Models;
using Ashlar.Core.Application.Trust.Models;
using Ashlar.Infrastructure.Adaptation;
using Ashlar.Infrastructure.Copilot;
using Ashlar.Infrastructure.Observation;
using Ashlar.Infrastructure.SelfContext;
using Ashlar.Infrastructure.Trust;
using Ashlar.Tests.Infrastructure.Helpers;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Persistence;

/// <summary>
/// What a store writes concurrently must still be there afterwards.
/// </summary>
/// <remarks>
/// <para>The behavioural half of <c>LiteDbSharedModeConventionTests</c>. That one reads text and so
/// can only prove the connection string is composed in one place; this one proves the composition
/// does what it is for. Undo any store's call to
/// <see cref="LiteDbConnectionString.ForSharedAccess"/> and this class fails immediately.</para>
///
/// <para><b>Asserted on counts, never on an expected exception.</b> LiteDB's Direct mode takes an
/// exclusive file lock for the lifetime of a <c>LiteDatabase</c>, and every store method opens one
/// per call. On Windows the losing thread throws <c>IOException("...used by another process")</c>,
/// which is how UAT tier 10 found this. On Linux the second open is not refused at all — the writers
/// interleave and corrupt each other's pages, and the writes vanish with no exception. A regression
/// test that expected the IOException would therefore pass on a developer's box and assert nothing
/// in CI, which is the exact shape of escape hatch the previous two rounds of this bug slipped
/// through. Measured in the Linux container at 2 instances x 2 threads x 25 writes: Direct persisted
/// roughly a third of them; Shared persisted all of them, 20 trials out of 20.</para>
///
/// <para><b>Two instances, one file.</b> Several stores serialise their own writes with an instance
/// lock (<c>LiteDbPipelineRunStore</c>, <c>LiteDbDataDecisionAuditLog</c>, and both commercial
/// registries), so a single-instance race would call them safe when they are not. Two singletons
/// over one path is also the shape that actually ships: <c>ObservationServiceCollectionExtensions</c>
/// registers <c>LiteDbPatternStore</c> and <c>LiteDbPatternProcessedStore</c> on the same file, and
/// <c>FleetServiceCollectionExtensions</c> does the same for the two fleet registries.</para>
///
/// <para>The readback is deliberately a RAW, non-generic collection count. Reading through the
/// document type would put the answer behind the same <c>BsonMapper</c> the cold-mapper tests in
/// this directory exercise, and a mapper fault would then read as data loss.</para>
/// </remarks>
public sealed class LiteDbStoreSharedModeConcurrencyTests : TempDirTestBase
{
    private const int Writers = 2;
    private const int WritesPerWriter = 25;

    /// <summary>Initializes a new lite db store shared mode concurrency tests.</summary>
    public LiteDbStoreSharedModeConcurrencyTests()
        : base("ashlar-litedb-shared")
    {
    }

    [Fact]
    public void TestFailureStore_loses_nothing_when_two_instances_share_a_file()
    {
        AssertNoLoss("test_failures", path =>
        {
            var store = new LiteDbTestFailureStore(path);
            return _ => store.RecordAsync(new TestFailureRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                Timestamp = DateTimeOffset.UtcNow,
                TestName = "concurrent",
            }).GetAwaiter().GetResult();
        });
    }

    [Fact]
    public void AdaptationAuditLog_loses_nothing_when_two_instances_share_a_file()
    {
        AssertNoLoss("adaptation_audit", path =>
        {
            var store = new LiteDbAdaptationAuditLog(path);
            return _ => store.LogAsync(new AdaptationAuditEntry
            {
                Id = Guid.NewGuid().ToString("N"),
                Timestamp = DateTimeOffset.UtcNow,
                AutonomyLevel = "L1",
                Outcome = "Applied",
            }).GetAwaiter().GetResult();
        });
    }

    [Fact]
    public void UserKnowledgeLogStore_loses_nothing_when_two_instances_share_a_file()
    {
        AssertNoLoss("user_knowledge_log", path =>
        {
            var store = new LiteDbUserKnowledgeLogStore(path);
            return _ => store.UpsertAsync(new UserKnowledgeLogEntry
            {
                Id = Guid.NewGuid().ToString("N"),
                DataType = "preference",
                Content = "concurrent",
            }).GetAwaiter().GetResult();
        });
    }

    [Fact]
    public void CopilotTaskStore_loses_nothing_when_two_instances_share_a_file()
    {
        AssertNoLoss("copilot_tasks", path =>
        {
            var store = new LiteDbCopilotTaskStore(path);
            return _ => store.StoreAsync(new CopilotTaskRecord
            {
                TaskId = Guid.NewGuid().ToString("N"),
                Task = "concurrent",
                SubmittedAt = DateTimeOffset.UtcNow,
            }).GetAwaiter().GetResult();
        });
    }

    /// <summary>
    /// The pair that ships on ONE file. Neither store has an instance lock, and nothing above the DI
    /// registration coordinates them, so Shared mode is the whole of their mutual protection.
    /// </summary>
    [Fact]
    public void PatternStore_and_ProcessedStore_lose_nothing_sharing_one_file()
    {
        var path = Path.Combine(TempDir, $"patterns-{Guid.NewGuid():N}.db");
        var patterns = new LiteDbPatternStore(path);
        var processed = new LiteDbPatternProcessedStore(path);

        RunConcurrently(
            _ => patterns.AddAsync(new ObservedPattern
            {
                PatternId = Guid.NewGuid().ToString("N"),
                EventType = "concurrent",
                FirstSeen = DateTimeOffset.UtcNow,
                LastSeen = DateTimeOffset.UtcNow,
            }).GetAwaiter().GetResult(),
            _ => processed.TryClaimAsync(Guid.NewGuid().ToString("N")).GetAwaiter().GetResult());

        RawCount(path, "observed_patterns").Should().Be(WritesPerWriter, "every pattern written must be readable");
        RawCount(path, "processed_patterns").Should().Be(WritesPerWriter, "every processed marker written must be readable");
    }

    /// <summary>
    /// Two INSTANCES of the same store on one file, the shape a second process also takes. An
    /// instance-level lock cannot see the other instance; the named mutex Shared mode opens can.
    /// </summary>
    private void AssertNoLoss(string collection, Func<string, Action<int>> makeWriter)
    {
        var path = Path.Combine(TempDir, $"{collection}-{Guid.NewGuid():N}.db");

        RunConcurrently(makeWriter(path), makeWriter(path));

        RawCount(path, collection).Should().Be(
            Writers * WritesPerWriter,
            "every write a store accepts must still be readable afterwards. Direct mode loses most "
            + "of them here — silently on Linux — which is why the connection string is composed by "
            + "LiteDbConnectionString.ForSharedAccess and not by the store.");
    }

    private static void RunConcurrently(params Action<int>[] writers)
    {
        var ready = new Barrier(writers.Length);
        var threads = writers.Select(writer => Task.Factory.StartNew(
            () =>
            {
                // Start together: the loss only happens while the opens actually overlap.
                ready.SignalAndWait();
                for (var i = 0; i < WritesPerWriter; i++)
                    writer(i);
            },
            TaskCreationOptions.LongRunning)).ToArray();

        Task.WaitAll(threads);
    }

    /// <summary>Counts documents without going through the store's document type or its mapper.</summary>
    private static long RawCount(string path, string collection)
    {
        using var db = new LiteDatabase(LiteDbConnectionString.ForSharedAccess(path));
        return db.GetCollection(collection).LongCount();
    }
}
