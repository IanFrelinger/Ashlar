using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Ashlar.Core.Application.Execution;
using Ashlar.Infrastructure.Execution;
using Ashlar.Core.Application.Common.Ports;
using Ashlar.Core.Application.NodeCapabilityRuntime.Models;
using Ashlar.Core.Application.NodeCapabilityRuntime.Ports;

namespace Ashlar.Infrastructure.NodeCapabilityRuntime.Backends;

/// <summary>
/// Ollama-backed implementation of NCR model serving backend.
/// </summary>
public sealed class OllamaModelServingBackend : IModelServingBackend
{
    private readonly HttpClient _httpClient;
    private readonly IMetricsCollector? _metricsCollector;

    /// <summary>Initializes a new ollama model serving backend.</summary>
    public OllamaModelServingBackend(
        HttpClient httpClient,
        IOptions<OllamaBackendOptions> options,
        IMetricsCollector? metricsCollector = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _metricsCollector = metricsCollector;
        var opts = options?.Value ?? throw new ArgumentNullException(nameof(options));
        if (_httpClient.BaseAddress is null)
        {
            _httpClient.BaseAddress = new Uri(opts.BaseUrl.TrimEnd('/') + "/", UriKind.Absolute);
        }
    }

    /// <summary>Backend type.</summary>
    public BackendType BackendType => BackendType.Ollama;

    /// <summary>Is available asynchronously.</summary>
    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        var started = DateTimeOffset.UtcNow;
        try
        {
            using var response = await _httpClient.GetAsync("/api/tags", ct).ConfigureAwait(false);
            _metricsCollector?.RecordExecutionTime("ncr.ollama.tags.duration", DateTimeOffset.UtcNow - started);
            _metricsCollector?.IncrementCounter(response.IsSuccessStatusCode ? "ncr.ollama.tags.success" : "ncr.ollama.tags.failure");
            return response.IsSuccessStatusCode;
        }
        catch
        {
            _metricsCollector?.RecordExecutionTime("ncr.ollama.tags.duration", DateTimeOffset.UtcNow - started);
            _metricsCollector?.IncrementCounter("ncr.ollama.tags.error");
            throw;
        }
    }

    /// <summary>Run inference asynchronously.</summary>
    public async Task<InferenceResult> RunInferenceAsync(InferenceRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var modelId = string.IsNullOrWhiteSpace(request.ModelId) ? throw new ArgumentException("ModelId is required", nameof(request)) : request.ModelId;
        var prompt = request.Prompt ?? string.Empty;
        var system = request.SystemPrompt ?? string.Empty;
        var parameters = request.Parameters ?? new Dictionary<string, object>();
        var started = DateTimeOffset.UtcNow;

        try
        {
            var payload = BuildChatPayload(modelId, system, prompt, parameters);
            _metricsCollector?.IncrementCounter("ncr.ollama.chat.request");
            using var attempt = new ProviderUsageRequest().Begin("ollama", modelId, ct);
            using var response = await PostJsonAsync("/api/chat", payload, ct).ConfigureAwait(false);
            var body = await attempt.ReadBodyAsync(response, ct, ollama: true).ConfigureAwait(false);
            _metricsCollector?.RecordExecutionTime("ncr.ollama.chat.duration", DateTimeOffset.UtcNow - started);
            _metricsCollector?.IncrementCounter(response.IsSuccessStatusCode ? "ncr.ollama.chat.success" : "ncr.ollama.chat.failure");
            response.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(body);
            var content = doc.RootElement
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? string.Empty;

            // Keep the legacy int field usable; exact long counts remain in Usage even if they exceed int.
            var reportedOutput = attempt.Usage.OutputTokenCount;
            var legacyReported = reportedOutput is >= 0 and <= int.MaxValue;
            attempt.Succeeded();
            return new InferenceResult
            {
                Output = content,
                TokenCount = legacyReported ? (int)reportedOutput!.Value : ApproximateTokenCount(content),
                TokenCountSource = legacyReported ? ProviderUsageSource.ProviderReported : ProviderUsageSource.Estimated,
                Usage = attempt.Usage,
                Duration = DateTimeOffset.UtcNow - started
            };
        }
        catch
        {
            _metricsCollector?.RecordExecutionTime("ncr.ollama.chat.duration", DateTimeOffset.UtcNow - started);
            _metricsCollector?.IncrementCounter("ncr.ollama.chat.error");
            throw;
        }
    }

    /// <summary>Load model asynchronously.</summary>
    public async Task LoadModelAsync(string modelId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(modelId)) throw new ArgumentException("Model id is required", nameof(modelId));
        var started = DateTimeOffset.UtcNow;
        try
        {
            var payload = BuildGeneratePayload(modelId, "30m");
            _metricsCollector?.IncrementCounter("ncr.ollama.generate.request");
            using var response = await PostJsonAsync("/api/generate", payload, ct).ConfigureAwait(false);
            _metricsCollector?.RecordExecutionTime("ncr.ollama.generate.duration", DateTimeOffset.UtcNow - started);
            _metricsCollector?.IncrementCounter(response.IsSuccessStatusCode ? "ncr.ollama.generate.success" : "ncr.ollama.generate.failure");
            response.EnsureSuccessStatusCode();
        }
        catch
        {
            _metricsCollector?.RecordExecutionTime("ncr.ollama.generate.duration", DateTimeOffset.UtcNow - started);
            _metricsCollector?.IncrementCounter("ncr.ollama.generate.error");
            throw;
        }
    }

    /// <summary>Unload model asynchronously.</summary>
    public async Task UnloadModelAsync(string modelId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(modelId)) throw new ArgumentException("Model id is required", nameof(modelId));
        var started = DateTimeOffset.UtcNow;
        try
        {
            var payload = BuildGeneratePayload(modelId, "0s");
            _metricsCollector?.IncrementCounter("ncr.ollama.generate.request");
            using var response = await PostJsonAsync("/api/generate", payload, ct).ConfigureAwait(false);
            _metricsCollector?.RecordExecutionTime("ncr.ollama.generate.duration", DateTimeOffset.UtcNow - started);
            _metricsCollector?.IncrementCounter(response.IsSuccessStatusCode ? "ncr.ollama.generate.success" : "ncr.ollama.generate.failure");
            response.EnsureSuccessStatusCode();
        }
        catch
        {
            _metricsCollector?.RecordExecutionTime("ncr.ollama.generate.duration", DateTimeOffset.UtcNow - started);
            _metricsCollector?.IncrementCounter("ncr.ollama.generate.error");
            throw;
        }
    }

    /// <summary>Pull model asynchronously.</summary>
    public async Task PullModelAsync(string modelId, IProgress<PullProgress>? progress = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(modelId)) throw new ArgumentException("Model id is required", nameof(modelId));
        var started = DateTimeOffset.UtcNow;
        try
        {
            var payload = BuildPullPayload(modelId);
            _metricsCollector?.IncrementCounter("ncr.ollama.pull.request");
            using var response = await PostJsonAsync("/api/pull", payload, ct).ConfigureAwait(false);
            _metricsCollector?.RecordExecutionTime("ncr.ollama.pull.duration", DateTimeOffset.UtcNow - started);
            _metricsCollector?.IncrementCounter(response.IsSuccessStatusCode ? "ncr.ollama.pull.success" : "ncr.ollama.pull.failure");
            response.EnsureSuccessStatusCode();
            progress?.Report(new PullProgress { ModelId = modelId, BytesDownloaded = 1, TotalBytes = 1 });
        }
        catch
        {
            _metricsCollector?.RecordExecutionTime("ncr.ollama.pull.duration", DateTimeOffset.UtcNow - started);
            _metricsCollector?.IncrementCounter("ncr.ollama.pull.error");
            throw;
        }
    }

    /// <summary>List loaded models asynchronously.</summary>
    public async Task<IReadOnlyList<string>> ListLoadedModelsAsync(CancellationToken ct = default)
    {
        var started = DateTimeOffset.UtcNow;
        try
        {
            using var response = await _httpClient.GetAsync("/api/ps", ct).ConfigureAwait(false);
            _metricsCollector?.RecordExecutionTime("ncr.ollama.ps.duration", DateTimeOffset.UtcNow - started);
            _metricsCollector?.IncrementCounter(response.IsSuccessStatusCode ? "ncr.ollama.ps.success" : "ncr.ollama.ps.failure");
            if (!response.IsSuccessStatusCode)
            {
                return [];
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            if (!doc.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var names = new List<string>();
            foreach (var model in models.EnumerateArray())
            {
                if (model.TryGetProperty("name", out var name))
                {
                    var parsed = name.GetString();
                    if (!string.IsNullOrWhiteSpace(parsed))
                    {
                        names.Add(parsed);
                    }
                }
            }

            return names;
        }
        catch
        {
            _metricsCollector?.RecordExecutionTime("ncr.ollama.ps.duration", DateTimeOffset.UtcNow - started);
            _metricsCollector?.IncrementCounter("ncr.ollama.ps.error");
            throw;
        }
    }

    private static int ApproximateTokenCount(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        return Math.Max(1, text.Length / 4);
    }

    private async Task<HttpResponseMessage> PostJsonAsync(
        string requestUri,
        string payload,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
        {
            Content = new StringContent(payload, Encoding.UTF8)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
    }

    private static string BuildChatPayload(
        string modelId,
        string systemPrompt,
        string userPrompt,
        IReadOnlyDictionary<string, object> parameters)
    {
        var systemEscaped = EscapeJsonString(systemPrompt);
        var userEscaped = EscapeJsonString(userPrompt);
        var modelEscaped = EscapeJsonString(modelId);
        var optionsEntries = parameters
            .Select(kvp => $"\"{EscapeJsonString(kvp.Key)}\":{FormatJsonValue(kvp.Value)}");
        var options = "{"+ string.Join(",", optionsEntries) + "}";
        return
            $"{{\"model\":\"{modelEscaped}\",\"stream\":false," +
            "\"messages\":[" +
            $"{{\"role\":\"system\",\"content\":\"{systemEscaped}\"}}," +
            $"{{\"role\":\"user\",\"content\":\"{userEscaped}\"}}" +
            "]," +
            $"\"options\":{options}" +
            "}";
    }

    private static string BuildGeneratePayload(string modelId, string keepAlive)
    {
        return
            $"{{\"model\":\"{EscapeJsonString(modelId)}\",\"prompt\":\"\",\"stream\":false,\"keep_alive\":\"{EscapeJsonString(keepAlive)}\"}}";
    }

    private static string BuildPullPayload(string modelId)
    {
        return $"{{\"name\":\"{EscapeJsonString(modelId)}\",\"stream\":false}}";
    }

    private static string EscapeJsonString(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var sb = new StringBuilder(value.Length + 8);
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '\"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (char.IsControl(ch))
                    {
                        sb.Append("\\u");
                        sb.Append(((int)ch).ToString("x4"));
                    }
                    else
                    {
                        sb.Append(ch);
                    }
                    break;
            }
        }
        return sb.ToString();
    }

    private static string FormatJsonValue(object value)
    {
        return value switch
        {
            null => "null",
            bool b => b ? "true" : "false",
            byte or sbyte or short or ushort or int or uint or long or ulong =>
                Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "0",
            float f => f.ToString(System.Globalization.CultureInfo.InvariantCulture),
            double d => d.ToString(System.Globalization.CultureInfo.InvariantCulture),
            decimal m => m.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => $"\"{EscapeJsonString(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty)}\""
        };
    }
}
