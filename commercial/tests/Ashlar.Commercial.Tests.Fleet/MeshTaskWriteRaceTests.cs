using FluentAssertions;
using LiteDB;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Ashlar.Commercial.Fleet.Contracts.Models;
using Ashlar.Commercial.Fleet.Contracts.Ports;
using Ashlar.Commercial.Fleet.Infrastructure;
using Ashlar.Core.Application.Persistence;
using Xunit;

namespace Ashlar.Commercial.Tests.Fleet;

/// <summary>
/// The CALLER-side half of the LiteDB lost update: a read through one store call, a decision, and a
/// write through another, with the database opened and closed in between.
/// </summary>
/// <remarks>
/// <para><c>LiteDbMeshRegistryAtomicWriteTests</c> next door covers what a store method does inside
/// itself. These facts cover what the director's own services do ACROSS store calls, which no
/// transaction inside a store could ever span. The fix was to remove the shape from the port: every
/// write now hands the registry a transform the registry applies to the document it reads inside its
/// own write transaction, so each precondition is evaluated against the row that is about to be
/// overwritten instead of against a snapshot minutes old.</para>
///
/// <para><b>Two registry instances over one path, never one.</b> Every store guards itself with a
/// <c>SemaphoreSlim</c>, so a single instance serialises its own calls and every fact here would pass
/// without the fix. The director is one file under the state directory and the CLI opens it in a
/// second PROCESS, which no in-process lock reaches; two instances are the smallest honest stand-in
/// and are also the registered shape.</para>
///
/// <para><b>Counts, never an expected exception.</b> None of these races throws on any platform -
/// the loss is accepted, reported successful, and absent - so an exception-shaped assertion would be
/// green everywhere and assert nothing. Every fact also carries a positive count, because a store
/// that refused every write would satisfy the violation counts alone.</para>
///
/// <para><b>Measured on Linux only</b> (devtest container, net8.0). No automatically triggered CI
/// lane runs this project at all: <c>composition-mesh-gate</c> owns it and is
/// <c>workflow_dispatch</c>-only. Read these as a regression record, not as a guard - the guard is
/// <c>LiteDbAtomicWriteConventionTests</c> in cert-gate.</para>
/// </remarks>
[Collection(nameof(LiteDbFleetCollection))]
public sealed class MeshTaskWriteRaceTests : IDisposable
{
    private const int PlacementRounds = 12;
    private const int FinishRounds = 24;
    private const int SweepKeys = 120;
    private const int ExtendPasses = 10;
    private const int MigrateRounds = 24;

    /// <summary>
    /// Bounds on the waits below. Neither is a synchronisation primitive and nothing positive is
    /// asserted after one elapses: they exist so a racer that throws reports THAT instead of parking
    /// the run. Same reasoning, same budgets as <c>LiteDbMeshRegistryAtomicWriteTests</c>.
    /// </summary>
    private static readonly TimeSpan RacerBudget = TimeSpan.FromSeconds(90);

    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), $"ashlar-mesh-race-{Guid.NewGuid():N}");

    /// <summary>Initializes a new mesh task write race tests.</summary>
    public MeshTaskWriteRaceTests() => Directory.CreateDirectory(_dir);

    /// <summary>
    /// Two placements of one pending task must not both hand out a lease.
    /// </summary>
    /// <remarks>
    /// <para>This is the reason the fix is a transform and not an <c>expectedLeaseToken</c>
    /// parameter. A pending task's lease token is null on BOTH sides of this race, so two placements
    /// both satisfy "expect null", both mint a GUID, and the later write wins - leaving peer A
    /// holding a token the document no longer carries while peer B holds the stored one, and both
    /// executing. The transform states the precondition that actually distinguishes them
    /// (<c>MeshTaskPlacementService.PlacementSnapshotStillStands</c>: lease token, status, attempt
    /// count and assigned peer together).</para>
    ///
    /// <para>Counted as GHOST LEASES: a caller told <c>Ok</c> while holding a token the store does
    /// not. The positive count is the number of rounds that produced a real assignment, so a
    /// placement service that simply refused everything would fail here rather than pass.</para>
    /// </remarks>
    [Fact]
    public void Two_placements_of_one_task_hand_out_one_lease() => RacePlacements();

    private void RacePlacements()
    {
        var path = Path.Combine(_dir, "placement.db");
        var nodesA = new LiteDbFleetNodeRegistry(path);
        var nodesB = new LiteDbFleetNodeRegistry(path);
        var tasksA = new LiteDbMeshTaskRegistry(path);
        var tasksB = new LiteDbMeshTaskRegistry(path);

        foreach (var peer in new[] { "peer-a", "peer-b" })
        {
            nodesA.RegisterOrUpdateAsync(new MeshFleetNodeState(
                PeerId: peer,
                ApiBaseUrl: $"http://{peer}.invalid",
                Labels: new Dictionary<string, string>(),
                AdvertisedBrickIds: Array.Empty<string>(),
                Drained: false,
                LastHeartbeatUtc: DateTimeOffset.UtcNow,
                RegisteredAtUtc: DateTimeOffset.UtcNow)).GetAwaiter().GetResult();
        }

        var placementA = Placement(nodesA, tasksA);
        var placementB = Placement(nodesB, tasksB);

        var ghostLeases = new List<string>();
        var placed = 0;

        for (var round = 0; round < PlacementRounds; round++)
        {
            var created = tasksA.CreateAsync(new MeshTaskCreateSpec(
                Name: $"job-{round}",
                Steps: 1,
                RequiredBrickIds: Array.Empty<string>(),
                Affinity: null,
                Priority: 0,
                DeadlineUtc: null)).GetAwaiter().GetResult();

            var results = new (bool Ok, MeshTaskState? Task, string? Error)[2];
            RunConcurrently(
                () => results[0] = placementA.TryScheduleAsync(created.TaskId).GetAwaiter().GetResult(),
                () => results[1] = placementB.TryScheduleAsync(created.TaskId).GetAwaiter().GetResult());

            var stored = StoredTask(path, created.TaskId);
            var storedToken = stored["LeaseToken"].IsNull ? null : stored["LeaseToken"].AsString;
            if (storedToken is not null)
                placed++;

            foreach (var r in results)
            {
                if (!r.Ok)
                    continue;
                var reported = r.Task?.LeaseToken;
                if (!string.Equals(reported, storedToken, StringComparison.Ordinal))
                {
                    ghostLeases.Add(
                        $"{created.TaskId}: caller was told lease={reported ?? "<null>"} but the store holds {storedToken ?? "<null>"}");
                }
            }
        }

        placed.Should().Be(
            PlacementRounds,
            "the positive control: every round must actually place its task. Counting only the "
            + "violations would be satisfied by a placement service that refused everything.");

        ghostLeases.Should().BeEmpty(
            "a caller told a placement succeeded must hold the lease the store holds. Two peers "
            + "each believing they own one task is two executions of it, and the loser is refused "
            + "only later, when it tries to extend or report - after it has done the work. Nothing "
            + "throws on either side. Observed: {0}",
            string.Join(" | ", ghostLeases));
    }

    /// <summary>
    /// A lease extension must not reopen a task that finished while the extension was in flight.
    /// </summary>
    /// <remarks>
    /// The old <c>ExtendLeaseAsync</c> read the task, compared the token, and then wrote the WHOLE
    /// snapshot back with a fresh expiry - so a <c>PATCH /status</c> reporting Succeeded in that
    /// window was undone: the task went back to Assigned, the cleared lease came back, and the
    /// result summary the worker had just reported was replaced by the snapshot's null. Both writers
    /// here are real HTTP paths (<c>/tasks/{id}/lease/extend</c> and <c>PATCH /tasks/{id}/status</c>).
    /// </remarks>
    [Fact]
    public void A_finished_task_is_not_reopened_by_a_lease_extension() => RaceExtendAgainstFinish();

    private void RaceExtendAgainstFinish()
    {
        var path = Path.Combine(_dir, "finish.db");
        var tasksA = new LiteDbMeshTaskRegistry(path);
        var tasksB = new LiteDbMeshTaskRegistry(path);
        var extending = Execution(tasksA);
        var finishing = Execution(tasksB);

        var reopened = new List<string>();
        var finished = 0;

        for (var round = 0; round < FinishRounds; round++)
        {
            var token = Guid.NewGuid().ToString("N");
            var taskId = SeedLeasedTask(tasksA, path, $"finish-{round}", token);

            (bool Ok, MeshTaskState? Task, string? Error) extend = default;
            (bool Ok, MeshTaskState? Task, string? Error) finish = default;
            RunConcurrently(
                () => extend = extending.ExtendLeaseAsync(taskId, token, 3600).GetAwaiter().GetResult(),
                () => finish = finishing.ApplyStatusAsync(
                        taskId,
                        MeshTaskStatus.Succeeded,
                        token,
                        reason: null,
                        correlationId: null,
                        resultSummary: "the-result",
                        resultHandle: null)
                    .GetAwaiter().GetResult());

            var stored = StoredTask(path, taskId);
            var status = (MeshTaskStatus)stored["Status"].AsInt32;
            var storedToken = stored["LeaseToken"].IsNull ? null : stored["LeaseToken"].AsString;
            var summary = stored["ResultSummary"].IsNull ? null : stored["ResultSummary"].AsString;

            if (status == MeshTaskStatus.Succeeded)
                finished++;

            if (finish.Ok && status != MeshTaskStatus.Succeeded)
                reopened.Add($"{taskId}: reported Succeeded, stored {status}");
            if (finish.Ok && summary != "the-result")
                reopened.Add($"{taskId}: the reported result was overwritten with {summary ?? "<null>"}");
            if (finish.Ok && storedToken is not null)
                reopened.Add($"{taskId}: the cleared lease came back as {storedToken}");
            if (!finish.Ok && status == MeshTaskStatus.Succeeded)
                reopened.Add($"{taskId}: completion refused, yet the task is stored Succeeded");

            // Deliberately NOT a violation: an extension that lands BEFORE the completion is
            // reported Ok and then finds no lease, because the completion cleared it. That is the
            // two writes agreeing, in order. The loss is the other direction - the extension
            // putting the pre-completion document back - and every arm above catches it.
            _ = extend;
        }

        finished.Should().BeGreaterThan(
            0,
            "the positive control: at least some rounds must actually finish. A store that declined "
            + "every transform would satisfy the violation count on its own.");

        reopened.Should().BeEmpty(
            "whichever writer lands second has to see what the first one did. Before the fix the "
            + "extension wrote a whole snapshot back over a completed task - status, lease and "
            + "result together - and returned success to both callers. Observed: {0}",
            string.Join(" | ", reopened));
    }

    /// <summary>
    /// The sweep must leave a lease alone once a worker has renewed it, and a worker must not be
    /// told a lease was renewed that the sweep then clears.
    /// </summary>
    /// <remarks>
    /// <para><c>MeshLeaseSweepBackgroundService</c> takes ONE whole-collection snapshot per round
    /// and then writes its elements one at a time, each through a separate database open, so the
    /// last element's snapshot is as old as the entire preceding round. This drives the real hosted
    /// service against a worker renewing the very leases it is walking.</para>
    ///
    /// <para><b>The invariant is per task and order-independent: a task may not be BOTH extended and
    /// refused.</b> Under the fix, an extension that lands puts the expiry in the future, so every
    /// later sweep declines and no later extension can be refused; a sweep that lands first clears
    /// the token, so every extension is refused and none can succeed. Exactly one of the two states
    /// is reachable. Under the old shape the sweep wrote a snapshot taken before the extension, so a
    /// worker was told its lease was renewed and then found it gone - which shows up here as a task
    /// that is both. Deciding this on the FINAL state alone would miss it whenever a later write
    /// happened to restore a consistent-looking row, which is why the extender makes several passes
    /// and the fact remembers every answer it was given.</para>
    /// </remarks>
    [Fact]
    public void The_sweep_leaves_a_renewed_lease_alone() => RaceSweepAgainstExtend();

    private void RaceSweepAgainstExtend()
    {
        var path = Path.Combine(_dir, "sweep.db");
        var seeding = new LiteDbMeshTaskRegistry(path);
        var sweeping = new LiteDbMeshTaskRegistry(path);
        var extending = new LiteDbMeshTaskRegistry(path);

        var tokens = new List<(string TaskId, string Token)>();
        for (var i = 0; i < SweepKeys; i++)
        {
            var token = Guid.NewGuid().ToString("N");
            tokens.Add((SeedLeasedTask(seeding, path, $"sweep-{i}", token, expired: true), token));
        }

        var sweep = new MeshLeaseSweepBackgroundService(
            sweeping,
            new StaticOptionsMonitor<MeshCheckpointOptions>(new MeshCheckpointOptions
            {
                SweepEnabled = true,
                SweepIntervalMinutes = 1,
            }),
            NullLogger<MeshLeaseSweepBackgroundService>.Instance);

        var execution = Execution(extending);
        var everExtended = new HashSet<string>(StringComparer.Ordinal);
        var everRefused = new HashSet<string>(StringComparer.Ordinal);

        using (var cts = new CancellationTokenSource())
        {
            RunConcurrently(
                () => sweep.StartAsync(cts.Token).GetAwaiter().GetResult(),
                () =>
                {
                    // Several passes, walking the list in the opposite direction to the sweep
                    // (ListAsync orders newest first), so the two writers cross rather than run in
                    // convoy, and so a task the sweep clears AFTER an extension is asked again and
                    // can report the contradiction.
                    for (var pass = 0; pass < ExtendPasses; pass++)
                    {
                        foreach (var (taskId, token) in tokens)
                        {
                            var (ok, _, _) = execution.ExtendLeaseAsync(taskId, token, 3600)
                                .GetAwaiter().GetResult();
                            lock (everExtended)
                            {
                                if (ok) everExtended.Add(taskId);
                                else everRefused.Add(taskId);
                            }
                        }
                    }
                });

            cts.Cancel();
            sweep.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        }

        var violations = new List<string>();
        var reclaimed = 0;
        var renewed = 0;

        foreach (var (taskId, token) in tokens)
        {
            var stored = StoredTask(path, taskId);
            var storedToken = stored["LeaseToken"].IsNull ? null : stored["LeaseToken"].AsString;
            var status = (MeshTaskStatus)stored["Status"].AsInt32;
            var extended = everExtended.Contains(taskId);
            var refused = everRefused.Contains(taskId);

            if (storedToken is null) reclaimed++;
            else renewed++;

            if (extended && refused)
            {
                violations.Add(
                    $"{taskId}: the worker was told its lease was renewed AND that the token was invalid");
            }

            if (extended && !refused && storedToken != token)
                violations.Add($"{taskId}: every extension was accepted, yet the lease is {storedToken ?? "<cleared>"}");
            if (refused && !extended && storedToken is not null)
                violations.Add($"{taskId}: every extension was refused, yet the lease {storedToken} is still held");
            if (refused && !extended && status != MeshTaskStatus.Pending)
                violations.Add($"{taskId}: every extension was refused, yet the task is {status} rather than reclaimed");
        }

        (reclaimed + renewed).Should().Be(
            SweepKeys,
            "the positive control: every seeded task must have been decided one way or the other.");

        violations.Should().BeEmpty(
            "the sweep clears a lease only if the lease it saw expire is still the stored one, and "
            + "an extension lands only if the token it was given is still the stored one. Before "
            + "the fix both wrote a whole snapshot: the sweep reverted an extension made after its "
            + "list was taken, and the extension put the whole pre-sweep document back with a FRESH "
            + "expiry, so the task stayed leased to a peer the director had already reclaimed it "
            + "from and the sweep would not look at it again for another interval. Observed: {0}",
            string.Join(" | ", violations));
    }

    /// <summary>
    /// A migrate carrying a stale lease token must not overwrite a finished task.
    /// </summary>
    /// <remarks>
    /// <c>MigrateForCheckpointAsync</c> checked the token AND the Assigned/Running status against a
    /// snapshot, then wrote the whole document back as Pending with a checkpoint handle. A task that
    /// reached Succeeded in the window was re-queued carrying the snapshot's result fields and
    /// executed again. Both guards now live in the transform.
    /// </remarks>
    [Fact]
    public void A_migrate_cannot_requeue_a_task_that_finished() => RaceMigrateAgainstFinish();

    private void RaceMigrateAgainstFinish()
    {
        var path = Path.Combine(_dir, "migrate.db");
        var tasksA = new LiteDbMeshTaskRegistry(path);
        var tasksB = new LiteDbMeshTaskRegistry(path);
        var migrating = Execution(tasksA);
        var finishing = Execution(tasksB);

        var requeued = new List<string>();
        var decided = 0;

        for (var round = 0; round < MigrateRounds; round++)
        {
            var token = Guid.NewGuid().ToString("N");
            var taskId = SeedLeasedTask(tasksA, path, $"migrate-{round}", token);

            (bool Ok, MeshTaskState? Task, string? Error) migrate = default;
            (bool Ok, MeshTaskState? Task, string? Error) finish = default;
            RunConcurrently(
                () => migrate = migrating.MigrateForCheckpointAsync(taskId, token, "chk://handle")
                    .GetAwaiter().GetResult(),
                () => finish = finishing.ApplyStatusAsync(
                        taskId,
                        MeshTaskStatus.Succeeded,
                        token,
                        reason: null,
                        correlationId: null,
                        resultSummary: "the-result",
                        resultHandle: null)
                    .GetAwaiter().GetResult());

            var stored = StoredTask(path, taskId);
            var status = (MeshTaskStatus)stored["Status"].AsInt32;
            var handle = stored["CheckpointHandle"].IsNull ? null : stored["CheckpointHandle"].AsString;

            if (migrate.Ok || finish.Ok)
                decided++;

            if (migrate.Ok && finish.Ok)
                requeued.Add($"{taskId}: BOTH the migrate and the completion were accepted");
            if (finish.Ok && status != MeshTaskStatus.Succeeded)
                requeued.Add($"{taskId}: completion reported, stored {status}");
            if (migrate.Ok && handle != "chk://handle")
                requeued.Add($"{taskId}: migrate reported, stored handle {handle ?? "<null>"}");
        }

        decided.Should().Be(
            MigrateRounds,
            "the positive control: every round must accept one of the two writers.");

        requeued.Should().BeEmpty(
            "the migrate and the completion both clear the lease, so exactly one of them can win. "
            + "Before the fix both read the same snapshot, both passed their own guard against it, "
            + "and both wrote - re-queueing a completed task with the snapshot's result fields. "
            + "Observed: {0}",
            string.Join(" | ", requeued));
    }

    /// <summary>
    /// The positive control for the port itself: an uncontended transform is applied, and what the
    /// caller is handed back is what a fresh store reads.
    /// </summary>
    /// <remarks>
    /// Without this, every fact above is satisfied by a registry whose transform path declines
    /// everything - which is the failure mode a refusal-only suite cannot see (HowGatesGoQuiet,
    /// section 4).
    /// </remarks>
    [Fact]
    public async Task An_uncontended_update_is_applied_and_readable_through_a_second_store()
    {
        var path = Path.Combine(_dir, "control.db");
        var writer = new LiteDbMeshTaskRegistry(path);
        var reader = new LiteDbMeshTaskRegistry(path);

        var created = await writer.CreateAsync(new MeshTaskCreateSpec("control", 1, [], null, 0, null));

        var applied = await writer.UpdateAsync(
            created.TaskId,
            current => current with { Status = MeshTaskStatus.Running, PlacementReason = "control" });

        applied.Outcome.Should().Be(MeshTaskUpdateOutcome.Applied);
        applied.State!.Status.Should().Be(MeshTaskStatus.Running);

        (await reader.GetAsync(created.TaskId))!.PlacementReason.Should().Be("control");

        var declined = await writer.UpdateAsync(created.TaskId, _ => null);
        declined.Outcome.Should().Be(MeshTaskUpdateOutcome.PreconditionFailed);
        declined.State!.Status.Should().Be(MeshTaskStatus.Running, "a refusal writes nothing");

        var missing = await writer.UpdateAsync("no-such-task", current => current);
        missing.Outcome.Should().Be(MeshTaskUpdateOutcome.NotFound);
        missing.State.Should().BeNull();
    }

    private static MeshTaskPlacementService Placement(IFleetNodeRegistry nodes, IMeshTaskRegistry tasks)
        => new(
            nodes,
            tasks,
            Options.Create(new MeshCheckpointOptions { LeaseSeconds = 3600 }),
            Options.Create(new MeshPlacementTrustOptions()),
            NullLogger<MeshTaskPlacementService>.Instance);

    private static MeshTaskExecutionService Execution(IMeshTaskRegistry tasks)
        => new(
            tasks,
            Options.Create(new MeshCheckpointOptions { LeaseSeconds = 3600 }),
            NullLogger<MeshTaskExecutionService>.Instance);

    /// <summary>
    /// Creates a task and puts it into the Assigned-with-a-lease state the workers report against.
    /// </summary>
    /// <remarks>
    /// Arranged through the registry rather than through a placement so the lease token is known to
    /// the test. The arrange shim is the one place the removed whole-document write still lives; see
    /// <see cref="RegistryArrangeExtensions"/>.
    /// </remarks>
    private static string SeedLeasedTask(
        IMeshTaskRegistry tasks,
        string path,
        string name,
        string leaseToken,
        bool expired = false)
    {
        var created = tasks.CreateAsync(new MeshTaskCreateSpec(name, 1, [], null, 0, null))
            .GetAwaiter().GetResult();

        tasks.UpdateAsync(created with
        {
            Status = MeshTaskStatus.Assigned,
            AssignedPeerId = "peer-a",
            AssignedApiBaseUrl = "http://peer-a.invalid/",
            AttemptCount = 1,
            LeaseToken = leaseToken,
            LeaseOwnerPeerId = "peer-a",
            LeaseExpiresUtc = expired
                ? DateTimeOffset.UtcNow.AddMinutes(-5)
                : DateTimeOffset.UtcNow.AddMinutes(30),
        }).GetAwaiter().GetResult();

        // Read it back raw so a seeding failure is a seeding failure rather than a mysterious zero
        // in whichever count the fact goes on to take.
        StoredTask(path, created.TaskId)["LeaseToken"].AsString.Should().Be(leaseToken);
        return created.TaskId;
    }

    /// <summary>
    /// Runs each action on its own real thread, all released together. A <c>Task.Delay</c> would be
    /// a guess about timing rather than a synchronisation primitive, so there is none.
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

        if (!Task.WaitAll(threads, RacerBudget))
        {
            throw new TimeoutException(
                $"a racer did not finish within {RacerBudget.TotalSeconds:0} s. A bound here rather "
                + "than an unbounded wait so the lane reports this rather than a hang dump.");
        }

        // Surfaces a racer's exception; Task.WaitAll above only tells us they finished.
        Task.WaitAll(threads);
    }

    /// <summary>Reads one task document raw, without either registry or its mapper.</summary>
    private static BsonDocument StoredTask(string path, string taskId)
    {
        using var db = new LiteDatabase(LiteDbConnectionString.ForSharedAccess(path));
        var doc = db.GetCollection("mesh_tasks").FindById(new BsonValue(taskId));
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
