using FluentAssertions;
using LiteDB;
using Ashlar.Commercial.Fleet.Contracts.Models;
using Ashlar.Commercial.Fleet.Infrastructure;
using Ashlar.Core.Application.Persistence;
using Xunit;

namespace Ashlar.Commercial.Tests.Fleet;

/// <summary>
/// A read whose value decides the write that follows it has to be one operation on the director file.
/// </summary>
/// <remarks>
/// <para>The behavioural half, on the commercial side, of
/// <c>LiteDbAtomicWriteConventionTests</c>. <c>LiteDbFleetRegistrySharedModeTests</c> next door counts
/// INSERTS and proves Shared mode stops two opens colliding; this counts what a read-modify-write
/// converged on, which Shared mode does not protect at all — its named mutex is taken and released per
/// ENGINE OPERATION, so another writer commits between the read and the write.</para>
///
/// <para><b>Why two registry instances and not one.</b> Both registries guard themselves with a
/// <c>SemaphoreSlim</c>, so a single instance serialises its own calls and would make either test
/// below pass without a transaction. That lock is exactly what does not exist in the case that
/// matters: the director database is one file under the state directory and the CLI opens it in a
/// second PROCESS, which no in-process lock reaches. Two instances over one path is the smallest
/// honest stand-in, and it is also the registered shape — <c>FleetServiceCollectionExtensions</c>
/// puts both registries on the same <c>dbPath</c>.</para>
///
/// <para><b>Asserted on counts and on a final field value, never on an exception.</b> Neither race
/// throws on any platform. The original mutation measurement was Linux-only; current native
/// readiness also executes these tests on Windows and macOS. See <c>docs/CommercialCiCoverage.md</c>.</para>
/// </remarks>
[Collection(nameof(LiteDbFleetCollection))]
public sealed class LiteDbMeshRegistryAtomicWriteTests : IDisposable
{
    private const int Keys = 20;
    private const int RacersPerKey = 4;
    private const int Rounds = 6;
    private const int OpsPerRound = 50;

    /// <summary>
    /// Bounds on the two waits below. Neither is a synchronisation primitive and nothing positive is
    /// asserted after either elapses: they exist so that a writer throwing — a LiteDB mutex timeout on
    /// a loaded runner, a transient IO error, a full temp disk — reports THAT exception instead of
    /// parking the observer forever. A round is 5-8 s of wall clock here; the project's lane runs with
    /// <c>--blame-hang-timeout 180s</c>, so both fire well inside it and the failure names itself
    /// rather than arriving as a hang dump pointed at the wrong thread.
    /// </summary>
    private static readonly TimeSpan ObserverBudget = TimeSpan.FromSeconds(60);

    /// <summary>See <see cref="ObserverBudget"/>.</summary>
    private static readonly TimeSpan RacerBudget = TimeSpan.FromSeconds(90);

    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), $"ashlar-fleet-atomic-{Guid.NewGuid():N}");

    /// <summary>Initializes a new lite db mesh registry atomic write tests.</summary>
    public LiteDbMeshRegistryAtomicWriteTests() => Directory.CreateDirectory(_dir);

    /// <summary>
    /// The client retry that <c>Idempotency-Key</c> exists to absorb is itself the concurrent case, and
    /// the key's index is not unique — so nothing but the transaction stops a check-then-insert from
    /// creating the task twice. Two tasks for one key are both placed and both executed.
    /// </summary>
    [Fact]
    public void One_idempotency_key_creates_one_task_however_many_requests_race()
        => RaceCreatesOnOneKey();

    private void RaceCreatesOnOneKey()
    {
        var path = Path.Combine(_dir, "idempotency.db");
        var registries = Enumerable.Range(0, RacersPerKey)
            .Select(_ => new LiteDbMeshTaskRegistry(path))
            .ToArray();

        for (var k = 0; k < Keys; k++)
        {
            var key = $"key-{k}";
            RunConcurrently(registries.Select(registry => new Action(() =>
                registry.CreateAsync(new MeshTaskCreateSpec(
                    Name: key,
                    Steps: 1,
                    RequiredBrickIds: Array.Empty<string>(),
                    Affinity: null,
                    Priority: 0,
                    DeadlineUtc: null,
                    CorrelationId: null,
                    IdempotencyKey: key)).GetAwaiter().GetResult())).ToArray());
        }

        RawCount(path, "mesh_tasks").Should().Be(
            Keys,
            "one idempotency key is one task. The probe that looks the key up and the insert that "
            + "follows it are separate engine operations, so without a transaction two racing "
            + "submissions of one key both find nothing and both insert — and the index on "
            + "IdempotencyKey is not unique, so the database does not refuse the second one either.");
    }

    /// <summary>
    /// Every write here puts the WHOLE node document back from the snapshot it read, so a drain and
    /// an un-admit that overlap each revert the other's field. Placement selects on exactly
    /// <c>Admitted &amp;&amp; !Drained</c>, so either revert sends new work to a node an operator was
    /// taking out of service — and un-admitting a peer is not an operation you want a race to undo.
    /// </summary>
    /// <remarks>
    /// <para>Asserted as MONOTONICITY, watched by a third reader, rather than only on the final
    /// state. Both flags are one-way here: nothing in the race ever asks for <c>Drained = false</c> or
    /// <c>Admitted = true</c>, so once either has been observed it may never be observed back. A
    /// final-state check would see only the LAST revert and miss every earlier one, which made it miss
    /// the bug entirely in six runs out of ten; the observer catches any of them.</para>
    ///
    /// <para>The observer reads the document raw, through its own connection, which is a third
    /// participant on the same named mutex — deliberately, because that is also what the CLI is while
    /// the director runs.</para>
    /// </remarks>
    [Fact]
    public void A_drain_and_an_un_admit_do_not_revert_each_other()
        => RaceDrainAgainstUnadmit();

    private void RaceDrainAgainstUnadmit()
    {
        var path = Path.Combine(_dir, "drain.db");
        var draining = new LiteDbFleetNodeRegistry(path);
        var admitting = new LiteDbFleetNodeRegistry(path);

        var reverts = new List<string>();

        for (var round = 0; round < Rounds; round++)
        {
            var peerId = $"peer-{round}";
            draining.RegisterOrUpdateAsync(new MeshFleetNodeState(
                PeerId: peerId,
                ApiBaseUrl: "http://node.invalid",
                Labels: new Dictionary<string, string>(),
                AdvertisedBrickIds: Array.Empty<string>(),
                Drained: false,
                LastHeartbeatUtc: null,
                RegisteredAtUtc: DateTimeOffset.UtcNow)).GetAwaiter().GetResult();

            var writersDone = 0;
            var observed = new List<string>();

            // The increments are in a finally, and only in a finally. The observer's exit condition is
            // these two counters, so a writer that throws on its first iteration would otherwise leave
            // it spinning on a file nobody is writing, Task.WaitAll would never return, and the
            // exception that should have failed this test would never be observed at all.
            RunConcurrently(
                () =>
                {
                    try
                    {
                        for (var i = 0; i < OpsPerRound; i++)
                            draining.SetDrainedAsync(peerId, drained: true).GetAwaiter().GetResult();
                    }
                    finally
                    {
                        Interlocked.Increment(ref writersDone);
                    }
                },
                () =>
                {
                    try
                    {
                        for (var i = 0; i < OpsPerRound; i++)
                            admitting.SetAdmittedAsync(peerId, admitted: false).GetAwaiter().GetResult();
                    }
                    finally
                    {
                        Interlocked.Increment(ref writersDone);
                    }
                },
                () => Watch(path, peerId, () => Volatile.Read(ref writersDone) >= 2, observed));

            reverts.AddRange(observed.Select(o => $"{peerId}: {o}"));

            var node = StoredNode(path, peerId);
            if (!node["Drained"].AsBoolean) reverts.Add($"{peerId}: ended Drained=false");
            if (node["Admitted"].AsBoolean) reverts.Add($"{peerId}: ended Admitted=true");
        }

        reverts.Should().BeEmpty(
            "both flags are one-way in this race — nothing asks for Drained=false or Admitted=true — "
            + "so once either has been seen it can never come back. A revert is a write that was "
            + "accepted and then overwritten by another writer's stale snapshot, silently, because "
            + "neither path throws. Observed: {0}",
            string.Join(" | ", reverts));
    }

    /// <summary>
    /// Watches one node document until the writers are done, recording any flag that goes backwards.
    /// </summary>
    private static void Watch(string path, string peerId, Func<bool> stop, List<string> observed)
    {
        var sawDrained = false;
        var sawUnadmitted = false;
        var deadline = DateTime.UtcNow + ObserverBudget;

        while (!stop())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException(
                    $"the writers on {peerId} did not finish within {ObserverBudget.TotalSeconds:0} s. "
                    + "They signal completion from a finally, so this means they are still running, "
                    + "not that one of them threw.");
            }

            var node = StoredNode(path, peerId);
            var drained = node["Drained"].AsBoolean;
            var admitted = node["Admitted"].AsBoolean;

            if (drained) sawDrained = true;
            else if (sawDrained) observed.Add("Drained went true -> false");

            if (!admitted) sawUnadmitted = true;
            else if (sawUnadmitted) observed.Add("Admitted went false -> true");
        }
    }

    /// <summary>
    /// Runs each action on its own real thread, all released together. A <c>Task.Delay</c> would be a
    /// guess about timing rather than a synchronisation primitive, so there is none.
    /// </summary>
    private static void RunConcurrently(params Action[] racers)
    {
        var ready = new Barrier(racers.Length);
        var threads = racers.Select(racer => Task.Factory.StartNew(
            () =>
            {
                ready.SignalAndWait();
                racer();
            },
            TaskCreationOptions.LongRunning)).ToArray();

        WaitFor(threads);
    }

    /// <summary>
    /// xUnit1031 rejects a blocking wait written inside a test method, and an async test would not
    /// race real threads — which is why both facts above delegate their body one call down.
    /// </summary>
    private static void WaitFor(Task[] threads)
    {
        if (!Task.WaitAll(threads, RacerBudget))
        {
            throw new TimeoutException(
                $"a racer did not finish within {RacerBudget.TotalSeconds:0} s. A bound here rather "
                + "than an unbounded wait so the lane reports this rather than a hang dump.");
        }
    }

    /// <summary>Counts documents without going through either registry's document type or its mapper.</summary>
    private static long RawCount(string path, string collection)
    {
        using var db = new LiteDatabase(LiteDbConnectionString.ForSharedAccess(path));
        return db.GetCollection(collection).LongCount();
    }

    /// <summary>Reads one node document raw, for the same reason.</summary>
    private static BsonDocument StoredNode(string path, string peerId)
    {
        using var db = new LiteDatabase(LiteDbConnectionString.ForSharedAccess(path));
        var doc = db.GetCollection("mesh_fleet_nodes").FindById(new BsonValue(peerId));
        Assert.NotNull(doc);
        return doc;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* a temp directory that outlives the test is not a test failure */ }
        catch (UnauthorizedAccessException) { /* likewise: a racer that outlived its budget still holds the file */ }
    }
}
