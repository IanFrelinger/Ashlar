using System.Collections.Concurrent;
using FluentAssertions;
using LiteDB;
using Ashlar.Commercial.Fleet.Contracts.Models;
using Ashlar.Commercial.Fleet.Infrastructure;
using Xunit;

namespace Ashlar.Commercial.Tests.Fleet;

/// <summary>
/// The mesh task registry must survive concurrent FIRST use of its document type.
/// </summary>
/// <remarks>
/// LiteDB resolved the old <c>EnsureIndex(x =&gt; x.IdempotencyKey)</c> and
/// <c>FindOne(x =&gt; x.IdempotencyKey == key)</c> through <c>BsonMapper.Global</c>, which is
/// process-wide and not safe to drive from several threads at once: the first concurrent touch of a
/// document type threw <c>NotSupportedException</c> out of <c>LinqExpressionVisitor.ResolveMember</c>.
/// The registry's own <c>SemaphoreSlim</c> was never protection — it serialises one instance, and
/// <c>TryGetByIdempotencyKeyAsync</c> is the one method that does not take it at all, so it reads the
/// same collection from an HTTP request thread while <c>CreateAsync</c> holds the lock.
///
/// The mapper is reset so the race window is open on purpose: once a type is fully built the site
/// stops failing for the life of the process, so a test that ran after another class warmed
/// <c>MeshTaskDoc</c> would pin nothing. This class joins <see cref="LiteDbFleetCollection"/>, which
/// already runs alone, because that reset is process-global. What the lookup RETURNS is pinned by
/// <c>LiteDbMeshTaskRegistryGapCoverageTests</c>, deterministically and on a warm mapper.
///
/// Two things this class used to let through, and no longer does.
///
/// It tolerated any exception whose stack passed through LiteDB, on the grounds that the remaining
/// mapper races were LiteDB's to fix and unavoidable at the call site. They were unavoidable at the
/// call site; they were not unavoidable. <c>LiteDbDocumentMapper</c> builds the document type once,
/// single-threaded, before a racer can reach it, so there is nothing left for that hatch to excuse.
/// It also excused precisely the throw the serialize-only version of that warm-up still produced —
/// <c>InvalidOperationException</c> out of <c>BsonMapper.GetTypeCtor</c> under <c>Deserialize</c>,
/// which is why the warm-up now round-trips through <c>ToObject</c> as well.
///
/// And it declined to look at what reached disk, which is the larger half of this defect: a document
/// serialized from a half-built <c>EntityMapper</c> is written with fields missing and the insert
/// returns success. So every raced file is now reopened RAW — the non-generic <c>GetCollection</c>,
/// never through the type's mapper, which would fill a missing field with a default — and checked
/// against a warm single-threaded reference write.
/// </remarks>
[Collection(nameof(LiteDbFleetCollection))]
public sealed class LiteDbMeshTaskRegistryMapperConcurrencyTests : IDisposable
{
    private const int Threads = 8;

    /// <summary>
    /// Rounds of the race per test, each against a mapper reset again.
    /// </summary>
    /// <remarks>
    /// One round is not enough to pin this reliably from inside a test host. Measured in the devtest
    /// container against the pre-fix code: a console harness doing the identical race failed 18 of 20
    /// rounds, while a single-round test passed five runs in a row; at ten rounds the test failed five
    /// runs out of five.
    ///
    /// Twenty rather than that ten, to match the core suite: the corruption assertion below is the
    /// one that has to hold, and throw counts are the timing-sensitive half of the signal.
    /// </remarks>
    private const int Rounds = 20;

    private readonly string _root;

    /// <summary>Initializes a new lite db mesh task registry mapper concurrency tests.</summary>
    public LiteDbMeshTaskRegistryMapperConcurrencyTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"ashlar-mesh-mapper-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    /// <summary>Dispose.</summary>
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }
        catch (IOException)
        {
            // Best-effort temp cleanup; a locked file must not fail the test.
        }
    }

    [Fact]
    public void Concurrent_first_use_neither_throws_nor_writes_short_documents()
    {
        var (errors, shortDocuments) = RaceOnAColdMapper();

        errors.Should().BeEmpty(
            "concurrent first use of MeshTaskDoc must not throw — not NotSupportedException out of " +
            "LinqExpressionVisitor.ResolveMember, and not out of the serializer, the collection " +
            "lookup or the deserializer either");

        shortDocuments.Should().BeEmpty(
            "every mesh task written while racing must carry every field the reference write " +
            "carried. A document serialized from a half-built EntityMapper is written short and the " +
            "insert still returns success, which is how this defect does most of its damage");
    }

    /// <summary>
    /// One racer's work: create a task, then read it back through the unlocked lookup.
    /// </summary>
    /// <remarks>
    /// The read is not decoration. <c>MeshTaskDoc.Affinity</c> is a
    /// <c>Dictionary&lt;string, string&gt;</c> whose own <c>EntityMapper</c> LiteDB builds lazily on
    /// the READ path, in <c>GetTypeCtor</c> — so a warm-up that only serialized left this call a
    /// concurrent first touch, and it threw here in 5 of 8 measured iterations.
    /// </remarks>
    private static void Race(int index, string dbPath)
    {
        var registry = new LiteDbMeshTaskRegistry(dbPath);
        var key = $"idem-{index}";
        registry.CreateAsync(new MeshTaskCreateSpec(
            $"task-{index}",
            1,
            Array.Empty<string>(),
            null,
            0,
            null,
            IdempotencyKey: key)).GetAwaiter().GetResult();

        // The unlocked read path — the one CreateAsync's index declaration races against on another
        // thread.
        registry.TryGetByIdempotencyKeyAsync(key).GetAwaiter().GetResult();
    }

    /// <summary>
    /// The widest document a racer writes when nothing is racing it.
    /// </summary>
    /// <remarks>
    /// A reference rather than a hand-counted constant: a field added to <c>MeshTaskDoc</c> must not
    /// need this file edited, and a constant that drifted low would silently stop detecting anything.
    /// Single threaded, so the mapper it serializes through cannot be half-built.
    /// </remarks>
    private int WidestDocument()
    {
        var referencePath = Path.Combine(_root, "mesh-reference.db");
        Race(0, referencePath);

        using var db = new LiteDatabase($"Filename={referencePath}");
        var widest = 0;
        foreach (var document in db.GetCollection("mesh_tasks").FindAll())
            widest = Math.Max(widest, document.Keys.Count);
        return widest;
    }

    /// <summary>
    /// Drives the registry from <see cref="Threads"/> real threads released together, against a
    /// mapper that has never seen the document type, <see cref="Rounds"/> times, and returns
    /// whatever they threw together with whatever reached disk short.
    /// </summary>
    private (IReadOnlyList<Exception> Errors, IReadOnlyList<string> ShortDocuments) RaceOnAColdMapper()
    {
        var previousMapper = BsonMapper.Global;
        try
        {
            var widest = WidestDocument();
            widest.Should().BeGreaterThan(0, "the reference write must produce a document to compare against");

            var errors = new ConcurrentBag<Exception>();
            var shortDocuments = new List<string>();
            for (var round = 0; round < Rounds; round++)
            {
                var dbPaths = new string[Threads];
                for (var i = 0; i < Threads; i++)
                    // Each thread on its own file: the CI shape, and it keeps LiteDB's per-file lock
                    // out of the way so a failure here can only be the mapper.
                    dbPaths[i] = Path.Combine(_root, $"mesh-{round}-{i}.db");

                // A mapper that has never seen the document type. Every round needs its own, because
                // the previous round left the type fully built and the race only exists while it is not.
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
                                Race(index, dbPath);
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
                    foreach (var document in db.GetCollection("mesh_tasks").FindAll())
                        if (document.Keys.Count < widest)
                            shortDocuments.Add($"{document.Keys.Count}/{widest} keys: [{string.Join(",", document.Keys)}]");
                }
            }

            return (errors.ToList(), shortDocuments);
        }
        finally
        {
            BsonMapper.Global = previousMapper;
        }
    }
}
