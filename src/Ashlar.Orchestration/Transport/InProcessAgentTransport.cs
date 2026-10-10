using Ashlar.Abstractions.Security.Egress;
using Microsoft.Extensions.Logging;
using Ashlar.Abstractions.Transport;
using Ashlar.Orchestration.Agents;

namespace Ashlar.Orchestration.Transport;

/// <summary>
/// Default in-process transport implementation.
/// Delegates invocations to the existing lifecycle manager to preserve behavior.
/// </summary>
public sealed class InProcessAgentTransport : IAgentTransport
{
    private readonly LifecycleManager _lifecycleManager;
    private readonly ILogger<InProcessAgentTransport> _logger;

    public InProcessAgentTransport(
        LifecycleManager lifecycleManager,
        ILogger<InProcessAgentTransport> logger)
    {
        _lifecycleManager = lifecycleManager ?? throw new ArgumentNullException(nameof(lifecycleManager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<AgentResult> SendAsync(
        AgentInvocationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var startedAt = DateTimeOffset.UtcNow;

        try
        {
            var dependencyPayload = request.DependencyOutputs
                ?? request.Payload?.ToDictionary(
                    kvp => kvp.Key,
                    kvp => (object)kvp.Value!,
                    StringComparer.OrdinalIgnoreCase);

            var output = await _lifecycleManager.ExecuteAgentAsync(
                request.AgentName,
                dependencyPayload,
                cancellationToken);

            return new AgentResult(
                Success: true,
                Output: output,
                Duration: DateTimeOffset.UtcNow - startedAt,
                CorrelationId: request.CorrelationId,
                SpanId: request.SpanId);
        }
        catch (Exception ex) when (EgressRefusal.Find(ex) is { } refusal)
        {
            return new AgentResult(
                Success: false,
                ErrorMessage: refusal.Message,
                Duration: DateTimeOffset.UtcNow - startedAt,
                Metadata: EgressRefusal.Metadata(refusal),
                ErrorCode: EgressRefusal.Code,
                CorrelationId: request.CorrelationId,
                SpanId: request.SpanId);
        }
        catch (InvalidOperationException ex) when (
            ex.Message.Contains("not registered", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                ex,
                "In-process transport could not resolve agent {AgentName} (CorrelationId: {CorrelationId})",
                request.AgentName,
                request.CorrelationId);

            return new AgentResult(
                Success: false,
                ErrorMessage: ex.Message,
                Duration: DateTimeOffset.UtcNow - startedAt,
                Metadata: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["errorCode"] = "AGENT_NOT_FOUND"
                },
                ErrorCode: "AGENT_NOT_FOUND",
                CorrelationId: request.CorrelationId,
                SpanId: request.SpanId);
        }
        catch (TimeoutException ex)
        {
            _logger.LogWarning(
                ex,
                "In-process transport timed out for agent {AgentName} (CorrelationId: {CorrelationId})",
                request.AgentName,
                request.CorrelationId);

            return new AgentResult(
                Success: false,
                ErrorMessage: ex.Message,
                Duration: DateTimeOffset.UtcNow - startedAt,
                Metadata: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["errorCode"] = "TIMEOUT"
                },
                ErrorCode: "TIMEOUT",
                CorrelationId: request.CorrelationId,
                SpanId: request.SpanId);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "In-process transport failed executing agent {AgentName} (CorrelationId: {CorrelationId})",
                request.AgentName,
                request.CorrelationId);

            return new AgentResult(
                Success: false,
                ErrorMessage: ex.Message,
                Duration: DateTimeOffset.UtcNow - startedAt,
                Metadata: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["errorCode"] = "TRANSPORT_ERROR"
                },
                ErrorCode: "TRANSPORT_ERROR",
                CorrelationId: request.CorrelationId,
                SpanId: request.SpanId);
        }
    }

    public Task<TransportHealth> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new TransportHealth(
            IsHealthy: true,
            TransportName: nameof(InProcessAgentTransport),
            Message: "In-process transport is available"));
    }
}
