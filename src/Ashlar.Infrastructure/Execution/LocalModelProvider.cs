using LLama;
using LLama.Common;
using Microsoft.Extensions.Logging;

using Ashlar.Core.Application.Execution.Ports;

namespace Ashlar.Infrastructure.Execution;

/// <summary>
/// In-process LLM inference via LlamaSharp (GGUF models).
/// Config: ASHLAR_LOCAL_MODEL_PATH (required), ASHLAR_LOCAL_PROVIDER=llama (default).
/// </summary>
public static class LocalModelProvider
{
    private static readonly object Lock = new();
    private static LLamaWeights? _weights;
    private static LLamaContext? _context;

    /// <summary>
    /// Returns true when ASHLAR_LOCAL_MODEL_PATH is set and the model file exists.
    /// </summary>
    public static bool IsAvailable()
    {
        var path = GetModelPath();
        return !string.IsNullOrWhiteSpace(path) && File.Exists(path);
    }

    private static string? GetModelPath()
    {
        var path = Environment.GetEnvironmentVariable("ASHLAR_LOCAL_MODEL_PATH")?.Trim();
        if (string.IsNullOrWhiteSpace(path))
            return null;
        path = Environment.ExpandEnvironmentVariables(path);
        return Path.IsPathRooted(path) ? path : Path.GetFullPath(path);
    }

    /// <summary>
    /// Executes an LLM request using the local model.
    /// </summary>
    public static async Task<string> ExecuteAsync(
        string systemPrompt,
        string userPrompt,
        object? config,
        CancellationToken cancellationToken = default)
    {
        var path = GetModelPath();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new ModelUnavailableException(
                "Local model not configured. Set ASHLAR_LOCAL_MODEL_PATH to a GGUF model file (e.g. llama-3.2-3b-Q4_K_M.gguf).");

        EnsureModelLoaded(path);

        if (_weights == null || _context == null)
            throw new ModelUnavailableException("Failed to load local model. Check ASHLAR_LOCAL_MODEL_PATH.");

        var executor = new InteractiveExecutor(_context);
        var session = new ChatSession(executor);

        var fullPrompt = string.IsNullOrWhiteSpace(systemPrompt)
            ? userPrompt
            : $"{systemPrompt}\n\n{userPrompt}";

        var inferenceParams = new InferenceParams
        {
            MaxTokens = 4096,
            AntiPrompts = new List<string> { "User:", "user:" }
        };

        var sb = new System.Text.StringBuilder();
        await foreach (var token in session.ChatAsync(
            new ChatHistory.Message(AuthorRole.User, fullPrompt),
            inferenceParams,
            cancellationToken))
        {
            sb.Append(token);
        }

        return sb.ToString();
    }

    private static void EnsureModelLoaded(string path)
    {
        if (_weights != null)
            return;

        lock (Lock)
        {
            if (_weights != null)
                return;

            try
            {
                var contextSize = int.TryParse(Environment.GetEnvironmentVariable("ASHLAR_LOCAL_CONTEXT_SIZE"), out var cs) && cs > 0
                    ? cs
                    : 2048;

                var parameters = new ModelParams(path)
                {
                    ContextSize = (uint)contextSize
                };

                _weights = LLamaWeights.LoadFromFile(parameters);
                _context = _weights.CreateContext(parameters);
            }
            catch (Exception ex)
            {
                throw new ModelUnavailableException(
                    $"Failed to load local model at {path}. Ensure the file is a valid GGUF model. {ex.Message}",
                    ex);
            }
        }
    }
}
