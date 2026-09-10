using Xunit;

namespace Ashlar.Mcp.Client.Tests;

/// <summary>
/// Runs every test class in this assembly that writes or reads the PROCESS-WIDE
/// <c>ASHLAR_DEPLOYMENT_PROFILE</c> environment variable on its own, with nothing else in flight.
/// <see cref="ValidateAshlarMcpClientOptionsTests"/> sets it to <c>AirGapped</c> /
/// <c>SecureWorkstation</c> variants (<c>Enabled_under_secure_workstation_profile_fails</c> / <c>Enabled_under_airgapped_profile_fails</c>) and clears
/// it in <c>finally</c>; <see cref="McpRoundTripTests"/> reads it indirectly — its
/// <c>ServerHarness.StartAsync</c> resolves the hosted bridge, whose first
/// <c>IOptions&lt;AshlarMcpServerOptions&gt;.Value</c> read runs
/// <c>ValidateAshlarMcpServerOptions.Validate</c> (<c>Ashlar.Mcp.Server/ValidateAshlarMcpServerOptions.cs:29-35</c>).
/// With xUnit's default class-level parallelism the <c>AirGapped</c> write can land between the
/// harness being built and the validator running, so every round-trip fact fails with
/// "Enabled=true is not permitted under the AirGapped deployment profile" — the same
/// env-var-leak shape fixed for <c>OfflineAgentEnvironment</c> in the application suite (#578).
/// <c>DisableParallelization</c> (the shape of <c>Trust/DataTaxonomyGapCollection</c> in the
/// background-agents suite) keeps this collection from overlapping ANY other collection, which is
/// what process-global state needs.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DeploymentProfileEnvironmentCollection
{
    /// <summary>Collection name shared by the classes that touch the deployment-profile variable.</summary>
    public const string Name = "DeploymentProfileEnvironment";
}
