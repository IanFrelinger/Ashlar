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
/// Two things this class used to let through, and no longer does.
///
/// It tolerated any exception whose stack passed through LiteDB, on the grounds that the remaining
/// mapper races were LiteDB's to fix and unavoidable at the call site. They were unavoidable at the
/// call site; they were not unavoidable. <c>LiteDbDocumentMapper</c> builds each document type once,
/// single-threaded, before a racer can reach it, so there is nothing left for that hatch to excuse
/// and every exception now fails the test.
///
/// And it declined to look at what reached disk, because the gap could write a partially serialized
/// document rather than throwing. That was the larger half of the defect: measured at these
/// dimensions, the stores threw 451 times on net8.0 while writing 6,357 documents with fields
/// missing, and the inserts that produced them returned success. So every racer now names its
/// collection and every raced file is reopened RAW — the non-generic <c>GetCollection</c>, never
/// through the type's mapper, which would happily fill a missing field with a default — and checked
/// against a warm single-threaded reference write.
///
/// The per-round reset also guards the warm-up's own shape. <c>LiteDbDocumentMapper</c> keys what it
/// has built on the mapper INSTANCE rather than a one-shot flag; anyone simplifying that to a
/// <c>bool</c> or a <c>Lazy&lt;T&gt;</c> would leave rounds two onward racing a cold mapper it
/// believed was warm, and these tests go red rather than quietly proving nothing.
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
    /// rounds, while a single-round test passed five runs in a row.
    ///
    /// Twenty, not the ten this class started with, because the corruption assertion below is the one
    /// that has to hold. Throw counts are timing-sensitive and were modest even in the harness; short
    /// documents arrived in the thousands and at the same order of magnitude on both TFMs, so the
    /// rounds are spent where the signal is. The class costs about ten seconds, which is why it
    /// carries the Integration trait.
    /// </remarks>
    private const int Rounds = 20;

    private static readonly DateTimeOffset Anchor = new(2024, 5, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Initializes a new lite db mapper concurrency tests.</summary>
    public LiteDbMapperConcurrencyTests() : base("ashlar-litedb-mapper-concurrency")
    {
    }

    /// <summary>
    /// The widest document this case writes when nothing is racing it.
    /// </summary>
    /// <remarks>
    /// A reference, not a hand-counted constant: adding a field to a document type must not need this
    /// file edited, and a constant that drifted low would silently stop detecting anything. Single
    /// threaded, so the mapper it serializes through cannot be half-built whether or not the warm-up
    /// is in place — which is what makes it a fair yardstick for the raced writes.
    ///
    /// The <paramref name="writer"/> is the seed where a case has one, because a read-only racer
    /// writes nothing and its raced files hold whatever the seed put there.
    /// </remarks>
    private int WidestDocument(string what, string collection, Action<int, string> writer)
    {
        var referencePath = Path.Combine(TempDir, $"{what}-reference.db");
        writer(0, referencePath);

        using var db = new LiteDatabase($"Filename={referencePath}");
        var widest = 0;
        foreach (var document in db.GetCollection(collection).FindAll())
            widest = Math.Max(widest, document.Keys.Count);
        return widest;
    }

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
    private void RaceOnAColdMapper(string what, string collection, Action<int, string> work, Action<int, string>? seed = null)
    {
        var previousMapper = BsonMapper.Global;
        try
        {
            var widest = WidestDocument(what, collection, seed ?? work);
            widest.Should().BeGreaterThan(0, "the reference write for {0} must produce a document to compare against", what);

            var errors = new ConcurrentBag<Exception>();
            var shortDocuments = new List<string>();
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

                // Raw, never through the type's mapper: deserializing would fill a field the writer
                // never wrote with its default and report the document as intact.
                foreach (var dbPath in dbPaths)
                {
                    if (!File.Exists(dbPath)) continue;

                    using var db = new LiteDatabase($"Filename={dbPath}");
                    foreach (var document in db.GetCollection(collection).FindAll())
                        if (document.Keys.Count < widest)
                            shortDocuments.Add($"{document.Keys.Count}/{widest} keys: [{string.Join(",", document.Keys)}]");
                }
            }

            errors.Should().BeEmpty(
                "{0} must survive concurrent first use of its document type. Before the warm-up this " +
                "threw out of BsonMapper.SerializeObject under Insert, out of EntityMapper.get_Id " +
                "under GetCollection<T>, and out of BsonMapper.DeserializeObject on the read path — " +
                "none of which a call site can spell its way around",
                what);

            shortDocuments.Should().BeEmpty(
                "every document {0} wrote while racing must carry every field the reference write " +
                "carried. A document serialized from a half-built EntityMapper is written short and " +
                "the insert still returns success, which is how this defect does most of its damage",
                what);
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
        RaceOnAColdMapper("test-failure", "test_failures", (index, dbPath) =>
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
            "test_failures",
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
        RaceOnAColdMapper("execution-tracer", "execution_traces", (index, dbPath) =>
        {
            var tracer = new LiteDbExecutionTracer(dbPath);
            tracer.TraceAsync($"op-{index}").GetAwaiter().GetResult();

            tracer.QueryAsync(Anchor).GetAwaiter().GetResult();
        });
    }

    [Fact]
    public void LiteDbAdaptationLog_survives_concurrent_first_use()
    {
        RaceOnAColdMapper("adaptation-log", "adaptation_records", (index, dbPath) =>
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
        RaceOnAColdMapper("adaptation-audit", "adaptation_audit", (index, dbPath) =>
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
        RaceOnAColdMapper("pattern-store", "observed_patterns", (index, dbPath) =>
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
        RaceOnAColdMapper("pattern-processed", "processed_patterns", (index, dbPath) =>
        {
            var store = new LiteDbPatternProcessedStore(dbPath);
            store.MarkProcessedAsync($"pattern-{index}").GetAwaiter().GetResult();

            store.IsProcessedAsync($"pattern-{index}").GetAwaiter().GetResult();
        });
    }

    /// <summary>
    /// The pipeline store, saving a run that HAS a stage run.
    /// </summary>
    /// <remarks>
    /// The stage runs are not decoration. <c>PipelineStageRunDocument</c> is a second document type
    /// that appears in no <c>GetCollection&lt;T&gt;</c> — LiteDB only ever meets it as an element of
    /// <c>PipelineRunDocument.StageRuns</c> — so serializing a run with an empty list never touches
    /// it, and a warm-up that covered only the collection type would leave it cold. Measured: warming
    /// the parent alone still wrote 146-160 of ~480 raced stage sub-documents with fields missing,
    /// and on net10.0 without throwing once. A version of this test that saved a bare run passed
    /// throughout.
    /// </remarks>
    [Fact]
    public void LiteDbPipelineRunStore_survives_concurrent_first_use()
    {
        // Its own lock only serialises this instance; another store type driving the shared mapper
        // concurrently is what this reproduces.
        RaceOnAColdMapper("pipeline-run", "pipeline_runs", (index, dbPath) =>
        {
            var store = new LiteDbPipelineRunStore(dbPath);
            store.SaveAsync(new PipelineRun
            {
                RunId = $"run-{index}",
                TemplateId = "template",
                State = PipelineRunState.Running,
                StartedAt = Anchor,
                StageRuns = new[]
                {
                    new PipelineStageRun
                    {
                        StageId = "stage-1",
                        State = PipelineStageRunState.Completed,
                        Attempt = 1,
                        WorkerId = "worker",
                        WorkerType = PipelineWorkerType.Agentic,
                        Output = "output",
                    },
                },
            }).GetAwaiter().GetResult();

            store.GetAsync($"run-{index}").GetAwaiter().GetResult();
        });
    }

    /// <summary>
    /// The nested stage-run documents must be whole too, which the parent collection's key count
    /// cannot tell you.
    /// </summary>
    /// <remarks>
    /// <c>RaceOnAColdMapper</c> compares the keys of the documents in a collection, and a
    /// <c>PipelineRunDocument</c> whose <c>StageRuns</c> array is full of half-serialized elements
    /// has exactly the same key count as a whole one. This walks into the array instead. It is the
    /// case that separates warming <c>PipelineStageRunDocument</c> from forgetting to.
    /// </remarks>
    [Fact]
    public void LiteDbPipelineRunStore_writes_whole_stage_runs_while_racing()
    {
        var (totalStages, thinStages) = RaceStageRunsOnAColdMapper();

        totalStages.Should().Be(
            Rounds * Threads,
            "every racer saves exactly one stage run, and a run that reached disk without its " +
            "StageRuns array is itself the corruption this is looking for");

        thinStages.Should().BeEmpty(
            "PipelineStageRunDocument is reached only as an element of PipelineRunDocument, so it " +
            "needs its own warm-up — warming the parent alone left 146-160 of ~480 stage " +
            "sub-documents short, silently");
    }

    /// <summary>
    /// Races the pipeline store saving runs that carry a stage run, and reports how many stage
    /// sub-documents reached disk and how many of those were short.
    /// </summary>
    /// <remarks>
    /// A helper rather than inline in the test, because xUnit1031 rejects a blocking wait inside a
    /// test method — and an async test would not do: these racers must be real threads that overlap,
    /// which is the whole point.
    /// </remarks>
    private (int TotalStages, IReadOnlyList<string> ThinStages) RaceStageRunsOnAColdMapper()
    {
        var previousMapper = BsonMapper.Global;
        try
        {
            var thinStages = new List<string>();
            var totalStages = 0;
            for (var round = 0; round < Rounds; round++)
            {
                var dbPaths = new string[Threads];
                for (var i = 0; i < Threads; i++)
                    dbPaths[i] = Path.Combine(TempDir, $"pipeline-stage-{round}-{i}.db");

                BsonMapper.Global = new BsonMapper();

                using var start = new Barrier(Threads);
                var racers = new Task[Threads];
                for (var i = 0; i < Threads; i++)
                {
                    var index = i;
                    var dbPath = dbPaths[index];
                    racers[i] = Task.Factory.StartNew(
                        () =>
                        {
                            start.SignalAndWait();
                            var store = new LiteDbPipelineRunStore(dbPath);
                            store.SaveAsync(new PipelineRun
                            {
                                RunId = $"run-{index}",
                                TemplateId = "template",
                                State = PipelineRunState.Running,
                                StartedAt = Anchor,
                                StageRuns = new[]
                                {
                                    new PipelineStageRun
                                    {
                                        StageId = "stage-1",
                                        State = PipelineStageRunState.Completed,
                                        Attempt = 1,
                                        WorkerId = "worker",
                                        WorkerType = PipelineWorkerType.Agentic,
                                        Output = "output",
                                        Error = "error",
                                    },
                                },
                            }).GetAwaiter().GetResult();
                        },
                        TaskCreationOptions.LongRunning);
                }

                Task.WaitAll(racers);

                foreach (var dbPath in dbPaths)
                {
                    if (!File.Exists(dbPath)) continue;

                    using var db = new LiteDatabase($"Filename={dbPath}");
                    foreach (var document in db.GetCollection("pipeline_runs").FindAll())
                    {
                        if (!document.ContainsKey("StageRuns")) continue;

                        foreach (var stage in document["StageRuns"].AsArray)
                        {
                            totalStages++;
                            var keys = stage.AsDocument.Keys;
                            // Seven properties on PipelineStageRunDocument, all of them set above.
                            if (keys.Count < 7)
                                thinStages.Add($"{keys.Count}/7 keys: [{string.Join(",", keys)}]");
                        }
                    }
                }
            }

            return (totalStages, thinStages);
        }
        finally
        {
            BsonMapper.Global = previousMapper;
        }
    }

}
