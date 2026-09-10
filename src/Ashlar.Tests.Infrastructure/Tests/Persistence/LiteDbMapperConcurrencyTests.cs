using System.Collections.Concurrent;
using FluentAssertions;
using LiteDB;
using Ashlar.Core.Application.Adaptation.Models;
using Ashlar.Core.Application.Copilot.Models;
using Ashlar.Core.Application.Observation.Models;
using Ashlar.Core.Application.Pipelines.Models;
using Ashlar.Core.Application.SelfContext.Models;
using Ashlar.Core.Application.Trust.Models;
using Ashlar.Infrastructure.Adaptation;
using Ashlar.Infrastructure.Copilot;
using Ashlar.Infrastructure.Observation;
using Ashlar.Infrastructure.Pipelines;
using Ashlar.Infrastructure.SelfContext;
using Ashlar.Infrastructure.Trust;
using Ashlar.Tests.Infrastructure.Helpers;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Persistence;

/// <summary>
/// Every LiteDB-backed store must survive concurrent FIRST use of its document type.
/// </summary>
/// <remarks>
/// Found in CI (PR #583, macOS lane): a single-threaded round-trip test failed with
/// <c>NotSupportedException: Member Timestamp not found on BsonMapper for type
/// LiteDbTestFailureStore+TestFailureDoc</c> out of <c>LinqExpressionVisitor.ResolveMember</c>, and the
/// same commit passed in the other run of the same workflow. LiteDB resolves an
/// <c>EnsureIndex(x =&gt; x.Field)</c> — and a LINQ <c>Where</c> — through <c>BsonMapper.Global</c>,
/// which is process-wide and not safe to drive from several threads at once. Nothing in the failing
/// test was concurrent; three classes in this assembly first-touch <c>TestFailureDoc</c> and xunit
/// runs two at a time, so the concurrency came from the runner.
///
/// The race is on the COLD mapper — the first concurrent touch of a document type, where whichever
/// thread wins decides the mapping. Once the type is fully built the site stops failing for the life
/// of the process, which is exactly why the failure was non-deterministic. So each test resets
/// <c>BsonMapper.Global</c> rather than hoping the mapper is still cold by the time it runs; without
/// that these tests would pin nothing whenever another class warmed the type first. That reset is
/// process-global, which is why the class sits in a collection that runs alone.
///
/// Each thread gets its OWN database file. That is the shape CI had — separate classes, separate
/// files, one shared mapper — and it keeps LiteDB's per-file lock out of the way, so a failure here
/// can only be the mapper.
///
/// The racers exercise the store and do not assert on what comes back. What each store RETURNS is
/// pinned by its own round-trip tests, deterministically and on a warm mapper; asserting it again
/// from inside the race would only re-report one of LiteDB's remaining mapper races as an assertion
/// failure, because that gap can write a partially serialized document rather than throwing.
/// </remarks>
[Collection("LiteDbMapper")]
[Trait("Category", "Integration")]
public sealed class LiteDbMapperConcurrencyTests : TempDirTestBase
{
    private const int Threads = 8;

    /// <summary>
    /// Rounds of the race per test, each against a mapper reset again.
    /// </summary>
    /// <remarks>
    /// One round is not enough to pin this reliably from inside a test host. Measured in the devtest
    /// container against the pre-fix code: a console harness doing the identical race failed 18 of 20
    /// rounds, while a single-round test passed five runs in a row. Ten rounds closes that gap without
    /// making the suite slow — the whole class runs in well under a second.
    /// </remarks>
    private const int Rounds = 10;

    private static readonly DateTimeOffset Anchor = new(2024, 5, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Initializes a new lite db mapper concurrency tests.</summary>
    public LiteDbMapperConcurrencyTests() : base("ashlar-litedb-mapper-concurrency")
    {
    }

    /// <summary>
    /// True when an exception came out of LiteDB itself rather than out of the store or this test.
    /// </summary>
    /// <remarks>
    /// LiteDB 5.0.21 publishes a type's <c>EntityMapper</c> into its cache before that mapper's member
    /// list is filled, so a thread arriving while it is half-built can fail in at least three places:
    /// <c>NotSupportedException</c> out of <c>LinqExpressionVisitor.ResolveMember</c>;
    /// <c>InvalidOperationException("Collection was modified")</c> out of
    /// <c>BsonMapper.SerializeObject</c>, under <c>LiteCollection.Insert</c>; and the same exception
    /// out of <c>EntityMapper.get_Id</c>, under <c>LiteDatabase.GetCollection&lt;T&gt;</c>. Only the
    /// first goes through an expression, so only the first can be avoided at the call site — and that
    /// is the one CI hit and the one asserted on by name below. The other two are inside LiteDB's own
    /// document conversion and collection lookup, every caller reaches them, and they were already
    /// firing underneath the <c>NotSupportedException</c> before this fix.
    ///
    /// So the second assertion tolerates them by ORIGIN rather than by exception shape, which would
    /// mean chasing each new manifestation. Anything raised outside LiteDB — the store, or an
    /// assertion inside a racer — still fails.
    /// </remarks>
    private static bool CameFromLiteDb(Exception ex) =>
        ex.StackTrace?.Contains("LiteDB.", StringComparison.Ordinal) == true;

    /// <summary>
    /// Runs <paramref name="work"/> on <see cref="Threads"/> real threads released together, against a
    /// mapper that has never seen the document type, <see cref="Rounds"/> times, and fails if any of
    /// them threw.
    /// </summary>
    /// <param name="seed">
    /// Optional per-thread setup, run single-threaded and BEFORE the mapper is reset, so a racer can
    /// find a populated database without having written it itself. Whatever a racer does first builds
    /// the document type's mapper for the rest of that racer's work, so a call path can only be raced
    /// cold if everything it needs on disk was put there outside the race.
    /// </param>
    private void RaceOnAColdMapper(string what, Action<int, string> work, Action<int, string>? seed = null)
    {
        var previousMapper = BsonMapper.Global;
        try
        {
            var errors = new ConcurrentBag<Exception>();
            for (var round = 0; round < Rounds; round++)
            {
                var dbPaths = new string[Threads];
                for (var i = 0; i < Threads; i++)
                    dbPaths[i] = Path.Combine(TempDir, $"{what}-{round}-{i}.db");

                if (seed is not null)
                    for (var i = 0; i < Threads; i++)
                        seed(i, dbPaths[i]);

                // A mapper that has never seen the document type. Every round needs its own, because
                // the previous round left the type fully built and the race only exists while it is not
                // -- and because seeding, where a case does it, built the type as well.
                BsonMapper.Global = new BsonMapper();

                using var start = new Barrier(Threads);
                var racers = new Task[Threads];
                for (var i = 0; i < Threads; i++)
                {
                    var index = i;
                    var dbPath = dbPaths[index];
                    // LongRunning so these are real threads rather than pool work items that could be
                    // serialised onto one thread and never overlap.
                    racers[i] = Task.Factory.StartNew(
                        () =>
                        {
                            start.SignalAndWait();
                            try
                            {
                                work(index, dbPath);
                            }
                            catch (Exception ex)
                            {
                                errors.Add(ex);
                            }
                        },
                        TaskCreationOptions.LongRunning);
                }

                Task.WaitAll(racers);
            }

            errors.Should().NotContain(
                e => e is NotSupportedException,
                "{0} must survive concurrent first use of its document type — a LINQ EnsureIndex or " +
                "Where here throws NotSupportedException out of LinqExpressionVisitor.ResolveMember, " +
                "which is the failure that took CI red",
                what);

            errors.Where(e => !CameFromLiteDb(e)).Should().BeEmpty(
                "nothing outside LiteDB should have failed while racing {0}", what);
        }
        finally
        {
            BsonMapper.Global = previousMapper;
        }
    }

    [Fact]
    public void LiteDbTestFailureStore_survives_concurrent_first_use()
    {
        // The store that actually failed CI.
        RaceOnAColdMapper("test-failure", (index, dbPath) =>
        {
            var store = new LiteDbTestFailureStore(dbPath);
            store.RecordAsync(new TestFailureRecord
            {
                Id = $"fail-{index}",
                Timestamp = Anchor,
                TestName = "concurrent probe",
            }).GetAwaiter().GetResult();

            store.QueryAsync(Anchor.AddMinutes(-1), Anchor.AddMinutes(1)).GetAwaiter().GetResult();
        });
    }

    /// <summary>
    /// The same store, raced by threads that only READ.
    /// </summary>
    /// <remarks>
    /// Every other case here writes before it reads, and that write builds the document type's mapper
    /// -- so the read runs warm and pins nothing about how the query is expressed. This case seeds
    /// each thread's file outside the race and then resets the mapper, so <c>QueryAsync</c> is the
    /// first thing to touch the type. Measured against the LINQ form of that query: it throws
    /// <c>NotSupportedException</c> here while every other case in this class stays green, which is
    /// what makes the <c>Where("$.Field ...")</c> and <c>OrderByDescending("$.Field")</c> rewrites
    /// load-bearing rather than defensive.
    ///
    /// A read-only race is the real deployment shape too, not a test contrivance:
    /// <c>SelfImprovementLoop</c> queries a history that another process wrote, so its mapper is cold
    /// for a document type that only ever arrives from disk.
    ///
    /// One store carries this. The mechanism is the shared <c>BsonMapper.Global</c> and it is the same
    /// for all nine converted queries; repeating the case per store would multiply runtime without
    /// exercising anything new.
    /// </remarks>
    [Fact]
    public void LiteDbTestFailureStore_survives_concurrent_first_use_that_only_reads()
    {
        RaceOnAColdMapper(
            "test-failure-read-only",
            (_, dbPath) =>
            {
                var store = new LiteDbTestFailureStore(dbPath);
                store.QueryAsync(Anchor.AddMinutes(-1), Anchor.AddMinutes(1)).GetAwaiter().GetResult();
            },
            seed: (index, dbPath) =>
            {
                var store = new LiteDbTestFailureStore(dbPath);
                store.RecordAsync(new TestFailureRecord
                {
                    Id = $"fail-{index}",
                    Timestamp = Anchor,
                    TestName = "seeded before the race",
                }).GetAwaiter().GetResult();
            });
    }

    [Fact]
    public void LiteDbExecutionTracer_survives_concurrent_first_use()
    {
        RaceOnAColdMapper("execution-tracer", (index, dbPath) =>
        {
            var tracer = new LiteDbExecutionTracer(dbPath);
            tracer.TraceAsync($"op-{index}").GetAwaiter().GetResult();

            tracer.QueryAsync(Anchor).GetAwaiter().GetResult();
        });
    }

    [Fact]
    public void LiteDbAdaptationLog_survives_concurrent_first_use()
    {
        RaceOnAColdMapper("adaptation-log", (index, dbPath) =>
        {
            var log = new LiteDbAdaptationLog(dbPath);
            log.LogAsync(new AdaptationRecord
            {
                Id = $"rec-{index}",
                Timestamp = Anchor,
                BrickId = $"brick-{index}",
                FailureType = "compile",
                FixApplied = AdaptationFixType.Source,
            }).GetAwaiter().GetResult();

            log.QueryAsync(Anchor.AddMinutes(-1), Anchor.AddMinutes(1), $"brick-{index}")
                .GetAwaiter().GetResult();
        });
    }

    [Fact]
    public void LiteDbAdaptationAuditLog_survives_concurrent_first_use()
    {
        RaceOnAColdMapper("adaptation-audit", (index, dbPath) =>
        {
            var log = new LiteDbAdaptationAuditLog(dbPath);
            log.LogAsync(new AdaptationAuditEntry
            {
                Id = $"audit-{index}",
                Timestamp = Anchor,
                AutonomyLevel = "Supervised",
                Outcome = "applied",
            }).GetAwaiter().GetResult();

            log.QueryAsync(Anchor.AddMinutes(-1), Anchor.AddMinutes(1)).GetAwaiter().GetResult();
        });
    }

    [Fact]
    public void LiteDbPatternStore_survives_concurrent_first_use()
    {
        // The store with genuine hosted-service-versus-HTTP concurrency: ObservationPipelineService
        // writes while KnowledgeQueryService reads behind GET /knowledge/query.
        RaceOnAColdMapper("pattern-store", (index, dbPath) =>
        {
            var store = new LiteDbPatternStore(dbPath);
            store.AddAsync(new ObservedPattern
            {
                PatternId = $"pattern-{index}",
                EventType = "build",
                FirstSeen = Anchor,
                LastSeen = Anchor.AddMinutes(1),
                ProjectPath = $"/proj/{index}",
            }).GetAwaiter().GetResult();

            store.QueryAsync(new PatternStoreQueryParams
            {
                Since = Anchor,
                Until = Anchor.AddMinutes(5),
                EventType = "build",
                ProjectPath = $"/proj/{index}",
            }).GetAwaiter().GetResult();
        });
    }

    [Fact]
    public void LiteDbPatternProcessedStore_survives_concurrent_first_use()
    {
        RaceOnAColdMapper("pattern-processed", (index, dbPath) =>
        {
            var store = new LiteDbPatternProcessedStore(dbPath);
            store.MarkProcessedAsync($"pattern-{index}").GetAwaiter().GetResult();

            store.IsProcessedAsync($"pattern-{index}").GetAwaiter().GetResult();
        });
    }

    [Fact]
    public void LiteDbPipelineRunStore_survives_concurrent_first_use()
    {
        // Its own lock only serialises this instance; another store type driving the shared mapper
        // concurrently is what this reproduces.
        RaceOnAColdMapper("pipeline-run", (index, dbPath) =>
        {
            var store = new LiteDbPipelineRunStore(dbPath);
            store.SaveAsync(new PipelineRun
            {
                RunId = $"run-{index}",
                TemplateId = "template",
                State = PipelineRunState.Running,
                StartedAt = Anchor,
            }).GetAwaiter().GetResult();

            store.GetAsync($"run-{index}").GetAwaiter().GetResult();
        });
    }

    [Fact]
    public void LiteDbUserKnowledgeLogStore_survives_concurrent_first_use()
    {
        RaceOnAColdMapper("user-knowledge", (index, dbPath) =>
        {
            var store = new LiteDbUserKnowledgeLogStore(dbPath);
            store.UpsertAsync(new UserKnowledgeLogEntry
            {
                Id = $"entry-{index}",
                DataType = "preference",
                Content = "content",
            }).GetAwaiter().GetResult();

            store.GetByIdAsync($"entry-{index}").GetAwaiter().GetResult();
            store.GetAsync("preference").GetAwaiter().GetResult();
        });
    }

    [Fact]
    public void LiteDbCopilotTaskStore_survives_concurrent_first_use()
    {
        // The store the pattern came from. It is already converted; this keeps it that way.
        RaceOnAColdMapper("copilot-task", (index, dbPath) =>
        {
            var store = new LiteDbCopilotTaskStore(dbPath);
            store.StoreAsync(new CopilotTaskRecord
            {
                TaskId = $"task-{index}",
                TenantId = "default",
                Task = "concurrent probe",
                SubmittedAt = Anchor,
                Success = true,
            }).GetAwaiter().GetResult();

            store.QueryAsync(maxCount: 10, since: Anchor.AddMinutes(-1)).GetAwaiter().GetResult();
        });
    }
}
