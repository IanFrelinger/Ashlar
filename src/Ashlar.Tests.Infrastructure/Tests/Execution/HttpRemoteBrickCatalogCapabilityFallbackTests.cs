using Ashlar.Agents.TestKit;
using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Ashlar.Brick.Contracts;
using Ashlar.Brick.Contracts.Capabilities;
using Ashlar.Infrastructure.Execution;
using Xunit;
using Ashlar.Abstractions.Security.Egress;
using Microsoft.Extensions.Logging;
using Moq;

namespace Ashlar.Tests.Infrastructure.Tests.Execution;

/// <summary>Tests for http remote brick catalog capability fallback.</summary>
public sealed class HttpRemoteBrickCatalogCapabilityFallbackTests
{
    [Fact]
    public async Task Refusal_returns_empty_results_without_reusing_a_stale_manifest()
    {
        var refusal = new EgressRefusedException(new EgressGuard("full", "enforce").Evaluate(
            new EgressRequest(EgressFamilies.Http, "remote.catalog", new Uri("https://remote.example"))));
        var calls = 0;
        using var client = new HttpClient(StubHttpMessageHandler.FromSync((_, _) =>
        {
            if (++calls == 1)
                return JsonResponse($$"""{"nodeId":"node-a","generatedAt":"{{DateTimeOffset.UtcNow:O}}"}""");
            throw new IOException("private-canary", refusal);
        })) { BaseAddress = new Uri("https://remote.example/") };
        var logger = new Mock<ILogger<HttpRemoteBrickCatalog>>();
        using var catalog = new HttpRemoteBrickCatalog(client, logger.Object, new StaleCapabilitiesSnapshotStore(),
            capabilityTtl: TimeSpan.Zero);
        (await catalog.GetCapabilitiesWithStalenessAsync()).Capabilities.Should().NotBeNull();
        var refused = await catalog.GetCapabilitiesWithStalenessAsync();
        refused.Capabilities.Should().BeNull();
        refused.IsStale.Should().BeFalse();
        (await catalog.GetAllAsync()).Should().BeEmpty();
        (await catalog.GetByIdAsync("brick")).Should().BeNull();
        calls.Should().Be(4);
        logger.Verify(x => x.Log(LogLevel.Warning, It.Is<EventId>(e => e.Id == 7307),
            It.IsAny<It.IsAnyType>(), It.IsAny<Exception>(), It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
        logger.Verify(x => x.Log(LogLevel.Warning, It.Is<EventId>(e => e.Id != 7307),
            It.IsAny<It.IsAnyType>(), It.IsAny<Exception>(), It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Never);
    }

    [Fact]
    public async Task GetCapabilitiesAsync_ReturnsStaleSnapshot_WhenEndpointUnavailable()
    {
        var freshGeneratedAt = DateTimeOffset.UtcNow.AddSeconds(-5).ToString("O");
        var responses = new Queue<Func<HttpRequestMessage, HttpResponseMessage>>(
        [
            _ => JsonResponse($$"""
            {
              "nodeId": "node-a",
              "tier": 1,
              "platform": 2,
              "hotModelIds": [],
              "availableModelIds": ["phi3-mini"],
              "supportedCapabilities": [0],
              "acceptingRemoteWork": true,
              "generatedAt": "{{freshGeneratedAt}}"
            }
            """),
            static _ => throw new HttpRequestException("network down")
        ]);

        using var httpClient = new HttpClient(new StubHttpMessageHandler((req, _) => Task.FromResult(responses.Dequeue().Invoke(req))))
        {
            BaseAddress = new Uri("http://remote:7777", UriKind.Absolute)
        };

        var staleStore = new StaleCapabilitiesSnapshotStore();
        var sut = new HttpRemoteBrickCatalog(
            httpClient,
            NullLogger<HttpRemoteBrickCatalog>.Instance,
            staleStore,
            capabilityTtl: TimeSpan.Zero);

        var first = await sut.GetCapabilitiesWithStalenessAsync();
        var second = await sut.GetCapabilitiesWithStalenessAsync();

        first.Capabilities.Should().NotBeNull();
        first.IsStale.Should().BeFalse();
        second.Capabilities.Should().NotBeNull();
        second.IsStale.Should().BeTrue();
        second.Capabilities!.NodeId.Should().Be("node-a");
        second.Capabilities.Platform.Should().Be(PlatformTypeDto.Linux);
    }

    [Fact]
    public async Task GetCapabilitiesAsync_DropsStaleSnapshot_WhenOlderThanMaxAge()
    {
        var staleStore = new StaleCapabilitiesSnapshotStore();
        var tooOld = DateTimeOffset.UtcNow.AddMinutes(-20);
        staleStore.Put("http://remote:7777/", new NodeCapabilityManifestDto
        {
            NodeId = "node-stale",
            Tier = NodeTierDto.Micro,
            Platform = PlatformTypeDto.Linux,
            HotModelIds = [],
            AvailableModelIds = ["phi3-mini"],
            SupportedCapabilities = [TaskCapabilityDto.TextGeneration],
            AcceptingRemoteWork = true,
            GeneratedAt = tooOld
        });

        using var httpClient = new HttpClient(new StubHttpMessageHandler((req, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))))
        {
            BaseAddress = new Uri("http://remote:7777", UriKind.Absolute)
        };

        var sut = new HttpRemoteBrickCatalog(
            httpClient,
            NullLogger<HttpRemoteBrickCatalog>.Instance,
            staleStore,
            capabilityTtl: TimeSpan.Zero,
            maxStaleCapabilityAge: TimeSpan.FromMinutes(5));

        var fetch = await sut.GetCapabilitiesWithStalenessAsync();
        fetch.Capabilities.Should().BeNull();
        fetch.IsStale.Should().BeFalse();
    }

    [Fact]
    public async Task GetCapabilitiesAsync_DropsFutureTimestampSnapshot()
    {
        var staleStore = new StaleCapabilitiesSnapshotStore();
        staleStore.Put("http://remote:7777/", new NodeCapabilityManifestDto
        {
            NodeId = "node-future",
            Tier = NodeTierDto.Standard,
            Platform = PlatformTypeDto.Linux,
            HotModelIds = [],
            AvailableModelIds = ["phi3-mini"],
            SupportedCapabilities = [TaskCapabilityDto.TextGeneration],
            AcceptingRemoteWork = true,
            GeneratedAt = DateTimeOffset.UtcNow.AddHours(1)
        });

        using var httpClient = new HttpClient(new StubHttpMessageHandler((req, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))))
        {
            BaseAddress = new Uri("http://remote:7777", UriKind.Absolute)
        };

        var sut = new HttpRemoteBrickCatalog(
            httpClient,
            NullLogger<HttpRemoteBrickCatalog>.Instance,
            staleStore,
            capabilityTtl: TimeSpan.Zero,
            maxStaleCapabilityAge: TimeSpan.FromMinutes(15));

        var fetch = await sut.GetCapabilitiesWithStalenessAsync();
        fetch.Capabilities.Should().BeNull();
        fetch.IsStale.Should().BeFalse();
    }

    [Fact]
    public async Task GetAllAsync_AttachesCapabilitiesFromStaleCache_WhenFreshFetchFails()
    {
        var freshGeneratedAt = DateTimeOffset.UtcNow.AddSeconds(-5).ToString("O");
        var capabilitiesCalls = 0;
        var brickCalls = 0;
        using var httpClient = new HttpClient(new StubHttpMessageHandler((req, _) =>
        {
            if (req.RequestUri!.AbsolutePath == "/api/capabilities")
            {
                capabilitiesCalls++;
                return capabilitiesCalls == 1
                    ? Task.FromResult(JsonResponse($$"""
                    {
                      "nodeId": "node-cache",
                      "tier": 2,
                      "platform": 2,
                      "hotModelIds": [],
                      "availableModelIds": ["phi3-mini"],
                      "supportedCapabilities": [0],
                      "acceptingRemoteWork": true,
                      "generatedAt": "{{freshGeneratedAt}}"
                    }
                    """))
                    : Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            }

            if (req.RequestUri.AbsolutePath.StartsWith("/api/bricks", StringComparison.Ordinal))
            {
                brickCalls++;
                if (brickCalls > 1)
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
                }

                return Task.FromResult(JsonResponse("""
                {
                  "wireFormatVersion": "2025-02",
                  "bricks": [
                    {
                      "wireFormatVersion": "2025-02",
                      "id": "remote.gen",
                      "name": "RemoteGen",
                      "version": "1.0.0",
                      "icon": "pkg",
                      "category": "Generation",
                      "description": "desc",
                      "interface": { "inputs": [], "outputs": [] },
                      "hasDeterministic": true,
                      "hasAgentic": true,
                      "metadata": { "author": "n", "license": "MIT", "repository": "", "usageCount": 1, "lastUpdated": "2026-01-01T00:00:00Z" }
                    }
                  ]
                }
                """));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }))
        {
            BaseAddress = new Uri("http://remote:7777", UriKind.Absolute)
        };

        var staleStore = new StaleCapabilitiesSnapshotStore();
        var sut = new HttpRemoteBrickCatalog(
            httpClient,
            NullLogger<HttpRemoteBrickCatalog>.Instance,
            staleStore,
            capabilityTtl: TimeSpan.Zero);

        var warm = await sut.GetCapabilitiesWithStalenessAsync();
        warm.Capabilities.Should().NotBeNull();
        warm.IsStale.Should().BeFalse();

        _ = await sut.GetCapabilitiesWithStalenessAsync();
        capabilitiesCalls.Should().BeGreaterThanOrEqualTo(2);
        var entries = await sut.GetAllAsync();
        entries.Should().HaveCount(1);
        entries[0].HostCapabilities.Should().NotBeNull();
        entries[0].HostCapabilities!.NodeId.Should().Be("node-cache");
    }

    [Fact]
    public async Task GetByIdAsync_returns_null_for_empty_id()
    {
        using var httpClient = new HttpClient(new StubHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))))
        {
            BaseAddress = new Uri("http://remote:7777/", UriKind.Absolute)
        };
        var sut = new HttpRemoteBrickCatalog(httpClient);

        (await sut.GetByIdAsync("", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task GetByIdAsync_returns_entry_with_capabilities_when_found()
    {
        using var httpClient = new HttpClient(new StubHttpMessageHandler((req, _) =>
        {
            if (req.RequestUri!.AbsolutePath == "/api/capabilities")
            {
                return Task.FromResult(JsonResponse("""
                    {
                      "nodeId": "node-by-id",
                      "tier": 1,
                      "platform": 2,
                      "hotModelIds": [],
                      "availableModelIds": ["phi3-mini"],
                      "supportedCapabilities": [0],
                      "acceptingRemoteWork": true,
                      "generatedAt": "2026-06-08T00:00:00Z"
                    }
                    """));
            }

            if (req.RequestUri.AbsolutePath == "/api/bricks/remote.gen")
            {
                return Task.FromResult(JsonResponse("""
                    {
                      "id": "remote.gen",
                      "name": "RemoteGen",
                      "category": "Generation",
                      "description": "desc",
                      "interface": { "inputs": [], "outputs": [] },
                      "hasDeterministic": true
                    }
                    """));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }))
        {
            BaseAddress = new Uri("http://remote:7777/", UriKind.Absolute)
        };

        var sut = new HttpRemoteBrickCatalog(httpClient, NullLogger<HttpRemoteBrickCatalog>.Instance);
        var entry = await sut.GetByIdAsync("remote.gen");

        entry.Should().NotBeNull();
        entry!.Id.Should().Be("remote.gen");
        entry.HostCapabilities.Should().NotBeNull();
        entry.HostCapabilities!.NodeId.Should().Be("node-by-id");
    }

    [Fact]
    public async Task GetByIdAsync_returns_null_when_remote_errors()
    {
        using var httpClient = new HttpClient(new StubHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError))))
        {
            BaseAddress = new Uri("http://remote:7777/", UriKind.Absolute)
        };

        var sut = new HttpRemoteBrickCatalog(httpClient, NullLogger<HttpRemoteBrickCatalog>.Instance);
        (await sut.GetByIdAsync("broken")).Should().BeNull();
    }

    [Fact]
    public async Task GetAllAsync_parses_full_brick_catalog_payload()
    {
        using var httpClient = new HttpClient(new StubHttpMessageHandler((req, _) =>
        {
            if (req.RequestUri!.AbsolutePath == "/api/capabilities")
            {
                return Task.FromResult(JsonResponse("""
                    {
                      "nodeId": "node-rich",
                      "tier": "Standard",
                      "platform": "Linux",
                      "hotModelIds": ["hot-1"],
                      "availableModelIds": [42],
                      "supportedCapabilities": ["TextGeneration"],
                      "acceptingRemoteWork": "true",
                      "generatedAt": "not-a-date"
                    }
                    """));
            }

            if (req.RequestUri.AbsolutePath == "/api/bricks")
            {
                return Task.FromResult(JsonResponse("""
                    {
                      "bricks": [
                        "skip-non-object",
                        {
                          "wireFormatVersion": "2025-02",
                          "id": "rich.brick",
                          "name": "RichBrick",
                          "version": "2.0.0",
                          "icon": "star",
                          "category": "Generation",
                          "description": "full parse",
                          "hostBaseUrl": "http://peer:8080",
                          "hasDeterministic": true,
                          "hasAgentic": false,
                          "interface": {
                            "inputs": [
                              { "name": "prompt", "type": "string", "description": "in", "required": true, "default": "hi" }
                            ],
                            "outputs": [
                              { "name": "result", "type": "string", "description": "out" }
                            ]
                          },
                          "metadata": {
                            "author": "ashlar",
                            "license": "MIT",
                            "repository": "https://example.com",
                            "usageCount": "7",
                            "lastUpdated": "2026-06-08T12:00:00Z"
                          }
                        }
                      ]
                    }
                    """));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }))
        {
            BaseAddress = new Uri("http://remote:7777/", UriKind.Absolute)
        };

        var sut = new HttpRemoteBrickCatalog(httpClient, NullLogger<HttpRemoteBrickCatalog>.Instance);
        var entries = await sut.GetAllAsync();

        entries.Should().HaveCount(1);
        var brick = entries[0];
        brick.Id.Should().Be("rich.brick");
        brick.Interface.Inputs.Should().ContainSingle(i => i.Name == "prompt" && i.Required);
        brick.Metadata.UsageCount.Should().Be(7);
        brick.HostCapabilities!.Tier.Should().Be(NodeTierDto.Standard);
    }

    [Fact]
    public async Task GetCapabilitiesWithStaleness_returns_cached_value_within_ttl_without_refetch()
    {
        var capabilityCalls = 0;
        using var httpClient = new HttpClient(new StubHttpMessageHandler((req, _) =>
        {
            if (req.RequestUri!.AbsolutePath == "/api/capabilities")
            {
                Interlocked.Increment(ref capabilityCalls);
                return Task.FromResult(JsonResponse("""
                    {
                      "nodeId": "node-cache",
                      "tier": 1,
                      "platform": 2,
                      "hotModelIds": [],
                      "availableModelIds": [],
                      "supportedCapabilities": [0],
                      "acceptingRemoteWork": true,
                      "generatedAt": "2026-06-08T00:00:00Z"
                    }
                    """));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }))
        {
            BaseAddress = new Uri("http://remote:7777/", UriKind.Absolute)
        };

        var sut = new HttpRemoteBrickCatalog(
            httpClient,
            NullLogger<HttpRemoteBrickCatalog>.Instance,
            capabilityTtl: TimeSpan.FromMinutes(5));

        _ = await sut.GetCapabilitiesWithStalenessAsync();
        var second = await sut.GetCapabilitiesWithStalenessAsync();

        second.IsStale.Should().BeFalse();
        capabilityCalls.Should().Be(1);
    }

    [Fact]
    public async Task GetAllAsync_returns_empty_when_payload_has_no_bricks_array()
    {
        using var httpClient = new HttpClient(new StubHttpMessageHandler((req, _) =>
        {
            if (req.RequestUri!.AbsolutePath == "/api/bricks")
                return Task.FromResult(JsonResponse("""{"items":[]}"""));

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }))
        {
            BaseAddress = new Uri("http://remote:7777/", UriKind.Absolute)
        };

        var sut = new HttpRemoteBrickCatalog(httpClient, NullLogger<HttpRemoteBrickCatalog>.Instance);
        (await sut.GetAllAsync()).Should().BeEmpty();
    }

    private static HttpResponseMessage JsonResponse(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    /// <summary>Tests for fake http message handler.</summary>
}
