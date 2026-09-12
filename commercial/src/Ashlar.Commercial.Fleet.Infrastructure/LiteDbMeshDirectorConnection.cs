using LiteDB;
using Ashlar.Commercial.Fleet.Contracts.Models;

namespace Ashlar.Commercial.Fleet.Infrastructure;

/// <summary>Collection names shared by the two LiteDB-backed director registries.</summary>
/// <remarks>
/// Both registries are registered as singletons over the SAME file
/// (<c>FleetServiceCollectionExtensions</c>), which is why the constants live in one place — and
/// why each guarding itself with its own <c>SemaphoreSlim</c> was never enough: two different
/// objects do not serialise against each other. The Shared-mode named mutex composed by
/// <c>LiteDbConnectionString</c> is what actually covers that pair.
/// </remarks>
internal static class LiteDbMeshDirectorConnection
{
    internal const string TasksCollection = "mesh_tasks";
    internal const string FleetCollection = "mesh_fleet_nodes";
}
