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
/// What <c>ApplyStatusAsync</c> accepts and refuses, and what a placement does when it loses the
/// reclaim race - the two windows the contended facts in <c>MeshTaskWriteRaceTests</c> cannot open
/// on purpose.
/// </summary>
/// <remarks>
/// <para><b>Deterministic interleaving, not a race.</b> A race test can only report the
/// interleavings it happened to get. Both defects here live in ONE window - between a placement's
/// outer read and its reclaim transform, and between a status patch's arrival and the transform that
/// decides it - so a decorator that commits the competing write exactly there is strictly stronger
/// evidence than a thread barrier: it produces the interleaving every time rather than sometimes,
/// and a fix that only narrows the window fails it.</para>
///
/// <para><b>Why these did not exist.</b> Nothing anywhere covered any <c>ApplyStatusAsync</c> case:
/// the method was extracted from <c>CommercialFleetEndpoints.PatchMeshTaskStatusAsync</c> during a
/// concurrency change, its two behaviour changes were described in a commit message, and the only
/// code that reached it was the two race facts, which pass a token every time. That is how the
/// no-token operator path was narrowed - an operator can no longer force a stuck leased task
/// terminal - while the commit message, the CHANGELOG and the method's own comment all said the
/// operator path was unchanged. These facts are what makes that statement checkable.</para>
///
/// <para>Measured on Linux only (devtest container, net8.0), like everything else in this project,
/// and this project runs in no automatically triggered lane - <c>composition-mesh-gate</c> owns it
/// and is <c>workflow_dispatch</c>-only.</para>
/// </remarks>
[Collection(nameof(LiteDbFleetCollection))]
public sealed class MeshStatusAndReclaimTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), $"ashlar-mesh-status-{Guid.NewGuid():N}");

    /// <summary>Initializes a new mesh status and reclaim tests.</summary>
    public MeshStatusAndReclaimTests() => Directory.CreateDirectory(_dir);

    /// <summary>
    /// A worker's token must match the stored one for EVERY status, not only Running and Assigned.
    /// </summary>
    /// <remarks>
    /// This is the half of the change that is real and load-bearing. Succeeded, Failed and Pending
    /// are the three arms that null the lease fields, and they had no comparison at all, so a worker
    /// whose lease had been swept or re-placed could clear the new owner's lease and overwrite its
    /// result.
    /// </remarks>
    [Theory]
    [InlineData(MeshTaskStatus.Succeeded)]
    [InlineData(MeshTaskStatus.Failed)]
    [InlineData(MeshTaskStatus.Pending)]
    [InlineData(MeshTaskStatus.Running)]
    [InlineData(MeshTaskStatus.Assigned)]
    public async Task A_worker_offering_the_wrong_token_is_refused_whatever_status_it_reports(
        MeshTaskStatus status)
    {
        var path = Path.Combine(_dir, $"wrong-token-{status}.db");
        var tasks = new LiteDbMeshTaskRegistry(path);
        var held = Guid.NewGuid().ToString("N");
        var taskId = await SeedLeasedTaskAsync(tasks, "wrong-token", held);

        var (ok, _, error) = await Execution(tasks).ApplyStatusAsync(
            taskId, status, leaseToken: "not-the-stored-token", reason: null, correlationId: null,
            resultSummary: "should-not-land", resultHandle: null);

        ok.Should().BeFalse("offering a token is a claim of ownership and this one is not the stored token");
        error.Should().Be("lease.token_mismatch_or_missing");
        Stored(path, taskId)["LeaseToken"].AsString.Should().Be(held, "the lease is untouched");
        Stored(path, taskId)["ResultSummary"].IsNull.Should().BeTrue("and so is the result");
    }

    /// <summary>
    /// A token offered against an UNLEASED task is refused too.
    /// </summary>
    /// <remarks>
    /// The measured need: without it, a migrate that cleared the lease was then overwritten by the
    /// same stale worker reporting Succeeded, 24 of 24 rounds.
    /// </remarks>
    [Fact]
    public async Task A_token_offered_against_an_unleased_task_is_refused()
    {
        var path = Path.Combine(_dir, "unleased.db");
        var tasks = new LiteDbMeshTaskRegistry(path);
        var created = await tasks.CreateAsync(new MeshTaskCreateSpec("unleased", 1, [], null, 0, null));

        var (ok, _, error) = await Execution(tasks).ApplyStatusAsync(
            created.TaskId, MeshTaskStatus.Succeeded, leaseToken: "a-stale-token", reason: null,
            correlationId: null, resultSummary: "should-not-land", resultHandle: null);

        ok.Should().BeFalse();
        error.Should().Be("lease.token_mismatch_or_missing");
        ((MeshTaskStatus)Stored(path, created.TaskId)["Status"].AsInt32)
            .Should().Be(MeshTaskStatus.Pending);
    }

    /// <summary>
    /// The holder of the lease is accepted - the positive control for the two refusals above.
    /// </summary>
    [Fact]
    public async Task The_lease_holder_is_accepted()
    {
        var path = Path.Combine(_dir, "holder.db");
        var tasks = new LiteDbMeshTaskRegistry(path);
        var held = Guid.NewGuid().ToString("N");
        var taskId = await SeedLeasedTaskAsync(tasks, "holder", held);

        var (ok, state, error) = await Execution(tasks).ApplyStatusAsync(
            taskId, MeshTaskStatus.Succeeded, leaseToken: held, reason: null, correlationId: null,
            resultSummary: "the-result", resultHandle: null);

        ok.Should().BeTrue(
            "the positive control: every other assertion in this class is a refusal, and a method "
            + "that refused everything would satisfy all of them. Error was: {0}",
            error ?? "<none>");
        state!.Status.Should().Be(MeshTaskStatus.Succeeded);
        Stored(path, taskId)["ResultSummary"].AsString.Should().Be("the-result");
        Stored(path, taskId)["LeaseToken"].IsNull.Should().BeTrue("a completion clears the lease");
    }

    /// <summary>
    /// An operator holds no token, and on a LEASED task it can still force the terminal and requeue
    /// arms - which is the behaviour the endpoint shipped with.
    /// </summary>
    /// <remarks>
    /// <para>An earlier revision refused a no-token caller on any leased task, for every status.
    /// That removed the only lever an operator has over a task whose worker died holding a live
    /// lease: <c>MeshCheckpointOptions.SweepEnabled</c> is <c>false</c> by default and
    /// <c>LeaseSeconds</c> is 1800, there is no cancel or abandon route, and <c>/retry</c> re-places
    /// the task rather than failing it. No measured race needed it closed - a worker always sends
    /// its token and is refused by the comparison the facts above cover - and the commit message,
    /// the CHANGELOG and the method's own comment all claimed the operator path was
    /// unchanged.</para>
    ///
    /// <para>Running and Assigned stay refused for a no-token caller on a leased task, which is
    /// exactly the gate the endpoint had: those two arms do not clear the lease, so accepting them
    /// from someone who does not hold it would let a bystander move a task under its owner.</para>
    /// </remarks>
    [Theory]
    [InlineData(MeshTaskStatus.Failed, true)]
    [InlineData(MeshTaskStatus.Pending, true)]
    [InlineData(MeshTaskStatus.Succeeded, true)]
    [InlineData(MeshTaskStatus.Running, false)]
    [InlineData(MeshTaskStatus.Assigned, false)]
    public async Task An_operator_with_no_token_keeps_the_gate_the_endpoint_shipped_with(
        MeshTaskStatus status,
        bool expectAccepted)
    {
        var path = Path.Combine(_dir, $"operator-{status}.db");
        var tasks = new LiteDbMeshTaskRegistry(path);
        var held = Guid.NewGuid().ToString("N");
        var taskId = await SeedLeasedTaskAsync(tasks, "operator", held);

        var (ok, _, error) = await Execution(tasks).ApplyStatusAsync(
            taskId, status, leaseToken: null, reason: "operator forced it", correlationId: null,
            resultSummary: null, resultHandle: null);

        ok.Should().Be(
            expectAccepted,
            "PATCH /api/mesh/tasks/{{id}}/status with no leaseToken is the operator path. Before "
            + "this change the endpoint compared tokens only for Running and Assigned, so the three "
            + "arms that CLEAR the lease were an operator's way out of a stuck task. Status {0}, "
            + "error {1}",
            status,
            error ?? "<none>");

        var stored = Stored(path, taskId);
        if (expectAccepted)
        {
            ((MeshTaskStatus)stored["Status"].AsInt32).Should().Be(status);
            stored["LeaseToken"].IsNull.Should().BeTrue(
                "the terminal and requeue arms clear the lease, which is what unsticks the task");
        }
        else
        {
            error.Should().Be("lease.token_mismatch_or_missing");
            ((MeshTaskStatus)stored["Status"].AsInt32).Should().Be(
                MeshTaskStatus.Assigned,
                "an arm that does not clear the lease may not be driven by someone who does not hold it");
            stored["LeaseToken"].AsString.Should().Be(held);
        }
    }

    /// <summary>
    /// A placement that loses the reclaim race must not adopt the winner's state as a fresh premise.
    /// </summary>
    /// <remarks>
    /// <para><c>TryPlaceAsync</c> reads the task, sees an Assigned row with an expired lease, and
    /// asks the store to reclaim it. When that transform is REFUSED, the store hands back the
    /// document it actually holds - and the loser used to assign that to <c>task</c> and carry on.
    /// From there <c>PlacementSnapshotStillStands</c> passes by construction, <c>Shape()</c> forces
    /// <c>Status = Pending</c> from any status, and the <c>Succeeded</c> guard at the top of the
    /// method is never re-applied: a task that FINISHED in the window was re-placed, reported Ok,
    /// and handed a fresh lease, with the completion's result left dangling on an Assigned row. The
    /// second reachable refusal has the same shape and is worse - an extension changes only
    /// <c>LeaseExpiresUtc</c>, which the four-field precondition does not compare, so a renewed
    /// live lease also refused the reclaim and was also re-placed.</para>
    ///
    /// <para>A refusal there means this call's premise is void, so it is now reported as
    /// <c>schedule.conflict</c>.</para>
    /// </remarks>
    [Fact]
    public async Task A_placement_that_loses_the_reclaim_race_does_not_replace_a_finished_task()
    {
        var path = Path.Combine(_dir, "reclaim-launder.db");
        var nodes = new LiteDbFleetNodeRegistry(path);
        var tasks = new LiteDbMeshTaskRegistry(path);

        await nodes.RegisterOrUpdateAsync(Node("peer-a"));
        await nodes.RegisterOrUpdateAsync(Node("peer-b"));

        var held = Guid.NewGuid().ToString("N");
        var taskId = await SeedLeasedTaskAsync(tasks, "finishing", held, expired: true);

        // The competing writer commits INSIDE the placement's reclaim window: on the first
        // UpdateAsync the decorator lets the worker's completion through first, then forwards.
        var completing = Execution(new LiteDbMeshTaskRegistry(path));
        var racing = new CompetingWriterOnFirstUpdate(
            tasks,
            () => completing.ApplyStatusAsync(
                taskId, MeshTaskStatus.Succeeded, held, reason: null, correlationId: null,
                resultSummary: "the-result", resultHandle: null).GetAwaiter().GetResult());

        var placement = new MeshTaskPlacementService(
            nodes,
            racing,
            Options.Create(new MeshCheckpointOptions { LeaseSeconds = 3600 }),
            Options.Create(new MeshPlacementTrustOptions()),
            NullLogger<MeshTaskPlacementService>.Instance);

        var (ok, _, error) = await placement.TryScheduleAsync(taskId);

        racing.Fired.Should().BeTrue(
            "the control for the decorator itself: if the competing write never happened this fact "
            + "is asserting nothing about a race");

        ok.Should().BeFalse("the placement lost the race and must say so");
        error.Should().Be("schedule.conflict");

        var stored = Stored(path, taskId);
        ((MeshTaskStatus)stored["Status"].AsInt32).Should().Be(
            MeshTaskStatus.Succeeded,
            "the completion stands. Reported Assigned means the placement adopted the winner's "
            + "document as its own snapshot and re-placed a finished task.");
        stored["LeaseToken"].IsNull.Should().BeTrue("no fresh lease was handed out over a completion");
        stored["ResultSummary"].AsString.Should().Be("the-result");
        stored["AttemptCount"].AsInt32.Should().Be(1, "and no second attempt was recorded");
    }

    /// <summary>
    /// Uncontended, the reclaim path still reclaims and re-places - the positive control for the
    /// refusal above.
    /// </summary>
    [Fact]
    public async Task An_uncontended_expired_lease_is_reclaimed_and_replaced()
    {
        var path = Path.Combine(_dir, "reclaim-control.db");
        var nodes = new LiteDbFleetNodeRegistry(path);
        var tasks = new LiteDbMeshTaskRegistry(path);

        await nodes.RegisterOrUpdateAsync(Node("peer-b"));

        var held = Guid.NewGuid().ToString("N");
        var taskId = await SeedLeasedTaskAsync(tasks, "stale", held, expired: true);

        var placement = new MeshTaskPlacementService(
            nodes,
            tasks,
            Options.Create(new MeshCheckpointOptions { LeaseSeconds = 3600 }),
            Options.Create(new MeshPlacementTrustOptions()),
            NullLogger<MeshTaskPlacementService>.Instance);

        var (ok, state, error) = await placement.TryScheduleAsync(taskId);

        ok.Should().BeTrue(
            "the positive control: returning schedule.conflict whenever the reclaim transform "
            + "declines would satisfy the fact above while breaking lease reclamation entirely. "
            + "Error was: {0}",
            error ?? "<none>");
        state!.Status.Should().Be(MeshTaskStatus.Assigned);
        state.AssignedPeerId.Should().Be("peer-b");
        state.LeaseToken.Should().NotBe(held, "a new placement mints a new token");
        Stored(path, taskId)["AttemptCount"].AsInt32.Should().Be(2);
    }

    private static MeshTaskExecutionService Execution(IMeshTaskRegistry tasks)
        => new(
            tasks,
            Options.Create(new MeshCheckpointOptions { LeaseSeconds = 3600 }),
            NullLogger<MeshTaskExecutionService>.Instance);

    private static MeshFleetNodeState Node(string peerId)
        => new(
            PeerId: peerId,
            ApiBaseUrl: $"http://{peerId}.invalid",
            Labels: new Dictionary<string, string>(),
            AdvertisedBrickIds: Array.Empty<string>(),
            Drained: false,
            LastHeartbeatUtc: DateTimeOffset.UtcNow,
            RegisteredAtUtc: DateTimeOffset.UtcNow);

    private static async Task<string> SeedLeasedTaskAsync(
        IMeshTaskRegistry tasks,
        string name,
        string leaseToken,
        bool expired = false)
    {
        var created = await tasks.CreateAsync(new MeshTaskCreateSpec(name, 1, [], null, 0, null));

        await tasks.UpdateAsync(created with
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
        });

        return created.TaskId;
    }

    private static BsonDocument Stored(string path, string taskId)
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
        catch (UnauthorizedAccessException) { /* likewise */ }
    }

    /// <summary>
    /// Runs a competing write once, immediately before the first <c>UpdateAsync</c> it forwards.
    /// </summary>
    /// <remarks>
    /// The window under test is between <c>TryPlaceAsync</c>'s outer <c>GetAsync</c> and its reclaim
    /// transform, and that is the first <c>UpdateAsync</c> the placement makes. Committing there
    /// every time is what makes this a fact rather than a lottery.
    /// </remarks>
    private sealed class CompetingWriterOnFirstUpdate : IMeshTaskRegistry
    {
        private readonly IMeshTaskRegistry _inner;
        private readonly Action _competingWrite;
        private int _updates;

        public CompetingWriterOnFirstUpdate(IMeshTaskRegistry inner, Action competingWrite)
        {
            _inner = inner;
            _competingWrite = competingWrite;
        }

        /// <summary>Whether the competing write actually ran.</summary>
        public bool Fired { get; private set; }

        public Task<MeshTaskState> CreateAsync(MeshTaskCreateSpec spec, CancellationToken cancellationToken = default)
            => _inner.CreateAsync(spec, cancellationToken);

        public Task<MeshTaskState?> TryGetByIdempotencyKeyAsync(string idempotencyKey, CancellationToken cancellationToken = default)
            => _inner.TryGetByIdempotencyKeyAsync(idempotencyKey, cancellationToken);

        public Task<MeshTaskState?> GetAsync(string taskId, CancellationToken cancellationToken = default)
            => _inner.GetAsync(taskId, cancellationToken);

        public Task<IReadOnlyList<MeshTaskState>> ListAsync(CancellationToken cancellationToken = default)
            => _inner.ListAsync(cancellationToken);

        public Task<MeshTaskUpdateResult> UpdateAsync(
            string taskId,
            Func<MeshTaskState, MeshTaskState?> transform,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _updates) == 1)
            {
                _competingWrite();
                Fired = true;
            }

            return _inner.UpdateAsync(taskId, transform, cancellationToken);
        }
    }
}
