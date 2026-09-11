using System.Collections.Concurrent;
using FluentAssertions;
using LiteDB;
using Ashlar.Commercial.Fleet.Contracts.Models;
using Ashlar.Commercial.Fleet.Infrastructure;
using Xunit;

namespace Ashlar.Commercial.Tests.Fleet;

/// <summary>
/// The fleet node registry must survive concurrent FIRST use of its document type, on the READ path
/// as well as the write one.
/// </summary>
/// <remarks>
/// This is the deployment shape the director actually has: several HTTP threads registering peers
/// and listing them against one process-wide <c>BsonMapper</c>, with the registry's own
/// <c>SemaphoreSlim</c> serialising nothing but this instance.
///
/// It exists as a second class rather than a case on the mesh-task one because it pins a different
/// half of the warm-up. <c>ListAsync</c> DESERIALIZES, and deserialization walks the member list
/// again in <c>BsonMapper.GetTypeCtor</c> — for the <c>EntityMapper</c> of a member type, here
/// <c>MeshFleetNodeDoc.Labels</c>, that serializing an empty instance never builds. A warm-up that
/// only called <c>ToDocument</c> therefore left this call a concurrent first touch: measured at 60
/// rounds x 8 threads over the two commercial document types, 5 of 8 iterations threw, 21 throws in
/// all, every one <c>InvalidOperationException("Collection was modified")</c> out of
/// <c>GetTypeCtor</c> under <c>Deserialize</c>, and <c>LiteDbFleetNodeRegistry.ListAsync</c> is
/// where they surfaced. Round-tripping the warm-up through <c>ToObject</c> gave 8 of 8 clean.
///
/// The mapper is reset so the race window is open on purpose: once a type is fully built the site
/// stops failing for the life of the process, so a test that ran after another class warmed
/// <c>MeshFleetNodeDoc</c> would pin nothing. This class joins <see cref="LiteDbFleetCollection"/>,
/// which already runs alone, because that reset is process-global. What the registry RETURNS is
/// pinned by <c>LiteDbFleetNodeRegistryGapCoverageTests</c>, on a warm mapper.
/// </remarks>
[Collection(nameof(LiteDbFleetCollection))]
public sealed class LiteDbFleetNodeRegistryMapperConcurrencyTests : IDisposable
{
    private const int Threads = 8;

    /// <summary>
    /// Rounds of the race per test, each against a mapper reset again.
    /// </summary>
    /// <remarks>
    /// One round is not enough to pin this from inside a test host — the pre-fix code passed a
    /// single-round test five runs in a row while a console harness doing the identical race failed
    /// 18 of 20 rounds. Twenty matches the core suite.
    /// </remarks>
    private const int Rounds = 20;

    private static readonly DateTimeOffset Anchor = new(2024, 5, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly string _root;

    /// <summary>Initializes a new lite db fleet node registry mapper concurrency tests.</summary>
    public LiteDbFleetNodeRegistryMapperConcurrencyTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"ashlar-fleet-mapper-{Guid.NewGuid():N}");
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
            "concurrent first use of MeshFleetNodeDoc must not throw — including out of " +
            "BsonMapper.GetTypeCtor under Deserialize, which is where ListAsync failed while the " +
            "warm-up only serialized");

        shortDocuments.Should().BeEmpty(
            "every fleet node registration written while racing must carry every field the " +
            "reference write carried. A document serialized from a half-built EntityMapper is " +
            "written short and the insert still returns success");
    }

    /// <summary>
    /// One racer's work: register a peer, then read the collection back.
    /// </summary>
    /// <remarks>
    /// <c>ListAsync</c> rather than <c>GetAsync</c> because it is the call the director serves on
    /// every fleet page and the one the measured throws came out of; both reach the same
    /// deserializer.
    /// </remarks>
    private static void Race(int index, string dbPath)
    {
        var registry = new LiteDbFleetNodeRegistry(dbPath);
        registry.RegisterOrUpdateAsync(new MeshFleetNodeState(
            $"peer-{index}",
            $"http://node-{index}.invalid",
            new Dictionary<string, string> { ["zone"] = "a" },
            new[] { "brick-1" },
            Drained: false,
            LastHeartbeatUtc: Anchor,
            RegisteredAtUtc: Anchor,
            ReportedQueueDepth: 1,
            TrustTier: MeshFleetTrustTier.Trusted,
            Admitted: true,
            RegistrationKeyFingerprint: "fingerprint")).GetAwaiter().GetResult();

        registry.ListAsync().GetAwaiter().GetResult();
    }

    /// <summary>
    /// The widest document a racer writes when nothing is racing it.
    /// </summary>
    /// <remarks>
    /// A reference rather than a hand-counted constant: a field added to <c>MeshFleetNodeDoc</c> must
    /// not need this file edited, and a constant that drifted low would silently stop detecting
    /// anything. Single threaded, so the mapper it serializes through cannot be half-built.
    /// </remarks>
    private int WidestDocument()
    {
        var referencePath = Path.Combine(_root, "fleet-reference.db");
        Race(0, referencePath);

        using var db = new LiteDatabase($"Filename={referencePath}");
        var widest = 0;
        foreach (var document in db.GetCollection("mesh_fleet_nodes").FindAll())
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
                    // Each thread on its own file, so LiteDB's per-file lock is out of the way and a
                    // failure here can only be the mapper.
                    dbPaths[i] = Path.Combine(_root, $"fleet-{round}-{i}.db");

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
                    foreach (var document in db.GetCollection("mesh_fleet_nodes").FindAll())
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
