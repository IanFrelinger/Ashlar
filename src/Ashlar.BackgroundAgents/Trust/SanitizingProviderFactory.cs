using Microsoft.Extensions.Logging;
using Ashlar.Core.Application.Execution.Ports;

namespace Ashlar.BackgroundAgents.Trust;

/// <summary>
/// Intentional dual-port edge adapter for trust boundary DI.
/// Wraps IProviderFactory and sanitizes outgoing context before delegating.
/// Blocks when classification is uncertain. Logs all redactions.
/// Implements both Application port and Infrastructure interface to allow
/// ImproveCommand to register ONE sanitized instance for all consumers.
/// </summary>
public sealed class SanitizingProviderFactory : 
    IProviderFactory,
    Ashlar.Infrastructure.Execution.IProviderFactory
{
    private readonly IProviderFactory _inner;
    private readonly ICloudSanitizationProxy _proxy;
    private readonly ILogger<SanitizingProviderFactory> _logger;

    /// <summary>
    /// Creates a sanitizing wrapper around the inner provider factory.
    /// Accepts Application port IProviderFactory.
    /// </summary>
    public SanitizingProviderFactory(
        IProviderFactory inner,
        ICloudSanitizationProxy proxy,
        ILogger<SanitizingProviderFactory> logger)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _proxy = proxy ?? throw new ArgumentNullException(nameof(proxy));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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
        var context = new OutgoingContext
        {
            SystemPrompt = systemPrompt ?? string.Empty,
            UserPrompt = userPrompt ?? string.Empty,
            Provider = provider ?? string.Empty,
            IsAirGapped = false, // Caller responsibility to set from execution context
        };

        var result = _proxy.SanitizeForCloud(context, cancellationToken);
        if (!result.Allowed)
        {
            _logger.LogWarning("LLM request blocked: {Reason}", result.BlockReason);
            throw new InvalidOperationException($"Cloud request blocked: {result.BlockReason}");
        }

        var ctx = result.SanitizedContext!;
        return await _inner.ExecuteLLMAsync(provider ?? "mock", ctx.SystemPrompt, ctx.UserPrompt, config, cancellationToken);
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
        var context = new OutgoingContext
        {
            SystemPrompt = systemPrompt ?? string.Empty,
            UserPrompt = userPrompt ?? string.Empty,
            Provider = provider ?? string.Empty,
        };

        var result = _proxy.SanitizeForCloud(context, cancellationToken);
        if (!result.Allowed)
        {
            _logger.LogWarning("Vision request blocked: {Reason}", result.BlockReason);
            throw new InvalidOperationException($"Cloud request blocked: {result.BlockReason}");
        }

        var ctx = result.SanitizedContext!;
        return await _inner.ExecuteVisionAsync(provider ?? "mock", ctx.SystemPrompt, ctx.UserPrompt, imageBytes, config, cancellationToken);
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
        var context = new OutgoingContext
        {
            SystemPrompt = systemPrompt ?? string.Empty,
            UserPrompt = userPrompt ?? string.Empty,
            Provider = provider ?? string.Empty,
        };

        var result = _proxy.SanitizeForCloud(context, cancellationToken);
        if (!result.Allowed)
        {
            _logger.LogWarning("Multi-frame vision request blocked: {Reason}", result.BlockReason);
            throw new InvalidOperationException($"Cloud request blocked: {result.BlockReason}");
        }

        var ctx = result.SanitizedContext!;
        return await _inner.ExecuteVisionMultiFrameAsync(provider ?? "mock", ctx.SystemPrompt, ctx.UserPrompt, frameBytes, config, cancellationToken);
    }

    /// <inheritdoc />
    public Task<string> ExecuteVideoAsync(
        string systemPrompt,
        string userPrompt,
        IReadOnlyList<byte[]> frameBytes,
        object config,
        CancellationToken cancellationToken = default)
    {
        var context = new OutgoingContext
        {
            SystemPrompt = systemPrompt ?? string.Empty,
            UserPrompt = userPrompt ?? string.Empty,
            Provider = "video",
        };

        var result = _proxy.SanitizeForCloud(context, cancellationToken);
        if (!result.Allowed)
        {
            _logger.LogWarning("Video request blocked: {Reason}", result.BlockReason);
            throw new InvalidOperationException($"Cloud request blocked: {result.BlockReason}");
        }

        var ctx = result.SanitizedContext!;
        return _inner.ExecuteVideoAsync(ctx.SystemPrompt, ctx.UserPrompt, frameBytes, config, cancellationToken);
    }

    /// <inheritdoc />
    public Task EnsureOllamaReachableAsync(bool requireVisionModel, CancellationToken cancellationToken = default) =>
        _inner.EnsureOllamaReachableAsync(requireVisionModel, cancellationToken);
}
