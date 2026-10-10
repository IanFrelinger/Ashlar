using Ashlar.Abstractions.Security.Egress;
using System.Collections.Concurrent;
using System.Text.Json;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Ashlar.Abstractions.Barriers;
using Ashlar.Abstractions.Transport;

namespace Ashlar.Transport.Grpc;

/// <summary>
/// gRPC-based implementation of <see cref="IAgentTransport"/> for remote agent execution.
/// </summary>
public sealed class GrpcAgentTransport : IAgentTransport, IDisposable
{
    private readonly IGrpcChannelFactory _channelFactory;
    private readonly ILogger<GrpcAgentTransport> _logger;
    private readonly IBarrierContextAmbient? _barrierContextAmbient;
    private readonly IBarrierAuditLog? _barrierAuditLog;
    private readonly ConcurrentDictionary<string, byte> _activeEndpoints = new(StringComparer.OrdinalIgnoreCase);

    public GrpcAgentTransport(
        IGrpcChannelFactory channelFactory,
        ILogger<GrpcAgentTransport> logger,
        IBarrierContextAmbient? barrierContextAmbient = null,
        IBarrierAuditLog? barrierAuditLog = null)
    {
        _channelFactory = channelFactory ?? throw new ArgumentNullException(nameof(channelFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _barrierContextAmbient = barrierContextAmbient;
        _barrierAuditLog = barrierAuditLog;
    }

    public async Task<AgentResult> SendAsync(
        AgentInvocationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var endpoint = request.EffectiveOptions.TargetEndpoint;
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return new AgentResult(
                Success: false,
                ErrorMessage: "Remote endpoint is required for gRPC transport.",
                ErrorCode: "TARGET_ENDPOINT_REQUIRED",
                CorrelationId: request.CorrelationId,
                SpanId: request.SpanId);
        }

        var startedAt = DateTimeOffset.UtcNow;
        _activeEndpoints[endpoint] = 0;

        try
        {
            var channel = _channelFactory.GetOrCreate(endpoint);
            var client = new AgentTransportService.AgentTransportServiceClient(channel);

            var invokeRequest = BuildInvokeRequest(request, endpoint);
            var headers = BuildHeaders(request);
            var deadline = DateTime.UtcNow + request.EffectiveOptions.Timeout;

            var response = await client.InvokeAsync(
                invokeRequest,
                headers: headers,
                deadline: deadline,
                cancellationToken: cancellationToken).ResponseAsync;

            if (!response.Success && EgressRefusal.IsCode(response.ErrorCode))
            {
                var reference = EgressRefusal.SafeReference(response.EgressRef);
                return new AgentResult(false, ErrorMessage: EgressRefusal.RemoteMessage(reference),
                    ErrorCode: EgressRefusal.Code, Metadata: EgressRefusal.Metadata(reference),
                    Duration: DateTimeOffset.UtcNow - startedAt,
                    CorrelationId: request.CorrelationId, SpanId: request.SpanId);
            }
            var output = DeserializeOutput(response.Output);
            return new AgentResult(
                Success: response.Success,
                Output: output,
                ErrorMessage: response.ErrorMessage,
                Duration: DateTimeOffset.UtcNow - startedAt,
                Metadata: BuildMetadata(response.ErrorCode),
                ErrorCode: string.IsNullOrWhiteSpace(response.ErrorCode) ? null : response.ErrorCode,
                CorrelationId: string.IsNullOrWhiteSpace(response.CorrelationId) ? request.CorrelationId : response.CorrelationId,
                SpanId: string.IsNullOrWhiteSpace(response.SpanId) ? request.SpanId : response.SpanId);
        }
        catch (Exception ex) when (EgressRefusal.Find(ex) is { } refusal)
        {
            return new AgentResult(false, ErrorMessage: refusal.Message,
                ErrorCode: EgressRefusal.Code, Metadata: EgressRefusal.Metadata(refusal),
                Duration: DateTimeOffset.UtcNow - startedAt,
                CorrelationId: request.CorrelationId, SpanId: request.SpanId);
        }
        catch (RpcException ex)
        {
            var errorCode = MapRpcErrorCode(ex.StatusCode);
            _logger.LogWarning(ex, "gRPC transport invocation failed for endpoint {Endpoint}", endpoint);

            return new AgentResult(
                Success: false,
                ErrorMessage: ex.Status.Detail,
                Duration: DateTimeOffset.UtcNow - startedAt,
                Metadata: BuildMetadata(errorCode),
                ErrorCode: errorCode,
                CorrelationId: request.CorrelationId,
                SpanId: request.SpanId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected gRPC transport error for endpoint {Endpoint}", endpoint);
            return new AgentResult(
                Success: false,
                ErrorMessage: ex.Message,
                Duration: DateTimeOffset.UtcNow - startedAt,
                Metadata: BuildMetadata("TRANSPORT_ERROR"),
                ErrorCode: "TRANSPORT_ERROR",
                CorrelationId: request.CorrelationId,
                SpanId: request.SpanId);
        }
    }

    public async Task<TransportHealth> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        if (_activeEndpoints.IsEmpty)
        {
            return new TransportHealth(
                IsHealthy: true,
                TransportName: "gRPC",
                Message: "No active channels",
                TransportType: "gRPC",
                DiagnosticMessage: "No active channels");
        }

        var diagnostics = new List<string>();
        var allHealthy = true;

        foreach (var endpoint in _activeEndpoints.Keys)
        {
            var endpointHealth = await CheckEndpointHealthAsync(endpoint, cancellationToken);
            diagnostics.Add($"{endpoint}: {(endpointHealth.IsHealthy ? "healthy" : "unhealthy")} - {endpointHealth.DiagnosticMessage ?? endpointHealth.Message}");
            allHealthy &= endpointHealth.IsHealthy;
        }

        var message = string.Join(" | ", diagnostics);
        return new TransportHealth(
            IsHealthy: allHealthy,
            TransportName: "gRPC",
            Message: message,
            TransportType: "gRPC",
            DiagnosticMessage: message);
    }

    /// <summary>
    /// Probes transport health for a specific endpoint.
    /// </summary>
    public async Task<TransportHealth> CheckEndpointHealthAsync(
        string endpoint,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _activeEndpoints[endpoint] = 0;
            var channel = _channelFactory.GetOrCreate(endpoint);
            var client = new AgentTransportService.AgentTransportServiceClient(channel);
            var response = await client.CheckHealthAsync(
                new HealthRequest(),
                cancellationToken: cancellationToken).ResponseAsync;

            return new TransportHealth(
                IsHealthy: response.IsHealthy,
                TransportName: "gRPC",
                Message: response.DiagnosticMessage,
                TransportType: response.TransportType,
                DiagnosticMessage: response.DiagnosticMessage);
        }
        catch (Exception ex) when (EgressRefusal.Find(ex) is { } refusal)
        {
            return new TransportHealth(false, "gRPC", EgressRefusal.RemoteMessage(refusal.Ref), "gRPC") { Refusal = refusal };
        }
        catch (Exception ex)
        {
            return new TransportHealth(
                IsHealthy: false,
                TransportName: "gRPC",
                Message: ex.Message,
                TransportType: "gRPC",
                DiagnosticMessage: ex.Message);
        }
    }

    public void Dispose()
    {
        _channelFactory.DisposeAll();
        _activeEndpoints.Clear();
    }

    private static InvokeRequest BuildInvokeRequest(AgentInvocationRequest request, string endpoint)
    {
        var payload = request.Payload
            ?? request.DependencyOutputs?.ToDictionary(kvp => kvp.Key, kvp => (object?)kvp.Value, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        var invokeRequest = new InvokeRequest
        {
            AgentName = request.AgentName,
            CorrelationId = request.CorrelationId ?? string.Empty,
            SpanId = request.SpanId ?? string.Empty,
            TargetEndpoint = endpoint,
            TimeoutMs = (int)Math.Clamp(request.EffectiveOptions.Timeout.TotalMilliseconds, 1, int.MaxValue),
            MaxRetries = Math.Max(0, request.EffectiveOptions.MaxRetries)
        };

        foreach (var (key, value) in payload)
        {
            invokeRequest.Payload[key] = SerializeToJson(value);
        }

        if (request.Metadata is { Count: > 0 })
        {
            foreach (var (key, value) in request.Metadata)
            {
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                invokeRequest.Metadata[key] = value ?? string.Empty;
            }
        }

        return invokeRequest;
    }

    private Metadata BuildHeaders(AgentInvocationRequest request)
    {
        var headers = new Metadata
        {
            { "x-ashlar-correlation-id", request.CorrelationId ?? string.Empty }
        };

        var barrier = _barrierContextAmbient?.Current;
        if (barrier == null)
            return headers;

        headers.Add("x-ashlar-barrier", barrier.Level);
        headers.Add("x-ashlar-barrier-source", barrier.AuthoritySource);
        if (_barrierAuditLog != null)
        {
            _ = _barrierAuditLog.RecordAsync(new BarrierAuditEvent(
                BarrierAuditEventType.PropagatedOverWire,
                barrier.Level,
                barrier.AuthoritySource,
                request.AgentName,
                request.CorrelationId ?? string.Empty,
                request.SpanId ?? string.Empty,
                DateTimeOffset.UtcNow,
                $"Propagated barrier over gRPC to {request.EffectiveOptions.TargetEndpoint}."));
        }

        return headers;
    }

    private static Dictionary<string, object?> DeserializeOutput(IDictionary<string, string> outputMap)
    {
        var output = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in outputMap)
        {
            output[key] = DeserializeFromJson(value);
        }
        return output;
    }

    private static IReadOnlyDictionary<string, string>? BuildMetadata(string? errorCode)
    {
        if (string.IsNullOrWhiteSpace(errorCode))
        {
            return null;
        }

        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["errorCode"] = errorCode
        };
    }

    private static string SerializeToJson(object? value)
    {
        return JsonSerializer.Serialize(value);
    }

    private static object? DeserializeFromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<object?>(json);
        }
        catch
        {
            return json;
        }
    }

    private static string MapRpcErrorCode(StatusCode statusCode)
    {
        return statusCode switch
        {
            StatusCode.DeadlineExceeded => "TIMEOUT",
            StatusCode.Cancelled => "TIMEOUT",
            StatusCode.NotFound => "AGENT_NOT_FOUND",
            StatusCode.Unavailable => "TRANSPORT_UNAVAILABLE",
            _ => $"GRPC_ERROR_{statusCode.ToString().ToUpperInvariant()}"
        };
    }
}
