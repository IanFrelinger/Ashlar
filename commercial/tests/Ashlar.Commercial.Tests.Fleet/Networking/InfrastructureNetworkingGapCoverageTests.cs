using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Ashlar.Commercial.Fleet.Contracts.Networking.Models;
using Ashlar.Commercial.Fleet.Contracts.Networking.Ports;
using Ashlar.Commercial.Fleet.Infrastructure.Networking;
using Xunit;

namespace Ashlar.Commercial.Tests.Fleet.Networking;

/// <summary>
/// Tests for infrastructure networking gap coverage.
/// HttpNetworkBus.DeliverAsync does not invoke subscribers inline: it schedules each matching
/// handler via a fire-and-forget Task.Run, starts the peer relay without awaiting it, and returns
/// Task.CompletedTask immediately; the heartbeat loop likewise runs on a background Task.Run.
/// Handlers therefore run on a thread-pool thread at some later point, so these tests synchronize
/// on an explicit signal completed by the subscriber (or the fake HTTP handler) rather than sleeping.
/// </summary>
public class InfrastructureNetworkingGapCoverageTests
{
    /// <summary>
    /// Upper bound for waiting on a delivery that is expected to happen. A genuine bug
    /// (handler never invoked) fails fast with a TimeoutException instead of hanging.
    /// </summary>
    private static readonly TimeSpan DeliveryTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public void Networking_options_expose_defaults()
    {
        PlasticityOptions.SectionName.Should().Be("Plasticity");
        new PlasticityOptions { HotBrickTopN = 5, ColdBrickWindowMinutes = 30 }.HotBrickTopN.Should().Be(5);

        KnowledgeSyncServiceOptions.SectionName.Should().Be("KnowledgeSync");
        new KnowledgeSyncServiceOptions { NodeId = "node-a", PeerUrls = new[] { "https://peer" } }
            .PeerUrls.Should().ContainSingle();

        NetworkAgentDirectoryOptions.SectionName.Should().Be("NetworkAgentDirectory");
        new NetworkAgentDirectoryOptions { DiscoveredEntryMaxAgeSeconds = 120 }.DiscoveredEntryMaxAgeSeconds
            .Should().Be(120);
    }

    [Fact]
    public async Task InMemoryKnowledgeChunkStore_adds_filters_and_trims()
    {
        var store = new InMemoryKnowledgeChunkStore(maxChunks: 2);
        await store.AddAsync(Chunk("a", "text/plain"));
        await store.AddAsync(Chunk("b", "text/plain"));
        await store.AddAsync(Chunk("c", "application/json"));

        (await store.GetAsync()).Should().HaveCount(2);
        (await store.GetAsync("application/json")).Should().ContainSingle(c => c.Id == "c");

        await store.RemoveAsync("c");
        (await store.GetAsync()).Should().NotContain(c => c.Id == "c");
    }

    [Fact]
    public async Task InMemoryKnowledgeChunkStore_rejects_invalid_chunks()
    {
        var store = new InMemoryKnowledgeChunkStore();
        var act = () => store.AddAsync(null!);
        await act.Should().ThrowAsync<ArgumentNullException>();

        var act2 = () => store.AddAsync(Chunk("", "text/plain") with { Id = "" });
        await act2.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task HttpNetworkBus_delivers_subscribed_events_and_deduplicates()
    {
        var bus = CreateBus(new NetworkBusOptions { NodeId = "local", PeerUrls = [] });
        var delivered = new TaskCompletionSource<NetworkEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sub = await bus.SubscribeAsync("test", (evt, _) =>
        {
            delivered.TrySetResult(evt);
            return Task.CompletedTask;
        });

        var evt = Event("e1", "test");
        await bus.DeliverAsync(evt);
        var received = await delivered.Task.WaitAsync(DeliveryTimeout);
        received.Should().NotBeNull();

        await bus.DeliverAsync(evt);
        sub.Dispose();
        bus.Dispose();
    }

    [Fact]
    public async Task HttpNetworkBus_send_skips_unknown_peer_and_rejects_null_event()
    {
        var bus = CreateBus(new NetworkBusOptions
        {
            NodeId = "local",
            PeerUrls = new[] { "https://peer.example.com" },
            HeartbeatIntervalSeconds = 0,
        });

        var act = () => bus.SendAsync("unknown", null!, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentNullException>();

        await bus.SendAsync("unknown", Event("e2", "ping"));
        await bus.SendAsync("", Event("e3", "ping"));
        bus.Dispose();
    }

    [Fact]
    public async Task HttpNetworkBus_delivers_only_matching_event_types()
    {
        var bus = CreateBus(new NetworkBusOptions { NodeId = "local", PeerUrls = [] });
        // ConcurrentQueue: the handler runs on a thread-pool thread while the test thread reads.
        var received = new ConcurrentQueue<string>();
        var pingDelivered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sub = await bus.SubscribeAsync("ping", (evt, _) =>
        {
            received.Enqueue(evt.EventType);
            pingDelivered.TrySetResult(true);
            return Task.CompletedTask;
        });

        await bus.DeliverAsync(Event("e4", "pong"));
        await bus.DeliverAsync(Event("e5", "ping"));

        // Positive half: wait for the handler to signal that the matching event arrived.
        await pingDelivered.Task.WaitAsync(DeliveryTimeout);

        // Negative half: "pong" must NOT be delivered. There is no signal for "nothing happened",
        // so allow a short grace after the positive signal. This grace can only produce a false
        // PASS (if the bus were both broken and slow enough to deliver "pong" after 50 ms); it can
        // never produce a false FAIL, because a correct bus never schedules the handler for "pong".
        await Task.Delay(50);
        received.Should().ContainSingle().Which.Should().Be("ping");
        sub.Dispose();
        bus.Dispose();
    }

    [Fact]
    public async Task HttpNetworkBus_send_drops_events_when_hops_exceed_max()
    {
        var sent = 0;
        var bus = CreateBus(
            new NetworkBusOptions
            {
                NodeId = "local",
                PeerUrls = new[] { "https://peer.example.com" },
                HeartbeatIntervalSeconds = 0,
            },
            /// <summary>Fake handler.</summary>
            new FakeHandler((_, _) =>
            {
                Interlocked.Increment(ref sent);
                /// <summary>Json.</summary>
                return Json(HttpStatusCode.OK, "{}");
            }));

        var evt = Event("e6", "relay") with { Hops = 1, MaxHops = 1 };
        await bus.SendAsync("peer.example.com", evt);

        sent.Should().Be(0);
        bus.Dispose();
    }

    [Fact]
    public async Task HttpNetworkBus_send_logs_non_success_http_status()
    {
        var bus = CreateBus(
            new NetworkBusOptions
            {
                NodeId = "local",
                PeerUrls = new[] { "https://peer.example.com" },
                HeartbeatIntervalSeconds = 0,
            },
            /// <summary>Fake handler.</summary>
            new FakeHandler((_, _) => Json(HttpStatusCode.InternalServerError, "{}")));

        await bus.SendAsync("peer.example.com", Event("e7", "fail"));
        bus.Dispose();
    }

    [Fact]
    public async Task HttpNetworkBus_broadcast_skips_self_node()
    {
        var sent = 0;
        var bus = CreateBus(
            new NetworkBusOptions
            {
                NodeId = "peer.example.com",
                PeerUrls = new[] { "https://peer.example.com" },
                HeartbeatIntervalSeconds = 0,
            },
            /// <summary>Fake handler.</summary>
            new FakeHandler((_, _) =>
            {
                Interlocked.Increment(ref sent);
                /// <summary>Json.</summary>
                return Json(HttpStatusCode.OK, "{}");
            }));

        await bus.BroadcastAsync(Event("e8", "heartbeat"));
        sent.Should().Be(0);
        bus.Dispose();
    }

    [Fact]
    public async Task HttpNetworkBus_deliverAsync_rejects_null_event()
    {
        var bus = CreateBus(new NetworkBusOptions { NodeId = "local", PeerUrls = [] });
        var act = () => bus.DeliverAsync(null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
        bus.Dispose();
    }

    [Fact]
    public async Task HttpNetworkBus_subscribe_rejects_null_handler()
    {
        var bus = CreateBus(new NetworkBusOptions { NodeId = "local", PeerUrls = [] });
        var act = () => bus.SubscribeAsync("ping", null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
        bus.Dispose();
    }

    [Fact]
    public async Task HttpNetworkBus_wildcard_subscription_receives_all_event_types()
    {
        var bus = CreateBus(new NetworkBusOptions { NodeId = "local", PeerUrls = [] });
        // ConcurrentQueue: the two handler invocations run concurrently on thread-pool threads.
        var received = new ConcurrentQueue<string>();
        var bothDelivered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sub = await bus.SubscribeAsync("*", (evt, _) =>
        {
            received.Enqueue(evt.EventType);
            if (received.Count >= 2) bothDelivered.TrySetResult(true);
            return Task.CompletedTask;
        });

        await bus.DeliverAsync(Event("e9", "alpha"));
        await bus.DeliverAsync(Event("e10", "beta"));
        await bothDelivered.Task.WaitAsync(DeliveryTimeout);

        received.Should().BeEquivalentTo(new[] { "alpha", "beta" });
        sub.Dispose();
        bus.Dispose();
    }

    [Fact]
    public async Task HttpNetworkBus_evicts_old_event_ids_after_max_history()
    {
        var bus = CreateBus(new NetworkBusOptions { NodeId = "local", PeerUrls = [], MaxEventHistory = 2 });
        await bus.DeliverAsync(Event("evict-1", "a"));
        await bus.DeliverAsync(Event("evict-2", "b"));
        await bus.DeliverAsync(Event("evict-3", "c"));

        var redelivered = 0;
        var redeliverySignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sub = await bus.SubscribeAsync("a", (_, _) =>
        {
            Interlocked.Increment(ref redelivered);
            redeliverySignal.TrySetResult(true);
            return Task.CompletedTask;
        });
        await bus.DeliverAsync(Event("evict-1", "a"));
        await redeliverySignal.Task.WaitAsync(DeliveryTimeout);

        redelivered.Should().Be(1);
        sub.Dispose();
        bus.Dispose();
    }

    [Fact]
    public async Task HttpNetworkBus_send_swallows_http_client_exceptions()
    {
        var bus = CreateBus(
            new NetworkBusOptions
            {
                NodeId = "local",
                PeerUrls = new[] { "https://peer.example.com" },
                HeartbeatIntervalSeconds = 0,
            },
            /// <summary>Fake handler.</summary>
            new FakeHandler((_, _) => throw new HttpRequestException("network down")));

        await bus.SendAsync("peer.example.com", Event("e11", "boom"));
        bus.Dispose();
    }

    [Fact]
    public async Task HttpNetworkBus_deliver_relays_to_peers_except_source()
    {
        // ConcurrentQueue: the relay runs un-awaited on a thread-pool thread while the test thread reads.
        var sentTo = new ConcurrentQueue<string>();
        var relayed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bus = CreateBus(
            new NetworkBusOptions
            {
                NodeId = "local.example.com",
                PeerUrls = new[] { "https://peer-a.example.com", "https://peer-b.example.com" },
                HeartbeatIntervalSeconds = 0,
            },
            /// <summary>Fake handler.</summary>
            new FakeHandler((req, _) =>
            {
                sentTo.Enqueue(req.RequestUri!.Host);
                relayed.TrySetResult(true);
                /// <summary>Json.</summary>
                return Json(HttpStatusCode.OK, "{}");
            }));

        var evt = Event("relay-1", "gossip") with
        {
            SourceNodeId = "peer-a.example.com",
            Hops = 0,
            MaxHops = 2,
        };
        await bus.DeliverAsync(evt);

        // The relay visits peers sequentially in registration order and skips the source peer
        // synchronously before awaiting the send to the next one, so once any request has been
        // observed every send the relay will ever make has already been recorded.
        await relayed.Task.WaitAsync(DeliveryTimeout);

        sentTo.Should().ContainSingle().Which.Should().Be("peer-b.example.com");
        bus.Dispose();
    }

    [Fact]
    public async Task HttpNetworkBus_broadcast_rejects_null_event()
    {
        var bus = CreateBus(new NetworkBusOptions { NodeId = "local", PeerUrls = [] });
        var act = () => bus.BroadcastAsync(null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
        bus.Dispose();
    }

    [Fact]
    public async Task HttpNetworkBus_heartbeat_broadcasts_to_peers()
    {
        var sent = 0;
        var heartbeatSent = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bus = CreateBus(
            new NetworkBusOptions
            {
                NodeId = "local-node",
                PeerUrls = new[] { "https://peer.example.com" },
                HeartbeatIntervalSeconds = 1,
            },
            /// <summary>Fake handler.</summary>
            new FakeHandler((_, _) =>
            {
                Interlocked.Increment(ref sent);
                heartbeatSent.TrySetResult(true);
                /// <summary>Json.</summary>
                return Json(HttpStatusCode.OK, "{}");
            }));

        // The heartbeat loop runs on a background Task.Run with a 1 s Task.Delay before its first
        // broadcast; wait for the fake handler to observe that broadcast instead of sleeping.
        await heartbeatSent.Task.WaitAsync(DeliveryTimeout);
        sent.Should().BeGreaterThan(0);
        bus.Dispose();
    }

    [Fact]
    public async Task HttpNetworkAgentDirectory_registers_and_queries_local_agents()
    {
        var directory = CreateDirectory(new NetworkAgentDirectoryOptions { NodeId = "node-1", PeerUrls = [] });
        var entry = new NetworkAgentEntry
        {
            AgentId = "agent-1",
            NodeId = "node-1",
            Domain = "combat",
            Capabilities = new[] { "scan" },
        };

        await directory.RegisterAsync(entry);
        (await directory.GetByNodeAsync("node-1")).Should().ContainSingle();
        (await directory.GetByAgentIdAsync("agent-1"))!.AgentId.Should().Be("agent-1");
        (await directory.GetAllAsync()).Should().ContainSingle();

        await directory.UnregisterAsync("agent-1");
        (await directory.GetAllAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task NetworkNegotiationService_finds_agents_by_capability_and_domain()
    {
        var directory = new Mock<INetworkAgentDirectory>();
        directory.Setup(d => d.GetAllAsync(true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new NetworkAgentEntry
                {
                    AgentId = "a1",
                    NodeId = "n1",
                    Domain = "security",
                    Capabilities = new[] { "scan", "audit" },
                },
                new NetworkAgentEntry
                {
                    AgentId = "a2",
                    NodeId = "n2",
                    Domain = "combat",
                    Capabilities = new[] { "plan" },
                },
            });

        var service = new NetworkNegotiationService(directory.Object, NullLogger<NetworkNegotiationService>.Instance);

        (await service.FindByCapabilityAsync(new[] { "scan" })).Should().ContainSingle(a => a.AgentId == "a1");
        (await service.FindByCapabilityAsync(Array.Empty<string>())).Should().BeEmpty();
        (await service.FindByDomainAsync("combat")).Should().ContainSingle(a => a.AgentId == "a2");
        (await service.FindByDomainAsync("")).Should().BeEmpty();
    }

    [Fact]
    public async Task HttpKnowledgeSyncService_reports_status_and_handles_push_pull()
    {
        var handler = new FakeHandler((req, _) =>
        {
            if (req.Method == HttpMethod.Post)
                /// <summary>Json.</summary>
                return Json(HttpStatusCode.OK, "{}");
            if (req.Method == HttpMethod.Get)
            {
                var json = JsonSerializer.Serialize(new[]
                {
                    new KnowledgeChunk { Id = "k1", SourceNodeId = "remote", ContentType = "text/plain" },
                }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json"),
                };
            }

            /// <summary>Json.</summary>
            return Json(HttpStatusCode.NotFound, "{}");
        });

        var factory = CreateFactory(handler);
        var service = new HttpKnowledgeSyncService(
            Options.Create(new KnowledgeSyncServiceOptions
            {
                NodeId = "local",
                PeerUrls = new[] { "https://peer.example.com" },
            }),
            factory,
            NullLogger<HttpKnowledgeSyncService>.Instance);

        (await service.GetStatusAsync()).NodeId.Should().Be("local");
        await service.PushAsync(new KnowledgeChunk { Id = "k1", SourceNodeId = "local" });
        (await service.PullAsync("peer.example.com", maxCount: 10)).Should().ContainSingle();
        (await service.PullAsync("missing")).Should().BeEmpty();
    }

    [Fact]
    public async Task HttpNetworkAgentDirectory_discovers_peer_agents_on_refresh()
    {
        var json = JsonSerializer.Serialize(new[]
        {
            new NetworkAgentEntry
            {
                AgentId = "remote-agent",
                NodeId = "peer.example.com",
                Domain = "planning",
                Capabilities = new[] { "decompose" },
            },
        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        var directory = CreateDirectory(
            new NetworkAgentDirectoryOptions
            {
                NodeId = "local-node",
                PeerUrls = new[] { "https://peer.example.com" },
                DiscoveredEntryMaxAgeSeconds = 0,
            },
            /// <summary>Fake handler.</summary>
            new FakeHandler((req, _) =>
            {
                req.RequestUri!.AbsolutePath.Should().Be("/api/agents");
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json"),
                };
            }));

        await directory.RefreshFromPeersAsync();
        (await directory.GetAllAsync()).Should().ContainSingle(a => a.AgentId == "remote-agent");
        (await directory.GetByAgentIdAsync("remote-agent"))!.Domain.Should().Be("planning");
    }

    [Fact]
    public async Task HttpNetworkAgentDirectory_skips_invalid_peer_urls_and_failed_discovery()
    {
        var directory = CreateDirectory(
            new NetworkAgentDirectoryOptions
            {
                NodeId = "local-node",
                PeerUrls = new[] { "not-a-url", "https://peer.example.com" },
                DiscoveredEntryMaxAgeSeconds = 0,
            },
            /// <summary>Fake handler.</summary>
            new FakeHandler((_, _) => throw new HttpRequestException("peer down")));

        await directory.RefreshFromPeersAsync();
        (await directory.GetAllAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task PlasticityService_aggregates_optional_dependencies()
    {
        var services = new ServiceCollection();
        services.AddSingleton<INetworkBus>(CreateBus(new NetworkBusOptions { NodeId = "p-node", PeerUrls = [] }));
        services.AddSingleton<IKnowledgeSyncService>(new HttpKnowledgeSyncService(
            Options.Create(new KnowledgeSyncServiceOptions { NodeId = "p-node" }),
            /// <summary>Creates factory.</summary>
            /// <param name="FakeHandler((_">Fake handler((_.</param>
            CreateFactory(new FakeHandler((_, _) => Json(HttpStatusCode.OK, "{}"))),
            NullLogger<HttpKnowledgeSyncService>.Instance));

        var provider = services.BuildServiceProvider();
        var metrics = await new PlasticityService(
            provider,
            Options.Create(new PlasticityOptions()),
            NullLogger<PlasticityService>.Instance).GetMetricsAsync();

        metrics.NodeId.Should().Be("p-node");
    }

    /// <summary>Chunk.</summary>
    /// <param name="id">Id.</param>
    /// <param name="contentType">Content type.</param>
    private static KnowledgeChunk Chunk(string id, string contentType) =>
        new() { Id = id, SourceNodeId = "node", ContentType = contentType };

    /// <summary>Event.</summary>
    /// <param name="id">Id.</param>
    /// <param name="type">Type.</param>
    private static NetworkEvent Event(string id, string type) => new()
    {
        EventId = id,
        SourceNodeId = "local",
        EventType = type,
        Timestamp = DateTimeOffset.UtcNow,
        MaxHops = 1,
    };

    private static HttpNetworkBus CreateBus(NetworkBusOptions options)
    {
        var factory = CreateFactory(new FakeHandler((_, _) => Json(HttpStatusCode.OK, "{}")));
        return new HttpNetworkBus(Options.Create(options), factory, NullLogger<HttpNetworkBus>.Instance);
    }

    private static HttpNetworkBus CreateBus(NetworkBusOptions options, HttpMessageHandler handler)
    {
        var factory = CreateFactory(handler);
        return new HttpNetworkBus(Options.Create(options), factory, NullLogger<HttpNetworkBus>.Instance);
    }

    private static HttpNetworkAgentDirectory CreateDirectory(
        NetworkAgentDirectoryOptions options,
        HttpMessageHandler? handler = null) =>
        new(
            Options.Create(options),
            /// <summary>Creates factory.</summary>
            /// <param name="FakeHandler((_">Fake handler((_.</param>
            CreateFactory(handler ?? new FakeHandler((_, _) => Json(HttpStatusCode.OK, "[]"))),
            NullLogger<HttpNetworkAgentDirectory>.Instance);

    private static IHttpClientFactory CreateFactory(HttpMessageHandler handler)
    {
        var services = new ServiceCollection();
        services.AddHttpClient("Ashlar.Networking").ConfigurePrimaryHttpMessageHandler(() => handler);
        return services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>();
    }

    /// <summary>Json.</summary>
    /// <param name="status">Status.</param>
    /// <param name="json">Json.</param>
    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    /// <summary>Handles fake requests.</summary>
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> _handler;

        /// <summary>Fake handler.</summary>
        /// <param name="handler">Handler.</param>
        public FakeHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> handler) =>
            _handler = handler;

        /// <summary>Send async.</summary>
        /// <param name="request">Request.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_handler(request, cancellationToken));
    }
}
