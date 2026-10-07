using System.Runtime.CompilerServices;
using System.Text;
using LLama;
using LLama.Common;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Ashlar.AI.Pipeline.Clients;

/// <summary>
/// <see cref="IChatClient"/> adapter over in-process LLamaSharp GGUF inference.
/// Registered under keyed DI name <c>local:onnx</c> (see migration notes: not ONNX Runtime).
/// </summary>
public sealed class LlamaSharpChatClient : IChatClient
{
    private static readonly object Gate = new();
    private static LLamaWeights? s_weights;
    private static LLamaContext? s_context;
    private static string? s_loadedPath;

    private readonly MeaiPipelineOptions _options;

    /// <summary>Creates a LLamaSharp-backed chat client.</summary>
    public LlamaSharpChatClient(IOptions<MeaiPipelineOptions> options)
    {
        _options = options.Value;
    }

    /// <inheritdoc />
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var text = await CompleteAsync(messages, options, cancellationToken).ConfigureAwait(false);
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, text))
        {
            ModelId = options?.ModelId ?? "llamasharp-gguf",
        };
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // LocalModelProvider-style inference is session-based; surface as a single streamed chunk.
        var text = await CompleteAsync(messages, options, cancellationToken).ConfigureAwait(false);
        yield return new ChatResponseUpdate(ChatRole.Assistant, text)
        {
            ModelId = options?.ModelId ?? "llamasharp-gguf",
        };
    }

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        if (serviceType == typeof(ChatClientMetadata))
        {
            return new ChatClientMetadata("llamasharp", providerUri: null, defaultModelId: "llamasharp-gguf");
        }

        return serviceType.IsInstanceOfType(this) ? this : null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // Weights/context are process-scoped (shared with Hosting LocalModelProvider pattern).
    }

    private async Task<string> CompleteAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        CancellationToken cancellationToken)
    {
        var path = ResolveModelPath();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            throw new InvalidOperationException(
                "Local model not configured. Set Ashlar:Meai:LocalModelPath or ASHLAR_LOCAL_MODEL_PATH to a GGUF file.");
        }

        EnsureLoaded(path, _options.LocalContextSize);

        if (s_weights is null || s_context is null)
        {
            throw new InvalidOperationException("Failed to load local GGUF model.");
        }

        var prompt = BuildPrompt(messages);
        var executor = new InteractiveExecutor(s_context);
        var session = new ChatSession(executor);
        var maxTokens = options?.MaxOutputTokens ?? _options.LocalMaxTokens;
        var inferenceParams = new InferenceParams
        {
            MaxTokens = maxTokens,
            AntiPrompts = new List<string> { "User:", "user:" },
        };

        var sb = new StringBuilder();
        await foreach (var token in session.ChatAsync(
            new ChatHistory.Message(AuthorRole.User, prompt),
            inferenceParams,
            cancellationToken).ConfigureAwait(false))
        {
            sb.Append(token);
        }

        return sb.ToString();
    }

    private string? ResolveModelPath()
    {
        var path = _options.LocalModelPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            path = Environment.GetEnvironmentVariable("ASHLAR_LOCAL_MODEL_PATH");
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        path = Environment.ExpandEnvironmentVariables(path.Trim());
        return Path.IsPathRooted(path) ? path : Path.GetFullPath(path);
    }

    private static string BuildPrompt(IEnumerable<ChatMessage> messages)
    {
        var sb = new StringBuilder();
        foreach (var message in messages)
        {
            var role = message.Role == ChatRole.System ? "System"
                : message.Role == ChatRole.Assistant ? "Assistant"
                : "User";
            sb.Append(role).Append(": ").Append(message.Text).Append('\n');
        }

        return sb.ToString();
    }

    private static void EnsureLoaded(string path, int contextSize)
    {
        if (s_weights is not null && string.Equals(s_loadedPath, path, StringComparison.Ordinal))
        {
            return;
        }

        lock (Gate)
        {
            if (s_weights is not null && string.Equals(s_loadedPath, path, StringComparison.Ordinal))
            {
                return;
            }

            var parameters = new ModelParams(path)
            {
                ContextSize = (uint)Math.Max(256, contextSize),
            };
            s_weights = LLamaWeights.LoadFromFile(parameters);
            s_context = s_weights.CreateContext(parameters);
            s_loadedPath = path;
        }
    }
}

/// <summary>
/// Whether a chat client runs in process. It lives in this file because the egress convention (F5) keeps the
/// <see cref="LlamaSharpChatClient"/> type name confined here and to the governed registrations; the egress destination
/// resolver asks through this helper, by type, never by the provider name a client reports (SPEC-007 PR 4.1).
/// </summary>
internal static class InProcessChatClient
{
    /// <summary>
    /// <see langword="true"/> when <paramref name="client"/> is, or delegates to, the in-process
    /// <see cref="LlamaSharpChatClient"/>, which loads a local GGUF file and sends nothing out of the process.
    /// </summary>
    internal static bool IsLlamaSharp(IChatClient client) =>
        client.GetService(typeof(LlamaSharpChatClient)) is LlamaSharpChatClient;
}
