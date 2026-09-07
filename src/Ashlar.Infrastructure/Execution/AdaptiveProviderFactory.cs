using Microsoft.Extensions.Logging;
using Ashlar.Infrastructure.Execution.LoadPolicy;

using Ashlar.Core.Application.Execution.Ports;

namespace Ashlar.Infrastructure.Execution;

/// <summary>
/// Provider factory that routes LLM requests to edge (local) or server (cloud) based on ILoadPolicy.
/// Tries chosen provider first; on failure, falls back to the other. Never returns mock.
/// </summary>
public sealed class AdaptiveProviderFactory : IProviderFactory
{
    private readonly IProviderFactory _inner;
    private readonly ILoadPolicy _loadPolicy;
    private readonly ILogger<AdaptiveProviderFactory>? _logger;

    /// <summary>Initializes a new adaptive provider factory.</summary>
    public AdaptiveProviderFactory(
        IProviderFactory inner,
        ILoadPolicy loadPolicy,
        ILogger<AdaptiveProviderFactory>? logger = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _loadPolicy = loadPolicy ?? throw new ArgumentNullException(nameof(loadPolicy));
        _logger = logger;
    }

    /// <inheritdoc />
    public bool IsProviderAvailable(string provider) => _inner.IsProviderAvailable(provider);

    /// <inheritdoc />
    public async Task<string> ExecuteLLMAsync(
        string provider,
        string systemPrompt,
        string userPrompt,
        object config,
        CancellationToken cancellationToken = default)
    {
        var resolved = _loadPolicy.ResolveProvider(_inner);
        if (string.IsNullOrEmpty(resolved))
        {
            throw new ModelUnavailableException(
                "No model available. Ensure local model (Ollama) is running or server (OpenAI/Azure) is configured.");
        }

        var providersToTry = resolved == "ollama" || resolved == "local"
            ? new[] { resolved, "openai", "azure" }
            : new[] { resolved, "ollama", "local" };

        Exception? lastEx = null;
        foreach (var p in providersToTry)
        {
            if (!_inner.IsProviderAvailable(p))
                continue;
            try
            {
                return await _inner.ExecuteLLMAsync(p, systemPrompt, userPrompt, config, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                lastEx = ex;
                _logger?.LogWarning(ex, "Provider {Provider} failed, trying fallback", p);
            }
        }

        throw new ModelUnavailableException(
            "No model available. Ensure local model is loaded or server is reachable.",
            lastEx ?? new InvalidOperationException("All providers failed"));
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
        var resolved = _loadPolicy.ResolveProvider(_inner);
        if (string.IsNullOrEmpty(resolved))
            throw new ModelUnavailableException("No vision model available.");

        var providersToTry = new[] { resolved, "ollama", "openai", "azure" };
        Exception? lastEx = null;
        foreach (var p in providersToTry.Distinct())
        {
            if (!_inner.IsProviderAvailable(p))
                continue;
            try
            {
                return await _inner.ExecuteVisionAsync(p, systemPrompt, userPrompt, imageBytes, config, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                lastEx = ex;
                _logger?.LogWarning(ex, "Vision provider {Provider} failed", p);
            }
        }

        throw new ModelUnavailableException("No vision model available.", lastEx ?? new InvalidOperationException("All vision providers failed."));
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
        var resolved = _loadPolicy.ResolveProvider(_inner);
        if (string.IsNullOrEmpty(resolved))
            throw new ModelUnavailableException("No vision model available.");

        try
        {
            return await _inner.ExecuteVisionMultiFrameAsync(resolved, systemPrompt, userPrompt, frameBytes, config, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new ModelUnavailableException("Vision model failed.", ex);
        }
    }

    /// <inheritdoc />
    public Task<string> ExecuteVideoAsync(
        string systemPrompt,
        string userPrompt,
        IReadOnlyList<byte[]> frameBytes,
        object config,
        CancellationToken cancellationToken = default)
        => _inner.ExecuteVideoAsync(systemPrompt, userPrompt, frameBytes, config, cancellationToken);

    /// <inheritdoc />
    public Task EnsureOllamaReachableAsync(bool requireVisionModel, CancellationToken cancellationToken = default)
        => _inner.EnsureOllamaReachableAsync(requireVisionModel, cancellationToken);
}
