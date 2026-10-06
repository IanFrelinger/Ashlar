namespace Ashlar.Abstractions.Security.Egress;

/// <summary>
/// The reset seam for the process-wide egress state (SPEC-007 PR 4.6, design §2.10): the deployment profile
/// <c>AddAshlar</c> noted, and the <c>ASHLAR_EGRESS_MODE</c> latch.
/// </summary>
/// <remarks>
/// <para>That state belongs to the process, like an environment variable: the strictest profile noted wins and
/// the variable is read once, so nothing a test does afterwards lowers it. A test that composes AirGapped or
/// SecureWorkstation, raises the mode, or sets the variable takes a <see cref="Snapshot"/> first and
/// <see cref="Restore"/>s it in <c>Dispose</c>, inside a non-parallel collection.
/// <c>ProcessGlobalEnvironmentConventionTests</c> treats any use of this seam as a process-global write.</para>
/// <para>Production code never calls it. Test assemblies reach it by reflection, through one helper.
/// <c>ProcessGlobalEnvironmentConventionTests</c> pins, file by file, who names it and the other members that write
/// this state outside its rules, since <c>InternalsVisibleTo</c> exposes them beyond this assembly.</para>
/// </remarks>
internal static class EgressProcessState
{
    /// <summary>Captures the noted profile and the mode latch.</summary>
    /// <returns>An opaque state for <see cref="Restore"/>.</returns>
    internal static object Snapshot() =>
        new State(AshlarDeploymentProfileEnvironment.ResolvedRaw, EgressEnforcement.CaptureLatch());

    /// <summary>Puts back a state <see cref="Snapshot"/> returned, bypassing the strictest-wins rule.</summary>
    /// <param name="snapshot">The state.</param>
    internal static void Restore(object snapshot)
    {
        var state = (State)snapshot;
        AshlarDeploymentProfileEnvironment.RestoreResolved(state.ResolvedRaw);
        EgressEnforcement.RestoreLatch(state.Latch);
    }

    /// <summary>Clears the noted profile and the latch, as in a process that has done nothing yet.</summary>
    internal static void Reset()
    {
        AshlarDeploymentProfileEnvironment.RestoreResolved(null);
        EgressEnforcement.ResetLatch();
    }

    private sealed class State
    {
        internal State(string? resolvedRaw, object latch)
        {
            ResolvedRaw = resolvedRaw;
            Latch = latch;
        }

        internal string? ResolvedRaw { get; }

        internal object Latch { get; }
    }
}
