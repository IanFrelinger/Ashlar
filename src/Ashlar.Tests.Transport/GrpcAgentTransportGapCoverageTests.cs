using Ashlar.Abstractions.Security;
using Ashlar.Abstractions.Security.Egress;
using FluentAssertions;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Ashlar.Abstractions.Barriers;
using Ashlar.Abstractions.Transport;
using Ashlar.Runtime.Barriers;
using Ashlar.Transport.Grpc;
using Xunit;

namespace Ashlar.Tests.Transport;

/// <summary>Tests for grpc agent transport gap coverage.</summary>
[Collection("GrpcTransportEnvironment")]
public sealed class GrpcAgentTransportGapCoverageTests
{
    [Fact]
    public async Task SendAsync_without_target_endpoint_returns_required_error()
    {
        using var env = new EnvironmentVariableScope("DOTNET_ENVIRONMENT", "Development");
        var factory = new DefaultGrpcChannelFactory(
            Options.Create(new GrpcTransportOptions { AllowInsecure = true }),
            NullLogger<DefaultGrpcChannelFactory>.Instance);
        using var transport = new GrpcAgentTransport(factory, NullLogger<GrpcAgentTransport>.Instance);

        var result = await transport.SendAsync(new AgentInvocationRequest(
            AgentName: "agent-1",
            CorrelationId: "corr-no-endpoint",
            Options: new AgentInvocationOptions(
                Timeout: TimeSpan.FromSeconds(1),
                MaxRetries: 0,
                TargetEndpoint: null)));

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("TARGET_ENDPOINT_REQUIRED");
    }

    [Fact]
    public async Task SendAsync_null_request_throws()
    {
        using var env = new EnvironmentVariableScope("DOTNET_ENVIRONMENT", "Development");
        var factory = new DefaultGrpcChannelFactory(
            Options.Create(new GrpcTransportOptions { AllowInsecure = true }),
            NullLogger<DefaultGrpcChannelFactory>.Instance);
        using var transport = new GrpcAgentTransport(factory, NullLogger<GrpcAgentTransport>.Instance);

        var act = () => transport.SendAsync(null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task CheckHealthAsync_with_no_active_channels_reports_healthy_idle_state()
    {
        using var env = new EnvironmentVariableScope("DOTNET_ENVIRONMENT", "Development");
        var factory = new DefaultGrpcChannelFactory(
            Options.Create(new GrpcTransportOptions { AllowInsecure = true }),
            NullLogger<DefaultGrpcChannelFactory>.Instance);
        using var transport = new GrpcAgentTransport(factory, NullLogger<GrpcAgentTransport>.Instance);

        var health = await transport.CheckHealthAsync();

        health.IsHealthy.Should().BeTrue();
        health.DiagnosticMessage.Should().Contain("No active channels");
    }

    [Fact]
    public async Task SendAsync_uses_dependency_outputs_when_payload_missing()
    {
        using var subject = EgressSubject.Enter("test:grpc", new HighWaterMark());
        var authority = new SubjectClearanceAuthority(new Dictionary<string, string?> { ["test:grpc"] = "SystemHigh" });
        await using var fixture = await GrpcTransportTestFixture.StartAsync(new EchoTransport());

        using var env = new EnvironmentVariableScope("DOTNET_ENVIRONMENT", "Development");
        var factory = new DefaultGrpcChannelFactory(
            Options.Create(new GrpcTransportOptions { AllowInsecure = true }),
            NullLogger<DefaultGrpcChannelFactory>.Instance);
        using var transport = new GrpcAgentTransport(factory, NullLogger<GrpcAgentTransport>.Instance, clearances: authority);

        var request = new AgentInvocationRequest(
            AgentName: "agent-1",
            CorrelationId: "corr-deps",
            Payload: null,
            DependencyOutputs: new Dictionary<string, object>
            {
                ["dep-a"] = 7,
                ["dep-b"] = "value",
            },
            Options: new AgentInvocationOptions(
                Timeout: TimeSpan.FromSeconds(2),
                MaxRetries: 0,
                TargetEndpoint: fixture.Endpoint));

        var result = await transport.SendAsync(request);

        result.Success.Should().BeTrue();
        var output = result.Output.Should().BeAssignableTo<IReadOnlyDictionary<string, object?>>().Subject;
        output.Should().ContainKey("dep-a");
        output.Should().ContainKey("dep-b");
    }

    [Fact]
    public async Task CheckEndpointHealthAsync_reports_unhealthy_for_unreachable_endpoint()
    {
        using var env = new EnvironmentVariableScope("DOTNET_ENVIRONMENT", "Development");
        var factory = new DefaultGrpcChannelFactory(
            Options.Create(new GrpcTransportOptions { AllowInsecure = true }),
            NullLogger<DefaultGrpcChannelFactory>.Instance);
        using var transport = new GrpcAgentTransport(factory, NullLogger<GrpcAgentTransport>.Instance);

        var health = await transport.CheckEndpointHealthAsync("http://127.0.0.1:6550");

        health.IsHealthy.Should().BeFalse();
        health.DiagnosticMessage.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task CheckHealthAsync_reports_unhealthy_when_any_endpoint_fails()
    {
        await using var fixture = await GrpcTransportTestFixture.StartAsync(new EchoTransport());

        using var env = new EnvironmentVariableScope("DOTNET_ENVIRONMENT", "Development");
        var factory = new DefaultGrpcChannelFactory(
            Options.Create(new GrpcTransportOptions { AllowInsecure = true }),
            NullLogger<DefaultGrpcChannelFactory>.Instance);
        using var transport = new GrpcAgentTransport(factory, NullLogger<GrpcAgentTransport>.Instance);

        await transport.SendAsync(new AgentInvocationRequest(
            AgentName: "agent-1",
            CorrelationId: "corr-health-mix",
            Options: new AgentInvocationOptions(
                Timeout: TimeSpan.FromSeconds(2),
                MaxRetries: 0,
                TargetEndpoint: fixture.Endpoint)));

        await transport.CheckEndpointHealthAsync("http://127.0.0.1:6550");

        var health = await transport.CheckHealthAsync();
        health.IsHealthy.Should().BeFalse();
        health.DiagnosticMessage.Should().Contain("unhealthy");
    }

    [Fact]
    public async Task SendAsync_skips_blank_metadata_keys()
    {
        AgentInvocationRequest? captured = null;
        await using var fixture = await GrpcTransportTestFixture.StartAsync(new CaptureTransport(r => captured = r));

        using var env = new EnvironmentVariableScope("DOTNET_ENVIRONMENT", "Development");
        var factory = new DefaultGrpcChannelFactory(
            Options.Create(new GrpcTransportOptions { AllowInsecure = true }),
            NullLogger<DefaultGrpcChannelFactory>.Instance);
        using var transport = new GrpcAgentTransport(factory, NullLogger<GrpcAgentTransport>.Instance);

        await transport.SendAsync(new AgentInvocationRequest(
            AgentName: "agent-1",
            CorrelationId: "corr-meta-skip",
            Options: new AgentInvocationOptions(
                Timeout: TimeSpan.FromSeconds(2),
                MaxRetries: 0,
                TargetEndpoint: fixture.Endpoint),
            Metadata: new Dictionary<string, string>
            {
                [""] = "ignored",
                ["domain"] = "General",
            }));

        captured!.Metadata.Should().ContainKey("domain");
        captured.Metadata!.Should().NotContainKey("");
    }

    [Fact]
    public async Task SendAsync_propagates_barrier_context_and_records_audit()
    {
        await using var fixture = await GrpcTransportTestFixture.StartAsync(new EchoTransport());

        using var env = new EnvironmentVariableScope("DOTNET_ENVIRONMENT", "Development");
        var factory = new DefaultGrpcChannelFactory(
            Options.Create(new GrpcTransportOptions { AllowInsecure = true }),
            NullLogger<DefaultGrpcChannelFactory>.Instance);
        var hierarchy = new BarrierHierarchy([new BarrierLevel("public", 0)]);
        var ambient = new StubBarrierContextAmbient(
            BarrierContext.Create("public", "unit-test", "agent-1", "corr-barrier-propagate", hierarchy));
        var audit = new RecordingBarrierAuditLog();
        using var transport = new GrpcAgentTransport(
            factory,
            NullLogger<GrpcAgentTransport>.Instance,
            ambient,
            audit);

        var result = await transport.SendAsync(new AgentInvocationRequest(
            AgentName: "agent-1",
            CorrelationId: "corr-barrier-propagate",
            Options: new AgentInvocationOptions(
                Timeout: TimeSpan.FromSeconds(2),
                MaxRetries: 0,
                TargetEndpoint: fixture.Endpoint)));

        result.Success.Should().BeTrue();
        audit.Events.Should().ContainSingle(e => e.EventType == BarrierAuditEventType.PropagatedOverWire);
    }

    [Fact]
    public async Task SendAsync_surfaces_server_failure_response_without_exception()
    {
        await using var fixture = await GrpcTransportTestFixture.StartAsync(new FailingTransport("agent exploded"));

        using var env = new EnvironmentVariableScope("DOTNET_ENVIRONMENT", "Development");
        var factory = new DefaultGrpcChannelFactory(
            Options.Create(new GrpcTransportOptions { AllowInsecure = true }),
            NullLogger<DefaultGrpcChannelFactory>.Instance);
        using var transport = new GrpcAgentTransport(factory, NullLogger<GrpcAgentTransport>.Instance);

        var result = await transport.SendAsync(new AgentInvocationRequest(
            AgentName: "agent-1",
            CorrelationId: "corr-server-fail",
            Options: new AgentInvocationOptions(
                Timeout: TimeSpan.FromSeconds(2),
                MaxRetries: 0,
                TargetEndpoint: fixture.Endpoint)));

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("agent exploded");
    }

    [Fact]
    public void Dispose_disposes_channels()
    {
        using var env = new EnvironmentVariableScope("DOTNET_ENVIRONMENT", "Development");
        var factory = new DefaultGrpcChannelFactory(
            Options.Create(new GrpcTransportOptions { AllowInsecure = true }),
            NullLogger<DefaultGrpcChannelFactory>.Instance);
        var transport = new GrpcAgentTransport(factory, NullLogger<GrpcAgentTransport>.Instance);

        transport.Dispose();
        var act = () => factory.GetOrCreate("http://127.0.0.1:9");
        act.Should().NotThrow();
    }

    [Fact]
    public void DefaultGrpcChannelFactory_rejects_empty_endpoint()
    {
        using var env = new EnvironmentVariableScope("DOTNET_ENVIRONMENT", "Development");
        var factory = new DefaultGrpcChannelFactory(
            Options.Create(new GrpcTransportOptions { AllowInsecure = true }),
            NullLogger<DefaultGrpcChannelFactory>.Instance);

        var act = () => factory.GetOrCreate("  ");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void DefaultGrpcChannelFactory_rejects_insecure_outside_development()
    {
        using var env = new EnvironmentVariableScope("DOTNET_ENVIRONMENT", "Production");

        var act = () => new DefaultGrpcChannelFactory(
            Options.Create(new GrpcTransportOptions { AllowInsecure = true }),
            NullLogger<DefaultGrpcChannelFactory>.Instance);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*AllowInsecure*");
    }

    [Fact]
    public void Constructor_rejects_null_dependencies()
    {
        using var env = new EnvironmentVariableScope("DOTNET_ENVIRONMENT", "Development");
        var factory = new DefaultGrpcChannelFactory(
            Options.Create(new GrpcTransportOptions { AllowInsecure = true }),
            NullLogger<DefaultGrpcChannelFactory>.Instance);
        var actFactory = () => new GrpcAgentTransport(null!, NullLogger<GrpcAgentTransport>.Instance);
        var actLogger = () => new GrpcAgentTransport(factory, null!);

        actFactory.Should().Throw<ArgumentNullException>();
        actLogger.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task SendAsync_returns_transport_error_when_channel_factory_throws_unexpected_exception()
    {
        using var transport = new GrpcAgentTransport(
            new ThrowingChannelFactory(new InvalidOperationException("channel failed")),
            NullLogger<GrpcAgentTransport>.Instance);

        var result = await transport.SendAsync(new AgentInvocationRequest(
            AgentName: "agent-1",
            CorrelationId: "corr-transport-error",
            Options: new AgentInvocationOptions(
                Timeout: TimeSpan.FromSeconds(1),
                MaxRetries: 0,
                TargetEndpoint: "http://127.0.0.1:9")));

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("TRANSPORT_ERROR");
        result.ErrorMessage.Should().Be("channel failed");
    }

    [Fact]
    public async Task SendAsync_maps_rpc_status_codes_to_error_codes()
    {
        using var transport = new GrpcAgentTransport(
            new ThrowingChannelFactory(new RpcException(new Status(StatusCode.NotFound, "missing agent"))),
            NullLogger<GrpcAgentTransport>.Instance);

        var result = await transport.SendAsync(new AgentInvocationRequest(
            AgentName: "missing",
            CorrelationId: "corr-rpc",
            Options: new AgentInvocationOptions(
                Timeout: TimeSpan.FromSeconds(1),
                MaxRetries: 0,
                TargetEndpoint: "http://127.0.0.1:9")));

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("AGENT_NOT_FOUND");
    }

    [Fact]
    public async Task SendAsync_propagates_barrier_headers_without_audit_log()
    {
        await using var fixture = await GrpcTransportTestFixture.StartAsync(new CaptureTransport(_ => { }));

        using var env = new EnvironmentVariableScope("DOTNET_ENVIRONMENT", "Development");
        var factory = new DefaultGrpcChannelFactory(
            Options.Create(new GrpcTransportOptions { AllowInsecure = true }),
            NullLogger<DefaultGrpcChannelFactory>.Instance);
        var hierarchy = new BarrierHierarchy([new BarrierLevel("internal", 1)]);
        var ambient = new StubBarrierContextAmbient(
            BarrierContext.Create("internal", BarrierAuthoritySource.Header, "agent-1", "corr-barrier-no-audit", hierarchy));
        using var transport = new GrpcAgentTransport(factory, NullLogger<GrpcAgentTransport>.Instance, ambient);

        var result = await transport.SendAsync(new AgentInvocationRequest(
            AgentName: "agent-1",
            CorrelationId: "corr-barrier-no-audit",
            Options: new AgentInvocationOptions(
                Timeout: TimeSpan.FromSeconds(2),
                MaxRetries: 0,
                TargetEndpoint: fixture.Endpoint)));

        result.Success.Should().BeTrue();
    }

    /// <summary>Throwing channel factory.</summary>
    private sealed class ThrowingChannelFactory : IGrpcChannelFactory
    {
        private readonly Exception _exception;

        /// <summary>Throwing channel factory.</summary>
        /// <param name="exception">Exception.</param>
        public ThrowingChannelFactory(Exception exception) => _exception = exception;

        /// <summary>Gets or create.</summary>
        /// <param name="endpoint">Endpoint.</param>
        public GrpcChannel GetOrCreate(string endpoint) => throw _exception;

        public void DisposeAll()
        {
        }
    }

    /// <summary>Echo transport.</summary>
    private sealed class EchoTransport : IAgentTransport
    {
        public Task<AgentResult> SendAsync(AgentInvocationRequest request, CancellationToken cancellationToken = default)
        {
            var output = request.Payload
                ?? request.DependencyOutputs?.ToDictionary(kvp => kvp.Key, kvp => (object?)kvp.Value, StringComparer.OrdinalIgnoreCase)
                ?? new Dictionary<string, object?>();
            return Task.FromResult(new AgentResult(
                Success: true,
                Output: output,
                CorrelationId: request.CorrelationId,
                SpanId: request.SpanId));
        }

        /// <summary>Check health async.</summary>
        /// <param name="default">Default.</param>
        public Task<TransportHealth> CheckHealthAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new TransportHealth(true, "echo"));
    }

    /// <summary>Failing transport.</summary>
    private sealed class FailingTransport : IAgentTransport
    {
        private readonly string _message;
        /// <summary>Failing transport.</summary>
        /// <param name="message">Message.</param>
        public FailingTransport(string message) => _message = message;

        /// <summary>Send async.</summary>
        /// <param name="request">Request.</param>
        /// <param name="default">Default.</param>
        public Task<AgentResult> SendAsync(AgentInvocationRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AgentResult(
                Success: false,
                ErrorMessage: _message,
                ErrorCode: "AGENT_FAILED",
                CorrelationId: request.CorrelationId,
                SpanId: request.SpanId));

        /// <summary>Check health async.</summary>
        /// <param name="default">Default.</param>
        public Task<TransportHealth> CheckHealthAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new TransportHealth(false, "fail"));
    }

    /// <summary>Stub barrier context ambient.</summary>
    private sealed class StubBarrierContextAmbient : IBarrierContextAmbient
    {
        private BarrierContext? _context;
        /// <summary>Stub barrier context ambient.</summary>
        /// <param name="context">Context.</param>
        public StubBarrierContextAmbient(BarrierContext context) => _context = context;
        public BarrierContext? Current => _context;
        /// <summary>Sets current.</summary>
        /// <param name="context">Context.</param>
        public void SetCurrent(BarrierContext? context) => _context = context;
    }

    /// <summary>Recording barrier audit log.</summary>
    private sealed class RecordingBarrierAuditLog : IBarrierAuditLog
    {
        /// <summary>Events.</summary>
        public List<BarrierAuditEvent> Events { get; } = new();
        public ValueTask RecordAsync(BarrierAuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            Events.Add(auditEvent);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>Capture transport.</summary>
    private sealed class CaptureTransport : IAgentTransport
    {
        private readonly Action<AgentInvocationRequest> _onInvoke;
        /// <summary>Capture transport.</summary>
        /// <param name="onInvoke">On invoke.</param>
        public CaptureTransport(Action<AgentInvocationRequest> onInvoke) => _onInvoke = onInvoke;

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

        /// <summary>Check health async.</summary>
        /// <param name="default">Default.</param>
        public Task<TransportHealth> CheckHealthAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new TransportHealth(true, "capture"));
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

        /// <summary>Dispose.</summary>
        public void Dispose() => Environment.SetEnvironmentVariable(_key, _priorValue);
    }
}
