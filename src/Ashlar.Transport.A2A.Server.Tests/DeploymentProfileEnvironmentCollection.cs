using Xunit;

namespace Ashlar.Transport.A2A.Server.Tests;

/// <summary>
/// Runs every test class in this assembly that writes or reads the PROCESS-WIDE
/// <c>ASHLAR_DEPLOYMENT_PROFILE</c> environment variable on its own, with nothing else in flight.
/// </summary>
/// <remarks>
/// <para><b>This is the same defect the MCP client suite already fixed</b>, in the same variable,
/// through the same validator shape — see
/// <c>src/Ashlar.Mcp.Client.Tests/DeploymentProfileEnvironmentCollection.cs</c>. The A2A server
/// suite is its untreated twin.</para>
///
/// <para><see cref="ValidateAshlarA2AServerOptionsTests"/> sets the variable to
/// <c>airgapped</c> / <c>secure-workstation</c> variants in
/// <c>Enabled_under_no_egress_profiles_fails</c> and clears it in a <c>finally</c>.
/// <see cref="A2AServerRoundTripTests"/> reads it indirectly: it builds a host, and the first
/// <c>IOptions&lt;AshlarA2AServerOptions&gt;.Value</c> read runs
/// <c>ValidateAshlarA2AServerOptions.Validate</c>. Under xUnit's default class-level parallelism
/// the write lands inside that window and the round trip fails with "Enabled=true is not permitted
/// under the AirGapped deployment profile". <b>Restoring in a <c>finally</c> does nothing about
/// it</b> — the window between the write and the restore is the whole problem.</para>
///
/// <para>Observed in CI on <c>Failed_agent_execution_surfaces_as_a_failed_task_result</c>, on
/// unrelated branches and on <c>master</c> itself (Full Platform Readiness Gate runs 34672054742
/// and 34666565242), which is what an intermittent cross-class race looks like: it lands on
/// whichever pull request happens to be running when the scheduler interleaves them.</para>
///
/// <para><c>DisableParallelization</c> keeps this collection from overlapping ANY other
/// collection, which is what process-global state needs. The other two classes here
/// (<c>AshlarA2ACardProjectorTests</c>, <c>AshlarA2AExposurePolicyTests</c>) construct
/// <c>AshlarA2AServerOptions</c> directly and never reach the validator, so they stay out —
/// the same scoping the MCP fix used.</para>
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DeploymentProfileEnvironmentCollection
{
    /// <summary>Collection name shared by the classes that touch the deployment-profile variable.</summary>
    public const string Name = "DeploymentProfileEnvironment";
}
