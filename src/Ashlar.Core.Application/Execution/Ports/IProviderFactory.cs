namespace Ashlar.Core.Application.Execution.Ports;

/// <summary>
/// Port for LLM provider execution services.
/// 
/// Defines the contract for interacting with language model providers:
/// - Check provider availability
/// - Execute text completions
/// - Execute vision-based completions
/// - Execute multi-frame and video analysis
/// 
/// Implementations (in Infrastructure layer) handle:
/// - Provider-specific API integration
/// - Model lifecycle management
/// - Request/response formatting
/// - Error handling and retries
/// 
/// Used by agents and background services that need LLM capabilities
/// without depending on concrete infrastructure implementations.
/// </summary>
public interface IProviderFactory
{
    /// <summary>
    /// Returns true if the provider is configured and available.
    /// </summary>
    /// <param name="provider">Provider name (e.g., "ollama", "openai", "anthropic").</param>
    /// <returns>True if provider is available, false otherwise.</returns>
    bool IsProviderAvailable(string provider);

    /// <summary>
    /// Executes an LLM text request and returns the model response as a string.
    /// </summary>
    /// <param name="provider">Provider name.</param>
    /// <param name="systemPrompt">System prompt to set context.</param>
    /// <param name="userPrompt">User prompt/query.</param>
    /// <param name="config">Provider-specific configuration object.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Model response text.</returns>
    Task<string> ExecuteLLMAsync(
        string provider,
        string systemPrompt,
        string userPrompt,
        object config,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Execute LLM with vision support (image analysis).
    /// </summary>
    /// <param name="provider">Provider name.</param>
    /// <param name="systemPrompt">System prompt.</param>
    /// <param name="userPrompt">User prompt/query.</param>
    /// <param name="imageBytes">Image data as bytes.</param>
    /// <param name="config">Provider-specific configuration.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Model response text.</returns>
    Task<string> ExecuteVisionAsync(
        string provider,
        string systemPrompt,
        string userPrompt,
        byte[] imageBytes,
        object config,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Execute LLM with multiple frames (for temporal context analysis).
    /// Models like Llama 3.2 Vision, Gemma 3, Qwen 2.5 VL support multiple images in one request.
    /// Falls back to single-frame (most recent) if provider doesn't support multi-image.
    /// </summary>
    /// <param name="provider">Provider name.</param>
    /// <param name="systemPrompt">System prompt.</param>
    /// <param name="userPrompt">User prompt/query.</param>
    /// <param name="frameBytes">Multiple frames as byte arrays.</param>
    /// <param name="config">Provider-specific configuration.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Model response text.</returns>
    Task<string> ExecuteVisionMultiFrameAsync(
        string provider,
        string systemPrompt,
        string userPrompt,
        IReadOnlyList<byte[]> frameBytes,
        object config,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Execute video analysis via a model container (e.g. SmolVLM2-Video in Docker).
    /// Encodes frames to video, POSTs to VIDEO_SERVICE_URL, returns response.
    /// Used when provider is "video" and VIDEO_SERVICE_URL is set.
    /// </summary>
    /// <param name="systemPrompt">System prompt.</param>
    /// <param name="userPrompt">User prompt/query.</param>
    /// <param name="frameBytes">Video frames as byte arrays.</param>
    /// <param name="config">Provider-specific configuration.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Model response text.</returns>
    Task<string> ExecuteVideoAsync(
        string systemPrompt,
        string userPrompt,
        IReadOnlyList<byte[]> frameBytes,
        object config,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Ensures Ollama is reachable and, if required, that a vision-capable model is available.
    /// Throws if unreachable or if requireVisionModel and no llava model is present.
    /// </summary>
    /// <param name="requireVisionModel">If true, requires a vision model to be available.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Task that completes when verification succeeds.</returns>
    Task EnsureOllamaReachableAsync(bool requireVisionModel, CancellationToken cancellationToken = default);
}
