using Ashlar.Agents.TestKit;
using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Ashlar.Core.Application.Execution.Routing;
using Ashlar.Core.Domain.Execution;
using Ashlar.Infrastructure.Execution.Routing;
using Ashlar.Tests.Infrastructure.Helpers;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Execution;

/// <summary>Tests for peer to peer routing smoke.</summary>
[Trait("Category", "Smoke")]
public sealed class PeerToPeerRoutingSmokeTests
{
    [Fact]
    public async Task PeerExecutor_FallsBackToNextPeer_WhenFirstPeerFails()
    {
        var requestedHosts = new List<string>();
        using var httpClient = new HttpClient(new StubHttpMessageHandler(async (request, _) =>
        {
            requestedHosts.Add(request.RequestUri?.Host ?? string.Empty);
            if (string.Equals(request.RequestUri?.Host, "peer-a", StringComparison.OrdinalIgnoreCase))
            {
                /// <summary>Http response message.</summary>
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }

            var json = CreateSuccessResponseJson([9, 8, 7], "/tmp/peer-b-output.bin", "peer-b ok");
            return await Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }));

        var snapshot = new StaticPeerSnapshot(
        [
            new PeerExecutionCandidate
            {
                PeerId = "peer-a",
                Endpoint = "http://peer-a:8080",
                AvailableVramBytes = 12L * 1024 * 1024 * 1024,
                ComputeClass = GpuComputeClass.High,
                QueueDepth = 0
            },
            new PeerExecutionCandidate
            {
                PeerId = "peer-b",
                Endpoint = "http://peer-b:8080",
                AvailableVramBytes = 16L * 1024 * 1024 * 1024,
                ComputeClass = GpuComputeClass.Extreme,
                QueueDepth = 1
            }
        ]);
        var config = Options.Create(new RunPodBrickConfig
        {
            QueueDepthThreshold = 10,
            PeerRequestTimeout = TimeSpan.FromSeconds(1),
            PeerRoutingBrickId = "generation.capability-routing"
        });
        var sut = new AshlarPeerBrickExecutor(
            new StaticHttpClientFactory(httpClient),
            NullLogger<AshlarPeerBrickExecutor>.Instance,
            snapshot,
            config);

        var result = await sut.ExecuteAsync(
            new RunPodJobPayload { ModelId = "model-x", Prompt = "hello" },
            new JobRequirements { ModelId = "model-x", MinimumVramBytes = 1, ComputeClass = GpuComputeClass.Low },
            TestExecutionContext());

        result.IsSuccess.Should().BeTrue($"{result.Error?.Code}:{result.Error?.Message}:{result.Error?.Detail}");
        result.Value.Should().NotBeNull();
        result.Value!.Provider.Should().Be("ashlar-peer");
        result.Value.Payload.Should().Equal([9, 8, 7]);
        requestedHosts.Should().ContainInOrder("peer-a", "peer-b");
    }

    [Fact]
    public async Task PeerExecutor_FailsOverWhenPeerTimesOut()
    {
        var requestedHosts = new List<string>();
        var slowPeerCanceled = false;
        using var httpClient = new HttpClient(new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            requestedHosts.Add(request.RequestUri?.Host ?? string.Empty);
            if (string.Equals(request.RequestUri?.Host, "peer-slow", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    slowPeerCanceled = true;
                    throw;
                }
            }

            var json = CreateSuccessResponseJson([1, 2, 3], "/tmp/peer-fast-output.bin", "peer-fast ok");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        })) { Timeout = Timeout.InfiniteTimeSpan };

        var snapshot = new StaticPeerSnapshot(
        [
            new PeerExecutionCandidate
            {
                PeerId = "peer-slow",
                Endpoint = "http://peer-slow:8080",
                AvailableVramBytes = 12L * 1024 * 1024 * 1024,
                ComputeClass = GpuComputeClass.High,
                QueueDepth = 0
            },
            new PeerExecutionCandidate
            {
                PeerId = "peer-fast",
                Endpoint = "http://peer-fast:8080",
                AvailableVramBytes = 12L * 1024 * 1024 * 1024,
                ComputeClass = GpuComputeClass.High,
                QueueDepth = 1
            }
        ]);
        var config = Options.Create(new RunPodBrickConfig
        {
            QueueDepthThreshold = 10,
            PeerRequestTimeout = TimeSpan.FromSeconds(5),
            PeerRoutingBrickId = "generation.capability-routing"
        });
        var sut = new AshlarPeerBrickExecutor(
            new StaticHttpClientFactory(httpClient),
            NullLogger<AshlarPeerBrickExecutor>.Instance,
            snapshot,
            config);

        using var cleanup = new CancellationTokenSource();
        try
        {
            var execution = sut.ExecuteAsync(
                new RunPodJobPayload { ModelId = "model-timeout", Prompt = "timeout test" },
                new JobRequirements { ModelId = "model-timeout", MinimumVramBytes = 1, ComputeClass = GpuComputeClass.Low },
                TestExecutionContext(), cleanup.Token);
            // The executor's timer must cancel the slow peer; this outer bound only fails the test.
            var result = await execution.WaitAsync(TimeSpan.FromSeconds(30));

            slowPeerCanceled.Should().BeTrue("the first peer must observe the executor's timeout cancellation");
            result.IsSuccess.Should().BeTrue($"{result.Error?.Code}:{result.Error?.Message}:{result.Error?.Detail}");
            result.Value.Should().NotBeNull();
            result.Value!.Payload.Should().Equal([1, 2, 3]);
            result.Value.OutputPath.Should().Be("/tmp/peer-fast-output.bin");
            result.Value.Summary.Should().Be("peer-fast ok");
            requestedHosts.Should().Equal("peer-slow", "peer-fast");
        }
        finally
        {
            cleanup.Cancel();
        }
    }

    [Fact]
    public async Task PeerExecutor_ParsesTopLevelSuccessFlag_IgnoringNestedSuccessFields()
    {
        using var httpClient = new HttpClient(new StubHttpMessageHandler((request, _) =>
        {
            var base64 = Convert.ToBase64String([3, 1, 4]);
            var json = $$"""
            {
              "meta": { "success": false, "error": "nested should be ignored" },
              "success": true,
              "summary": "peer ok",
              "output": {
                "payload": {
                  "__type": "bytes",
                  "base64": "{{base64}}"
                },
                "outputPath": "/tmp/peer-output.bin"
              }
            }
            """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }));

        var snapshot = new StaticPeerSnapshot(
        [
            new PeerExecutionCandidate
            {
                PeerId = "peer-top-level",
                Endpoint = "http://peer-top-level:8080",
                AvailableVramBytes = 16L * 1024 * 1024 * 1024,
                ComputeClass = GpuComputeClass.Extreme,
                QueueDepth = 0
            }
        ]);
        var config = Options.Create(new RunPodBrickConfig
        {
            QueueDepthThreshold = 10,
            PeerRequestTimeout = TimeSpan.FromSeconds(1),
            PeerRoutingBrickId = "generation.capability-routing"
        });
        var sut = new AshlarPeerBrickExecutor(
            new StaticHttpClientFactory(httpClient),
            NullLogger<AshlarPeerBrickExecutor>.Instance,
            snapshot,
            config);

        var result = await sut.ExecuteAsync(
            new RunPodJobPayload { ModelId = "model-parse", Prompt = "parse test" },
            new JobRequirements { ModelId = "model-parse", MinimumVramBytes = 1, ComputeClass = GpuComputeClass.Low },
            TestExecutionContext());

        result.IsSuccess.Should().BeTrue($"{result.Error?.Code}:{result.Error?.Message}:{result.Error?.Detail}");
        result.Value.Should().NotBeNull();
        result.Value!.Payload.Should().Equal([3, 1, 4]);
    }

    [Fact]
    public async Task PeerExecutor_ReturnsAggregatedFailure_WhenAllPeersFail()
    {
        using var httpClient = new HttpClient(new StubHttpMessageHandler((request, _) =>
        {
            if (string.Equals(request.RequestUri?.Host, "peer-down", StringComparison.OrdinalIgnoreCase))
            {
                /// <summary>Http request exception.</summary>
                /// <param name="refused"">Refused".</param>
                throw new HttpRequestException("connection refused");
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway));
        }));

        var snapshot = new StaticPeerSnapshot(
        [
            new PeerExecutionCandidate
            {
                PeerId = "peer-down",
                Endpoint = "http://peer-down:8080",
                AvailableVramBytes = 12L * 1024 * 1024 * 1024,
                ComputeClass = GpuComputeClass.High,
                QueueDepth = 0
            },
            new PeerExecutionCandidate
            {
                PeerId = "peer-bad-gateway",
                Endpoint = "http://peer-bad-gateway:8080",
                AvailableVramBytes = 12L * 1024 * 1024 * 1024,
                ComputeClass = GpuComputeClass.High,
                QueueDepth = 1
            }
        ]);
        var config = Options.Create(new RunPodBrickConfig
        {
            QueueDepthThreshold = 10,
            PeerRequestTimeout = TimeSpan.FromSeconds(1),
            PeerRoutingBrickId = "generation.capability-routing"
        });
        var sut = new AshlarPeerBrickExecutor(
            new StaticHttpClientFactory(httpClient),
            NullLogger<AshlarPeerBrickExecutor>.Instance,
            snapshot,
            config);

        var result = await sut.ExecuteAsync(
            new RunPodJobPayload { ModelId = "model-fail", Prompt = "all fail" },
            new JobRequirements { ModelId = "model-fail", MinimumVramBytes = 1, ComputeClass = GpuComputeClass.Low },
            TestExecutionContext());

        result.IsFailure.Should().BeTrue();
        result.Error.Should().NotBeNull();
        result.Error!.Code.Should().Be("peer-routing.all_peers_failed");
        result.Error.Detail.Should().NotBeNullOrWhiteSpace();
        result.Error.Detail!.Should().Contain("peer-down");
        result.Error.Detail.Should().Contain("peer-bad-gateway");
    }

    [Fact]
    public async Task PeerExecutor_TrustedOnlyPolicy_RejectsUntrustedCandidates()
    {
        using var httpClient = new HttpClient(new StubHttpMessageHandler((request, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))));
        var snapshot = new StaticPeerSnapshot(
        [
            new PeerExecutionCandidate
            {
                PeerId = "peer-untrusted",
                Endpoint = "http://peer-untrusted:8080",
                AvailableVramBytes = 16L * 1024 * 1024 * 1024,
                ComputeClass = GpuComputeClass.High,
                QueueDepth = 0,
                TrustTier = Ashlar.Core.Application.Mesh.Models.PeerTrustTier.Untrusted
            }
        ]);
        var config = Options.Create(new RunPodBrickConfig
        {
            QueueDepthThreshold = 10,
            PeerRequestTimeout = TimeSpan.FromSeconds(1),
            PeerRoutingBrickId = "generation.capability-routing",
            PeerTrustPolicy = "trusted-only"
        });
        var sut = new AshlarPeerBrickExecutor(
            new StaticHttpClientFactory(httpClient),
            NullLogger<AshlarPeerBrickExecutor>.Instance,
            snapshot,
            config);

        var result = await sut.ExecuteAsync(
            new RunPodJobPayload { ModelId = "model-trust", Prompt = "trust check" },
            new JobRequirements { ModelId = "model-trust", MinimumVramBytes = 1, ComputeClass = GpuComputeClass.Low },
            TestExecutionContext());

        result.IsFailure.Should().BeTrue();
        result.Error.Should().NotBeNull();
        result.Error!.Code.Should().Be("peer-routing.no_eligible_peers");
    }

    // Explicit longer hang net: 120 concurrent requests through the stub handler finish in
    // seconds when healthy, but the 60 s Integration bound occasionally tripped under coverage
    // instrumentation on the kernel-coverage-gate lane. There is no "Stress" trait on this
    // class (it is Smoke), so the lane cannot filter it out; the timeout is the lever.
    [Fact(Timeout = TestTimeouts.Stress)]
    public async Task PeerExecutor_StressBurstConcurrency_MaintainsHighSuccessRate()
    {
        const int requestCount = 120;
        var peerCallCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["peer-1"] = 0,
            ["peer-2"] = 0,
            ["peer-3"] = 0
        };
        var gate = new object();
        using var httpClient = new HttpClient(new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            var host = request.RequestUri?.Host ?? string.Empty;
            lock (gate)
            {
                if (!peerCallCounts.TryAdd(host, 1))
                {
                    peerCallCounts[host]++;
                }
            }

            // peer-1 intermittently fails to force failover pressure.
            if (string.Equals(host, "peer-1", StringComparison.OrdinalIgnoreCase))
            {
                var count = peerCallCounts[host];
                if (count % 3 == 0)
                {
                    /// <summary>Http response message.</summary>
                    return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                }
            }

            // peer-2 intermittently slow but usually succeeds.
            if (string.Equals(host, "peer-2", StringComparison.OrdinalIgnoreCase))
            {
                var count = peerCallCounts[host];
                if (count % 5 == 0)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(120), cancellationToken);
                }
            }

            byte[] payload = [7, 7, 7];
            var response = CreateSuccessResponseJson(payload, $"/tmp/{host}-burst.bin", $"{host} ok");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json")
            };
        }));

        var snapshot = new StaticPeerSnapshot(
        [
            new PeerExecutionCandidate
            {
                PeerId = "peer-1",
                Endpoint = "http://peer-1:8080",
                AvailableVramBytes = 16L * 1024 * 1024 * 1024,
                ComputeClass = GpuComputeClass.High,
                QueueDepth = 0
            },
            new PeerExecutionCandidate
            {
                PeerId = "peer-2",
                Endpoint = "http://peer-2:8080",
                AvailableVramBytes = 24L * 1024 * 1024 * 1024,
                ComputeClass = GpuComputeClass.Extreme,
                QueueDepth = 1
            },
            new PeerExecutionCandidate
            {
                PeerId = "peer-3",
                Endpoint = "http://peer-3:8080",
                AvailableVramBytes = 20L * 1024 * 1024 * 1024,
                ComputeClass = GpuComputeClass.High,
                QueueDepth = 2
            }
        ]);

        var config = Options.Create(new RunPodBrickConfig
        {
            QueueDepthThreshold = 10,
            PeerRequestTimeout = TimeSpan.FromMilliseconds(180),
            PeerRoutingBrickId = "generation.capability-routing"
        });
        var sut = new AshlarPeerBrickExecutor(
            new StaticHttpClientFactory(httpClient),
            NullLogger<AshlarPeerBrickExecutor>.Instance,
            snapshot,
            config);

        var tasks = Enumerable.Range(0, requestCount)
            .Select(i => sut.ExecuteAsync(
                new RunPodJobPayload
                {
                    ModelId = "model-burst",
                    Prompt = $"burst-{i}",
                    GenerationParameters = new Dictionary<string, object> { ["seed"] = i }
                },
                new JobRequirements
                {
                    ModelId = "model-burst",
                    MinimumVramBytes = 1,
                    ComputeClass = GpuComputeClass.Low
                },
                TestExecutionContext()))
            .ToArray();

        var results = await Task.WhenAll(tasks);
        var successCount = results.Count(r => r.IsSuccess);
        var failureCount = results.Length - successCount;

        // Stress acceptance: some failures allowed under chaos, but majority should still pass.
        successCount.Should().BeGreaterThanOrEqualTo((int)(requestCount * 0.90));
        failureCount.Should().BeLessThanOrEqualTo((int)(requestCount * 0.10));
        results.Where(r => r.IsSuccess).Should().OnlyContain(r => r.Value != null && r.Value.Provider == "ashlar-peer");

        // Burst should exercise fallback across multiple peers with non-trivial call volume.
        peerCallCounts["peer-1"].Should().BeGreaterThan(0);
        peerCallCounts["peer-2"].Should().BeGreaterThan(0);
        peerCallCounts.Values.Count(v => v > 0).Should().BeGreaterThanOrEqualTo(2);
        peerCallCounts.Values.Sum().Should().BeGreaterThanOrEqualTo(requestCount);
    }

    [Fact(Timeout = TestTimeouts.Integration)]
    public async Task PeerExecutor_StressChaosIntermittentOutages_RecoversWithFallback()
    {
        const int requestCount = 80;
        var random = new Random(1337);
        var peerSuccessCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["peer-chaos-a"] = 0,
            ["peer-chaos-b"] = 0,
            ["peer-chaos-c"] = 0
        };
        var gate = new object();

        using var httpClient = new HttpClient(new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            var host = request.RequestUri?.Host ?? string.Empty;
            var roll = random.Next(0, 100);

            if (string.Equals(host, "peer-chaos-a", StringComparison.OrdinalIgnoreCase))
            {
                if (roll < 35)
                {
                    /// <summary>Http response message.</summary>
                    return new HttpResponseMessage(HttpStatusCode.BadGateway);
                }
            }

            if (string.Equals(host, "peer-chaos-b", StringComparison.OrdinalIgnoreCase))
            {
                if (roll < 20)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
                }
            }

            if (string.Equals(host, "peer-chaos-c", StringComparison.OrdinalIgnoreCase))
            {
                if (roll < 15)
                {
                    /// <summary>Http request exception.</summary>
                    /// <param name="reset"">Reset".</param>
                    throw new HttpRequestException("socket reset");
                }
            }

            lock (gate)
            {
                if (!peerSuccessCounts.TryAdd(host, 1))
                {
                    peerSuccessCounts[host]++;
                }
            }

            var response = CreateSuccessResponseJson([4, 2, 0], $"/tmp/{host}-chaos.bin", $"{host} ok");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json")
            };
        }));

        var snapshot = new StaticPeerSnapshot(
        [
            new PeerExecutionCandidate
            {
                PeerId = "peer-chaos-a",
                Endpoint = "http://peer-chaos-a:8080",
                AvailableVramBytes = 12L * 1024 * 1024 * 1024,
                ComputeClass = GpuComputeClass.High,
                QueueDepth = 0
            },
            new PeerExecutionCandidate
            {
                PeerId = "peer-chaos-b",
                Endpoint = "http://peer-chaos-b:8080",
                AvailableVramBytes = 14L * 1024 * 1024 * 1024,
                ComputeClass = GpuComputeClass.High,
                QueueDepth = 1
            },
            new PeerExecutionCandidate
            {
                PeerId = "peer-chaos-c",
                Endpoint = "http://peer-chaos-c:8080",
                AvailableVramBytes = 18L * 1024 * 1024 * 1024,
                ComputeClass = GpuComputeClass.Extreme,
                QueueDepth = 2
            }
        ]);

        var config = Options.Create(new RunPodBrickConfig
        {
            QueueDepthThreshold = 10,
            PeerRequestTimeout = TimeSpan.FromMilliseconds(120),
            PeerRoutingBrickId = "generation.capability-routing"
        });
        var sut = new AshlarPeerBrickExecutor(
            new StaticHttpClientFactory(httpClient),
            NullLogger<AshlarPeerBrickExecutor>.Instance,
            snapshot,
            config);

        var tasks = Enumerable.Range(0, requestCount)
            .Select(i => sut.ExecuteAsync(
                new RunPodJobPayload
                {
                    ModelId = "model-chaos",
                    Prompt = $"chaos-{i}",
                    GenerationParameters = new Dictionary<string, object> { ["step"] = i }
                },
                new JobRequirements
                {
                    ModelId = "model-chaos",
                    MinimumVramBytes = 1,
                    ComputeClass = GpuComputeClass.Low
                },
                TestExecutionContext()))
            .ToArray();

        var results = await Task.WhenAll(tasks);
        var successCount = results.Count(r => r.IsSuccess);

        // Under heavier outage simulation, require strong but not perfect success.
        successCount.Should().BeGreaterThanOrEqualTo((int)(requestCount * 0.80));
        results.Where(r => r.IsSuccess).Should().OnlyContain(r => r.Value != null && r.Value.Payload.Length > 0);

        // Ensure fallback reached alternate peers (not single-point success).
        peerSuccessCounts.Values.Count(v => v > 0).Should().BeGreaterThanOrEqualTo(2);
    }

    private static string CreateSuccessResponseJson(byte[] payload, string outputPath, string summary)
    {
        var base64 = Convert.ToBase64String(payload);
        return $$"""
        {
          "wireFormatVersion": "2025-02",
          "success": true,
          "summary": "{{summary}}",
          "output": {
            "payload": {
              "__type": "bytes",
              "base64": "{{base64}}"
            },
            "outputPath": "{{outputPath}}"
          }
        }
        """;
    }

    /// <summary>Tests for static peer snapshot.</summary>
    private sealed class StaticPeerSnapshot : IPeerCapabilitySnapshot
    {
        public StaticPeerSnapshot(IReadOnlyList<PeerExecutionCandidate> candidates)
        {
            Candidates = candidates;
        }

        /// <summary>Candidates.</summary>
        public IReadOnlyList<PeerExecutionCandidate> Candidates { get; }
    }

    /// <summary>Tests for static http client factory.</summary>
    private sealed class StaticHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;

        public StaticHttpClientFactory(HttpClient client)
        {
            _client = client;
        }

        public HttpClient CreateClient(string name)
            => _client;
    }

    /// <summary>Tests for fake http message handler.</summary>

    /// <summary>Tests for test execution context.</summary>
    // Execution context for these tests. The IExecutionContext implementation now
    // lives in Ashlar.Agents.TestKit.FakeExecutionContext; only the fixture values
    // that are specific to this suite stay here.
    private static FakeExecutionContext TestExecutionContext() => new()
    {
        AgentId = "smoke-agent",
        BehaviorId = "smoke-behavior",
        AuditMode = true,
        Provider = "ashlar"
    };
}
