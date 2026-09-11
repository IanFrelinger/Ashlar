using System.Collections.Concurrent;
using FluentAssertions;
using LiteDB;
using Ashlar.BackgroundAgents.Trust;
using Xunit;

namespace Ashlar.Tests.BackgroundAgents.Trust;

/// <summary>
/// Tests that deliberately reset <c>BsonMapper.Global</c> run alone.
/// </summary>
/// <remarks>
/// The mapper is process-wide static state shared by every LiteDB store in the assembly; swapping it
/// out under a test running in parallel would hand that test the race these tests exist to disprove.
/// </remarks>
[CollectionDefinition("LiteDbMapper", DisableParallelization = true)]
public sealed class LiteDbMapperCollection;

/// <summary>
/// The audit log must survive concurrent FIRST use of its document type.
/// </summary>
/// <remarks>
/// This is the highest-exposure LiteDB site in the repo: one shared instance is registered behind
/// both <c>IDataDecisionAuditLog</c> and <c>ISanitizationAuditLog</c>, it buffers through a
/// <c>ConcurrentQueue</c> — so it is built for concurrent callers by design — and any caller crossing
/// the flush threshold runs the flush inline. LiteDB resolved the old
/// <c>EnsureIndex(x =&gt; x.Timestamp)</c> through <c>BsonMapper.Global</c>, which is process-wide and
/// not safe to drive from several threads at once; the first concurrent touch of a document type
/// threw <c>NotSupportedException</c> out of <c>LinqExpressionVisitor.ResolveMember</c>.
///
/// The mapper is reset so the race window is open on purpose — once a type is fully built the site
/// stops failing for the life of the process, so a test that waited its turn would pin nothing.
/// Each thread gets its own database file, which is the shape CI had and keeps LiteDB's per-file
/// lock out of the way: a failure here can only be the mapper. What the log RETURNS is pinned by
/// <c>LiteDbDataDecisionAuditLogTests</c>, deterministically and on a warm mapper.
///
/// Throws are not what this defect mostly does, which is why asserting on them alone left this class
/// proving nothing: with <c>LiteDbDocumentMapper.EnsureMapped</c> neutered, every test in this
/// assembly stayed green on both frameworks, while a console harness at 60 rounds x 8 threads
/// against that same unwarmed code wrote 3,890 <c>AuditDoc</c>s with fields missing for 211 throws.
/// So the race now asserts the way its sibling in <c>Ashlar.Tests.Infrastructure</c> does — enough
/// rounds to catch it from inside a test host, and enough entries per thread to cross the buffer's
/// flush threshold while other threads are still building the type — and every raced file is
/// reopened RAW and compared against a warm single-threaded reference write.
/// </remarks>
[Collection("LiteDbMapper")]
public sealed class LiteDbDataDecisionAuditLogMapperConcurrencyTests : IDisposable
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
    /// Sixty rather than that ten, and sixty rather than the twenty its sibling in
    /// Ashlar.Tests.Infrastructure spends, because this type is the hardest of the set to catch from
    /// inside a test host: each racer must open its own database before it can touch the mapper, and
    /// that open staggers the threads. Measured against a neutered warm-up in the devtest container,
    /// one raced round in eight threads: at twenty rounds the run went red two times in three on
    /// net8.0, at sixty it went red four times in four. Each round is another chance, and a round
    /// costs milliseconds.
    /// </remarks>
    private const int Rounds = 60;

    /// <summary>
    /// Entries each racer appends before it reads.
    /// </summary>
    /// <remarks>
    /// More than one, and more than the class's ten-entry flush threshold, on purpose. A single
    /// buffered entry reaches disk only when the closing <c>GetRecent</c> flushes it, by which time
    /// the racers have largely finished building the type; crossing the threshold puts an inline
    /// flush — a real serialize-and-insert — inside the window where they have not.
    /// </remarks>
    private const int EntriesPerThread = 12;

    private static readonly DateTimeOffset Anchor = new(2024, 5, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly string _root;

    /// <summary>Initializes a new lite db data decision audit log mapper concurrency tests.</summary>
    public LiteDbDataDecisionAuditLogMapperConcurrencyTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"ashlar-dd-audit-mapper-{Guid.NewGuid():N}");
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

    /// <summary>
    /// Concurrent first use of the audit document type must not throw at all.
    /// </summary>
    /// <remarks>
    /// This assertion used to tolerate anything whose stack passed through LiteDB, because the
    /// remaining mapper races were inside LiteDB's own document conversion and collection lookup and
    /// no call-site spelling reached them: <c>InvalidOperationException("Collection was modified")</c>
    /// out of <c>BsonMapper.SerializeObject</c> under <c>Insert</c> and out of
    /// <c>EntityMapper.get_Id</c> under <c>GetCollection&lt;T&gt;</c>, and
    /// <c>InvalidCastException</c> out of <c>BsonMapper.DeserializeObject</c> on the read path.
    ///
    /// <c>LiteDbDocumentMapper</c> removes the concurrent first touch that produced all of them, so
    /// the hatch has nothing left to excuse and is gone. An exception from LiteDB here now means the
    /// warm-up stopped covering this type — which is exactly what a future document type with a
    /// nested element type would look like.
    /// </remarks>
    [Fact]
    public void Concurrent_first_use_neither_throws_nor_writes_short_entries()
    {
        var (errors, shortDocuments) = RaceOnAColdMapper();

        errors.Should().BeEmpty(
            "concurrent first use of the audit document type must not throw — not out of " +
            "LinqExpressionVisitor.ResolveMember, and not out of the serializer, the collection " +
            "lookup or the deserializer either");

        shortDocuments.Should().BeEmpty(
            "every audit entry written while racing must carry every field the reference write " +
            "carried. An entry serialized from a half-built EntityMapper reaches disk with fields " +
            "missing and its insert still returns success — the half of this defect a throw-only " +
            "assertion cannot see, and the half that leaves a compliance record silently wrong");
    }

    /// <summary>
    /// The widest entry this race writes when nothing is racing it.
    /// </summary>
    /// <remarks>
    /// A reference rather than a hand-counted constant: a field added to the entry type must not need
    /// this file edited, and a constant that drifted low would silently stop detecting anything.
    /// Single threaded, so the mapper it serializes through cannot be half-built whether or not the
    /// warm-up is in place — which is what makes it a fair yardstick for the raced writes.
    /// </remarks>
    private int WidestEntry()
    {
        var referencePath = Path.Combine(_root, "audit-reference.db");
        Append(0, referencePath);

        using var db = new LiteDatabase($"Filename={referencePath};Connection=Shared");
        var widest = 0;
        foreach (var document in db.GetCollection("data_decision_audit").FindAll())
            widest = Math.Max(widest, document.Keys.Count);
        return widest;
    }

    /// <summary>
    /// One racer's work: <see cref="EntriesPerThread"/> appends, then the read that flushes the tail.
    /// </summary>
    private static void Append(int index, string dbPath)
    {
        var log = new LiteDbDataDecisionAuditLog(dbPath);
        for (var entry = 0; entry < EntriesPerThread; entry++)
            log.LogRedaction(Anchor, "v1", $"field-{index}-{entry}", "redacted", "pii");

        // GetRecent flushes the buffer first, so this drives the write path and the converted query
        // in one call.
        log.GetRecent(10, Anchor.AddMinutes(-1), Anchor.AddMinutes(1), "Sanitization");
    }

    /// <summary>
    /// Drives the log from <see cref="Threads"/> real threads released together, against a mapper
    /// that has never seen the document type, <see cref="Rounds"/> times, and returns whatever they
    /// threw together with whatever reached disk short.
    /// </summary>
    private (IReadOnlyList<Exception> Errors, IReadOnlyList<string> ShortDocuments) RaceOnAColdMapper()
    {
        var previousMapper = BsonMapper.Global;
        try
        {
            var widest = WidestEntry();
            widest.Should().BeGreaterThan(0, "the reference write must produce an entry to compare against");

            var errors = new ConcurrentBag<Exception>();
            var shortDocuments = new List<string>();
            for (var round = 0; round < Rounds; round++)
            {
                var dbPaths = new string[Threads];
                for (var i = 0; i < Threads; i++)
                    dbPaths[i] = Path.Combine(_root, $"audit-{round}-{i}.db");

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
                                Append(index, dbPath);
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
                // never wrote with its default and report the entry as intact.
                foreach (var dbPath in dbPaths)
                {
                    if (!File.Exists(dbPath)) continue;

                    using var db = new LiteDatabase($"Filename={dbPath};Connection=Shared");
                    foreach (var document in db.GetCollection("data_decision_audit").FindAll())
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
