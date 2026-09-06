using Ashlar.Core.Application.Execution.Ports;
using InfraProviderFactory = Ashlar.Infrastructure.Execution.IProviderFactory;

namespace Ashlar.Infrastructure.Adapters;

/// <summary>
/// Adapter implementing the Application layer IProviderFactory port
/// using the Infrastructure layer IProviderFactory implementation.
/// 
/// This adapter acts as a bridge between the Application layer port
/// and the concrete Infrastructure implementation, allowing components
/// like BackgroundAgents to depend on Application ports without
/// directly coupling to Infrastructure implementations.
/// 
/// The adapter simply delegates all calls to the underlying implementation
/// since the port interface matches the infrastructure interface exactly.
/// </summary>
public sealed class ProviderFactoryAdapter : Core.Application.Execution.Ports.IProviderFactory
{
    private readonly InfraProviderFactory _inner;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProviderFactoryAdapter"/> class.
    /// </summary>
    /// <param name="inner">The underlying infrastructure provider factory.</param>
    public ProviderFactoryAdapter(InfraProviderFactory inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    /// <inheritdoc />
    public bool IsProviderAvailable(string provider)
        => _inner.IsProviderAvailable(provider);

    /// <inheritdoc />
    public Task<string> ExecuteLLMAsync(
        string provider,
        string systemPrompt,
        string userPrompt,
        object config,
        CancellationToken cancellationToken = default)
        => _inner.ExecuteLLMAsync(provider, systemPrompt, userPrompt, config, cancellationToken);

    /// <inheritdoc />
    public Task<string> ExecuteVisionAsync(
        string provider,
        string systemPrompt,
        string userPrompt,
        byte[] imageBytes,
        object config,
        CancellationToken cancellationToken = default)
        => _inner.ExecuteVisionAsync(provider, systemPrompt, userPrompt, imageBytes, config, cancellationToken);

    /// <inheritdoc />
    public Task<string> ExecuteVisionMultiFrameAsync(
        string provider,
        string systemPrompt,
        string userPrompt,
        IReadOnlyList<byte[]> frameBytes,
        object config,
        CancellationToken cancellationToken = default)
        => _inner.ExecuteVisionMultiFrameAsync(provider, systemPrompt, userPrompt, frameBytes, config, cancellationToken);

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
