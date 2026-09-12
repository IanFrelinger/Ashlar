using System.Collections.Concurrent;
using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Ashlar.Abstractions.Barriers;
using Ashlar.Abstractions.Routing;
using Ashlar.Abstractions.Transport;
using Ashlar.Runtime.Barriers;
using Ashlar.Runtime.Routing;
using Ashlar.Transport.Grpc;
using Ashlar.Transport.Grpc.Server;
using Xunit;

namespace Ashlar.Tests.Orchestration.Routing;

/// <summary>Tests for endpoint health monitor.</summary>
public sealed class EndpointHealthMonitorTests
{
    /// <summary>
    /// Upper bound for waiting on a probe that is expected to happen. A hang net, not a budget: a
    /// genuine bug (the warning is never logged) fails fast with a TimeoutException instead of
    /// hanging into the blame timeout.
    /// </summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task ExecuteAsync_HealthyProbe_UpdatesRegistryTrue()
    {
        await using var fixture = await GrpcServerFixture.StartAsync(health: true);

        var registry = new TestEndpointRegistry([
            new EndpointDescriptor(
                Endpoint: fixture.Endpoint,
                Name: "healthy",
                SupportedCapabilities: ["CodeGeneration"],
                AcceptedBarrierLevels: [],
                Region: null,
                Priority: 1,
                IsHealthy: false)
        ]);

        using var transport = CreateGrpcTransport();
        var monitor = new EndpointHealthMonitor(
            registry,
            transport,
            Options.Create(new RoutingOptions { HealthCheckIntervalSeconds = 30 }),
            NullLogger<EndpointHealthMonitor>.Instance);

        await monitor.StartAsync(CancellationToken.None);
        await WaitForFirstUpdateAsync(registry.Updates);
        await monitor.StopAsync(CancellationToken.None);

        registry.Updates.Should().ContainSingle();
        registry.Updates[0].isHealthy.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_UnhealthyProbe_UpdatesRegistryFalse_AndLogsWarning()
    {
        var endpoint = "http://127.0.0.1:6551";
        var registry = new TestEndpointRegistry([
            new EndpointDescriptor(
                Endpoint: endpoint,
                Name: "down",
                SupportedCapabilities: ["CodeGeneration"],
                AcceptedBarrierLevels: [],
                Region: null,
                Priority: 1,
                IsHealthy: true)
        ]);

        using var transport = CreateGrpcTransport();
        var logger = new Mock<ILogger<EndpointHealthMonitor>>();
        var monitor = new EndpointHealthMonitor(
            registry,
            transport,
            Options.Create(new RoutingOptions { HealthCheckIntervalSeconds = 30 }),
            logger.Object);

        await monitor.StartAsync(CancellationToken.None);
        await WaitForFirstUpdateAsync(registry.Updates);
        await monitor.StopAsync(CancellationToken.None);

        registry.Updates.Should().ContainSingle();
        registry.Updates[0].isHealthy.Should().BeFalse();
        logger.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task ExecuteAsync_UnhealthyProbe_DoesNotLogBeforeThreshold()
    {
        var endpoint = "http://127.0.0.1:6552";
        var registry = new TestEndpointRegistry([
            new EndpointDescriptor(
                Endpoint: endpoint,
                Name: "down-threshold",
                SupportedCapabilities: ["CodeGeneration"],
                AcceptedBarrierLevels: [],
                Region: null,
                Priority: 1,
                IsHealthy: true)
        ]);

        using var transport = CreateGrpcTransport();
        var logger = new Mock<ILogger<EndpointHealthMonitor>>();
        var monitor = new EndpointHealthMonitor(
            registry,
            transport,
            Options.Create(new RoutingOptions
            {
                HealthCheckIntervalSeconds = 30,
                DegradedLogFailureThreshold = 2
            }),
            logger.Object);

        await monitor.StartAsync(CancellationToken.None);
        await WaitForFirstUpdateAsync(registry.Updates);
        await monitor.StopAsync(CancellationToken.None);

        registry.Updates.Should().ContainSingle();
        registry.Updates[0].isHealthy.Should().BeFalse();
        logger.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_CancellationRequested_StopsPromptly()
    {
        var registry = new TestEndpointRegistry([]);
        using var transport = CreateGrpcTransport();
        var monitor = new EndpointHealthMonitor(
            registry,
            transport,
            Options.Create(new RoutingOptions { HealthCheckIntervalSeconds = 30 }),
            NullLogger<EndpointHealthMonitor>.Instance);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await monitor.StartAsync(CancellationToken.None);
        await monitor.StopAsync(CancellationToken.None);
        sw.Stop();

        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task ExecuteAsync_SkipsEndpointsWithBlankUri()
    {
        var registry = new TestEndpointRegistry([
            new EndpointDescriptor(
                Endpoint: "   ",
                Name: "blank",
                SupportedCapabilities: ["CodeGeneration"],
                AcceptedBarrierLevels: [],
                Region: null,
                Priority: 1,
                IsHealthy: true),
        ]);

        using var transport = CreateGrpcTransport();
        var monitor = new EndpointHealthMonitor(
            registry,
            transport,
            Options.Create(new RoutingOptions { HealthCheckIntervalSeconds = 30 }),
            NullLogger<EndpointHealthMonitor>.Instance);

        await monitor.StartAsync(CancellationToken.None);
        // Negative assertion (Updates.Should().BeEmpty()): this grace can only produce a false PASS, never a false FAIL.
        await Task.Delay(200);
        await monitor.StopAsync(CancellationToken.None);

        registry.Updates.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_WhenUpdateHealthThrows_LogsWarningAfterThreshold()
    {
        await using var fixture = await GrpcServerFixture.StartAsync(health: true);
        var registry = new ThrowingUpdateEndpointRegistry([
            new EndpointDescriptor(
                Endpoint: fixture.Endpoint,
                Name: "throws",
                SupportedCapabilities: ["CodeGeneration"],
                AcceptedBarrierLevels: [],
                Region: null,
                Priority: 1,
                IsHealthy: true),
        ]);

        using var transport = CreateGrpcTransport();
        var logger = new Mock<ILogger<EndpointHealthMonitor>>();

        // The warning itself is the signal. The 2.5 second sleep this replaces was defended in a
        // comment claiming it was not load-bearing because StopAsync joins ExecuteAsync, whose
        // first statement awaits ProbeAllAsync -- but StopAsync CANCELS before it joins, and
        // ProbeAllAsync returns without logging when the token trips mid-probe
        // (EndpointHealthMonitor.cs, `catch (OperationCanceledException) when (...) return;`). The
        // sleep was the only thing keeping the first probe ahead of the cancellation.
        var warned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        logger
            .Setup(x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()))
            .Callback(() => warned.TrySetResult());

        var monitor = new EndpointHealthMonitor(
            registry,
            transport,
            Options.Create(new RoutingOptions
            {
                HealthCheckIntervalSeconds = 1,
                DegradedLogFailureThreshold = 1,
            }),
            logger.Object);

        await monitor.StartAsync(CancellationToken.None);
        await warned.Task.WaitAsync(ProbeTimeout);
        await monitor.StopAsync(CancellationToken.None);

        logger.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task ExecuteAsync_WithoutGrpcTransport_StaysIdle_AndDoesNotTouchRegistry()
    {
        var registry = new TestEndpointRegistry([
            new EndpointDescriptor(
                Endpoint: "http://127.0.0.1:6553",
                Name: "no-transport",
                SupportedCapabilities: ["CodeGeneration"],
                AcceptedBarrierLevels: [],
                Region: null,
                Priority: 1,
                IsHealthy: true),
        ]);

        // Profiles without runtime transport (Edge/AirGapped/System) register the routing
        // services but no GrpcAgentTransport; DI then falls back to the transport-less
        // constructor and the monitor must still start (idle) instead of failing the host.
        var monitor = new EndpointHealthMonitor(
            registry,
            Options.Create(new RoutingOptions { HealthCheckIntervalSeconds = 1 }),
            NullLogger<EndpointHealthMonitor>.Instance);

        await monitor.StartAsync(CancellationToken.None);
        // Negative assertion (Updates.Should().BeEmpty()): this grace can only produce a false PASS, never a false FAIL.
        await Task.Delay(200);
        await monitor.StopAsync(CancellationToken.None);

        registry.Updates.Should().BeEmpty();
    }

    /// <summary>
    /// Bounded poll instead of a fixed delay: the first probe is a real gRPC round-trip
    /// (or a connection refusal) whose latency depends on the host, and StopAsync cancels a
    /// probe that has not finished yet -- the registry would then be asserted before the
    /// update it is waiting for was ever written.
    /// </summary>
    private static async Task WaitForFirstUpdateAsync(List<(string endpoint, bool isHealthy)> updates)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (updates.Count == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }
    }

    /// <summary>Throwing update endpoint registry.</summary>
    private sealed class ThrowingUpdateEndpointRegistry : IEndpointRegistry
    {
        private readonly ConcurrentDictionary<string, EndpointDescriptor> _endpoints =
            new(StringComparer.OrdinalIgnoreCase);

        public ThrowingUpdateEndpointRegistry(IReadOnlyList<EndpointDescriptor> endpoints)
        {
            foreach (var endpoint in endpoints)
                _endpoints[endpoint.Endpoint] = endpoint;
        }

        public List<(string endpoint, bool isHealthy)> Updates { get; } = [];

        /// <summary>Gets all.</summary>
        public IReadOnlyList<EndpointDescriptor> GetAll() => _endpoints.Values.ToList();

        /// <summary>Register.</summary>
        /// <param name="descriptor">Descriptor.</param>
        public void Register(EndpointDescriptor descriptor) => _endpoints[descriptor.Endpoint] = descriptor;

        public void UpdateHealth(string endpoint, bool isHealthy)
        {
            Updates.Add((endpoint, isHealthy));
            if (isHealthy)
                /// <summary>Invalid operation exception.</summary>
                /// <param name="failed"">Failed".</param>
                throw new InvalidOperationException("update failed");
        }
    }

    /// <summary>
    /// Builds the transport these tests probe with, without touching process-global state.
    ///
    /// <para>Seven tests in this class used to wrap themselves in a scope that set
    /// <c>DOTNET_ENVIRONMENT=Development</c>, the only reason being that
    /// <c>GrpcTransportOptions.Validate</c> refuses <c>AllowInsecure</c> outside development. That
    /// variable is per-PROCESS; this assembly has no <c>xunit.runner.json</c> and no collection
    /// definitions, so xUnit runs its test classes in parallel and every one of them saw
    /// Development for as long as a scope was open. Restoring the value on dispose does nothing
    /// about a concurrent reader, and one of those scopes was held across a 2.5 second wait. That
    /// is the shape behind the gRPC transport flakes fixed in #581, whose
    /// <c>GrpcTransportEnvironmentCollection</c> still carries the note. Because
    /// <c>DefaultGrpcChannelFactory</c> already had an internal constructor taking the flag
    /// directly, the fix is to stop mutating the global rather than to serialize around it.</para>
    /// </summary>
    private static GrpcAgentTransport CreateGrpcTransport()
    {
        var factory = new DefaultGrpcChannelFactory(
            new GrpcTransportOptions { AllowInsecure = true },
            NullLogger<DefaultGrpcChannelFactory>.Instance,
            isDevelopment: true);
        return new GrpcAgentTransport(factory, NullLogger<GrpcAgentTransport>.Instance);
    }

    /// <summary>Test endpoint registry.</summary>
    private sealed class TestEndpointRegistry : IEndpointRegistry
    {
        private readonly ConcurrentDictionary<string, EndpointDescriptor> _endpoints =
            new(StringComparer.OrdinalIgnoreCase);

        public TestEndpointRegistry(IReadOnlyList<EndpointDescriptor> endpoints)
        {
            foreach (var endpoint in endpoints)
            {
                _endpoints[endpoint.Endpoint] = endpoint;
            }
        }

        public List<(string endpoint, bool isHealthy)> Updates { get; } = [];

        /// <summary>Gets all.</summary>
        public IReadOnlyList<EndpointDescriptor> GetAll() => _endpoints.Values.ToList();

        public void Register(EndpointDescriptor descriptor)
        {
            _endpoints[descriptor.Endpoint] = descriptor;
        }

        public void UpdateHealth(string endpoint, bool isHealthy)
        {
            Updates.Add((endpoint, isHealthy));
            if (_endpoints.TryGetValue(endpoint, out var descriptor))
            {
                _endpoints[endpoint] = descriptor with { IsHealthy = isHealthy };
            }
        }
    }

    /// <summary>Grpc server fixture.</summary>
    private sealed class GrpcServerFixture : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private GrpcServerFixture(WebApplication app, string endpoint)
        {
            _app = app;
            Endpoint = endpoint;
        }

        /// <summary>Endpoint.</summary>
        public string Endpoint { get; }

        public static async Task<GrpcServerFixture> StartAsync(bool health)
        {
            var port = GetFreePort();
            var endpoint = $"http://127.0.0.1:{port}";

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = Environments.Development
            });

            builder.WebHost.ConfigureKestrel(options =>
            {
                options.Listen(IPAddress.Loopback, port, listen =>
                {
                    listen.Protocols = HttpProtocols.Http2;
                });
            });

            builder.Services.AddSingleton<IAgentTransport>(new FixedHealthTransport(health));
            builder.Services.AddSingleton<BarrierHierarchy>(_ =>
                new BarrierHierarchy([new BarrierLevel("public", 0), new BarrierLevel("internal", 1)]));
            builder.Services.AddSingleton<IOptions<BarrierOptions>>(
                _ => Options.Create(new BarrierOptions { Levels = ["public", "internal"], RequireExplicitBarrier = false }));
            builder.Services.AddScoped<IBarrierContextAccessor, ScopedBarrierContextAccessor>();
            builder.Services.AddSingleton<IBarrierAuditLog, StructuredBarrierAuditLog>();
            builder.Services.AddAshlarGrpcServer();

            var app = builder.Build();
            app.MapAshlarGrpcServer();
            await app.StartAsync();

            /// <summary>Grpc server fixture.</summary>
            return new GrpcServerFixture(app, endpoint);
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        private static int GetFreePort()
        {
            var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
    }

    /// <summary>Fixed health transport.</summary>
    private sealed class FixedHealthTransport : IAgentTransport
    {
        private readonly bool _healthy;

        public FixedHealthTransport(bool healthy)
        {
            _healthy = healthy;
        }

        public Task<AgentResult> SendAsync(AgentInvocationRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new AgentResult(Success: true));

        public Task<TransportHealth> CheckHealthAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new TransportHealth(
                IsHealthy: _healthy,
                TransportName: "fixed",
                Message: _healthy ? "ok" : "down",
                TransportType: "gRPC",
                DiagnosticMessage: _healthy ? "ok" : "down"));
    }

}
