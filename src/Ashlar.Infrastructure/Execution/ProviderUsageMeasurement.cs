using System.Diagnostics;
using System.Text.Json;
using Ashlar.Core.Application.Execution;

namespace Ashlar.Infrastructure.Execution;

internal sealed class ProviderUsageRequest
{
    private readonly Guid _id = Guid.NewGuid();
    private readonly ProviderUsageScope? _scope = ProviderUsageScope.Capture();
    private int _attempt;

    internal ProviderUsageAttempt Begin(string provider, string model, CancellationToken ct) =>
        new(_scope, _id, Interlocked.Increment(ref _attempt), provider, model, ct);
}

internal sealed class ProviderUsageAttempt : IDisposable
{
    private readonly ProviderUsageScope? _scope;
    private readonly ProviderUsageRecord _record;
    private readonly CancellationToken _ct;
    private readonly Stopwatch _timer = Stopwatch.StartNew();
    private bool _completed;
    private bool _succeeded;
    internal int? HttpStatusCode { get; set; }
    internal ProviderTokenUsage Usage { get; private set; } = new();

    internal ProviderUsageAttempt(ProviderUsageScope? scope, Guid id, int attempt, string provider, string model, CancellationToken ct)
    {
        _scope = scope;
        _ct = ct;
        _record = new ProviderUsageRecord
        {
            RequestId = id, Attempt = attempt, Provider = provider, Model = model, StartedAt = DateTimeOffset.UtcNow
        };
    }

    internal void Observe(string body, bool ollama = false) => Usage = ProviderUsageParser.Parse(body, ollama);
    internal void Succeeded() => _succeeded = true;

    internal async Task<string> ReadBodyAsync(HttpResponseMessage response, CancellationToken ct, bool ollama = false)
    {
        HttpStatusCode = (int)response.StatusCode;
        // These send sites use ResponseContentRead: the response is already buffered. Retain known
        // usage even when cancellation arrives after HTTP completion, then honor the caller's token.
        var body = await response.Content.ReadAsStringAsync(CancellationToken.None).ConfigureAwait(false);
        Observe(body, ollama);
        ct.ThrowIfCancellationRequested();
        return body;
    }

    public void Dispose()
    {
        if (_completed) return;
        _completed = true;
        _scope?.Record(_record with
        {
            Duration = _timer.Elapsed,
            HttpStatusCode = HttpStatusCode,
            Outcome = _succeeded ? ProviderAttemptOutcome.Succeeded : _ct.IsCancellationRequested ? ProviderAttemptOutcome.Cancelled : ProviderAttemptOutcome.Failed,
            Usage = Usage
        });
    }
}

internal static class ProviderUsageParser
{
    internal static ProviderTokenUsage Parse(string body, bool ollama)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return Parse(doc.RootElement, ollama);
        }
        catch (JsonException)
        {
            return new ProviderTokenUsage { HasInvalidCounts = true };
        }
    }

    private static ProviderTokenUsage Parse(JsonElement root, bool ollama)
    {
        if (root.ValueKind != JsonValueKind.Object) return new ProviderTokenUsage { HasInvalidCounts = true };
        var invalid = false;
        long? input, output, cached = null, reasoning = null;
        if (ollama)
        {
            input = Count(root, "prompt_eval_count", ref invalid);
            output = Count(root, "eval_count", ref invalid);
        }
        else
        {
            if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind == JsonValueKind.Null) return new();
            if (usage.ValueKind != JsonValueKind.Object) return new ProviderTokenUsage { HasInvalidCounts = true };
            input = Count(usage, "prompt_tokens", ref invalid);
            output = Count(usage, "completion_tokens", ref invalid);
            cached = DetailCount(usage, "prompt_tokens_details", "cached_tokens", ref invalid);
            reasoning = DetailCount(usage, "completion_tokens_details", "reasoning_tokens", ref invalid);
        }

        // Provider detail counts describe subsets. A contradictory subset cannot be used for accounting.
        if (cached.HasValue && input.HasValue && cached > input) { cached = null; invalid = true; }
        if (reasoning.HasValue && output.HasValue && reasoning > output) { reasoning = null; invalid = true; }
        return new ProviderTokenUsage
        {
            InputTokenCount = input, OutputTokenCount = output, CachedInputTokenCount = cached, ReasoningTokenCount = reasoning,
            Source = input.HasValue || output.HasValue || cached.HasValue || reasoning.HasValue ? ProviderUsageSource.ProviderReported : ProviderUsageSource.Unknown,
            HasInvalidCounts = invalid
        };
    }

    private static long? DetailCount(JsonElement usage, string name, string countName, ref bool invalid)
    {
        if (!usage.TryGetProperty(name, out var details) || details.ValueKind == JsonValueKind.Null) return null;
        if (details.ValueKind != JsonValueKind.Object) { invalid = true; return null; }
        return Count(details, countName, ref invalid);
    }

    private static long? Count(JsonElement usage, string name, ref bool invalid)
    {
        if (!usage.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var count) && count >= 0) return count;
        invalid = true;
        return null;
    }
}

internal sealed class ProviderUsageHttpResponse : IDisposable
{
    internal HttpResponseMessage Response { get; }
    internal ProviderUsageAttempt Attempt { get; }

    internal ProviderUsageHttpResponse(HttpResponseMessage response, ProviderUsageAttempt attempt)
    {
        Response = response;
        Attempt = attempt;
    }

    internal Task<string> ReadBodyAsync(CancellationToken ct) => Attempt.ReadBodyAsync(Response, ct);

    public void Dispose()
    {
        Attempt.Dispose();
        Response.Dispose();
    }
}
