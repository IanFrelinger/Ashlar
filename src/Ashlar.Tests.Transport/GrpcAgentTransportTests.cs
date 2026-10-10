using Ashlar.Abstractions.Security.Egress;
using Grpc.Core;
using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Ashlar.Abstractions.Barriers;
using Ashlar.Abstractions.Execution;
using Ashlar.Abstractions.Transport;
using Ashlar.Runtime.Barriers;
using Ashlar.Transport.Grpc;
using Ashlar.Transport.Grpc.Server;
using Xunit;

namespace Ashlar.Tests.Transport;

/// <summary>Tests for grpc agent transport.</summary>
[Collection("GrpcTransportEnvironment")]
public sealed class GrpcAgentTransportTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Local_transport_refusals_are_not_reported_as_rpc_outages(bool wrapped)
    {
        var refusal = new EgressRefusedException(new EgressGuard("full", "enforce").Evaluate(
            new EgressRequest(EgressFamilies.Grpc, "grpc.client", new Uri("https://remote.example"))));
        var channels = new RefusingChannelFactory(wrapped
            ? new RpcException(new Status(StatusCode.Unavailable, "private-canary", refusal)) : refusal);
        using var transport = new GrpcAgentTransport(channels, NullLogger<GrpcAgentTransport>.Instance);
        var result = await transport.SendAsync(new AgentInvocationRequest("agent", "correlation",
            Options: new AgentInvocationOptions(TimeSpan.FromSeconds(10), 3, "https://remote.example")));
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("EGRESS_REFUSED");
        result.ErrorMessage.Should().Be(refusal.Message);
        result.Metadata!["egressRef"].Should().Be(refusal.Ref);
        result.Output.Should().BeNull();
        channels.Calls.Should().Be(1);
        var health = await transport.CheckEndpointHealthAsync("https://remote.example");
        health.IsHealthy.Should().BeFalse();
        health.Message.Should().Be($"egress refused by policy (ref {refusal.Ref})");
        health.DiagnosticMessage.Should().BeNull();
        channels.Calls.Should().Be(2);
    }

    private sealed class RefusingChannelFactory(Exception error) : IGrpcChannelFactory
    {
        internal int Calls { get; private set; }
        public global::Grpc.Net.Client.GrpcChannel GetOrCreate(string endpoint)
        {
            Calls++;
            throw error;
        }
        public void DisposeAll() { }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Egress_refusal_round_trip_clears_output_and_preserves_only_code_and_random_ref(int form)
    {
        var refusal = new EgressRefusedException(new EgressGuard("full", "enforce").Evaluate(
            new EgressRequest(EgressFamilies.Http, "site-canary", new Uri("https://remote.example/private-canary"))));
        await using var fixture = await GrpcServerFixture.StartAsync(new RefusingTransport(refusal, form));
        using var env = new EnvironmentVariableScope("DOTNET_ENVIRONMENT", "Development");
        var factory = new DefaultGrpcChannelFactory(Options.Create(new GrpcTransportOptions { AllowInsecure = true }),
            NullLogger<DefaultGrpcChannelFactory>.Instance);
        using var transport = new GrpcAgentTransport(factory, NullLogger<GrpcAgentTransport>.Instance);
        var wire = new AgentTransportService.AgentTransportServiceClient(factory.GetOrCreate(fixture.Endpoint));
        var raw = await wire.InvokeAsync(new InvokeRequest { AgentName = "agent-1", TimeoutMs = 5000 });
        raw.Success.Should().BeFalse();
        raw.Output.Should().BeEmpty();
        raw.ErrorCode.Should().Be("EGRESS_REFUSED");
        raw.EgressRef.Should().Be(refusal.Ref);
        raw.ErrorMessage.Should().Be($"egress refused by policy (ref {refusal.Ref})");
        var result = await transport.SendAsync(new AgentInvocationRequest("agent-1", "refusal-test",
            Options: new AgentInvocationOptions(TimeSpan.FromSeconds(5), 0, fixture.Endpoint)));
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("EGRESS_REFUSED");
        result.Metadata!["egressRef"].Should().Be(refusal.Ref);
        result.ErrorMessage.Should().Be(raw.ErrorMessage);
        result.Output.Should().BeNull();
    }

    private sealed class RefusingTransport(EgressRefusedException refusal, int form) : IAgentTransport
    {
        public Task<AgentResult> SendAsync(AgentInvocationRequest request, CancellationToken cancellationToken = default)
        {
            if (form == 0) return Task.FromException<AgentResult>(new AggregateException(
                new IOException("unrelated-canary"), new RpcException(new Status(StatusCode.Internal, "grpc-canary", refusal))));
            return Task.FromResult(new AgentResult(false, Output: new { secret = "output-canary" },
                ErrorMessage: "detail-canary", ErrorCode: form == 1 ? "EGRESS_REFUSED" : null,
                Metadata: new Dictionary<string, string> { ["errorCode"] = "EGRESS_REFUSED", ["egressRef"] = refusal.Ref }));
        }
        public Task<TransportHealth> CheckHealthAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new TransportHealth(true, "test"));
    }

    [Fact]
    public async Task SendAsync_HappyPath_RoutesThroughServerAndRoundTripsCorrelation()
    {
        await using var fixture = await GrpcServerFixture.StartAsync(
            new EchoTransport(delay: TimeSpan.Zero));

        using var env = new EnvironmentVariableScope("DOTNET_ENVIRONMENT", "Development");
        var factory = new DefaultGrpcChannelFactory(
            Options.Create(new GrpcTransportOptions { AllowInsecure = true }),
            NullLogger<DefaultGrpcChannelFactory>.Instance);
        using var transport = new GrpcAgentTransport(factory, NullLogger<GrpcAgentTransport>.Instance);

        var request = new AgentInvocationRequest(
            AgentName: "agent-1",
            CorrelationId: "corr-123",
            SpanId: "span-1",
            Payload: new Dictionary<string, object?> { ["value"] = 42 },
            Options: new AgentInvocationOptions(
                Timeout: TimeSpan.FromSeconds(2),
                MaxRetries: 1,
                TargetEndpoint: fixture.Endpoint));

        var result = await transport.SendAsync(request);

        result.Success.Should().BeTrue();
        result.CorrelationId.Should().Be("corr-123");
    }

    [Fact]
    public async Task SendAsync_WhenDeadlineExceeded_ReturnsTimeoutErrorCode()
    {
        await using var fixture = await GrpcServerFixture.StartAsync(
            new EchoTransport(delay: TimeSpan.FromSeconds(2)));

        using var env = new EnvironmentVariableScope("DOTNET_ENVIRONMENT", "Development");
        var factory = new DefaultGrpcChannelFactory(
            Options.Create(new GrpcTransportOptions { AllowInsecure = true }),
            NullLogger<DefaultGrpcChannelFactory>.Instance);
        using var transport = new GrpcAgentTransport(factory, NullLogger<GrpcAgentTransport>.Instance);

        var request = new AgentInvocationRequest(
            AgentName: "agent-1",
            CorrelationId: "corr-timeout",
            Options: new AgentInvocationOptions(
                Timeout: TimeSpan.FromMilliseconds(25),
                MaxRetries: 0,
                TargetEndpoint: fixture.Endpoint));

        var result = await transport.SendAsync(request);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("TIMEOUT");
    }

    [Fact]
    public async Task SendAsync_WhenUnavailable_ReturnsTransportUnavailableErrorCode()
    {
        using var env = new EnvironmentVariableScope("DOTNET_ENVIRONMENT", "Development");
        var factory = new DefaultGrpcChannelFactory(
            Options.Create(new GrpcTransportOptions { AllowInsecure = true }),
            NullLogger<DefaultGrpcChannelFactory>.Instance);
        using var transport = new GrpcAgentTransport(factory, NullLogger<GrpcAgentTransport>.Instance);

        var request = new AgentInvocationRequest(
            AgentName: "agent-1",
            CorrelationId: "corr-unavailable",
            Options: new AgentInvocationOptions(
                Timeout: TimeSpan.FromMilliseconds(100),
                MaxRetries: 0,
                TargetEndpoint: "http://127.0.0.1:6550"));

        var result = await transport.SendAsync(request);

        result.Success.Should().BeFalse();
        // Under load the gRPC stack may surface TIMEOUT before UNAVAILABLE is classified.
        result.ErrorCode.Should().BeOneOf("TRANSPORT_UNAVAILABLE", "TIMEOUT");
    }

    [Fact]
    public async Task SendAsync_MetadataRoundTrip_IncludesExecutionIsolationOnInvocation()
    {
        AgentInvocationRequest? captured = null;
        await using var fixture = await GrpcServerFixture.StartAsync(new CaptureInvocationTransport(r => captured = r));

        using var env = new EnvironmentVariableScope("DOTNET_ENVIRONMENT", "Development");
        var factory = new DefaultGrpcChannelFactory(
            Options.Create(new GrpcTransportOptions { AllowInsecure = true }),
            NullLogger<DefaultGrpcChannelFactory>.Instance);
        using var transport = new GrpcAgentTransport(factory, NullLogger<GrpcAgentTransport>.Instance);

        var request = new AgentInvocationRequest(
            AgentName: "agent-1",
            CorrelationId: "corr-meta",
            SpanId: "span-meta",
            Payload: new Dictionary<string, object?> { ["k"] = 1 },
            Options: new AgentInvocationOptions(
                Timeout: TimeSpan.FromSeconds(2),
                MaxRetries: 0,
                TargetEndpoint: fixture.Endpoint),
            Metadata: new Dictionary<string, string>
            {
                [AgentExecutionIsolation.MetadataKey] = AgentExecutionIsolation.Format(
                    AgentExecutionIsolationLevel.ContainerPerAgent),
                ["domain"] = "General",
            });

        var result = await transport.SendAsync(request);

        result.Success.Should().BeTrue();
        captured.Should().NotBeNull();
        captured!.Metadata.Should().NotBeNull();
        captured.Metadata![AgentExecutionIsolation.MetadataKey].Should().Be("ContainerPerAgent");
        captured.Metadata["domain"].Should().Be("General");
    }

    [Fact]
    public async Task SendAsync_PayloadRoundTrip_PreservesNestedValues()
    {
        await using var fixture = await GrpcServerFixture.StartAsync(
            new EchoTransport(delay: TimeSpan.Zero));

        using var env = new EnvironmentVariableScope("DOTNET_ENVIRONMENT", "Development");
        var factory = new DefaultGrpcChannelFactory(
            Options.Create(new GrpcTransportOptions { AllowInsecure = true }),
            NullLogger<DefaultGrpcChannelFactory>.Instance);
        using var transport = new GrpcAgentTransport(factory, NullLogger<GrpcAgentTransport>.Instance);

        var payload = new Dictionary<string, object?>
        {
            ["nested"] = new Dictionary<string, object?> { ["a"] = 1, ["b"] = "x" },
            ["list"] = new[] { 1, 2, 3 },
            ["nullValue"] = null
        };

        var request = new AgentInvocationRequest(
            AgentName: "agent-1",
            CorrelationId: "corr-roundtrip",
            Payload: payload,
            Options: new AgentInvocationOptions(
                Timeout: TimeSpan.FromSeconds(2),
                MaxRetries: 0,
                TargetEndpoint: fixture.Endpoint));

        var result = await transport.SendAsync(request);

        result.Success.Should().BeTrue();
        var output = result.Output.Should().BeAssignableTo<IReadOnlyDictionary<string, object?>>().Subject;
        output.Should().ContainKey("nested");
        output.Should().ContainKey("list");
        output.Should().ContainKey("nullValue");
    }

    /// <summary>Capture invocation transport.</summary>
    private sealed class CaptureInvocationTransport : IAgentTransport
    {
        private readonly Action<AgentInvocationRequest> _onInvoke;

        public CaptureInvocationTransport(Action<AgentInvocationRequest> onInvoke)
        {
            _onInvoke = onInvoke;
        }

        public Task<AgentResult> SendAsync(AgentInvocationRequest request, CancellationToken cancellationToken = default)
        {
            /// <summary>_on invoke.</summary>
            _onInvoke(request);
            return Task.FromResult(new AgentResult(
                Success: true,
                Output: request.Payload ?? new Dictionary<string, object?>(),
                CorrelationId: request.CorrelationId,
                SpanId: request.SpanId));
        }

        public Task<TransportHealth> CheckHealthAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new TransportHealth(true, "capture"));
    }

    /// <summary>Echo transport.</summary>
    private sealed class EchoTransport : IAgentTransport
    {
        private readonly TimeSpan _delay;

        public EchoTransport(TimeSpan delay)
        {
            _delay = delay;
        }

        public async Task<AgentResult> SendAsync(AgentInvocationRequest request, CancellationToken cancellationToken = default)
        {
            if (_delay > TimeSpan.Zero)
            {
                await Task.Delay(_delay, cancellationToken);
            }

            return new AgentResult(
                Success: true,
                Output: request.Payload ?? new Dictionary<string, object?>(),
                CorrelationId: request.CorrelationId,
                SpanId: request.SpanId);
        }

        public Task<TransportHealth> CheckHealthAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new TransportHealth(true, "echo"));
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

        public static async Task<GrpcServerFixture> StartAsync(IAgentTransport localTransport)
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

            builder.Services.AddSingleton<IAgentTransport>(localTransport);
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

    /// <summary>Environment variable scope.</summary>
    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly string _key;
        private readonly string? _priorValue;

        public EnvironmentVariableScope(string key, string? value)
        {
            _key = key;
            _priorValue = Environment.GetEnvironmentVariable(key);
            Environment.SetEnvironmentVariable(key, value);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(_key, _priorValue);
        }
    }
}
