using Ashlar.Abstractions.Security.Egress;
using Microsoft.Extensions.AI;

namespace Ashlar.AI.Pipeline.Governance;

/// <summary>
/// Outermost governance middleware (SPEC-007 PR 3): records one report-only egress decision for the target's
/// destination on every call, then delegates unchanged. It never refuses and never reads the messages, and a guard
/// that throws never reaches the caller.
/// </summary>
/// <remarks>
/// <para>It is the first <c>Use()</c> in <see cref="AshlarGovernanceChatClientBuilderExtensions.UseAshlarGovernance"/>,
/// so an attempt that <see cref="PolicyGateChatClient"/> denies is still recorded.</para>
/// <para>Both overrides are deliberately not <c>async</c>: the decision is made when the call is made, and an exception
/// an inner layer throws synchronously (PolicyGate's denial) still leaves this call synchronously, as it did before.</para>
/// <para>A <see langword="null"/> request (the in-process LLamaSharp client under <c>local:onnx</c>) delegates without
/// deciding.</para>
/// <para><b>Ollama cloud models</b> (SPEC-007 PR 4.1). A model whose id ends in <c>-cloud</c> or <c>:cloud</c>, in any
/// case, runs on ollama.com: the local Ollama daemon relays the conversation there. A call with such a model is
/// recorded as an external model at <c>https://ollama.com</c>, under the target's site, whatever the target's own
/// destination. The model is the call's <see cref="ChatOptions.ModelId"/>, or, when that is blank, the inner client's
/// <see cref="ChatClientMetadata.DefaultModelId"/>, which is the model the default Ollama client sends. The rule is
/// unconditional: a wrong guess only records a model with that ending as leaving the host.</para>
/// </remarks>
public sealed class EgressGuardChatClient : DelegatingChatClient
{
    /// <summary>Where Ollama runs a <c>-cloud</c> or <c>:cloud</c> model.</summary>
    private static readonly Uri OllamaCloud = new("https://ollama.com");

    private readonly IEgressGuard _guard;
    private readonly string? _defaultModelId;

    /// <summary>Creates the guard layer around an inner client.</summary>
    /// <param name="innerClient">The client this layer delegates to.</param>
    /// <param name="request">What each call is reported as, or <see langword="null"/> for an in-process target.</param>
    /// <param name="guard">The guard; <see langword="null"/> means <see cref="EgressGuard.ProcessDefault"/>.</param>
    public EgressGuardChatClient(IChatClient innerClient, EgressRequest? request, IEgressGuard? guard = null)
        : base(innerClient)
    {
        Request = request;
        _guard = guard ?? EgressGuard.ProcessDefault;
        _defaultModelId = DefaultModelIdOf(innerClient);
    }

    /// <summary>
    /// The request each call is reported as, unless the call's model is an Ollama cloud model; <see langword="null"/>
    /// when the target is not evaluated.
    /// </summary>
    public EgressRequest? Request { get; }

    /// <inheritdoc />
    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        Decide(options);
        return base.GetResponseAsync(messages, options, cancellationToken);
    }

    /// <inheritdoc />
    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        Decide(options);
        return base.GetStreamingResponseAsync(messages, options, cancellationToken);
    }

    /// <summary>
    /// <see langword="true"/> when <paramref name="modelId"/>, trimmed, ends in <c>-cloud</c> or <c>:cloud</c>,
    /// ignoring case: Ollama's naming for a model the local daemon relays to ollama.com.
    /// </summary>
    internal static bool IsOllamaCloudModel(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
            return false;

        var id = modelId.Trim();
        return id.EndsWith("-cloud", StringComparison.OrdinalIgnoreCase)
            || id.EndsWith(":cloud", StringComparison.OrdinalIgnoreCase);
    }

    // Report-only: the decision is discarded. EgressGuard.Evaluate never throws, but a host may register its own
    // IEgressGuard, so a fault is swallowed here as EgressGuardHandler does, and the call goes ahead unchanged.
    private void Decide(ChatOptions? options)
    {
        if (Request is null)
            return;

        try
        {
            var model = string.IsNullOrWhiteSpace(options?.ModelId) ? _defaultModelId : options!.ModelId;
            var request = IsOllamaCloudModel(model)
                ? new EgressRequest(Request.Family, Request.Site, OllamaCloud)
                : Request;
            _ = _guard.Evaluate(request);
        }
        catch (Exception)
        {
            // A host IEgressGuard threw; report-only, the call proceeds.
        }
    }

    // The model the inner client sends when a call names none. Never throws: a host's client may fail GetService, and
    // the layer is built while the keyed client is.
    private static string? DefaultModelIdOf(IChatClient innerClient)
    {
        try
        {
            return (innerClient.GetService(typeof(ChatClientMetadata)) as ChatClientMetadata)?.DefaultModelId;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
