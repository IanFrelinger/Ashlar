using Ashlar.Abstractions.Security;
using Ashlar.Abstractions.Security.Egress;
using System.Net;
using FluentAssertions;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Ashlar.Abstractions.Barriers;
using Ashlar.Abstractions.Transport;
using Ashlar.Runtime.Barriers;
using Ashlar.Transport.Grpc;
using Ashlar.Transport.Grpc.Server;
using Xunit;

namespace Ashlar.Tests.Transport;

/// <summary>Tests for agent transport service impl gap coverage.</summary>
[Collection("GrpcTransportEnvironment")]
public sealed class AgentTransportServiceImplGapCoverageTests
{
    [Fact]
    public async Task Invoke_returns_barrier_validation_failure_when_explicit_barrier_required()
    {
        using var subject = EgressSubject.Enter("test:grpc", new HighWaterMark());
        var authority = new SubjectClearanceAuthority(new Dictionary<string, string?> { ["test:grpc"] = "SystemHigh" });
        await using var fixture = await GrpcTransportTestFixture.StartAsync(
            new EchoTransport(),
            requireExplicitBarrier: true);

        using var env = new EnvironmentVariableScope("DOTNET_ENVIRONMENT", "Development");
        var factory = new DefaultGrpcChannelFactory(
            Options.Create(new GrpcTransportOptions { AllowInsecure = true }),
            NullLogger<DefaultGrpcChannelFactory>.Instance);
        using var transport = new GrpcAgentTransport(factory, NullLogger<GrpcAgentTransport>.Instance, clearances: authority);

        var result = await transport.SendAsync(new AgentInvocationRequest(
            AgentName: "agent-1",
            CorrelationId: "corr-barrier",
            Options: new AgentInvocationOptions(
                Timeout: TimeSpan.FromSeconds(2),
                MaxRetries: 0,
                TargetEndpoint: fixture.Endpoint)));

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("BARRIER_VALIDATION_FAILED");
        result.ErrorMessage.Should().Contain("No explicit barrier");
    }

    [Fact]
    public async Task Invoke_returns_barrier_validation_failure_for_unknown_level()
    {
        await using var fixture = await GrpcTransportTestFixture.StartAsync(new EchoTransport());

        using var env = new EnvironmentVariableScope("DOTNET_ENVIRONMENT", "Development");
        var factory = new DefaultGrpcChannelFactory(
            Options.Create(new GrpcTransportOptions { AllowInsecure = true }),
            NullLogger<DefaultGrpcChannelFactory>.Instance);
        using var transport = new GrpcAgentTransport(factory, NullLogger<GrpcAgentTransport>.Instance);

        var channel = factory.GetOrCreate(fixture.Endpoint);
        var client = new AgentTransportService.AgentTransportServiceClient(channel);
        var headers = new Metadata { { "x-ashlar-barrier", "top-secret" } };

        var response = await client.InvokeAsync(
            new InvokeRequest
            {
                AgentName = "agent-1",
                CorrelationId = "corr-unknown-barrier",
                TimeoutMs = 2000,
            },
            headers: headers,
            deadline: DateTime.UtcNow.AddSeconds(2));

        response.Success.Should().BeFalse();
        response.ErrorCode.Should().Be("BARRIER_VALIDATION_FAILED");
        response.ErrorMessage.Should().Contain("Unknown barrier level");
    }

    [Fact]
    public async Task Invoke_surfaces_transport_failure_from_inner_transport()
    {
        await using var fixture = await GrpcTransportTestFixture.StartAsync(new FailingTransport("agent exploded"));

        using var env = new EnvironmentVariableScope("DOTNET_ENVIRONMENT", "Development");
        var factory = new DefaultGrpcChannelFactory(
            Options.Create(new GrpcTransportOptions { AllowInsecure = true }),
            NullLogger<DefaultGrpcChannelFactory>.Instance);
        using var transport = new GrpcAgentTransport(factory, NullLogger<GrpcAgentTransport>.Instance);

        var headers = new Metadata { { "x-ashlar-barrier", "public" } };
        var channel = factory.GetOrCreate(fixture.Endpoint);
        var client = new AgentTransportService.AgentTransportServiceClient(channel);
        var response = await client.InvokeAsync(
            new InvokeRequest
            {
                AgentName = "agent-1",
                CorrelationId = "corr-fail",
                TimeoutMs = 2000,
            },
            headers: headers,
            deadline: DateTime.UtcNow.AddSeconds(2));

        response.Success.Should().BeFalse();
        response.ErrorMessage.Should().Contain("agent exploded");
    }

    [Fact]
    public async Task CheckHealth_round_trips_server_health()
    {
        await using var fixture = await GrpcTransportTestFixture.StartAsync(new EchoTransport());

        using var env = new EnvironmentVariableScope("DOTNET_ENVIRONMENT", "Development");
        var factory = new DefaultGrpcChannelFactory(
            Options.Create(new GrpcTransportOptions { AllowInsecure = true }),
            NullLogger<DefaultGrpcChannelFactory>.Instance);
        using var transport = new GrpcAgentTransport(factory, NullLogger<GrpcAgentTransport>.Instance);

        var health = await transport.CheckEndpointHealthAsync(fixture.Endpoint);

        health.IsHealthy.Should().BeTrue();
        health.TransportType.Should().Be("echo");
    }

    [Fact]
    public async Task Invoke_serializes_scalar_output_under_result_key()
    {
        await using var fixture = await GrpcTransportTestFixture.StartAsync(new ScalarOutputTransport());

        using var env = new EnvironmentVariableScope("DOTNET_ENVIRONMENT", "Development");
        var factory = new DefaultGrpcChannelFactory(
            Options.Create(new GrpcTransportOptions { AllowInsecure = true }),
            NullLogger<DefaultGrpcChannelFactory>.Instance);

        var channel = factory.GetOrCreate(fixture.Endpoint);
        var client = new AgentTransportService.AgentTransportServiceClient(channel);
        var response = await client.InvokeAsync(
            new InvokeRequest
            {
                AgentName = "agent-1",
                CorrelationId = "corr-scalar",
                TimeoutMs = 2000,
            },
            headers: new Metadata { { "x-ashlar-barrier", "public" } },
            deadline: DateTime.UtcNow.AddSeconds(2));

        response.Success.Should().BeTrue();
        response.Output.Should().ContainKey("result");
    }

    [Fact]
    public async Task Invoke_applies_default_floor_when_no_barrier_header()
    {
        await using var fixture = await GrpcTransportTestFixture.StartAsync(new EchoTransport());

        using var env = new EnvironmentVariableScope("DOTNET_ENVIRONMENT", "Development");
        var factory = new DefaultGrpcChannelFactory(
            Options.Create(new GrpcTransportOptions { AllowInsecure = true }),
            NullLogger<DefaultGrpcChannelFactory>.Instance);

        var channel = factory.GetOrCreate(fixture.Endpoint);
        var client = new AgentTransportService.AgentTransportServiceClient(channel);
        var response = await client.InvokeAsync(
            new InvokeRequest
            {
                AgentName = "agent-1",
                CorrelationId = "corr-default-floor",
                TimeoutMs = 2000,
            },
            deadline: DateTime.UtcNow.AddSeconds(2));

        response.Success.Should().BeTrue();
    }

    [Fact]
    public async Task Invoke_round_trips_payload_metadata_and_correlation_header()
    {
        await using var fixture = await GrpcTransportTestFixture.StartAsync(new EchoTransport());

        using var env = new EnvironmentVariableScope("DOTNET_ENVIRONMENT", "Development");
        var factory = new DefaultGrpcChannelFactory(
            Options.Create(new GrpcTransportOptions { AllowInsecure = true }),
            NullLogger<DefaultGrpcChannelFactory>.Instance);

        var channel = factory.GetOrCreate(fixture.Endpoint);
        var client = new AgentTransportService.AgentTransportServiceClient(channel);
        var headers = new Metadata
        {
            { "x-ashlar-barrier", "public" },
            { "x-ashlar-correlation-id", "corr-from-header" },
        };

        var request = new InvokeRequest
        {
            AgentName = "agent-1",
            CorrelationId = "corr-body",
            TimeoutMs = 2000,
            MaxRetries = 1,
            TargetEndpoint = "https://remote.example",
        };
        request.Payload["answer"] = "42";
        request.Metadata["trace"] = "abc";

        var response = await client.InvokeAsync(
            request,
            headers: headers,
            deadline: DateTime.UtcNow.AddSeconds(2));

        response.Success.Should().BeTrue();
        response.CorrelationId.Should().Be("corr-from-header");
        response.Output.Should().ContainKey("answer");
    }

    [Fact]
    public async Task Invoke_serializes_dictionary_output_without_result_wrapper()
    {
        await using var fixture = await GrpcTransportTestFixture.StartAsync(new DictionaryOutputTransport());

        using var env = new EnvironmentVariableScope("DOTNET_ENVIRONMENT", "Development");
        var factory = new DefaultGrpcChannelFactory(
            Options.Create(new GrpcTransportOptions { AllowInsecure = true }),
            NullLogger<DefaultGrpcChannelFactory>.Instance);

        var channel = factory.GetOrCreate(fixture.Endpoint);
        var client = new AgentTransportService.AgentTransportServiceClient(channel);
        var response = await client.InvokeAsync(
            new InvokeRequest
            {
                AgentName = "agent-1",
                CorrelationId = "corr-dict",
                TimeoutMs = 2000,
            },
            headers: new Metadata { { "x-ashlar-barrier", "public" } },
            deadline: DateTime.UtcNow.AddSeconds(2));

        response.Success.Should().BeTrue();
        response.Output.Should().ContainKey("alpha");
        response.Output.Should().NotContainKey("result");
    }

    [Fact]
    public async Task Invoke_uses_metadata_error_code_when_transport_error_code_missing()
    {
        await using var fixture = await GrpcTransportTestFixture.StartAsync(new MetadataErrorTransport());

        using var env = new EnvironmentVariableScope("DOTNET_ENVIRONMENT", "Development");
        var factory = new DefaultGrpcChannelFactory(
            Options.Create(new GrpcTransportOptions { AllowInsecure = true }),
            NullLogger<DefaultGrpcChannelFactory>.Instance);

        var channel = factory.GetOrCreate(fixture.Endpoint);
        var client = new AgentTransportService.AgentTransportServiceClient(channel);
        var response = await client.InvokeAsync(
            new InvokeRequest
            {
                AgentName = "agent-1",
                CorrelationId = "corr-meta-error",
                TimeoutMs = 2000,
            },
            headers: new Metadata { { "x-ashlar-barrier", "public" } },
            deadline: DateTime.UtcNow.AddSeconds(2));

        response.Success.Should().BeFalse();
        response.ErrorCode.Should().Be("META_CODE");
    }

    [Fact]
    public async Task CheckHealth_uses_transport_name_when_type_missing()
    {
        await using var fixture = await GrpcTransportTestFixture.StartAsync(new NameOnlyHealthTransport());

        using var env = new EnvironmentVariableScope("DOTNET_ENVIRONMENT", "Development");
        var factory = new DefaultGrpcChannelFactory(
            Options.Create(new GrpcTransportOptions { AllowInsecure = true }),
            NullLogger<DefaultGrpcChannelFactory>.Instance);

        var channel = factory.GetOrCreate(fixture.Endpoint);
        var client = new AgentTransportService.AgentTransportServiceClient(channel);
        var response = await client.CheckHealthAsync(new HealthRequest(), deadline: DateTime.UtcNow.AddSeconds(2));

        response.IsHealthy.Should().BeTrue();
        response.TransportType.Should().Be("name-only");
        response.DiagnosticMessage.Should().Be("ready");
    }

    [Fact]
    public async Task Invoke_accepts_non_bearer_authorization_and_api_key_headers()
    {
        await using var fixture = await GrpcTransportTestFixture.StartAsync(new EchoTransport());

        using var env = new EnvironmentVariableScope("DOTNET_ENVIRONMENT", "Development");
        var factory = new DefaultGrpcChannelFactory(
            Options.Create(new GrpcTransportOptions { AllowInsecure = true }),
            NullLogger<DefaultGrpcChannelFactory>.Instance);

        var channel = factory.GetOrCreate(fixture.Endpoint);
        var client = new AgentTransportService.AgentTransportServiceClient(channel);
        var headers = new Metadata
        {
            { "x-ashlar-barrier", "public" },
            { "authorization", "ApiKey abc123" },
            { "x-ashlar-api-key", "secret-key" },
        };

        var response = await client.InvokeAsync(
            new InvokeRequest
            {
                AgentName = "agent-1",
                CorrelationId = "corr-auth",
                TimeoutMs = 2000,
            },
            headers: headers,
            deadline: DateTime.UtcNow.AddSeconds(2));

        response.Success.Should().BeTrue();
    }

    [Fact]
    public async Task Invoke_treats_invalid_payload_json_as_opaque_string()
    {
        await using var fixture = await GrpcTransportTestFixture.StartAsync(new EchoTransport());

        using var env = new EnvironmentVariableScope("DOTNET_ENVIRONMENT", "Development");
        var factory = new DefaultGrpcChannelFactory(
            Options.Create(new GrpcTransportOptions { AllowInsecure = true }),
            NullLogger<DefaultGrpcChannelFactory>.Instance);

        var channel = factory.GetOrCreate(fixture.Endpoint);
        var client = new AgentTransportService.AgentTransportServiceClient(channel);
        var request = new InvokeRequest
        {
            AgentName = "agent-1",
            CorrelationId = "corr-bad-json",
            TimeoutMs = 2000,
        };
        request.Payload["note"] = "not-json{{";

        var response = await client.InvokeAsync(
            request,
            headers: new Metadata { { "x-ashlar-barrier", "public" } },
            deadline: DateTime.UtcNow.AddSeconds(2));

        response.Success.Should().BeTrue();
        response.Output.Should().ContainKey("note");
    }

    [Fact]
    public async Task Invoke_returns_explicit_transport_error_code_when_present()
    {
        await using var fixture = await GrpcTransportTestFixture.StartAsync(new ExplicitErrorCodeTransport());

        using var env = new EnvironmentVariableScope("DOTNET_ENVIRONMENT", "Development");
        var factory = new DefaultGrpcChannelFactory(
            Options.Create(new GrpcTransportOptions { AllowInsecure = true }),
            NullLogger<DefaultGrpcChannelFactory>.Instance);

        var channel = factory.GetOrCreate(fixture.Endpoint);
        var client = new AgentTransportService.AgentTransportServiceClient(channel);
        var response = await client.InvokeAsync(
            new InvokeRequest
            {
                AgentName = "agent-1",
                CorrelationId = "corr-explicit-code",
                TimeoutMs = 2000,
            },
            headers: new Metadata { { "x-ashlar-barrier", "public" } },
            deadline: DateTime.UtcNow.AddSeconds(2));

        response.Success.Should().BeFalse();
        response.ErrorCode.Should().Be("EXPLICIT");
    }

    [Fact]
    public async Task Invoke_serializes_non_nullable_object_dictionary_output()
    {
        await using var fixture = await GrpcTransportTestFixture.StartAsync(new ObjectDictionaryOutputTransport());

        using var env = new EnvironmentVariableScope("DOTNET_ENVIRONMENT", "Development");
        var factory = new DefaultGrpcChannelFactory(
            Options.Create(new GrpcTransportOptions { AllowInsecure = true }),
            NullLogger<DefaultGrpcChannelFactory>.Instance);

        var channel = factory.GetOrCreate(fixture.Endpoint);
        var client = new AgentTransportService.AgentTransportServiceClient(channel);
        var response = await client.InvokeAsync(
            new InvokeRequest
            {
                AgentName = "agent-1",
                CorrelationId = "corr-object-dict",
                TimeoutMs = 2000,
            },
            headers: new Metadata { { "x-ashlar-barrier", "public" } },
            deadline: DateTime.UtcNow.AddSeconds(2));

        response.Success.Should().BeTrue();
        response.Output.Should().ContainKey("count");
        response.Output["count"].Should().Be("7");
    }

    /// <summary>Object dictionary output transport.</summary>
    private sealed class ObjectDictionaryOutputTransport : IAgentTransport
    {
        /// <summary>Send async.</summary>
        /// <param name="request">Request.</param>
        /// <param name="default">Default.</param>
        public Task<AgentResult> SendAsync(AgentInvocationRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AgentResult(
                Success: true,
                Output: new Dictionary<string, object> { ["count"] = 7 },
                CorrelationId: request.CorrelationId,
                SpanId: request.SpanId));

        /// <summary>Check health async.</summary>
        /// <param name="default">Default.</param>
        public Task<TransportHealth> CheckHealthAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new TransportHealth(true, "object-dict"));
    }

    /// <summary>Explicit error code transport.</summary>
    private sealed class ExplicitErrorCodeTransport : IAgentTransport
    {
        /// <summary>Send async.</summary>
        /// <param name="request">Request.</param>
        /// <param name="default">Default.</param>
        public Task<AgentResult> SendAsync(AgentInvocationRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AgentResult(
                Success: false,
                ErrorMessage: "failed",
                ErrorCode: "EXPLICIT",
                CorrelationId: request.CorrelationId,
                SpanId: request.SpanId));

        /// <summary>Check health async.</summary>
        /// <param name="default">Default.</param>
        public Task<TransportHealth> CheckHealthAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new TransportHealth(false, "explicit-error"));
    }

    /// <summary>Dictionary output transport.</summary>
    private sealed class DictionaryOutputTransport : IAgentTransport
    {
        /// <summary>Send async.</summary>
        /// <param name="request">Request.</param>
        /// <param name="default">Default.</param>
        public Task<AgentResult> SendAsync(AgentInvocationRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AgentResult(
                Success: true,
                Output: new Dictionary<string, object?> { ["alpha"] = 1 },
                CorrelationId: request.CorrelationId,
                SpanId: request.SpanId));

        /// <summary>Check health async.</summary>
        /// <param name="default">Default.</param>
        public Task<TransportHealth> CheckHealthAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new TransportHealth(true, "dict"));
    }

    /// <summary>Metadata error transport.</summary>
    private sealed class MetadataErrorTransport : IAgentTransport
    {
        /// <summary>Send async.</summary>
        /// <param name="request">Request.</param>
        /// <param name="default">Default.</param>
        public Task<AgentResult> SendAsync(AgentInvocationRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AgentResult(
                Success: false,
                ErrorMessage: "failed",
                Metadata: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["errorCode"] = "META_CODE",
                },
                CorrelationId: request.CorrelationId,
                SpanId: request.SpanId));

        /// <summary>Check health async.</summary>
        /// <param name="default">Default.</param>
        public Task<TransportHealth> CheckHealthAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new TransportHealth(false, "meta-error"));
    }

    /// <summary>Name only health transport.</summary>
    private sealed class NameOnlyHealthTransport : IAgentTransport
    {
        /// <summary>Send async.</summary>
        /// <param name="request">Request.</param>
        /// <param name="default">Default.</param>
        public Task<AgentResult> SendAsync(AgentInvocationRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AgentResult(
                Success: true,
                Output: new Dictionary<string, object?>(),
                CorrelationId: request.CorrelationId,
                SpanId: request.SpanId));

        /// <summary>Check health async.</summary>
        /// <param name="default">Default.</param>
        public Task<TransportHealth> CheckHealthAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new TransportHealth(
                IsHealthy: true,
                TransportName: "name-only",
                Message: "ready"));
    }

    /// <summary>Scalar output transport.</summary>
    private sealed class ScalarOutputTransport : IAgentTransport
    {
        /// <summary>Send async.</summary>
        /// <param name="request">Request.</param>
        /// <param name="default">Default.</param>
        public Task<AgentResult> SendAsync(AgentInvocationRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AgentResult(
                Success: true,
                Output: 42,
                CorrelationId: request.CorrelationId,
                SpanId: request.SpanId));

        /// <summary>Check health async.</summary>
        /// <param name="default">Default.</param>
        public Task<TransportHealth> CheckHealthAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new TransportHealth(true, "scalar"));
    }

    /// <summary>Echo transport.</summary>
    private sealed class EchoTransport : IAgentTransport
    {
        /// <summary>Send async.</summary>
        /// <param name="request">Request.</param>
        /// <param name="default">Default.</param>
        public Task<AgentResult> SendAsync(AgentInvocationRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AgentResult(
                Success: true,
                Output: request.Payload ?? new Dictionary<string, object?>(),
                CorrelationId: request.CorrelationId,
                SpanId: request.SpanId));

        /// <summary>Check health async.</summary>
        /// <param name="default">Default.</param>
        public Task<TransportHealth> CheckHealthAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new TransportHealth(
                IsHealthy: true,
                TransportName: "echo",
                Message: "ok",
                TransportType: "echo",
                DiagnosticMessage: "ok"));
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
