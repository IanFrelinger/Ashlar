using System.Collections.Concurrent;
using System.Reflection;
using Ashlar.Abstractions.Security.Egress;
using Ashlar.CLI.Commands;
using Ashlar.CLI.Commands.BackgroundAgent;
using FluentAssertions;
using Xunit;

namespace Ashlar.Tests.CLI.Tests.Commands;

/// <summary>
/// SPEC-007 PR 3b, behavioural twin of the CLI's raw clients: each records a decision under its inventory site
/// when it sends. Each test drives the CLI's own client at a loopback port nothing listens on.
/// </summary>
public sealed class EgressCliRawClientTwinTests
{
    private const string Refused = "http://127.0.0.1:1";

    [Fact]
    public async Task Mesh_auto_pull_client_records_EG_MESH_04()
    {
        using var sink = SiteSink.Subscribe("EG-MESH-04");
        var http = (HttpClient)typeof(MeshAutoPullService).GetField("Http", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

        var summary = await MeshAutoPullService.PullPeerOnceAsync(http, Refused, Path.GetTempPath());

        summary.Errors.Should().Be(1);
        sink.Seen.Should().NotBeEmpty().And.OnlyContain(d => d.Family == EgressFamilies.MeshPull);
    }

    [Fact]
    public async Task Mesh_health_client_records_EG_HTTP_06()
    {
        using var sink = SiteSink.Subscribe("EG-HTTP-06");
        var health = typeof(MeshCommand).GetMethod("ExecuteHealthAsync", BindingFlags.NonPublic | BindingFlags.Static)!;

        var exit = await (Task<int>)health.Invoke(null, [Refused, 5])!;

        exit.Should().Be(1);
        sink.Seen.Should().NotBeEmpty().And.OnlyContain(d => d.Family == EgressFamilies.Http);
    }

    [Fact]
    public async Task Workflow_mesh_peer_client_records_EG_HTTP_05()
    {
        using var sink = SiteSink.Subscribe("EG-HTTP-05");
        var run = typeof(WorkflowCommand).GetMethod("ExecuteScenarioOnMeshPeerAsync", BindingFlags.NonPublic | BindingFlags.Static)!;

        await (Task)run.Invoke(null, [Refused, "twin", "{}", null, false, false, CancellationToken.None])!;

        sink.Seen.Should().NotBeEmpty().And.OnlyContain(d => d.Family == EgressFamilies.Http);
    }

    private sealed class SiteSink : IEgressDecisionSink, IDisposable
    {
        private readonly string _site;
        private readonly ConcurrentQueue<EgressDecision> _seen = new();
        private IDisposable? _subscription;

        private SiteSink(string site) => _site = site;

        public IReadOnlyList<EgressDecision> Seen => _seen.ToArray();

        public static SiteSink Subscribe(string site)
        {
            var sink = new SiteSink(site);
            sink._subscription = EgressDecisionLog.Subscribe(sink);
            return sink;
        }

        public void Record(EgressDecision decision)
        {
            if (decision.Site == _site && decision.Destination == Refused)
                _seen.Enqueue(decision);
        }

        public void Dispose() => _subscription?.Dispose();
    }
}
