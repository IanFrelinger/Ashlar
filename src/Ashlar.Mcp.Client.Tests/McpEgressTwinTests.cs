using System.Collections.Concurrent;
using Ashlar.Abstractions.Security.Egress;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using Microsoft.Extensions.Logging;

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
    public async Task Refused_connections_contribute_no_tools_and_share_a_disposable_warning_window()
    {
        var refusal = new EgressRefusedException(new EgressGuard("full", "enforce").Evaluate(
            new EgressRequest(EgressFamilies.Mcp, "mcp.connect", new Uri("https://remote.example"))));
        var options = new AshlarMcpClientOptions { Enabled = true };
        options.Servers.Add(new McpServerEndpointOptions { Name = "one", Url = "https://one.example/mcp" });
        options.Servers.Add(new McpServerEndpointOptions { Name = "two", Url = "https://two.example/mcp" });
        var logger = new RecordingLogger();
        var calls = 0;
        var manager = new McpClientConnectionManager(Options.Create(options), NullLoggerFactory.Instance, logger)
        {
            ClientFactoryOverride = (_, _) =>
            {
                calls++;
                throw new IOException("private-canary", refusal);
            }
        };
        await manager.StartAsync(CancellationToken.None);
        calls.Should().Be(2);
        manager.GetTools().Should().BeEmpty();
        logger.Warnings.Should().Equal(7307);
        await manager.DisposeAsync();
        logger.Warnings.Should().Equal(7307, 7307);
    }

    private sealed class RecordingLogger : ILogger<McpClientConnectionManager>
    {
        internal List<int> Warnings { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> format)
        {
            if (level == LogLevel.Warning) Warnings.Add(id.Id);
        }
    }

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
