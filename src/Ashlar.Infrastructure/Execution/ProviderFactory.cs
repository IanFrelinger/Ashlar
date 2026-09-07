using Ashlar.Infrastructure.Execution.Scratch;
using Ashlar.Core.Application.Execution.Ports;
using Microsoft.Extensions.Logging;
using Ashlar.Core.Application.Ephemeral.Ports;
using Ashlar.Core.Application.Resilience.Ports;
using Ashlar.Core.Domain;
using Ashlar.Infrastructure.Execution.Ollama;
using Ashlar.Infrastructure.Resilience;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Ashlar.Infrastructure.Execution;

/// <summary>
/// Central factory for resolving and instantiating LLM provider backends.
///
/// <para><b>Role in execution pipeline:</b> The orchestration layer calls
/// <see cref="IProviderFactory"/> to obtain a provider for a given request.
/// The factory decides which backend to use based on the provider name
/// resolved through a precedence chain: explicit caller argument → environment
/// variables (e.g. <c>ASHLAR_PROVIDER</c>) → user configuration → compile-time
/// defaults in <see cref="AshlarDefaults"/>.</para>
///
/// <para><b>Available providers:</b> openai, openai_compat, azure, ollama, local
/// (in-process ONNX/LLamaSharp), video (SmolVLM2-Video in Docker), the offline
/// <c>deterministic</c> route (<see cref="AshlarDefaults.DeterministicProviderName"/>,
/// the framework's own default for anything that may run with no model behind it),
/// and the test-only mock/offline/mock-json/echo set. To add a new provider, add its
/// key to <see cref="KnownProviders"/>, implement availability detection in
/// <see cref="IsProviderAvailable"/>, and add instantiation logic in the
/// provider-creation path.</para>
///
/// <para><b>Retry policy:</b> LLM/HTTP calls are folded through
/// <see cref="IResilientExecutor"/> with exponential back-off from a 2s base
/// delay. Transient HTTP statuses (5xx / 429) are converted to
/// <see cref="HttpRequestException"/> so the domain-neutral
/// <see cref="TransientClassifiers.Network"/> classifier can retry them.
/// Provider-specific faults (e.g. <see cref="ModelUnavailableException"/>)
/// are classified only in this infrastructure layer — never in core ports.
/// Retry count is read from <c>ASHLAR_LLM_RETRY_COUNT</c> (non-negative int),
/// falling back to <see cref="AshlarDefaults.LlmRetryCount"/>.</para>
///
/// <para><b>Mock provider gating:</b> Mock/offline providers are only available
/// when <c>ASHLAR_ALLOW_MOCK=1</c>. This prevents accidental use in production
/// while allowing integration tests to opt-in.</para>
///
/// <para><b>Thread-safety:</b> <c>Http</c> is static and safe for concurrent use.
/// The Ollama provider instance is lazily cached behind <c>_ollamaProviderLock</c>
/// and re-created only when the base URL changes (e.g. ephemeral container restart).</para>
/// </summary>
public class ProviderFactory : IProviderFactory
{
    private readonly ILogger<ProviderFactory> _logger;
    private readonly IEphemeralModelLifecycle? _ephemeralLifecycle;
    private readonly IResilientExecutor _resilientExecutor;
    private readonly IScratchSpace _scratchSpace;
    private readonly object _ollamaProviderLock = new();
    private OllamaProvider? _ollamaProvider;
    private string? _ollamaProviderBaseUrl;
    private HttpClient? _ollamaHttpClient;
    private static readonly HttpClient Http = new();

    // --- Provider catalogue ---

    /// <summary>
    /// Every provider name this build can route to. Public because "is this a provider at all?" is a
    /// DIFFERENT question from "is it configured and reachable", and callers that conflate the two
    /// fail open: a misspelled or invented provider name used to sail through, fail at call time,
    /// and land in the echo fallback, which then reported success over work no model ever saw.
    ///
    /// <para>An unreachable provider may degrade. A name that is not a provider cannot — no
    /// fallback rescues a typo, it only hides it — so callers should refuse a name that is not in
    /// this set, and say what the set contains.</para>
    /// </summary>
    public static readonly IReadOnlySet<string> KnownProviders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "openai",
        "openai_compat",
        "azure",
        "ollama",
        "local", // In-process ONNX/LLamaSharp; requires ASHLAR_LOCAL_MODEL_PATH
        "video", // SmolVLM2-Video in Docker; requires VIDEO_SERVICE_URL
        "mock",
        "offline",
        "mock-json",
        "echo",
        // The framework's OWN default (BackgroundAgentConfig.ModelProvider), the no-LLM sentinel
        // BackgroundAgentRegistry reads, and an offline route MeaiBackedModel handles by name.
        // Omitting it made a scaffold that ashlar verify had just CERTIFIED refuse to run on the
        // same directory: "not a model provider this build knows". Spelled from the shared constant
        // so the allow-list and the default cannot drift apart again.
        AshlarDefaults.DeterministicProviderName,
    };

    /// <summary>
    /// True when <paramref name="provider"/> names a provider this build knows how to route to,
    /// whether or not it is configured. Never confuse this with availability.
    /// </summary>
    public static bool IsKnownProvider(string? provider) =>
        !string.IsNullOrWhiteSpace(provider) && KnownProviders.Contains(provider!.Trim());

    /// <summary>The known provider names, sorted, for refusal messages.</summary>
    public static string KnownProviderList() =>
        string.Join(", ", KnownProviders.OrderBy(p => p, StringComparer.Ordinal));

    private readonly HashSet<string> _availableProviders = new(KnownProviders, StringComparer.OrdinalIgnoreCase);

    private static bool AllowMock => string.Equals(Environment.GetEnvironmentVariable("ASHLAR_ALLOW_MOCK"), "1", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Creates a new <see cref="ProviderFactory"/> and starts warming the Ollama
    /// provider in the background. Warming is intentional: Ollama requires pulling a
    /// model manifest on first contact, which is slow (~seconds), so priming it ahead
    /// of time avoids a latency spike on the first user request. It runs OFF the
    /// calling thread — constructing this factory must never block on the network,
    /// because that makes every <see cref="IProviderFactory"/> resolution, host startup
    /// included, wait on a machine that may not be listening.
    /// Failure is non-fatal — Ollama simply won't be available until the next
    /// lazy attempt.
    /// </summary>
    /// <param name="logger">Logger for diagnostics.</param>
    /// <param name="ephemeralLifecycle">Optional ephemeral model lifecycle. When ASHLAR_EPHEMERAL_MODELS=1, use to resolve Ollama URL from container.</param>
    /// <param name="resilientExecutor">Optional resilient executor; defaults to <see cref="ResilientExecutor"/>.</param>
    /// <param name="scratchSpace">Optional scratch space for transient working directories; defaults to <see cref="FileScratchSpace"/>.</param>
    public ProviderFactory(
        ILogger<ProviderFactory> logger,
        IEphemeralModelLifecycle? ephemeralLifecycle = null,
        IResilientExecutor? resilientExecutor = null,
        IScratchSpace? scratchSpace = null)
    {
        _logger = logger;
        _ephemeralLifecycle = ephemeralLifecycle;
        _resilientExecutor = resilientExecutor ?? new ResilientExecutor();
        _scratchSpace = scratchSpace ?? new FileScratchSpace();

        // Warm the Ollama manifest in the BACKGROUND, never on this thread.
        //
        // The warm-up itself is deliberate and worth keeping: pulling the manifest on
        // first contact costs seconds, and doing it ahead of time avoids that latency
        // on the first user request. What was wrong was doing it synchronously here.
        // Both GetOllamaBaseUrlAsync and OllamaProvider's constructor block on network
        // I/O, so CONSTRUCTING this type — and therefore every resolution of
        // IProviderFactory, including during host startup — waited on a machine that
        // may not be listening.
        //
        // Fire-and-forget is safe precisely because the warm-up is an optimisation and
        // nothing depends on it: the provider is created on demand at each real use
        // site, and failure here has always been non-fatal by design.
        _ = Task.Run(async () =>
        {
            try
            {
                var baseUrl = await GetOllamaBaseUrlAsync(CancellationToken.None).ConfigureAwait(false);
                _ = GetOrCreateOllamaProvider(baseUrl);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to initialize Ollama provider manifest during ProviderFactory startup.");
            }
        });
    }

    private static RetryPolicy CreateLlmRetryPolicy(Func<Exception, bool>? isTransient = null)
    {
        var retryCount = int.TryParse(Environment.GetEnvironmentVariable("ASHLAR_LLM_RETRY_COUNT"), out var c) && c >= 0
            ? c
            : AshlarDefaults.LlmRetryCount;
        // Polly WaitAndRetryAsync(n) performed n retries after the first try → n+1 attempts.
        return new RetryPolicy(
            MaxAttempts: Math.Max(1, retryCount + 1),
            BaseDelay: TimeSpan.FromSeconds(2),
            IsTransient: isTransient);
    }

    /// <summary>
    /// Infrastructure-only classifier: network defaults plus provider-local
    /// <see cref="ModelUnavailableException"/>. Kept out of core ports.
    /// </summary>
    private static bool IsProviderCallTransient(Exception ex) =>
        TransientClassifiers.Network(ex) || ex is ModelUnavailableException;

    private Task<HttpResponseMessage> SendHttpWithResilienceAsync(
        Func<CancellationToken, Task<HttpResponseMessage>> send,
        CancellationToken cancellationToken)
    {
        return _resilientExecutor.ExecuteAsync(async ct =>
        {
            var resp = await send(ct).ConfigureAwait(false);
            if ((int)resp.StatusCode >= 500 || resp.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var code = (int)resp.StatusCode;
                resp.Dispose();
                throw new HttpRequestException($"Transient HTTP status {code}");
            }

            return resp;
        }, CreateLlmRetryPolicy(), cancellationToken);
    }
    
    /// <inheritdoc />
    public bool IsProviderAvailable(string provider)
    {
        provider = (provider ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(provider)) return false;

        if (!_availableProviders.Contains(provider)) return false;

        // Mock/offline providers: only available when ASHLAR_ALLOW_MOCK=1 (default: disabled)
        if (provider is "mock" or "offline" or "mock-json" or "echo")
            return AllowMock;

        return provider switch
        {
            "openai" => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENAI_API_KEY")),
            "openai_compat" => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENAI_COMPAT_API_KEY"))
                             && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENAI_COMPAT_BASE_URL")),
            "azure" => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT"))
                       && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY"))
                       && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT")),
            "ollama" => IsOllamaProviderAvailable(),
            "local" => LocalModelProvider.IsAvailable(),
            "video" => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VIDEO_SERVICE_URL")),
            _ => false
        };
    }
    
    /// <inheritdoc />
    public async Task<string> ExecuteLLMAsync(
        string provider,
        string systemPrompt,
        string userPrompt,
        object config,
        CancellationToken cancellationToken = default)
    {
        var defaultProvider = AllowMock ? "mock" : "ollama";
        provider = (provider ?? defaultProvider).Trim().ToLowerInvariant();
        _logger.LogInformation("Executing LLM request with provider {Provider}", provider);

        // Mock/offline providers: only when ASHLAR_ALLOW_MOCK=1
        if (provider is "mock" or "offline" or "mock-json" or "echo")
        {
            if (!AllowMock)
                throw new ModelUnavailableException("Mock providers are disabled. Set ASHLAR_ALLOW_MOCK=1 for tests/demos, or use a real provider (ollama, openai, openai_compat, azure, local).");
            await Task.Delay(AshlarDefaults.MockDelayMs, cancellationToken);
            return MockScaffoldingResponder.Generate(systemPrompt, userPrompt);
        }

        await Task.Delay(AshlarDefaults.MockDelayMs, cancellationToken);
        
        // Real providers: fail fast on misconfiguration or request failure (no mock fallback).
        if (provider is "openai")
        {
            var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new InvalidOperationException("OPENAI_API_KEY is not set. Set it or use provider mock/offline.");
            return await ExecuteOpenAiAsync(apiKey, systemPrompt, userPrompt, cancellationToken);
        }

        if (provider is "openai_compat")
        {
            var apiKey = Environment.GetEnvironmentVariable("OPENAI_COMPAT_API_KEY");
            var baseUrl = Environment.GetEnvironmentVariable("OPENAI_COMPAT_BASE_URL");
            if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(baseUrl))
            {
                throw new InvalidOperationException(
                    "OPENAI_COMPAT_API_KEY and OPENAI_COMPAT_BASE_URL must be set for provider openai_compat (e.g. vLLM, LiteLLM, llama.cpp server).");
            }

            var model = Environment.GetEnvironmentVariable("OPENAI_COMPAT_MODEL") ?? AshlarDefaults.OpenAiCompatDefaultModel;
            var url = OpenAiCompatibleEndpoint.NormalizeChatCompletionsUrl(baseUrl).ToString();
            return await ExecuteOpenAiChatCompletionAsync(url, apiKey, model, systemPrompt, userPrompt, cancellationToken);
        }

        if (provider is "azure")
        {
            var endpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT");
            var apiKey = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY");
            var deployment = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT");
            if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(deployment))
                throw new InvalidOperationException("Azure OpenAI env vars not set (AZURE_OPENAI_ENDPOINT, AZURE_OPENAI_API_KEY, AZURE_OPENAI_DEPLOYMENT). Set them or use provider mock/offline.");
            return await ExecuteAzureOpenAiAsync(endpoint, apiKey, deployment, systemPrompt, userPrompt, cancellationToken);
        }

        if (provider is "ollama")
        {
            return await _resilientExecutor.ExecuteAsync(
                ct => ExecuteOllamaAsync(systemPrompt, userPrompt, null, config, ct),
                CreateLlmRetryPolicy(IsProviderCallTransient),
                cancellationToken).ConfigureAwait(false);
        }

        if (provider is "local")
            return await LocalModelProvider.ExecuteAsync(systemPrompt, userPrompt, config, cancellationToken);

        throw new InvalidOperationException($"Unknown or unsupported provider: {provider}. Use ollama, openai, openai_compat, azure, local, or mock (ASHLAR_ALLOW_MOCK=1).");
    }

    private async Task<string> ExecuteOpenAiAsync(string apiKey, string systemPrompt, string userPrompt, CancellationToken ct)
    {
        var model = Environment.GetEnvironmentVariable("OPENAI_MODEL") ?? AshlarDefaults.OpenAiDefaultModel;
        var rawUrl = Environment.GetEnvironmentVariable("OPENAI_BASE_URL") ?? AshlarDefaults.OpenAiDefaultBaseUrl;
        var url = OpenAiCompatibleEndpoint.NormalizeChatCompletionsUrl(rawUrl).ToString();
        return await ExecuteOpenAiChatCompletionAsync(url, apiKey, model, systemPrompt, userPrompt, ct);
    }

    private async Task<string> ExecuteOpenAiChatCompletionAsync(
        string requestUrl,
        string apiKey,
        string model,
        string systemPrompt,
        string userPrompt,
        CancellationToken ct)
    {
        var payload = new
        {
            model,
            messages = new[]
            {
                new { role = "system", content = systemPrompt ?? "" },
                new { role = "user", content = userPrompt ?? "" }
            },
            temperature = AshlarDefaults.LlmTemperature
        };
        var json = JsonSerializer.Serialize(payload);

        using var resp = await SendHttpWithResilienceAsync(token =>
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, requestUrl);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            req.Content = new StringContent(json);
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            return Http.SendAsync(req, token);
        }, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct);
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(body);
        var content = doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString();

        return content ?? throw new InvalidOperationException("OpenAI-compatible response content was null");
    }

    private async Task<string> ExecuteAzureOpenAiAsync(string endpoint, string apiKey, string deployment, string systemPrompt, string userPrompt, CancellationToken ct)
    {
        endpoint = endpoint.TrimEnd('/');
        var apiVersion = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_VERSION") ?? AshlarDefaults.AzureOpenAiDefaultApiVersion;
        var url = $"{endpoint}/openai/deployments/{deployment}/chat/completions?api-version={apiVersion}";

        var payload = new
        {
            messages = new[]
            {
                new { role = "system", content = systemPrompt ?? "" },
                new { role = "user", content = userPrompt ?? "" }
            },
            temperature = AshlarDefaults.LlmTemperature
        };
        var json = JsonSerializer.Serialize(payload);

        using var resp = await SendHttpWithResilienceAsync(token =>
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.Add("api-key", apiKey);
            req.Content = new StringContent(json);
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            return Http.SendAsync(req, token);
        }, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct);
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(body);
        var content = doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString();

        return content ?? throw new InvalidOperationException("Azure OpenAI response content was null");
    }

    private async Task<string> ExecuteOpenAiVisionAsync(
        string apiKey,
        string openAiBaseUrl,
        string model,
        string systemPrompt,
        string userPrompt,
        byte[]? imageBytes,
        CancellationToken ct)
    {
        var url = OpenAiCompatibleEndpoint.NormalizeChatCompletionsUrl(openAiBaseUrl).ToString();

        object[] contentParts;
        if (imageBytes is { Length: > 0 })
        {
            var b64 = Convert.ToBase64String(imageBytes);
            contentParts = new object[] { new { type = "text", text = userPrompt ?? "" }, new { type = "image_url", image_url = new { url = $"data:image/png;base64,{b64}" } } };
        }
        else
        {
            contentParts = new object[] { new { type = "text", text = userPrompt ?? "" } };
        }

        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var messages = new object[]
        {
            new { role = "system", content = systemPrompt ?? "" },
            new { role = "user", content = (object)contentParts }
        };
        var payload = new { model, messages, temperature = AshlarDefaults.LlmTemperature, max_tokens = AshlarDefaults.LlmMaxTokens };

        req.Content = new StringContent(JsonSerializer.Serialize(payload));
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using var resp = await Http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(body);
        var content = doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString();

        return content ?? throw new InvalidOperationException("OpenAI vision response content was null");
    }

    private async Task<string> ExecuteAzureOpenAiVisionAsync(string endpoint, string apiKey, string deployment, string systemPrompt, string userPrompt, byte[]? imageBytes, object? config, CancellationToken ct)
    {
        endpoint = endpoint.TrimEnd('/');
        var apiVersion = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_VERSION") ?? AshlarDefaults.AzureOpenAiDefaultApiVersion;
        var url = $"{endpoint}/openai/deployments/{deployment}/chat/completions?api-version={apiVersion}";

        object[] contentParts;
        if (imageBytes is { Length: > 0 })
        {
            var b64 = Convert.ToBase64String(imageBytes);
            contentParts = new object[] { new { type = "text", text = userPrompt ?? "" }, new { type = "image_url", image_url = new { url = $"data:image/png;base64,{b64}" } } };
        }
        else
        {
            contentParts = new object[] { new { type = "text", text = userPrompt ?? "" } };
        }

        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Add("api-key", apiKey);

        var messages = new object[]
        {
            new { role = "system", content = systemPrompt ?? "" },
            new { role = "user", content = (object)contentParts }
        };
        var payload = new { messages, temperature = AshlarDefaults.LlmTemperature, max_tokens = AshlarDefaults.LlmMaxTokens };

        req.Content = new StringContent(JsonSerializer.Serialize(payload));
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using var resp = await Http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(body);
        var content = doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString();

        return content ?? throw new InvalidOperationException("Azure OpenAI vision response content was null");
    }

    private static string? GetModelFromConfig(object? config)
    {
        if (config == null) return null;
        if (config is IReadOnlyDictionary<string, object> rod && rod.TryGetValue("model", out var v))
            return v as string;
        var t = config.GetType();
        var prop = t.GetProperty("model", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase)
            ?? t.GetProperty("Model", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        return prop?.GetValue(config) as string;
    }

    private async Task<string> ExecuteOllamaAsync(string systemPrompt, string userPrompt, IReadOnlyList<byte[]>? imageBytesList, object? config, CancellationToken ct)
    {
        var baseUrl = await GetOllamaBaseUrlAsync(ct);
        var hasImages = imageBytesList is { Count: > 0 };
        var configModel = GetModelFromConfig(config);
        var requestedModel = configModel
            ?? (hasImages
                ? (Environment.GetEnvironmentVariable("OLLAMA_VISION_MODEL") ?? Environment.GetEnvironmentVariable("OLLAMA_MODEL") ?? AshlarDefaults.OllamaDefaultVisionModel)
                : (Environment.GetEnvironmentVariable("OLLAMA_MODEL") ?? AshlarDefaults.OllamaDefaultModel));
        var ollamaProvider = GetOrCreateOllamaProvider(baseUrl);

        var validation = ollamaProvider.ValidateModel(requestedModel);
        if (!validation.IsSuccess)
        {
            var refresh = await ollamaProvider.RefreshModelsAsync(ct).ConfigureAwait(false);
            if (!refresh.IsSuccess)
            {
                throw new ModelUnavailableException(
                    $"Ollama manifest refresh failed. {FormatOllamaError(refresh.Error)}");
            }

            validation = ollamaProvider.ValidateModel(requestedModel);
            if (!validation.IsSuccess)
            {
                throw new ModelUnavailableException(
                    $"Ollama model validation failed. {FormatOllamaError(validation.Error)}");
            }
        }

        var execution = await ollamaProvider
            .ExecuteChatAsync(requestedModel, systemPrompt, userPrompt, imageBytesList, ct)
            .ConfigureAwait(false);
        if (!execution.IsSuccess)
        {
            throw new ModelUnavailableException(
                $"Ollama execution failed. {FormatOllamaError(execution.Error)}");
        }

        return execution.Value ?? string.Empty;
    }

    /// <inheritdoc />
    public async Task<string> ExecuteVisionAsync(
        string provider,
        string systemPrompt,
        string userPrompt,
        byte[] imageBytes,
        object config,
        CancellationToken cancellationToken = default)
    {
        var defaultProvider = AllowMock ? "mock" : "ollama";
        provider = (provider ?? defaultProvider).Trim().ToLowerInvariant();
        _logger.LogInformation("Executing vision request with provider {Provider}", provider);

        if (provider is "mock" or "offline" or "mock-json" or "echo")
        {
            if (!AllowMock)
                throw new ModelUnavailableException("Mock providers are disabled. Set ASHLAR_ALLOW_MOCK=1 or use ollama, openai, openai_compat, azure.");
            return MockScaffoldingResponder.Generate(systemPrompt, userPrompt);
        }

        if (provider is "ollama" or "auto" or "local")
            return await ExecuteOllamaAsync(systemPrompt, userPrompt, imageBytes != null ? [imageBytes] : null, config, cancellationToken);

        if (provider is "openai")
        {
            var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new InvalidOperationException("OPENAI_API_KEY is not set. Set it or use provider ollama.");
            var baseUrl = Environment.GetEnvironmentVariable("OPENAI_BASE_URL") ?? "https://api.openai.com";
            var model = GetModelFromConfig(config)
                ?? Environment.GetEnvironmentVariable("OPENAI_VISION_MODEL")
                ?? Environment.GetEnvironmentVariable("OPENAI_MODEL")
                ?? AshlarDefaults.OpenAiDefaultVisionModel;
            return await ExecuteOpenAiVisionAsync(apiKey, baseUrl, model, systemPrompt, userPrompt, imageBytes, cancellationToken);
        }

        if (provider is "openai_compat")
        {
            var apiKey = Environment.GetEnvironmentVariable("OPENAI_COMPAT_API_KEY");
            var baseUrl = Environment.GetEnvironmentVariable("OPENAI_COMPAT_BASE_URL");
            if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(baseUrl))
            {
                throw new InvalidOperationException(
                    "OPENAI_COMPAT_API_KEY and OPENAI_COMPAT_BASE_URL must be set for provider openai_compat.");
            }

            var model = GetModelFromConfig(config)
                ?? Environment.GetEnvironmentVariable("OPENAI_COMPAT_VISION_MODEL")
                ?? Environment.GetEnvironmentVariable("OPENAI_COMPAT_MODEL")
                ?? AshlarDefaults.OpenAiCompatDefaultVisionModel;
            return await ExecuteOpenAiVisionAsync(apiKey, baseUrl, model, systemPrompt, userPrompt, imageBytes, cancellationToken);
        }

        if (provider is "azure")
        {
            var endpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT");
            var apiKey = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY");
            var deployment = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT");
            if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(deployment))
                throw new InvalidOperationException("Azure OpenAI env vars not set (AZURE_OPENAI_ENDPOINT, AZURE_OPENAI_API_KEY, AZURE_OPENAI_DEPLOYMENT). Set them or use provider ollama.");
            return await ExecuteAzureOpenAiVisionAsync(endpoint, apiKey, deployment, systemPrompt, userPrompt, imageBytes, config, cancellationToken);
        }

        throw new InvalidOperationException($"Unknown or unsupported vision provider: {provider}. Use ollama, openai, openai_compat, azure, auto, or local.");
    }

    /// <inheritdoc />
    public async Task<string> ExecuteVisionMultiFrameAsync(
        string provider,
        string systemPrompt,
        string userPrompt,
        IReadOnlyList<byte[]> frameBytes,
        object config,
        CancellationToken cancellationToken = default)
    {
        var defaultProvider = AllowMock ? "mock" : "ollama";
        provider = (provider ?? defaultProvider).Trim().ToLowerInvariant();
        var frames = (frameBytes ?? Array.Empty<byte[]>()).Where(b => b != null && b.Length > 0).ToList();

        if (frames.Count == 0)
            throw new ArgumentException("At least one non-empty frame is required.", nameof(frameBytes));

        // Single frame: delegate to existing path
        if (frames.Count == 1)
            return await ExecuteVisionAsync(provider, systemPrompt, userPrompt, frames[0], config, cancellationToken);

        _logger.LogInformation("Executing multi-frame vision request with provider {Provider}, {Count} frames", provider, frames.Count);

        if (provider is "mock" or "offline" or "mock-json" or "echo")
        {
            if (!AllowMock)
                throw new ModelUnavailableException("Mock providers are disabled. Set ASHLAR_ALLOW_MOCK=1 or use ollama, openai, openai_compat, azure.");
            return await ExecuteVisionAsync(provider, systemPrompt, userPrompt + $"\n[Note: {frames.Count} frames provided, analyzing most recent.]", frames[^1], config, cancellationToken);
        }

        if (provider is "ollama" or "auto" or "local")
            return await ExecuteOllamaAsync(systemPrompt, userPrompt, frames, config, cancellationToken);

        if (provider is "video")
            return await ExecuteVideoAsync(systemPrompt, userPrompt, frames, config, cancellationToken);

        if (provider is "openai")
        {
            var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new InvalidOperationException("OPENAI_API_KEY is not set. Set it or use provider ollama.");
            var baseUrl = Environment.GetEnvironmentVariable("OPENAI_BASE_URL") ?? "https://api.openai.com";
            var model = GetModelFromConfig(config)
                ?? Environment.GetEnvironmentVariable("OPENAI_VISION_MODEL")
                ?? Environment.GetEnvironmentVariable("OPENAI_MODEL")
                ?? AshlarDefaults.OpenAiDefaultVisionModel;
            return await ExecuteOpenAiVisionAsync(apiKey, baseUrl, model, systemPrompt, userPrompt, frames[^1], cancellationToken);
        }

        if (provider is "openai_compat")
        {
            var apiKey = Environment.GetEnvironmentVariable("OPENAI_COMPAT_API_KEY");
            var baseUrl = Environment.GetEnvironmentVariable("OPENAI_COMPAT_BASE_URL");
            if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(baseUrl))
            {
                throw new InvalidOperationException(
                    "OPENAI_COMPAT_API_KEY and OPENAI_COMPAT_BASE_URL must be set for provider openai_compat.");
            }

            var model = GetModelFromConfig(config)
                ?? Environment.GetEnvironmentVariable("OPENAI_COMPAT_VISION_MODEL")
                ?? Environment.GetEnvironmentVariable("OPENAI_COMPAT_MODEL")
                ?? AshlarDefaults.OpenAiCompatDefaultVisionModel;
            return await ExecuteOpenAiVisionAsync(apiKey, baseUrl, model, systemPrompt, userPrompt, frames[^1], cancellationToken);
        }

        if (provider is "azure")
        {
            var endpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT");
            var apiKey = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY");
            var deployment = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT");
            if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(deployment))
                throw new InvalidOperationException("Azure OpenAI env vars not set. Set them or use provider ollama.");
            return await ExecuteAzureOpenAiVisionAsync(endpoint, apiKey, deployment, systemPrompt, userPrompt, frames[^1], config, cancellationToken);
        }

        throw new InvalidOperationException($"Unknown or unsupported vision provider: {provider}.");
    }

    /// <inheritdoc />
    public async Task<string> ExecuteVideoAsync(
        string systemPrompt,
        string userPrompt,
        IReadOnlyList<byte[]> frameBytes,
        object config,
        CancellationToken cancellationToken = default)
    {
        var frames = (frameBytes ?? Array.Empty<byte[]>()).Where(b => b != null && b.Length > 0).ToList();
        if (frames.Count == 0)
            throw new ArgumentException("At least one frame is required for video analysis.", nameof(frameBytes));

        var baseUrl = (Environment.GetEnvironmentVariable("VIDEO_SERVICE_URL") ?? "").TrimEnd('/');
        if (string.IsNullOrEmpty(baseUrl))
            throw new InvalidOperationException("VIDEO_SERVICE_URL is not set. Start the SmolVLM2 video container and set VIDEO_SERVICE_URL.");

        var fps = AshlarDefaults.VideoDefaultFps;
        // Both the creation and the teardown of this directory go through
        // IScratchSpace now, so the temp-dir convention lives in one place
        // instead of being re-invented per call site.
        var scratch = _scratchSpace.CreateScratchDir("ashlar-video");
        var tmpDir = scratch.Path;
        try
        {
            for (var i = 0; i < frames.Count; i++)
                await File.WriteAllBytesAsync(Path.Combine(tmpDir, $"frame_{i:D5}.png"), frames[i], cancellationToken);

            var mp4Path = Path.Combine(tmpDir, "clip.mp4");
            var ffmpeg = FindFfmpeg();
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = ffmpeg,
                ArgumentList =
                {
                    "-y", "-framerate", fps.ToString(), "-i", Path.Combine(tmpDir, "frame_%05d.png"),
                    "-c:v", "libx264", "-pix_fmt", "yuv420p", "-t", (frames.Count / (double)fps).ToString("F2"), mp4Path
                },
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null)
                throw new InvalidOperationException("Failed to start ffmpeg. Install ffmpeg.");
            var err = await proc.StandardError.ReadToEndAsync(cancellationToken);
            await proc.WaitForExitAsync(cancellationToken);
            if (proc.ExitCode != 0 || !File.Exists(mp4Path))
                throw new InvalidOperationException($"ffmpeg failed: {err}");

            using var content = new MultipartFormDataContent();
            content.Add(new StringContent(userPrompt), "prompt");
            content.Add(new StringContent(systemPrompt), "system_prompt");
            var videoBytes = await File.ReadAllBytesAsync(mp4Path, cancellationToken);
            content.Add(new ByteArrayContent(videoBytes) { Headers = { ContentType = new MediaTypeHeaderValue("video/mp4") } }, "video", "clip.mp4");

            using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/v1/analyze") { Content = content };
            using var resp = await Http.SendAsync(req, cancellationToken);
            var body = await resp.Content.ReadAsStringAsync(cancellationToken);
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"Video service error {resp.StatusCode}: {body}");

            using var doc = JsonDocument.Parse(body);
            var summary = doc.RootElement.TryGetProperty("summary", out var s) ? s.GetString() ?? "" : body;
            var understanding = new
            {
                screenType = "Video",
                currentContext = summary,
                availableActions = Array.Empty<object>(),
                currentObjective = "Observation",
                progressPercent = 0,
                issues = Array.Empty<object>(),
                unexploredAreas = Array.Empty<string>(),
                confidence = 0.9
            };
            return JsonSerializer.Serialize(understanding);
        }
        finally
        {
            scratch.Dispose();
        }
    }

    private static string FindFfmpeg()
    {
        var name = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator))
        {
            var full = Path.Combine(dir.Trim(), name);
            if (File.Exists(full)) return full;
        }
        return name;
    }

    private async Task<string> GetOllamaBaseUrlAsync(CancellationToken ct)
    {
        if (_ephemeralLifecycle != null)
            return await _ephemeralLifecycle.GetBaseUrlAsync(ct);
        // Honor ASHLAR_OLLAMA_BASE_URL first, matching the MEAI OllamaEndpointResolver precedence,
        // so a single env var points BOTH model paths at the same endpoint. Fall back to the legacy
        // OLLAMA_BASE_URL that compose stacks and older docs set, then the default.
        var url = Environment.GetEnvironmentVariable("ASHLAR_OLLAMA_BASE_URL")
                  ?? Environment.GetEnvironmentVariable("OLLAMA_BASE_URL")
                  ?? AshlarDefaults.OllamaDefaultBaseUrl;
        return url.TrimEnd('/');
    }

    private bool IsOllamaProviderAvailable()
    {
        try
        {
            var baseUrl = GetOllamaBaseUrlAsync(CancellationToken.None).GetAwaiter().GetResult();
            var provider = GetOrCreateOllamaProvider(baseUrl);
            var health = provider.CheckHealthAsync(CancellationToken.None).GetAwaiter().GetResult();
            return health.IsSuccess && health.Value == true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to run Ollama health check.");
            return false;
        }
    }

    private OllamaProvider GetOrCreateOllamaProvider(string baseUrl)
    {
        lock (_ollamaProviderLock)
        {
            if (_ollamaProvider != null
                && string.Equals(_ollamaProviderBaseUrl, baseUrl, StringComparison.OrdinalIgnoreCase))
            {
                return _ollamaProvider;
            }

            var timeoutSeconds = int.TryParse(Environment.GetEnvironmentVariable("OLLAMA_TIMEOUT_SECONDS"), out var configuredTimeout)
                && configuredTimeout > 0
                ? configuredTimeout
                : 300;

            var httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(timeoutSeconds)
            };

            if (_ollamaHttpClient is not null)
            {
                _ollamaHttpClient.Dispose();
            }

            _ollamaHttpClient = httpClient;
            _ollamaProvider = new OllamaProvider(httpClient, baseUrl, _logger);
            _ollamaProviderBaseUrl = baseUrl;
            return _ollamaProvider;
        }
    }

    private static string FormatOllamaError(Error? error)
    {
        if (error is null)
        {
            return "Unknown Ollama error.";
        }

        var metadataSuffix = error.Metadata is { Count: > 0 }
            ? $" Metadata: {string.Join(", ", error.Metadata.Select(kvp => $"{kvp.Key}={kvp.Value}"))}."
            : string.Empty;

        return $"{error.Code}: {error.Message}{metadataSuffix}";
    }

    /// <inheritdoc />
    public async Task EnsureOllamaReachableAsync(bool requireVisionModel, CancellationToken cancellationToken = default)
    {
        var baseUrl = await GetOllamaBaseUrlAsync(cancellationToken);
        var provider = GetOrCreateOllamaProvider(baseUrl);
        var healthResult = await provider.CheckHealthAsync(cancellationToken).ConfigureAwait(false);
        if (!healthResult.IsSuccess || healthResult.Value != true)
        {
            throw new InvalidOperationException(
                $"Ollama is not reachable at {baseUrl}. {FormatOllamaError(healthResult.Error)}");
        }

        var models = provider.Manifest.Select(m => m.Name).ToList();
        if (models.Count == 0)
            throw new InvalidOperationException(
                $"Ollama at {baseUrl} returned no models. Pull a model: ollama pull llama3.2:3b (and for vision: ollama pull richardyoung/smolvlm2-2.2b-instruct or llava:7b).");

        if (requireVisionModel)
        {
            var hasVision = models.Any(m => m.Contains("smolvlm", StringComparison.OrdinalIgnoreCase) || m.Contains("llava", StringComparison.OrdinalIgnoreCase));
            if (!hasVision)
                throw new InvalidOperationException(
                    $"No vision model found at {baseUrl}. Available: {string.Join(", ", models)}. Pull one: ollama pull richardyoung/smolvlm2-2.2b-instruct (or llava:7b)");
        }
    }
}
