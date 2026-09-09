using Ashlar.Agents.TestKit;
using Ashlar.Bricks.SqlProfile;
using Ashlar.Core.Domain.Bricks.Ports;
using Xunit;

namespace Ashlar.Tests.Application.Tests.Generation;

/// <summary>
/// Part (e): the SQL profile against the SHARED conformance suite. Every case here
/// is inherited — this file supplies only what is genuinely profile-specific.
/// </summary>
[Collection(OfflineAgentEnvironmentCollection.Name)]
public sealed class SqlProfileConformanceTests
    : AgentProfileConformanceTests<SqlAgentProfileFactory>
{
    /// <inheritdoc />
    protected override string ExpectedTargetId => SqlAgentProfileFactory.TargetId;

    /// <inheritdoc />
    protected override GenerationRequest OfflineRequest(OfflineAgentEnvironment environment) =>
        new()
        {
            TargetId = SqlAgentProfileFactory.TargetId,
            // The SQL deterministic path takes a bare table identifier.
            Grounding = "customers"
        };
}
