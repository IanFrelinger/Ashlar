using FluentAssertions;
using LiteDB;
using Ashlar.Commercial.Fleet.Contracts.Models;
using Ashlar.Commercial.Fleet.Contracts.Ports;
using Ashlar.Commercial.Fleet.Infrastructure;
using Ashlar.Core.Application.Persistence;
using Xunit;

namespace Ashlar.Commercial.Tests.Fleet;

/// <summary>
/// A node re-registering must not undo an operator revoking it.
/// </summary>
/// <remarks>
/// <para><b>The shape.</b> <c>CommercialFleetEndpoints.RegisterFleetNodeAsync</c> read the node
/// through <c>IFleetNodeRegistry.GetAsync</c>, filled in the fields the request body omitted from
/// that snapshot (<c>Admitted</c> among them), and then wrote the WHOLE document back through an
/// unconditional upsert — on a database the store had opened and closed in between. A
/// <c>POST /fleet/nodes/{peerId}/revoke</c> committing in that window was written straight back as
/// <c>Admitted = true</c>. <c>MeshTaskPlacementService</c> selects on exactly
/// <c>Admitted &amp;&amp; !Drained</c>, so the peer an operator had just revoked went back into the
/// eligible set and received work. A node reconnects on a timer; an operator revokes once. This is
/// the containment action of the control plane, so it is not something a heartbeat may undo.</para>
///
/// <para><b>Why one contested pair per round, and why it is synchronised.</b> <c>Admitted</c> is
/// one-way here: after the FIRST revoke commits, every later registration reads <c>false</c> and
/// writes <c>false</c>, so two long loops leave exactly one narrow window at the very start.
/// Measured: an unsynchronised version of this fact went red in one run out of two with the fix
/// reverted, which is a detector that reports "fixed" half the times it is not. Each round is
/// therefore ONE revoke against ONE re-registration, with two events putting the revoke inside the
/// registration's read-to-write window by construction — see the comment on them. Under the fix
/// that handshake cannot complete, because the merge runs inside the write transaction, so the
/// stored answer is <c>false</c> in every round.</para>
///
/// <para>Counted, never asserted as an exception: neither writer throws on any platform, and both
/// report success. Measured on Linux only (devtest container, net8.0); no automatically triggered
/// lane runs this project.</para>
/// </remarks>
[Collection(nameof(LiteDbFleetCollection))]
public sealed class FleetNodeRegistrationRaceTests : IDisposable
{
    private const int Rounds = 20;

    /// <summary>
    /// Bound on the handshake below. It is a wait on a real event, not a sleep standing in for one,
    /// and NOTHING positive is asserted on the strength of it: under the fix it is expected to time
    /// out, because the merge runs inside the write transaction and the revoke cannot commit until
    /// the merge has. Its only job is to stop the round hanging if a writer throws.
    /// </summary>
    private static readonly TimeSpan HandshakeBudget = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// A bound so a throwing racer reports itself instead of parking the run; not a synchronisation
    /// primitive, and nothing positive is asserted after it elapses.
    /// </summary>
    private static readonly TimeSpan RacerBudget = TimeSpan.FromSeconds(90);

    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), $"ashlar-fleet-register-{Guid.NewGuid():N}");

    /// <summary>Initializes a new fleet node registration race tests.</summary>
    public FleetNodeRegistrationRaceTests() => Directory.CreateDirectory(_dir);

    /// <summary>A reconnecting node cannot re-admit itself over an operator's revoke.</summary>
    [Fact]
    public void A_reconnecting_node_does_not_revive_its_own_revoked_admission()
        => RaceRegisterAgainstRevoke();

    private void RaceRegisterAgainstRevoke()
    {
        var path = Path.Combine(_dir, "register.db");
        var registering = new LiteDbFleetNodeRegistry(path);
        var revoking = new LiteDbFleetNodeRegistry(path);

        var revived = new List<string>();
        var registrations = 0;
        var revokes = 0;

        for (var round = 0; round < Rounds; round++)
        {
            var peerId = $"peer-{round}";
            registering.RegisterOrUpdateAsync(new MeshFleetNodeState(
                PeerId: peerId,
                ApiBaseUrl: "http://node.invalid/",
                Labels: new Dictionary<string, string>(),
                AdvertisedBrickIds: Array.Empty<string>(),
                Drained: false,
                LastHeartbeatUtc: null,
                RegisteredAtUtc: DateTimeOffset.UtcNow)).GetAwaiter().GetResult();

            var registered = false;
            var revoked = false;

            // The window this fact is about is between the registration READING the node and
            // WRITING it back. Hoping two unsynchronised writers land inside it caught the defect in
            // one run out of two, which is a detector that reports "fixed" half the time it is not.
            // These two events put the revoke inside the window by construction:
            //
            //   * the revoke waits until the merge has been handed the document it read;
            //   * the merge then waits for the revoke to say it committed.
            //
            // Under the fix that second wait CANNOT be satisfied - the merge holds the write
            // transaction, so the revoke is still queued behind it - and it times out, after which
            // the merge writes against the row it was actually given. Under the old shape the merge
            // runs with nothing held, the revoke commits immediately, and the merge writes the
            // stale Admitted=true straight over it.
            using var registrationHasRead = new ManualResetEventSlim(false);
            using var revokeCommitted = new ManualResetEventSlim(false);

            RunConcurrently(
                () =>
                {
                    ReRegister(registering, peerId, round, onRead: () =>
                    {
                        registrationHasRead.Set();
                        revokeCommitted.Wait(HandshakeBudget);
                    });
                    registered = true;
                },
                () =>
                {
                    registrationHasRead.Wait(HandshakeBudget);
                    revoked = revoking.SetAdmittedAsync(peerId, admitted: false).GetAwaiter().GetResult();
                    revokeCommitted.Set();
                });

            if (registered) registrations++;
            if (revoked) revokes++;

            var node = StoredNode(path, peerId);
            if (node["Admitted"].AsBoolean)
                revived.Add($"{peerId}: ended Admitted=true after a revoke that reported success");

            // The registration has to have landed too, or "Admitted stayed false" would be true for
            // the uninteresting reason that nothing was written.
            node["ReportedQueueDepth"].AsInt32.Should().Be(
                round,
                "the positive control for {0}: the re-registration must actually have been stored, "
                + "otherwise this round proves nothing about what it did to Admitted",
                peerId);
        }

        registrations.Should().Be(Rounds, "every re-registration must have completed");
        revokes.Should().Be(Rounds, "every revoke must have found its node and reported success");

        revived.Should().BeEmpty(
            "a revoke that reported success may not be undone by a node re-registering itself. "
            + "Before the fix the endpoint read Admitted, the revoke committed, and the "
            + "registration wrote the snapshot's `true` back over it — silently, with both callers "
            + "told they succeeded, and placement selects on exactly Admitted && !Drained. "
            + "Observed: {0}",
            string.Join(" | ", revived));
    }

    /// <summary>
    /// The registration merge exactly as <c>CommercialFleetEndpoints.RegisterFleetNodeAsync</c>
    /// writes it: a body that carries a queue depth and nothing else, so every remaining field falls
    /// back to what is stored.
    /// </summary>
    /// <remarks>
    /// Reproduced here rather than called through the endpoint because
    /// <c>Ashlar.Commercial.Tests.Fleet</c> does not reference <c>Ashlar.Commercial.Fleet.Api</c>,
    /// and the project that does — <c>Ashlar.Commercial.Tests.Fleet.Host</c> — is in no solution and
    /// no CI lane (<c>ci/test-ownership.tsv</c> tracks it as UNOWNED). What is under test is that
    /// the registry applies this closure to the row it is about to overwrite.
    /// </remarks>
    private static void ReRegister(IFleetNodeRegistry registry, string peerId, int queueDepth, Action onRead)
        => registry.RegisterOrMergeAsync(
                peerId,
                existing =>
                {
                    onRead();
                    return new MeshFleetNodeState(
                        PeerId: peerId,
                        ApiBaseUrl: "http://node.invalid/",
                        Labels: new Dictionary<string, string>(),
                        AdvertisedBrickIds: Array.Empty<string>(),
                        Drained: existing?.Drained ?? false,
                        LastHeartbeatUtc: DateTimeOffset.UtcNow,
                        RegisteredAtUtc: existing?.RegisteredAtUtc ?? DateTimeOffset.UtcNow,
                        ReportedQueueDepth: queueDepth,
                        TrustTier: existing?.TrustTier ?? MeshFleetTrustTier.Trusted,
                        Admitted: existing?.Admitted ?? true,
                        RegistrationKeyFingerprint: existing?.RegistrationKeyFingerprint);
                })
            .GetAwaiter().GetResult();

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

        Task.WaitAll(threads);
    }

    /// <summary>Reads one node document raw, without the registry or its mapper.</summary>
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
