using Ashlar.Abstractions.Security.Egress;
using System.Collections.Generic;
using System.Text.Json;
using System.Security.Cryptography.X509Certificates;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ashlar.Abstractions.Barriers;
using Ashlar.Abstractions.Barriers.Identity;
using Ashlar.Abstractions.Transport;

namespace Ashlar.Transport.Grpc.Server;

/// <summary>
/// Thin gRPC server facade over <see cref="IAgentTransport"/>.
/// </summary>
public sealed class AgentTransportServiceImpl : AgentTransportService.AgentTransportServiceBase
{
    private const string BarrierHeader = "x-ashlar-barrier";
    private const string CorrelationHeader = "x-ashlar-correlation-id";

    private readonly IAgentTransport _transport;
    private readonly ILogger<AgentTransportServiceImpl> _logger;
    private readonly BarrierHierarchy _barrierHierarchy;
    private readonly BarrierOptions _barrierOptions;
    private readonly IBarrierContextAccessor _barrierContextAccessor;
    private readonly IBarrierAuditLog _barrierAuditLog;
    private readonly IBarrierIdentityResolverPipeline _barrierResolverPipeline;

    public AgentTransportServiceImpl(
        IAgentTransport transport,
        ILogger<AgentTransportServiceImpl> logger,
        BarrierHierarchy barrierHierarchy,
        IOptions<BarrierOptions> barrierOptions,
        IBarrierContextAccessor barrierContextAccessor,
        IBarrierAuditLog barrierAuditLog,
        IBarrierIdentityResolverPipeline barrierResolverPipeline)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _barrierHierarchy = barrierHierarchy ?? throw new ArgumentNullException(nameof(barrierHierarchy));
        _barrierOptions = barrierOptions?.Value ?? throw new ArgumentNullException(nameof(barrierOptions));
        _barrierContextAccessor = barrierContextAccessor ?? throw new ArgumentNullException(nameof(barrierContextAccessor));
        _barrierAuditLog = barrierAuditLog ?? throw new ArgumentNullException(nameof(barrierAuditLog));
        _barrierResolverPipeline = barrierResolverPipeline ?? throw new ArgumentNullException(nameof(barrierResolverPipeline));
    }

    public override async Task<InvokeResponse> Invoke(InvokeRequest request, ServerCallContext context)
    {
        try { return await InvokeCoreAsync(request, context).ConfigureAwait(false); }
        catch (Exception ex) when (EgressRefusal.Find(ex) is { } refusal)
        {
            return RefusedResponse(refusal.Ref, request.CorrelationId, request.SpanId);
        }
    }

    private async Task<InvokeResponse> InvokeCoreAsync(InvokeRequest request, ServerCallContext context)
    {
        _logger.LogDebug("gRPC Invoke request for agent {AgentName}", request.AgentName);
        var correlationId = GetHeader(context, CorrelationHeader) ?? request.CorrelationId;
        var spanId = string.IsNullOrWhiteSpace(request.SpanId) ? string.Empty : request.SpanId;

        var headers = ToHeaderDictionary(context);
        var explicitLevel = headers.TryGetValue(BarrierHeader, out var explicitHeaderLevel)
            ? explicitHeaderLevel
            : null;
        var rawJwt = TryGetBearerToken(headers);
        var certSubjects = ExtractCertificateSubjects(context);
        var certSans = ExtractCertificateSans(context);
        var resolutionContext = new BarrierResolutionContext(
            CorrelationId: correlationId ?? string.Empty,
            ExplicitLevel: string.IsNullOrWhiteSpace(explicitLevel) ? null : explicitLevel,
            Headers: headers,
            CertSubjects: certSubjects,
            CertSans: certSans,
            RawJwt: rawJwt,
            JwtClaims: JwtClaimParser.ParseClaims(rawJwt),
            ApiKey: TryGetApiKey(headers));

        var resolutionResult = await _barrierResolverPipeline.ResolveAsync(
            resolutionContext,
            context.CancellationToken);

        if (resolutionResult is null)
        {
            if (_barrierOptions.RequireExplicitBarrier)
            {
                await _barrierAuditLog.RecordAsync(new BarrierAuditEvent(
                    BarrierAuditEventType.ValidationFailed,
                    string.Empty,
                    BarrierAuthoritySource.Default,
                    request.AgentName,
                    correlationId ?? string.Empty,
                    spanId,
                    DateTimeOffset.UtcNow,
                    "No explicit barrier or identity resolver match."));
                return InvalidBarrierResponse(request, correlationId, spanId, "No explicit barrier or identity resolver match.");
            }

            resolutionResult = new BarrierResolutionResult(
                ResolvedLevel: _barrierHierarchy.Floor.Name,
                ResolverName: "DefaultFloor",
                AuthoritySource: BarrierAuthoritySource.Default,
                Detail: "No explicit barrier or identity match; defaulted to floor level.");
            await _barrierAuditLog.RecordAsync(new BarrierAuditEvent(
                BarrierAuditEventType.DefaultApplied,
                resolutionResult.ResolvedLevel,
                resolutionResult.AuthoritySource,
                request.AgentName,
                correlationId ?? string.Empty,
                spanId,
                DateTimeOffset.UtcNow,
                resolutionResult.Detail),
                context.CancellationToken);
        }

        await _barrierAuditLog.RecordAsync(new BarrierAuditEvent(
            BarrierAuditEventType.IdentityResolved,
            resolutionResult.ResolvedLevel,
            resolutionResult.AuthoritySource,
            request.AgentName,
            correlationId ?? string.Empty,
            spanId,
            DateTimeOffset.UtcNow,
            $"Resolved by: {resolutionResult.ResolverName}. {resolutionResult.Detail}"),
            context.CancellationToken);

        if (!_barrierHierarchy.IsKnown(resolutionResult.ResolvedLevel))
        {
            await _barrierAuditLog.RecordAsync(new BarrierAuditEvent(
                BarrierAuditEventType.ValidationFailed,
                resolutionResult.ResolvedLevel,
                resolutionResult.AuthoritySource,
                request.AgentName,
                correlationId ?? string.Empty,
                spanId,
                DateTimeOffset.UtcNow,
                "Resolved barrier level is unknown in hierarchy."),
                context.CancellationToken);
            return InvalidBarrierResponse(request, correlationId, spanId, $"Unknown barrier level '{resolutionResult.ResolvedLevel}'.");
        }

        var barrierContext = BarrierContext.Create(
            resolutionResult.ResolvedLevel,
            resolutionResult.AuthoritySource,
            request.AgentName,
            correlationId ?? string.Empty,
            _barrierHierarchy,
            resolutionDetail: string.IsNullOrWhiteSpace(resolutionResult.Detail)
                ? $"Resolved by: {resolutionResult.ResolverName}."
                : $"Resolved by: {resolutionResult.ResolverName}. {resolutionResult.Detail}");
        _barrierContextAccessor.Initialize(barrierContext);
        await _barrierAuditLog.RecordAsync(new BarrierAuditEvent(
            BarrierAuditEventType.ContextInitialized,
            barrierContext.Level,
            barrierContext.AuthoritySource,
            barrierContext.IssuedTo,
            barrierContext.CorrelationId,
            spanId,
            DateTimeOffset.UtcNow));

        var payload = DeserializePayload(request.Payload);
        IReadOnlyDictionary<string, string>? metadata = null;
        if (request.Metadata.Count > 0)
        {
            var meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in request.Metadata)
            {
                meta[kv.Key] = kv.Value;
            }

            metadata = meta;
        }

        var invocation = new AgentInvocationRequest(
            AgentName: request.AgentName,
            CorrelationId: correlationId ?? request.CorrelationId,
            SpanId: string.IsNullOrWhiteSpace(request.SpanId) ? null : request.SpanId,
            Payload: payload,
            Options: new AgentInvocationOptions(
                Timeout: TimeSpan.FromMilliseconds(Math.Max(1, request.TimeoutMs)),
                MaxRetries: Math.Max(0, request.MaxRetries),
                TargetEndpoint: string.IsNullOrWhiteSpace(request.TargetEndpoint) ? null : request.TargetEndpoint),
            Metadata: metadata);

        var result = await _transport.SendAsync(invocation, context.CancellationToken);
        if (EgressRefusal.IsRefused(result))
            return RefusedResponse(EgressRefusal.Reference(result),
                result.CorrelationId ?? correlationId ?? request.CorrelationId,
                result.SpanId ?? request.SpanId);
        var output = SerializeOutput(result.Output);
        var errorCode = !string.IsNullOrWhiteSpace(result.ErrorCode)
            ? result.ErrorCode
            : (result.Metadata != null && result.Metadata.TryGetValue("errorCode", out var code) ? code : string.Empty);

        return new InvokeResponse
        {
            Success = result.Success,
            CorrelationId = result.CorrelationId ?? correlationId ?? request.CorrelationId ?? string.Empty,
            SpanId = result.SpanId ?? request.SpanId ?? string.Empty,
            ErrorMessage = result.ErrorMessage ?? string.Empty,
            ErrorCode = errorCode ?? string.Empty,
            Output = { output }
        };
    }

    private static InvokeResponse RefusedResponse(string reference, string? correlationId, string? spanId) => new()
    {
        Success = false,
        ErrorCode = EgressRefusal.Code,
        ErrorMessage = EgressRefusal.RemoteMessage(reference),
        EgressRef = reference,
        CorrelationId = correlationId ?? string.Empty,
        SpanId = spanId ?? string.Empty
    };

    public override async Task<HealthResponse> CheckHealth(HealthRequest request, ServerCallContext context)
    {
        _logger.LogDebug("gRPC health check request");
        var health = await _transport.CheckHealthAsync(context.CancellationToken);
        return new HealthResponse
        {
            IsHealthy = health.IsHealthy,
            TransportType = health.TransportType ?? health.TransportName,
            DiagnosticMessage = health.DiagnosticMessage ?? health.Message ?? string.Empty
        };
    }

    private static Dictionary<string, object?> DeserializePayload(IDictionary<string, string> payload)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in payload)
        {
            result[key] = DeserializeJson(value);
        }
        return result;
    }

    private static Dictionary<string, string> SerializeOutput(object? output)
    {
        if (output is IReadOnlyDictionary<string, object?> roMap)
        {
            return roMap.ToDictionary(kvp => kvp.Key, kvp => JsonSerializer.Serialize(kvp.Value), StringComparer.OrdinalIgnoreCase);
        }

        if (output is IReadOnlyDictionary<string, object> map)
        {
            return map.ToDictionary(kvp => kvp.Key, kvp => JsonSerializer.Serialize(kvp.Value), StringComparer.OrdinalIgnoreCase);
        }

        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["result"] = JsonSerializer.Serialize(output)
        };
    }

    private static object? DeserializeJson(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<object?>(json);
        }
        catch
        {
            return json;
        }
    }

    private static string? GetHeader(ServerCallContext context, string key)
    {
        return context.RequestHeaders
            .FirstOrDefault(header => string.Equals(header.Key, key, StringComparison.OrdinalIgnoreCase))
            ?.Value;
    }

    private static Dictionary<string, string> ToHeaderDictionary(ServerCallContext context)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in context.RequestHeaders)
        {
            if (entry.IsBinary || string.IsNullOrWhiteSpace(entry.Key))
                continue;

            headers[entry.Key] = entry.Value ?? string.Empty;
        }

        return headers;
    }

    private static string? TryGetBearerToken(IReadOnlyDictionary<string, string> headers)
    {
        if (!headers.TryGetValue("authorization", out var authorizationValue))
            return null;

        const string bearerPrefix = "Bearer ";
        if (!authorizationValue.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase))
            return authorizationValue;

        return authorizationValue[bearerPrefix.Length..].Trim();
    }

    private static string? TryGetApiKey(IReadOnlyDictionary<string, string> headers)
    {
        if (headers.TryGetValue("x-ashlar-api-key", out var apiKey))
            return apiKey;

        return null;
    }

    private static IReadOnlyList<string> ExtractCertificateSubjects(ServerCallContext context)
    {
        var cert = context.GetHttpContext()?.Connection.ClientCertificate;
        if (cert is null || string.IsNullOrWhiteSpace(cert.Subject))
            return Array.Empty<string>();

        return [cert.Subject];
    }

    private static IReadOnlyList<string> ExtractCertificateSans(ServerCallContext context)
    {
        var cert = context.GetHttpContext()?.Connection.ClientCertificate;
        if (cert is null)
            return Array.Empty<string>();

        var sans = new List<string>();
        foreach (var extension in cert.Extensions.OfType<X509Extension>())
        {
            if (!string.Equals(extension.Oid?.Value, "2.5.29.17", StringComparison.Ordinal))
                continue;

            var formatted = extension.Format(false);
            if (string.IsNullOrWhiteSpace(formatted))
                continue;

            var parts = formatted.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var part in parts)
            {
                var value = part.Contains('=')
                    ? part[(part.IndexOf('=') + 1)..].Trim()
                    : part.Trim();
                if (!string.IsNullOrWhiteSpace(value))
                    sans.Add(value);
            }
        }

        return sans;
    }

    private static InvokeResponse InvalidBarrierResponse(
        InvokeRequest request,
        string? correlationId,
        string spanId,
        string message)
    {
        return new InvokeResponse
        {
            Success = false,
            CorrelationId = correlationId ?? request.CorrelationId ?? string.Empty,
            SpanId = spanId,
            ErrorMessage = message,
            ErrorCode = "BARRIER_VALIDATION_FAILED"
        };
    }
}
