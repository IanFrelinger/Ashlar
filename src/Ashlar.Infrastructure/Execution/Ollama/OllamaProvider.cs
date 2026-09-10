using System.Linq;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Ashlar.Infrastructure.Execution.Ollama;

/// <summary>Ollama provider.</summary>
public sealed class OllamaProvider
{
    private readonly HttpClient _httpClient;
    private readonly ILogger? _logger;
    private readonly object _manifestLock = new();

    private Dictionary<string, OllamaModelManifest> _manifestByName = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Initializes a new ollama provider.</summary>
    public OllamaProvider(
        HttpClient httpClient,
        string? baseUrl = null,
        ILogger? logger = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger;

        var resolvedBaseUrl = string.IsNullOrWhiteSpace(baseUrl)
            ? "http://localhost:11434"
            : baseUrl;

        if (_httpClient.BaseAddress is null)
        {
            _httpClient.BaseAddress = new Uri(resolvedBaseUrl.TrimEnd('/') + "/", UriKind.Absolute);
        }
    }

    /// <summary>
    /// Loads the model manifest. The constructor performs no I/O; call this once after
    /// construction, or let the first ValidateModel/CheckHealth path refresh lazily.
    /// </summary>
    /// <param name="cancellationToken">Bounds the <c>/api/tags</c> request; cancellation yields a failed result (<c>OLLAMA_TAGS_CANCELLED</c>), never an exception.</param>
    /// <returns>The manifest on success, or the structured failure from <see cref="RefreshModelsAsync"/>.</returns>
    public Task<Result<IReadOnlyList<OllamaModelManifest>>> InitializeAsync(CancellationToken cancellationToken) =>
        RefreshModelsAsync(cancellationToken);

    /// <summary>Whether the Ollama endpoint is reachable and has a loaded model manifest.</summary>
    public bool IsAvailable { get; private set; }

    /// <summary>UTC timestamp of the last successful model manifest refresh.</summary>
    public DateTimeOffset? LastRefreshUtc { get; private set; }

    /// <summary>Cached Ollama model manifests from the last refresh.</summary>
    public IReadOnlyList<OllamaModelManifest> Manifest
    {
        get
        {
            lock (_manifestLock)
            {
                return _manifestByName.Values
                    .OrderBy(model => model.Name, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
        }
    }

    /// <summary>Check health asynchronously.</summary>
    public async Task<Result<bool>> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        var refreshResult = await RefreshModelsAsync(cancellationToken).ConfigureAwait(false);
        if (refreshResult.IsSuccess)
        {
            return Result<bool>.Success(true);
        }

        return Result<bool>.Failure(refreshResult.Error ?? new Error(
            "OLLAMA_HEALTH_UNKNOWN",
            "Ollama health check failed for an unknown reason."));
    }

    /// <summary>Refresh models asynchronously.</summary>
    public async Task<Result<IReadOnlyList<OllamaModelManifest>>> RefreshModelsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _httpClient.GetAsync("api/tags", cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                IsAvailable = false;
                return Result<IReadOnlyList<OllamaModelManifest>>.Failure(new Error(
                    "OLLAMA_TAGS_HTTP_ERROR",
                    $"Ollama /api/tags returned {(int)response.StatusCode} ({response.StatusCode}).",
                    new Dictionary<string, string>
                    {
                        ["statusCode"] = ((int)response.StatusCode).ToString(),
                        ["reasonPhrase"] = response.ReasonPhrase ?? string.Empty
                    }));
            }

            await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var jsonDocument = await JsonDocument.ParseAsync(contentStream, cancellationToken: cancellationToken).ConfigureAwait(false);
            var manifest = ParseManifest(jsonDocument.RootElement);

            lock (_manifestLock)
            {
                _manifestByName = manifest.ToDictionary(model => model.Name, StringComparer.OrdinalIgnoreCase);
            }

            LastRefreshUtc = DateTimeOffset.UtcNow;
            IsAvailable = true;
            return Result<IReadOnlyList<OllamaModelManifest>>.Success(manifest);
        }
        catch (OperationCanceledException)
        {
            IsAvailable = false;
            return Result<IReadOnlyList<OllamaModelManifest>>.Failure(new Error(
                "OLLAMA_TAGS_CANCELLED",
                "Ollama /api/tags request was cancelled."));
        }
        catch (HttpRequestException ex)
        {
            IsAvailable = false;
            return Result<IReadOnlyList<OllamaModelManifest>>.Failure(new Error(
                "OLLAMA_UNREACHABLE",
                $"Unable to reach Ollama /api/tags endpoint at {_httpClient.BaseAddress}: {ex.Message}"));
        }
        catch (JsonException ex)
        {
            IsAvailable = false;
            return Result<IReadOnlyList<OllamaModelManifest>>.Failure(new Error(
                "OLLAMA_TAGS_INVALID_JSON",
                $"Failed to deserialize Ollama /api/tags response: {ex.Message}"));
        }
    }

    /// <summary>Validate model.</summary>
    public Result<OllamaModelManifest> ValidateModel(string requestedModel)
    {
        if (string.IsNullOrWhiteSpace(requestedModel))
        {
            return Result<OllamaModelManifest>.Failure(new Error(
                "OLLAMA_MODEL_REQUIRED",
                "A model name is required for Ollama inference."));
        }

        if (!IsAvailable)
        {
            return Result<OllamaModelManifest>.Failure(new Error(
                "OLLAMA_UNAVAILABLE",
                "Ollama provider is unavailable. Run health check or refresh the model manifest."));
        }

        lock (_manifestLock)
        {
            if (TryResolveManifestModel(requestedModel, out var model))
            {
                return Result<OllamaModelManifest>.Success(model);
            }

            var availableModels = string.Join(", ", _manifestByName.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
            return Result<OllamaModelManifest>.Failure(new Error(
                "OLLAMA_MODEL_NOT_FOUND",
                $"Requested Ollama model '{requestedModel}' was not found in the discovered model manifest.",
                new Dictionary<string, string>
                {
                    ["requestedModel"] = requestedModel,
                    ["availableModels"] = availableModels
                }));
        }
    }

    /// <summary>
    /// Resolves a requested tag to a manifest entry. Ollama lists models as <c>family:tag</c> (often <c>:latest</c>);
    /// configs often use the short form <c>llama3.1</c> which must still match <c>llama3.1:latest</c>.
    /// </summary>
    private bool TryResolveManifestModel(string requestedModel, out OllamaModelManifest model)
    {
        model = default!;
        var req = requestedModel.Trim();
        if (req.Length == 0 || _manifestByName.Count == 0)
            return false;

        if (_manifestByName.TryGetValue(req, out var exact))
        {
            model = exact;
            return true;
        }

        // Bare name → explicit :latest (common Ollama convention).
        if (req.IndexOf(':', StringComparison.Ordinal) < 0)
        {
            var latestKey = req + ":latest";
            if (_manifestByName.TryGetValue(latestKey, out var latest))
            {
                model = latest;
                return true;
            }
        }

        // Single family prefix (e.g. "llama3.2" → "llama3.2:3b" when it is the only tag).
        var prefix = req + ":";
        var candidates = _manifestByName.Keys
            .Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (candidates.Count == 0)
            return false;

        var preferred = candidates.FirstOrDefault(c => c.EndsWith(":latest", StringComparison.OrdinalIgnoreCase));
        var chosen = preferred ?? candidates[0];
        model = _manifestByName[chosen];
        return true;
    }

    /// <summary>Execute chat asynchronously.</summary>
    public async Task<Result<string>> ExecuteChatAsync(
        string requestedModel,
        string systemPrompt,
        string userPrompt,
        IReadOnlyList<byte[]>? imageBytesList,
        CancellationToken cancellationToken = default)
    {
        var validationResult = ValidateModel(requestedModel);
        if (!validationResult.IsSuccess || validationResult.Value is null)
        {
            return Result<string>.Failure(validationResult.Error ?? new Error(
                "OLLAMA_MODEL_VALIDATION_FAILED",
                "Model validation failed before Ollama execution."));
        }

        var json = BuildChatPayload(validationResult.Value.Name, systemPrompt, userPrompt, imageBytesList);
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/chat")
        {
            Content = new StringContent(json, Encoding.UTF8)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return Result<string>.Failure(new Error(
                    response.StatusCode == HttpStatusCode.NotFound ? "OLLAMA_CHAT_MODEL_NOT_FOUND" : "OLLAMA_CHAT_HTTP_ERROR",
                    $"Ollama /api/chat returned {(int)response.StatusCode} ({response.StatusCode})."));
            }

            await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var jsonDocument = await JsonDocument.ParseAsync(responseStream, cancellationToken: cancellationToken).ConfigureAwait(false);

            if (!jsonDocument.RootElement.TryGetProperty("message", out var messageElement)
                || !messageElement.TryGetProperty("content", out var contentElement))
            {
                return Result<string>.Failure(new Error(
                    "OLLAMA_CHAT_INVALID_RESPONSE",
                    "Ollama /api/chat response did not contain message.content."));
            }

            var content = contentElement.GetString() ?? string.Empty;
            return Result<string>.Success(content);
        }
        catch (OperationCanceledException)
        {
            return Result<string>.Failure(new Error(
                "OLLAMA_CHAT_CANCELLED",
                "Ollama /api/chat request was cancelled."));
        }
        catch (HttpRequestException ex)
        {
            IsAvailable = false;
            return Result<string>.Failure(new Error(
                "OLLAMA_UNREACHABLE",
                $"Unable to reach Ollama /api/chat endpoint at {_httpClient.BaseAddress}: {ex.Message}"));
        }
        catch (JsonException ex)
        {
            return Result<string>.Failure(new Error(
                "OLLAMA_CHAT_INVALID_JSON",
                $"Failed to parse Ollama /api/chat response: {ex.Message}"));
        }
    }

    private static OllamaModelManifest[] ParseManifest(JsonElement root)
    {
        if (!root.TryGetProperty("models", out var modelsElement) || modelsElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var manifest = new List<OllamaModelManifest>();
        foreach (var modelElement in modelsElement.EnumerateArray())
        {
            var name = modelElement.TryGetProperty("name", out var nameElement)
                ? nameElement.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var size = modelElement.TryGetProperty("size", out var sizeElement) && sizeElement.ValueKind == JsonValueKind.Number
                ? sizeElement.GetInt64()
                : 0L;

            DateTimeOffset? modifiedAt = null;
            if (modelElement.TryGetProperty("modified_at", out var modifiedAtElement)
                && modifiedAtElement.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(modifiedAtElement.GetString(), out var parsedModifiedAt))
            {
                modifiedAt = parsedModifiedAt;
            }

            manifest.Add(new OllamaModelManifest(name, size, modifiedAt));
        }

        return manifest.ToArray();
    }

    private static string BuildChatPayload(
        string modelName,
        string systemPrompt,
        string userPrompt,
        IReadOnlyList<byte[]>? imageBytesList)
    {
        var systemEscaped = EscapeJsonString(systemPrompt ?? string.Empty);
        var userEscaped = EscapeJsonString(userPrompt ?? string.Empty);
        var modelEscaped = EscapeJsonString(modelName);

        if (imageBytesList is { Count: > 0 })
        {
            var images = imageBytesList
                .Where(bytes => bytes != null && bytes.Length > 0)
                .Select(bytes => $"\"{EscapeJsonString(Convert.ToBase64String(bytes))}\"");

            return
                $"{{\"model\":\"{modelEscaped}\",\"stream\":false,\"messages\":[" +
                $"{{\"role\":\"system\",\"content\":\"{systemEscaped}\"}}," +
                $"{{\"role\":\"user\",\"content\":\"{userEscaped}\",\"images\":[{string.Join(",", images)}]}}" +
                "]}}";
        }

        return
            $"{{\"model\":\"{modelEscaped}\",\"stream\":false,\"messages\":[" +
            $"{{\"role\":\"system\",\"content\":\"{systemEscaped}\"}}," +
            $"{{\"role\":\"user\",\"content\":\"{userEscaped}\"}}" +
            "]}}";
    }

    private static string EscapeJsonString(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

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
}
