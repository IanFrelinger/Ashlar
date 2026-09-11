using System.Collections.Concurrent;
using FluentAssertions;
using Ashlar.BackgroundAgents.Trust;
using Xunit;

namespace Ashlar.Tests.BackgroundAgents.Trust;

/// <summary>
/// Nothing handed to the audit log may be lost between the call and the file.
/// </summary>
/// <remarks>
/// The shape these tests pin down: <c>Append</c> buffers, and any caller that crosses the flush
/// threshold runs the flush inline. The flush this replaced drained the buffer into a THREAD-LOCAL
/// list before opening the database and took no lock at all, so two threads could each be holding a
/// slice of the audit trail on their own stack with two Direct-mode <c>LiteDatabase</c> handles open
/// on one file. When one of them threw out of <c>col.Insert</c> — which is what two handles over one
/// set of pages produces — the stack unwound through <c>Append</c> and out of a void <c>Log*</c>
/// method, and its slice was gone. Nothing re-queued it.
///
/// That is not a corner case. Measured in the devtest container against the pre-fix code, 20 trials
/// of 8 threads x 200 appends: 32,000 entries in, 780 persisted on net8.0 and 1,500 on net10.0, with
/// every trial lossy on both.
///
/// These tests do not touch <c>BsonMapper.Global</c>, so they stay out of the <c>LiteDbMapper</c>
/// collection and can run alongside everything else. Each gets its own file.
/// </remarks>
public sealed class LiteDbDataDecisionAuditLogFlushTests : IDisposable
{
    private readonly string _root;

    /// <summary>Initializes a new lite db data decision audit log flush tests.</summary>
    public LiteDbDataDecisionAuditLogFlushTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"ashlar-dd-audit-flush-{Guid.NewGuid():N}");
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
    /// Concurrent writers against one instance, counted in and counted out.
    /// </summary>
    /// <remarks>
    /// One instance and one file on purpose — that is how the log is registered
    /// (<c>AddTrustServices</c> builds it eagerly and hands the same object to both
    /// <c>IDataDecisionAuditLog</c> and <c>ISanitizationAuditLog</c>), and it is the only shape in
    /// which two threads can be inside the flush together. The mapper concurrency tests give each
    /// thread its own file precisely so that they cannot, which is why they never saw this.
    ///
    /// Four threads and 150 appends each, rather than the harness's eight and 200: the pre-fix code
    /// lost entries in 20 trials out of 20 at half this size, so the smaller shape still fails
    /// reliably without it while keeping the test at roughly a second.
    /// </remarks>
    [Fact]
    public void Concurrent_appends_all_reach_disk()
    {
        const int threads = 4;
        const int appendsPerThread = 150;

        var dbPath = Path.Combine(_root, "concurrent-append.db");
        var log = new LiteDbDataDecisionAuditLog(dbPath);

        var errors = RaceAppends(log, threads, appendsPerThread);

        errors.Should().BeEmpty(
            "LogClassification returns void and its callers are fire-and-forget audit calls, so a " +
            "throw out of the inline flush is both unexpected and, before the fix, the mechanism by " +
            "which the buffered entries were lost");

        // GetRecent flushes whatever is still buffered before it queries, so this also drains the
        // tail that never reached the threshold.
        var persisted = log.GetRecent(int.MaxValue / 4, eventType: "Classification");

        persisted.Should().HaveCount(
            threads * appendsPerThread,
            "every entry handed to the log must be on disk. The pre-fix code lost 31,220 of 32,000 " +
            "at four times this size");

        persisted.Select(e => e.DataType).Should().OnlyHaveUniqueItems(
            "each append carries a distinct DataType, so a duplicate would mean an entry was flushed " +
            "twice — the other way a retained buffer could go wrong");
    }

    /// <summary>
    /// A flush that fails must leave the buffer exactly as it found it.
    /// </summary>
    /// <remarks>
    /// The lock stops two threads from colliding, but it does not by itself stop loss: the old code
    /// would have lost these entries single-threaded too, because it removed them from the buffer
    /// before the write that was supposed to persist them. So the flush counts inserts that
    /// RETURNED, and removes only those.
    ///
    /// A directory standing where the database file belongs is the cheapest deterministic way to make
    /// <c>new LiteDatabase</c> fail on every platform — no file locking, no permissions, no timing.
    /// Removing it makes the store writable again, which a full disk or a repaired mount would do
    /// too.
    /// </remarks>
    [Fact]
    public void Entries_survive_a_flush_that_could_not_open_the_database()
    {
        var dbPath = Path.Combine(_root, "unwritable.db");
        Directory.CreateDirectory(dbPath);

        // The constructor opens nothing, so the store builds against a path it can never write.
        var log = new LiteDbDataDecisionAuditLog(dbPath);

        // Ten is the flush threshold: the tenth append tries to flush and cannot.
        var failures = 0;
        for (var n = 0; n < 10; n++)
        {
            try
            {
                log.LogClassification($"stranded-{n}", "Internal", "written while the file was unwritable");
            }
            catch (Exception)
            {
                failures++;
            }
        }

        failures.Should().BeGreaterThan(0, "the flush must have actually failed for this test to mean anything");

        Directory.Delete(dbPath);

        var persisted = log.GetRecent(100, eventType: "Classification");

        persisted.Should().HaveCount(
            10,
            "entries stay buffered until an insert returns, so the ones stranded by the failed flush " +
            "must arrive on the next one rather than being dropped with the exception");
        persisted.Select(e => e.DataType).Should().BeEquivalentTo(
            Enumerable.Range(0, 10).Select(n => $"stranded-{n}"),
            "and they must all be there, not just the tail");
    }

    /// <summary>
    /// Drives <paramref name="log"/> from <paramref name="threads"/> real threads released together
    /// and returns whatever they threw.
    /// </summary>
    /// <remarks>
    /// A helper rather than inline in the test, because xUnit1031 rejects a blocking wait inside a
    /// test method — and an async test would not do: these racers must be real threads that overlap,
    /// which is the whole point.
    /// </remarks>
    private static IReadOnlyList<Exception> RaceAppends(LiteDbDataDecisionAuditLog log, int threads, int appendsPerThread)
    {
        var errors = new ConcurrentBag<Exception>();

        using var start = new Barrier(threads);
        var writers = new Task[threads];
        for (var i = 0; i < threads; i++)
        {
            var index = i;
            // LongRunning so these are real threads rather than pool work items that could be
            // serialised onto one thread and never overlap.
            writers[i] = Task.Factory.StartNew(
                () =>
                {
                    start.SignalAndWait();
                    for (var n = 0; n < appendsPerThread; n++)
                    {
                        try
                        {
                            log.LogClassification($"type-{index}-{n}", "Internal", "concurrent probe");
                        }
                        catch (Exception ex)
                        {
                            errors.Add(ex);
                        }
                    }
                },
                TaskCreationOptions.LongRunning);
        }

        Task.WaitAll(writers);
        return errors.ToList();
    }

}
