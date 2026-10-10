using Ashlar.Abstractions.Security.Egress;
using System.Text.Json;
using A2A;
using Ashlar.Abstractions.Barriers;
using Ashlar.Abstractions.Transport;

namespace Ashlar.Transport.A2A;

/// <summary>
/// Pure mapping between Ashlar transport envelopes and A2A protocol messages.
/// Static and side-effect free for direct unit testing (internal + InternalsVisibleTo).
/// </summary>
internal static class A2AInvocationMapper
{
    /// <summary>Metadata key carrying the Ashlar correlation id across the A2A boundary.</summary>
    public const string CorrelationIdKey = "ashlar.correlationId";

    /// <summary>Metadata key carrying the Ashlar span id.</summary>
    public const string SpanIdKey = "ashlar.spanId";

    /// <summary>Metadata key carrying the propagated barrier level.</summary>
    public const string BarrierLevelKey = "ashlar.barrier";

    /// <summary>Metadata key carrying the barrier authority source.</summary>
    public const string BarrierSourceKey = "ashlar.barrierSource";

    /// <summary>
    /// Builds the outbound A2A message: the invocation payload travels as a single structured
    /// data part; correlation/span/barrier context travels as protocol-level message metadata
    /// (the moral equivalent of the gRPC transport's x-ashlar-* headers, but visible to any
    /// A2A implementation rather than only to HTTP middlemen).
    /// </summary>
    public static Message BuildMessage(AgentInvocationRequest request, BarrierContext? barrier)
    {
        var payload = request.Payload
            ?? request.DependencyOutputs?.ToDictionary(kvp => kvp.Key, kvp => (object?)kvp.Value, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        var parts = new List<Part> { Part.FromData(JsonSerializer.SerializeToElement(payload)) };
        if (payload.TryGetValue("message", out var text) && text is string textMessage && !string.IsNullOrWhiteSpace(textMessage))
        {
            // Text-first agents on the remote side read UserText; keep it populated when the
            // payload carries a natural-language objective.
            parts.Insert(0, Part.FromText(textMessage));
        }

        var metadata = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            [CorrelationIdKey] = JsonSerializer.SerializeToElement(request.CorrelationId ?? string.Empty),
        };
        if (!string.IsNullOrWhiteSpace(request.SpanId))
        {
            metadata[SpanIdKey] = JsonSerializer.SerializeToElement(request.SpanId);
        }

        if (barrier is not null)
        {
            metadata[BarrierLevelKey] = JsonSerializer.SerializeToElement(barrier.Level);
            metadata[BarrierSourceKey] = JsonSerializer.SerializeToElement(barrier.AuthoritySource);
        }

        if (request.Metadata is { Count: > 0 })
        {
            foreach (var (key, value) in request.Metadata)
            {
                if (!string.IsNullOrWhiteSpace(key) && !metadata.ContainsKey(key))
                {
                    metadata[key] = JsonSerializer.SerializeToElement(value ?? string.Empty);
                }
            }
        }

        return new Message
        {
            Role = Role.User,
            MessageId = Guid.NewGuid().ToString("N"),
            Parts = parts,
            Metadata = metadata,
        };
    }

    /// <summary>Maps the terminal A2A response back onto the Ashlar <see cref="AgentResult"/>.</summary>
    public static AgentResult MapResponse(SendMessageResponse response, AgentInvocationRequest request, TimeSpan elapsed)
    {
        if (response.PayloadCase == SendMessageResponseCase.Message && response.Message is not null)
        {
            return new AgentResult(
                Success: true,
                Output: ExtractContent(response.Message.Parts),
                Duration: elapsed,
                CorrelationId: request.CorrelationId,
                SpanId: request.SpanId);
        }

        var task = response.Task;
        if (task is null)
        {
            return Failure(request, elapsed, "a2a.response.empty", "The A2A server returned neither a task nor a message.");
        }

        var state = task.Status?.State ?? TaskState.Unspecified;
        var statusText = task.Status?.Message is { } statusMessage ? ExtractText(statusMessage.Parts) : null;

        if (state == TaskState.Failed && task.Status?.Message?.Metadata is { } metadata
            && metadata.TryGetValue("errorCode", out var code) && code.ValueKind == JsonValueKind.String
            && EgressRefusal.IsCode(code.GetString()))
        {
            var reference = EgressRefusal.SafeReference(metadata.TryGetValue(EgressRefusal.ReferenceKey, out var value)
                && value.ValueKind == JsonValueKind.String ? value.GetString() : null);
            return Failure(request, elapsed, "a2a.egress_refused", EgressRefusal.RemoteMessage(reference)) with
            {
                Metadata = EgressRefusal.Metadata(reference)
            };
        }

        return state switch
        {
            TaskState.Completed => new AgentResult(
                Success: true,
                Output: ExtractTaskOutput(task) ?? statusText,
                Duration: elapsed,
                CorrelationId: request.CorrelationId,
                SpanId: request.SpanId),
            TaskState.Failed => Failure(request, elapsed, "a2a.task.failed", statusText ?? "Remote A2A task failed."),
            TaskState.Canceled => Failure(request, elapsed, "a2a.task.canceled", statusText ?? "Remote A2A task was canceled."),
            TaskState.Rejected => Failure(request, elapsed, "a2a.task.rejected", statusText ?? "Remote A2A task was rejected."),
            TaskState.InputRequired => Failure(request, elapsed, "a2a.task.input-required",
                statusText ?? "Remote A2A task requires additional input, which the synchronous transport cannot supply."),
            TaskState.AuthRequired => Failure(request, elapsed, "a2a.task.auth-required",
                statusText ?? "Remote A2A task requires additional authentication."),
            _ => Failure(request, elapsed, "a2a.task.incomplete",
                $"Remote A2A task ended in non-terminal state '{state}'."),
        };
    }

    private static AgentResult Failure(AgentInvocationRequest request, TimeSpan elapsed, string errorCode, string message)
        => new(
            Success: false,
            ErrorMessage: message,
            Duration: elapsed,
            ErrorCode: errorCode,
            CorrelationId: request.CorrelationId,
            SpanId: request.SpanId);

    /// <summary>Artifacts first (structured results), then the status/message content.</summary>
    private static object? ExtractTaskOutput(AgentTask task)
    {
        if (task.Artifacts is { Count: > 0 })
        {
            foreach (var artifact in task.Artifacts)
            {
                var content = ExtractContent(artifact.Parts);
                if (content is not null)
                {
                    return content;
                }
            }
        }

        return null;
    }

    private static object? ExtractContent(IEnumerable<Part>? parts)
    {
        if (parts is null)
        {
            return null;
        }

        var partList = parts.ToList();
        foreach (var part in partList)
        {
            if (part.ContentCase == PartContentCase.Data && part.Data is { } data)
            {
                return data;
            }
        }

        var text = ExtractText(partList);
        return string.IsNullOrEmpty(text) ? null : text;
    }

    private static string? ExtractText(IEnumerable<Part>? parts)
    {
        if (parts is null)
        {
            return null;
        }

        var texts = parts
            .Where(p => p.ContentCase == PartContentCase.Text && !string.IsNullOrEmpty(p.Text))
            .Select(p => p.Text!)
            .ToList();
        return texts.Count == 0 ? null : string.Join("\n", texts);
    }
}
