using Microsoft.Extensions.Logging;
using Ashlar.Infrastructure.Deployment;
using Ashlar.Infrastructure.Execution.LoadPolicy;

using Ashlar.Core.Application.Execution.Ports;

namespace Ashlar.Infrastructure.Execution;

/// <summary>
/// Provider factory that routes LLM requests to edge (local) or server (cloud) based on ILoadPolicy.
/// Tries chosen provider first; on failure, falls back to the other. Never returns mock.
/// </summary>
/// <remarks>
/// <b>AirGapped never escalates past local</b> (SPEC-007 PR 4.10, default D35). When the composition's
/// <see cref="ResolvedDeploymentProfile"/> is AirGapped, the cloud providers (<c>openai</c>, <c>azure</c>) are never
/// tried, on the LLM path and the single-image vision path alike, whatever the load policy resolved: a local resolve
/// tries only itself, and a cloud resolve falls back to the local providers alone. The multi-frame path, which uses
/// the resolved provider only, refuses a cloud resolve. The egress guard stays the backstop for anything else.
/// </remarks>
public sealed class AdaptiveProviderFactory : IProviderFactory
{
    private readonly IProviderFactory _inner;
    private readonly ILoadPolicy _loadPolicy;
    private readonly ILogger<AdaptiveProviderFactory>? _logger;
    private readonly bool _airGapped;

    /// <summary>Initializes a new adaptive provider factory.</summary>
    /// <param name="inner">The factory that runs each provider.</param>
    /// <param name="loadPolicy">Chooses the first provider.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="deploymentProfile">The profile <c>AddAshlar</c> resolved; on AirGapped the cloud providers are
    /// never tried. <see langword="null"/> escalates as before.</param>
    public AdaptiveProviderFactory(
        IProviderFactory inner,
        ILoadPolicy loadPolicy,
        ILogger<AdaptiveProviderFactory>? logger = null,
        ResolvedDeploymentProfile? deploymentProfile = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _loadPolicy = loadPolicy ?? throw new ArgumentNullException(nameof(loadPolicy));
        _logger = logger;
        _airGapped = deploymentProfile?.IsAirGapped == true;
    }

    /// <summary>The providers AirGapped never tries.</summary>
    internal static bool IsCloudProvider(string provider) =>
        string.Equals(provider, "openai", StringComparison.OrdinalIgnoreCase)
        || string.Equals(provider, "azure", StringComparison.OrdinalIgnoreCase);

    /// <summary>On AirGapped, the candidates without the cloud providers; otherwise the candidates.</summary>
    private string[] Candidates(string[] candidates) =>
        _airGapped ? candidates.Where(p => !IsCloudProvider(p)).ToArray() : candidates;

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

        var providersToTry = Candidates(resolved == "ollama" || resolved == "local"
            ? new[] { resolved, "openai", "azure" }
            : new[] { resolved, "ollama", "local" });

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

        var providersToTry = Candidates(new[] { resolved, "ollama", "openai", "azure" });
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
        if (_airGapped && IsCloudProvider(resolved))
            throw new ModelUnavailableException(
                $"No vision model available: the load policy chose '{resolved}', a cloud provider, which the AirGapped profile never tries.");

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
