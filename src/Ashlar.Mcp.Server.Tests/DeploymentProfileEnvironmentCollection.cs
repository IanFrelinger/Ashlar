using Xunit;

namespace Ashlar.Mcp.Server.Tests;

/// <summary>
/// Keeps deployment-profile environment writes from overlapping any other collection in this assembly.
/// Restoring the original value does not protect concurrently running MCP bridge tests from a temporary
/// AirGapped profile; the production validator must continue refusing an enabled server under that profile.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DeploymentProfileEnvironmentCollection
{
    public const string Name = "DeploymentProfileEnvironment";
}
