using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
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
/// <remarks>
/// <para>The route turned each client's object initializer into property assignments (R8), so the twins also pin
/// the preserved timeouts they can reach: the auto-pull client's 15 s, read off its static client, and the health
/// client's lower clamp of 5 s, observed through a peer that accepts and never answers.</para>
/// <para>Not pinned: the health clamp's 120 s ceiling, and WorkflowCommand's 3 min on EG-HTTP-05. Both clients are
/// locals of the method that sends, so a timeout is visible only by waiting it out, which costs 120 s and 180 s.</para>
/// </remarks>
public sealed class EgressCliRawClientTwinTests
{
    private const string Refused = "http://127.0.0.1:1";

    [Fact]
    public async Task Mesh_auto_pull_client_records_EG_MESH_04()
    {
        using var sink = SiteSink.Subscribe("EG-MESH-04");
        var http = (HttpClient)typeof(MeshAutoPullService).GetField("Http", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

        http.Timeout.Should().Be(TimeSpan.FromSeconds(15), "the route keeps the auto-pull timeout (R8)");

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
    public async Task Mesh_health_client_raises_a_1_second_timeout_to_its_5_second_floor()
    {
        using var peer = new TcpListener(IPAddress.Loopback, 0);
        peer.Start();
        var url = $"http://127.0.0.1:{((IPEndPoint)peer.LocalEndpoint).Port}";
        // Accept the connection and never answer, so only the client's own timeout ends the call. After 30 s the
        // backstop closes the connection and stops the listener, so a client without its timeout fails here instead
        // of waiting out the 100 s default (a still-listening port would take its retry into the backlog and hang).
        using var backstop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var stopListening = backstop.Token.Register(peer.Stop);
        var hold = Task.Run(async () =>
        {
            using var held = await peer.AcceptTcpClientAsync(backstop.Token);
            await Task.Delay(Timeout.Infinite, backstop.Token);
        });
        var health = typeof(MeshCommand).GetMethod("ExecuteHealthAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
        using var stderr = new StringWriter();

        int exit;
        Console.SetError(stderr);
        try
        {
            exit = await (Task<int>)health.Invoke(null, [url, 1])!;
        }
        finally
        {
            Console.SetError(ConsoleCapture.Error);
            await backstop.CancelAsync();
        }
        await hold.ContinueWith(static _ => { }, TaskScheduler.Default);

        exit.Should().Be(1);
        stderr.ToString().Should().Contain("HttpClient.Timeout of 5 seconds", "Math.Clamp(1, 5, 120) is 5 (R8)");
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
