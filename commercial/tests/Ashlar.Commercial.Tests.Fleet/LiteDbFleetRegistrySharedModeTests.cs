using FluentAssertions;
using LiteDB;
using Ashlar.Commercial.Fleet.Contracts.Models;
using Ashlar.Commercial.Fleet.Infrastructure;
using Ashlar.Core.Application.Persistence;
using Xunit;

namespace Ashlar.Commercial.Tests.Fleet;

/// <summary>
/// The two director registries share one file, and must not lose writes to each other.
/// </summary>
/// <remarks>
/// <para><c>FleetServiceCollectionExtensions</c> registers <c>LiteDbFleetNodeRegistry</c> and
/// <c>LiteDbMeshTaskRegistry</c> as singletons over the SAME <c>dbPath</c>. Each guards itself with
/// its own <c>SemaphoreSlim</c>, and those are two different objects — so in isolation each registry
/// looks serialised and in production neither serialises against the other. Under LiteDB's default
/// Direct mode that pair is two exclusive file locks on one file; the writes that lose the race are
/// gone, silently, on Linux. The Shared-mode named mutex composed by
/// <see cref="LiteDbConnectionString"/> is the only thing that actually covers them.</para>
///
/// <para>Asserted on readable COUNTS rather than on an expected <c>IOException</c>: Direct refuses
/// the second open on Windows but merely corrupts pages on Linux, so an exception-shaped assertion
/// would pass locally and assert nothing in CI.</para>
/// </remarks>
[Collection(nameof(LiteDbFleetCollection))]
public sealed class LiteDbFleetRegistrySharedModeTests : IDisposable
{
    private const int WritesPerRegistry = 25;

    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), $"ashlar-fleet-shared-{Guid.NewGuid():N}");

    /// <summary>Initializes a new lite db fleet registry shared mode tests.</summary>
    public LiteDbFleetRegistrySharedModeTests() => Directory.CreateDirectory(_dir);

    [Fact]
    public async Task Both_registries_writing_one_file_lose_nothing()
    {
        var path = Path.Combine(_dir, "mesh.db");
        var nodes = new LiteDbFleetNodeRegistry(path);
        var tasks = new LiteDbMeshTaskRegistry(path);

        var ready = new Barrier(2);

        var registering = Task.Run(async () =>
        {
            ready.SignalAndWait();
            for (var i = 0; i < WritesPerRegistry; i++)
            {
                await nodes.RegisterOrUpdateAsync(new MeshFleetNodeState(
                    PeerId: Guid.NewGuid().ToString("N"),
                    ApiBaseUrl: "http://node.invalid",
                    Labels: new Dictionary<string, string>(),
                    AdvertisedBrickIds: Array.Empty<string>(),
                    Drained: false,
                    LastHeartbeatUtc: null,
                    RegisteredAtUtc: DateTimeOffset.UtcNow)).ConfigureAwait(false);
            }
        });

        var creating = Task.Run(async () =>
        {
            ready.SignalAndWait();
            for (var i = 0; i < WritesPerRegistry; i++)
            {
                await tasks.CreateAsync(new MeshTaskCreateSpec(
                    Name: $"task-{i}",
                    Steps: 1,
                    RequiredBrickIds: Array.Empty<string>(),
                    Affinity: null,
                    Priority: 0,
                    DeadlineUtc: null)).ConfigureAwait(false);
            }
        });

        await Task.WhenAll(registering, creating);

        RawCount(path, "mesh_fleet_nodes").Should().Be(WritesPerRegistry, "every registered node must be readable");
        RawCount(path, "mesh_tasks").Should().Be(WritesPerRegistry, "every created task must be readable");
    }

    [Fact]
    public void Registries_reject_a_blank_path()
    {
        var node = () => new LiteDbFleetNodeRegistry("  ");
        var task = () => new LiteDbMeshTaskRegistry("  ");

        node.Should().Throw<ArgumentNullException>().WithParameterName("pathOrConnectionString");
        task.Should().Throw<ArgumentNullException>().WithParameterName("pathOrConnectionString");
    }

    /// <summary>Counts documents without going through either registry's document type or its mapper.</summary>
    private static long RawCount(string path, string collection)
    {
        using var db = new LiteDatabase(LiteDbConnectionString.ForSharedAccess(path));
        return db.GetCollection(collection).LongCount();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* a temp directory that outlives the test is not a test failure */ }
    }
}
