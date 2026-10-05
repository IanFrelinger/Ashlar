using System.Collections.Concurrent;
using Ashlar.Abstractions.Security.Egress;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Ashlar.Mcp.Client.Tests;

/// <summary>
/// SPEC-007 PR 3b: the MCP HTTP transport's client records a decision under EG-XPT-06 when the manager connects.
/// No factory override: this drives the real CreateHttpClientAsync path at a loopback port nothing listens on.
/// </summary>
[Collection(DeploymentProfileEnvironmentCollection.Name)]
public sealed class McpEgressTwinTests
{
    private const string Refused = "http://127.0.0.1:1";

    [Fact]
    public async Task Connecting_over_the_transport_client_records_EG_XPT_06()
    {
        var sink = new SiteSink("EG-XPT-06");
        using var subscription = EgressDecisionLog.Subscribe(sink);
        var options = new AshlarMcpClientOptions { Enabled = true };
        options.Servers.Add(new McpServerEndpointOptions { Name = "twin", Url = Refused + "/mcp" });
        await using var manager = new McpClientConnectionManager(
            Options.Create(options), NullLoggerFactory.Instance, NullLogger<McpClientConnectionManager>.Instance);

        await manager.StartAsync(CancellationToken.None);

        manager.GetTools().Should().BeEmpty("a server that cannot be reached contributes no tools");
        sink.Seen.Should().NotBeEmpty().And.OnlyContain(d => d.Family == EgressFamilies.Mcp);
    }

    private sealed class SiteSink(string site) : IEgressDecisionSink
    {
        private readonly ConcurrentQueue<EgressDecision> _seen = new();

        public IReadOnlyList<EgressDecision> Seen => _seen.ToArray();

        public void Record(EgressDecision decision)
        {
            if (decision.Site == site && decision.Destination == Refused)
                _seen.Enqueue(decision);
        }
    }
}
