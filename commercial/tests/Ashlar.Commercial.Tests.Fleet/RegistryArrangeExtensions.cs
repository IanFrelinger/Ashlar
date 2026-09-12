using Ashlar.Commercial.Fleet.Contracts.Models;
using Ashlar.Commercial.Fleet.Contracts.Ports;

namespace Ashlar.Commercial.Tests.Fleet;

/// <summary>
/// The unconditional whole-document writes the two fleet ports used to expose, kept here as
/// TEST-ONLY arrange helpers.
/// </summary>
/// <remarks>
/// <para><c>IMeshTaskRegistry.UpdateAsync(MeshTaskState)</c> and
/// <c>IFleetNodeRegistry.RegisterOrUpdateAsync(MeshFleetNodeState)</c> were removed from the ports
/// because they are the lost update: they take a document the caller read through an EARLIER store
/// call, on a database the store has since closed and reopened, and write every field of it back.
/// Product code can no longer spell that; the compiler finds any attempt.</para>
///
/// <para>Arrange code in a test is the one place where it is still the right thing to write, because
/// the test owns the file, there is no concurrent writer, and "put this exact document there" is the
/// whole intent. Keeping the shim in the test project rather than a default interface method on the
/// port is deliberate: a default interface member would be reachable from production again, and
/// <c>Ashlar.Core.Application</c> targets netstandard2.0 where they do not exist at all, so the two
/// halves of this change would not even be consistent with each other.</para>
///
/// <para>Do not use these in a race test. A concurrency fact that arranges through these is
/// arranging the very shape the fix removed.</para>
/// </remarks>
internal static class RegistryArrangeExtensions
{
    /// <summary>Writes <paramref name="task"/> verbatim, for arrange code only.</summary>
    /// <param name="registry">Registry under test.</param>
    /// <param name="task">The exact document to store.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True when a task with that id existed and was replaced.</returns>
    public static async Task<bool> UpdateAsync(
        this IMeshTaskRegistry registry,
        MeshTaskState task,
        CancellationToken cancellationToken = default)
    {
        var result = await registry.UpdateAsync(task.TaskId, _ => task, cancellationToken).ConfigureAwait(false);
        return result.Applied;
    }

    /// <summary>Writes <paramref name="node"/> verbatim, for arrange code only.</summary>
    /// <param name="registry">Registry under test.</param>
    /// <param name="node">The exact document to store.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The state that was written.</returns>
    public static Task<MeshFleetNodeState> RegisterOrUpdateAsync(
        this IFleetNodeRegistry registry,
        MeshFleetNodeState node,
        CancellationToken cancellationToken = default)
        => registry.RegisterOrMergeAsync(node.PeerId, _ => node, cancellationToken);
}
