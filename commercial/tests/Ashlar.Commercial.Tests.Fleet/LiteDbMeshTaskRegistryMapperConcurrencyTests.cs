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
    /// runs out of five. It still costs a fraction of a second.
    /// </remarks>
    private const int Rounds = 10;

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
            "concurrent first use of MeshTaskDoc must not throw NotSupportedException out of " +
            "LinqExpressionVisitor.ResolveMember");

        errors.Where(e => !CameFromLiteDb(e)).Should().BeEmpty(
            "nothing outside LiteDB should have failed while racing the mesh task registry");
    }

    /// <summary>
    /// Drives the registry from <see cref="Threads"/> real threads released together, against a
    /// mapper that has never seen the document type, <see cref="Rounds"/> times, and returns
    /// whatever they threw.
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
                    // Each thread on its own file: the CI shape, and it keeps LiteDB's per-file lock
                    // out of the way so a failure here can only be the mapper.
                    var dbPath = Path.Combine(_root, $"mesh-{round}-{index}.db");
                    // LongRunning so these are real threads rather than pool work items that could be
                    // serialised onto one thread and never overlap.
                    racers[i] = Task.Factory.StartNew(
                        () =>
                        {
                            start.SignalAndWait();
                            try
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

                                // The unlocked read path — the one CreateAsync's index declaration
                                // races against on another thread.
                                registry.TryGetByIdempotencyKeyAsync(key).GetAwaiter().GetResult();
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
