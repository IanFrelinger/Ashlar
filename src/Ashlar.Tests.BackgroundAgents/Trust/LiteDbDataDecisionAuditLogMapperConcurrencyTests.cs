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
    /// runs out of five. It still costs a fraction of a second.
    /// </remarks>
    private const int Rounds = 10;
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

    [Fact]
    public void Concurrent_first_use_does_not_throw_out_of_the_bson_mapper()
    {
        var errors = RaceOnAColdMapper();

        errors.Should().NotContain(
            e => e is NotSupportedException,
            "concurrent first use of the audit document type must not throw NotSupportedException " +
            "out of LinqExpressionVisitor.ResolveMember");

        errors.Where(e => !CameFromLiteDb(e)).Should().BeEmpty(
            "nothing outside LiteDB should have failed while racing the audit log");
    }

    /// <summary>
    /// Drives the log from <see cref="Threads"/> real threads released together, against a mapper
    /// that has never seen the document type, <see cref="Rounds"/> times, and returns whatever
    /// they threw.
    /// </summary>
    private IReadOnlyList<Exception> RaceOnAColdMapper()
    {
        var previousMapper = BsonMapper.Global;
        try
        {
            var errors = new ConcurrentBag<Exception>();
            for (var round = 0; round < Rounds; round++)
            {
                // A mapper that has never seen the document type. Every round needs its own, because
                // the previous round left the type fully built and the race only exists while it is not.
                BsonMapper.Global = new BsonMapper();

                using var start = new Barrier(Threads);
                var racers = new Task[Threads];
                for (var i = 0; i < Threads; i++)
                {
                    var index = i;
                    var dbPath = Path.Combine(_root, $"audit-{round}-{index}.db");
                    // LongRunning so these are real threads rather than pool work items that could be
                    // serialised onto one thread and never overlap.
                    racers[i] = Task.Factory.StartNew(
                        () =>
                        {
                            start.SignalAndWait();
                            try
                            {
                                var log = new LiteDbDataDecisionAuditLog(dbPath);
                                log.LogRedaction(Anchor, "v1", $"field-{index}", "redacted", "pii");

                                // GetRecent flushes the buffer first, so this drives the write path
                                // and the converted query in one call.
                                log.GetRecent(10, Anchor.AddMinutes(-1), Anchor.AddMinutes(1), "Sanitization");
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

            return errors.ToList();
        }
        finally
        {
            BsonMapper.Global = previousMapper;
        }
    }
}
