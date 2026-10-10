using Ashlar.Abstractions.Security.Egress;
using System.Text.Json;
using A2A;
using Microsoft.Extensions.Logging;
using Ashlar.Abstractions.Transport;

namespace Ashlar.Transport.A2A.Server;

/// <summary>
/// A2A agent handler for one exposed Ashlar agent: maps <c>message/send</c> onto
/// <see cref="IAgentTransport.SendAsync"/> and reports a terminal task within the request.
///
/// Deliberately mirrors the gRPC server facade rather than the MediatR <c>RunAgentCommand</c>
/// path: <c>AgentExecutorAdapter</c> does AppDomain reflection scans with caller-supplied names
/// under an allow-all policy and derives input paths from the process working directory — none
/// of which belongs behind an internet-facing endpoint. Here the agent identity is fixed at
/// construction from the allowlisted descriptor, execution goes through the same transport
/// pipeline as every other cross-boundary invocation, and the per-invocation budget is bounded.
/// </summary>
public sealed class AshlarA2AAgentHandler : IAgentHandler
{
    private readonly AshlarA2AAgentDescriptor _descriptor;
    private readonly IAgentTransport _transport;
    private readonly TimeSpan _defaultTimeout;
    private readonly ILogger<AshlarA2AAgentHandler> _logger;

    /// <summary>Creates a handler bound to one exposed agent.</summary>
    public AshlarA2AAgentHandler(
        AshlarA2AAgentDescriptor descriptor,
        IAgentTransport transport,
        TimeSpan defaultTimeout,
        ILogger<AshlarA2AAgentHandler> logger)
    {
        _descriptor = descriptor;
        _transport = transport;
        _defaultTimeout = defaultTimeout;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(RequestContext context, AgentEventQueue eventQueue, CancellationToken cancellationToken)
    {
        var updater = new TaskUpdater(eventQueue, context.TaskId, context.ContextId);
        await updater.SubmitAsync(cancellationToken).ConfigureAwait(false);
        await updater.StartWorkAsync(null, cancellationToken).ConfigureAwait(false);

        // Correlation context may arrive as message metadata or request metadata depending on
        // the caller's A2A implementation; check both before minting a fresh id.
        var correlationId = ReadMetadataString(context.Message?.Metadata, "ashlar.correlationId")
            ?? ReadMetadataString(context.Metadata, "ashlar.correlationId")
            ?? Guid.NewGuid().ToString("N");
        var spanId = ReadMetadataString(context.Message?.Metadata, "ashlar.spanId")
            ?? ReadMetadataString(context.Metadata, "ashlar.spanId");
        var timeout = _descriptor.MaxExecutionTime is { } max && max > TimeSpan.Zero ? max : _defaultTimeout;

        try
        {
            var request = new AgentInvocationRequest(
                AgentName: _descriptor.Id,
                CorrelationId: correlationId,
                SpanId: spanId,
                Payload: BuildPayload(context),
                // TargetEndpoint null ⇒ the routing transport executes in-process on this node;
                // MaxRetries 0 ⇒ the remote caller owns retry policy, not this facade.
                Options: new AgentInvocationOptions(timeout, MaxRetries: 0, TargetEndpoint: null));

            AgentResult result;
            using (var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                budget.CancelAfter(timeout);
                result = await _transport.SendAsync(request, budget.Token).ConfigureAwait(false);
            }

            if (EgressRefusal.IsRefused(result))
            {
                await updater.FailAsync(RefusalMessage(EgressRefusal.Reference(result)), cancellationToken).ConfigureAwait(false);
            }
            else if (result.Success)
            {
                await updater.AddArtifactAsync(
                    BuildResultParts(result.Output),
                    name: "result",
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                await updater.CompleteAsync(null, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                _logger.LogWarning(
                    "A2A-invoked agent failed. Agent={AgentId} Correlation={CorrelationId} ErrorCode={ErrorCode}",
                    _descriptor.Id, correlationId, result.ErrorCode);
                await updater.FailAsync(
                    AgentText(result.ErrorMessage ?? "Agent invocation failed."),
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (EgressRefusal.Find(ex) is { } refusal)
        {
            await updater.FailAsync(RefusalMessage(refusal.Ref), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            await updater.FailAsync(
                AgentText($"Agent execution exceeded its {timeout} budget."),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Internals stay in the host log; the remote caller gets a generic failure.
            _logger.LogError(ex,
                "A2A-invoked agent crashed. Agent={AgentId} Correlation={CorrelationId}",
                _descriptor.Id, correlationId);
            await updater.FailAsync(
                AgentText("Agent execution failed with an internal error."),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static Dictionary<string, object?> BuildPayload(RequestContext context)
    {
        var payload = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        // Structured data parts merge into the payload dictionary (the shape Ashlar agents read);
        // free text lands under "message" so text-first agents keep working.
        if (context.Message?.Parts is { } parts)
        {
            foreach (var part in parts)
            {
                if (part.ContentCase == PartContentCase.Data &&
                    part.Data is { ValueKind: JsonValueKind.Object } data)
                {
                    foreach (var property in data.EnumerateObject())
                    {
                        payload[property.Name] = property.Value.Clone();
                    }
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(context.UserText))
        {
            payload["message"] = context.UserText;
        }

        return payload;
    }

    private static List<Part> BuildResultParts(object? output) => output switch
    {
        null => [Part.FromText("(no output)")],
        string text => [Part.FromText(text)],
        JsonElement element => [Part.FromData(element)],
        _ => [Part.FromData(JsonSerializer.SerializeToElement(output))],
    };

    private static Message RefusalMessage(string reference)
    {
        var message = AgentText(EgressRefusal.RemoteMessage(reference));
        message.Metadata = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["errorCode"] = JsonSerializer.SerializeToElement(EgressRefusal.Code),
            [EgressRefusal.ReferenceKey] = JsonSerializer.SerializeToElement(reference)
        };
        return message;
    }

    private static Message AgentText(string text) => new()
    {
        Role = Role.Agent,
        MessageId = Guid.NewGuid().ToString("N"),
        Parts = [Part.FromText(text)],
    };

    private static string? ReadMetadataString(Dictionary<string, JsonElement>? metadata, string key)
        => metadata is not null &&
           metadata.TryGetValue(key, out var value) &&
           value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
