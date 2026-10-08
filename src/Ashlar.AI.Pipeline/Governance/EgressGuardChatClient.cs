using Ashlar.Abstractions.Security;
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
/// <para><b>Peer responses (SPEC-007 PR 4.5).</b> A response from a target whose key starts with <c>peer:</c>,
/// in any case, is another agent's output. It is unlabelled until PR 5, so after the response returns — or faults —
/// the layer observes <see cref="SecurityLabel.SystemHigh"/>. The decision for this call was already recorded, at
/// the mark from before the response. Model endpoints (<c>local:</c>, <c>cloud:</c>) are not a read. With no frame,
/// the observe does nothing. An inner layer that throws synchronously, as PolicyGate does, still leaves this call
/// synchronously and is not observed: no response came back.</para>
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
    private readonly string? _targetKey;

    /// <summary>Creates the guard layer around an inner client. No target key, so a response is not a read.</summary>
    /// <param name="innerClient">The client this layer delegates to.</param>
    /// <param name="request">What each call is reported as, or <see langword="null"/> for an in-process target.</param>
    /// <param name="guard">The guard; <see langword="null"/> means <see cref="EgressGuard.ProcessDefault"/>.</param>
    public EgressGuardChatClient(IChatClient innerClient, EgressRequest? request, IEgressGuard? guard = null)
        : this(innerClient, request, guard, targetKey: null)
    {
    }

    /// <summary>Creates the guard layer around an inner client for a governed target key.</summary>
    /// <param name="innerClient">The client this layer delegates to.</param>
    /// <param name="request">What each call is reported as, or <see langword="null"/> for an in-process target.</param>
    /// <param name="guard">The guard; <see langword="null"/> means <see cref="EgressGuard.ProcessDefault"/>.</param>
    /// <param name="targetKey">
    /// The governed chat target. A key that starts with <c>peer:</c> observes <see cref="SecurityLabel.SystemHigh"/>
    /// after a response. Model endpoints are not reads. Required, so this overload does not collide with the
    /// three-argument constructor.
    /// </param>
    public EgressGuardChatClient(IChatClient innerClient, EgressRequest? request, IEgressGuard? guard, string? targetKey)
        : base(innerClient)
    {
        Request = request;
        _guard = guard ?? EgressGuard.ProcessDefault;
        _defaultModelId = DefaultModelIdOf(innerClient);
        _targetKey = targetKey;
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
        var pending = base.GetResponseAsync(messages, options, cancellationToken);
        return ObservesAgentBackedResponse ? ObserveAfterAsync(pending) : pending;
    }

    /// <inheritdoc />
    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        Decide(options);
        var pending = base.GetStreamingResponseAsync(messages, options, cancellationToken);
        return ObservesAgentBackedResponse ? ObserveStream(pending) : pending;
    }

    // A peer response is another agent's output. Model endpoints are not. Request null (in-process local:onnx)
    // is not a response that left a model endpoint we record, and it is not observed.
    private bool ObservesAgentBackedResponse =>
        Request is not null
        && !string.IsNullOrWhiteSpace(_targetKey)
        && _targetKey.Trim().StartsWith("peer:", StringComparison.OrdinalIgnoreCase);

    private static async Task<ChatResponse> ObserveAfterAsync(Task<ChatResponse> pending)
    {
        try
        {
            return await pending.ConfigureAwait(false);
        }
        finally
        {
            EgressSubject.Observe(SecurityLabel.SystemHigh);
        }
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> ObserveStream(IAsyncEnumerable<ChatResponseUpdate> pending)
    {
        try
        {
            await foreach (var update in pending.ConfigureAwait(false))
                yield return update;
        }
        finally
        {
            EgressSubject.Observe(SecurityLabel.SystemHigh);
        }
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
