using System.Collections.Concurrent;
using System.Diagnostics;
using A2A;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ashlar.Abstractions.Barriers;
using Ashlar.Abstractions.Security.Egress;
using Ashlar.Abstractions.Transport;

namespace Ashlar.Transport.A2A;

/// <summary>
/// Outbound <see cref="IAgentTransport"/> over the A2A protocol (JSON-RPC over HTTP).
///
/// Selected by the <c>a2a+</c> endpoint scheme via the runtime's scheme-dispatching remote
/// transport; peers are ordinary routing endpoints (<c>a2a+https://peer</c>) so capability
/// routing, health filtering, and barrier levels on <c>EndpointDescriptor</c> work unchanged.
/// Correlation, span, and ambient barrier context propagate as protocol-level message metadata —
/// the A2A counterpart of the gRPC transport's <c>x-ashlar-*</c> headers.
/// </summary>
public sealed class A2AAgentTransport : IAgentTransport
{
    private readonly IOptions<A2ATransportOptions> _options;
    private readonly ILogger<A2AAgentTransport> _logger;
    private readonly IBarrierContextAmbient? _barrierContextAmbient;
    private readonly IBarrierAuditLog? _barrierAuditLog;
    private readonly ConcurrentDictionary<string, HttpClient> _httpClients = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Test seam: substitutes HttpClient creation so round-trip tests can hand in an ASP.NET
    /// TestServer-backed client instead of dialing a real socket.
    /// </summary>
    internal Func<Uri, HttpClient>? HttpClientFactoryOverride { get; set; }

    /// <summary>Creates the transport; barrier services are optional (propagation is skipped without them).</summary>
    public A2AAgentTransport(
        IOptions<A2ATransportOptions> options,
        ILogger<A2AAgentTransport> logger,
        IBarrierContextAmbient? barrierContextAmbient = null,
        IBarrierAuditLog? barrierAuditLog = null)
    {
        _options = options;
        _logger = logger;
        _barrierContextAmbient = barrierContextAmbient;
        _barrierAuditLog = barrierAuditLog;
    }

    /// <inheritdoc />
    public async Task<AgentResult> SendAsync(AgentInvocationRequest request, CancellationToken cancellationToken = default)
    {
        if (request is null)
            throw new ArgumentNullException(nameof(request));

        var endpoint = request.EffectiveOptions.TargetEndpoint;
        if (!A2AEndpointScheme.TryGetHttpBaseUrl(endpoint, out var baseUrl))
        {
            return new AgentResult(
                Success: false,
                ErrorMessage: $"Target endpoint '{endpoint}' is not a valid a2a+http(s) endpoint.",
                ErrorCode: "a2a.endpoint.invalid",
                CorrelationId: request.CorrelationId,
                SpanId: request.SpanId);
        }

        var barrier = _barrierContextAmbient?.Current;
        var message = A2AInvocationMapper.BuildMessage(request, barrier);
        if (barrier is not null && _barrierAuditLog is not null)
        {
            // Mirrors GrpcAgentTransport.BuildHeaders: propagation over a wire boundary is an
            // auditable event regardless of transport protocol.
            _ = _barrierAuditLog.RecordAsync(new BarrierAuditEvent(
                BarrierAuditEventType.PropagatedOverWire,
                barrier.Level,
                barrier.AuthoritySource,
                request.AgentName,
                request.CorrelationId ?? string.Empty,
                request.SpanId ?? string.Empty,
                DateTimeOffset.UtcNow,
                $"Propagated barrier over A2A to {endpoint}."));
        }

        var client = new A2AClient(baseUrl, GetHttpClient(baseUrl));
        // Context metadata rides on both the message and the request: A2A servers surface
        // request metadata via RequestContext.Metadata and message metadata via the message
        // itself, and different implementations read different ones.
        var sendRequest = new SendMessageRequest { Message = message, Metadata = message.Metadata };

        var stopwatch = Stopwatch.StartNew();
        var attempts = 1 + Math.Max(0, request.EffectiveOptions.MaxRetries);
        for (var attempt = 1; ; attempt++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(request.EffectiveOptions.Timeout);
            try
            {
                var response = await client.SendMessageAsync(sendRequest, timeout.Token).ConfigureAwait(false);
                return A2AInvocationMapper.MapResponse(response, request, stopwatch.Elapsed);
            }
            catch (Exception ex) when (EgressRefusal.Find(ex) is { } refusal)
            {
                return Failure(request, stopwatch.Elapsed, "a2a.egress_refused", refusal.Message) with
                {
                    Metadata = EgressRefusal.Metadata(refusal)
                };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                return Failure(request, stopwatch.Elapsed, "a2a.timeout",
                    $"A2A invocation of '{request.AgentName}' at {endpoint} timed out after {request.EffectiveOptions.Timeout}.");
            }
            catch (HttpRequestException ex) when (attempt < attempts)
            {
                // Transport-level transient failure: retry per EffectiveOptions.MaxRetries.
                // Task-level failures (Failed/Rejected states) are never retried — the remote
                // agent already did work and duplicating it is not this layer's call.
                _logger.LogWarning(ex,
                    "A2A invocation attempt {Attempt}/{Attempts} failed for {AgentName} at {Endpoint}; retrying.",
                    attempt, attempts, request.AgentName, endpoint);
                await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or A2AException)
            {
                _logger.LogWarning(ex, "A2A invocation failed for {AgentName} at {Endpoint}.", request.AgentName, endpoint);
                return Failure(request, stopwatch.Elapsed, "a2a.transport.failed",
                    $"A2A invocation of '{request.AgentName}' failed: {ex.Message}");
            }
        }
    }

    /// <inheritdoc />
    public Task<TransportHealth> CheckHealthAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new TransportHealth(
            IsHealthy: true,
            TransportName: "a2a",
            Message: "A2A transport ready; per-endpoint health via agent-card fetch.",
            TransportType: "a2a"));

    /// <summary>
    /// Fetches the remote agent card for an <c>a2a+</c> endpoint — the A2A liveness probe used
    /// by endpoint health monitoring and diagnostics.
    /// </summary>
    public async Task<TransportHealth> CheckEndpointAsync(string endpoint, CancellationToken cancellationToken = default)
    {
        if (!A2AEndpointScheme.TryGetHttpBaseUrl(endpoint, out var baseUrl))
        {
            return new TransportHealth(false, "a2a", $"'{endpoint}' is not a valid a2a+http(s) endpoint.", "a2a");
        }

        try
        {
            var resolver = new A2ACardResolver(baseUrl, GetHttpClient(baseUrl));
            var card = await resolver.GetAgentCardAsync(cancellationToken).ConfigureAwait(false);
            return new TransportHealth(true, "a2a", $"Agent card '{card.Name}' reachable.", "a2a");
        }
        catch (Exception ex) when (EgressRefusal.Find(ex) is { } refusal)
        {
            return new TransportHealth(false, "a2a", EgressRefusal.RemoteMessage(refusal.Ref), "a2a") { Refusal = refusal };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new TransportHealth(false, "a2a", $"Agent card fetch failed: {ex.Message}", "a2a", ex.ToString());
        }
    }

    private HttpClient GetHttpClient(Uri baseUrl)
    {
        if (HttpClientFactoryOverride is not null)
        {
            return HttpClientFactoryOverride(baseUrl);
        }

        // One HttpClient per authority, configured once with any matching API key header.
        // Secrets come from the environment at client-creation time, never from bound config.
        return _httpClients.GetOrAdd(baseUrl.GetLeftPart(UriPartial.Authority), _ =>
        {
            var client = EgressHttp.CreateClient(EgressFamilies.A2A, "EG-XPT-01");
            var match = _options.Value.Endpoints.FirstOrDefault(e =>
                !string.IsNullOrWhiteSpace(e.UrlPrefix) &&
                baseUrl.AbsoluteUri.StartsWith(e.UrlPrefix, StringComparison.OrdinalIgnoreCase));
            if (match?.ApiKeyHeader is { Length: > 0 } header && match.ApiKeyEnvVar is { Length: > 0 } envVar)
            {
                var secret = Environment.GetEnvironmentVariable(envVar);
                if (!string.IsNullOrEmpty(secret))
                {
                    client.DefaultRequestHeaders.Add(header, secret);
                }
                else
                {
                    _logger.LogError(
                        "A2A endpoint '{Prefix}' references env var '{EnvVar}' which is unset; requests will go out unauthenticated.",
                        match.UrlPrefix, envVar);
                }
            }

            return client;
        });
    }

    private static AgentResult Failure(AgentInvocationRequest request, TimeSpan elapsed, string errorCode, string message)
        => new(
            Success: false,
            ErrorMessage: message,
            Duration: elapsed,
            ErrorCode: errorCode,
            CorrelationId: request.CorrelationId,
            SpanId: request.SpanId);
}
