using System.Runtime.CompilerServices;
using Ashlar.Abstractions.Security;
using Ashlar.Abstractions.Security.Egress;
using Microsoft.Extensions.AI;

namespace Ashlar.AI.Pipeline.Governance;

/// <summary>
/// Outermost governance middleware: decides egress for the target before calling the inner client or creating
/// its stream. Egress refusal follows the process mode; response reads always require a trusted clearance.
/// It never reads the messages. Host guard faults become NoDecision records under the process mode.
/// </summary>
/// <remarks>
/// <para>It is the first <c>Use()</c> in <see cref="AshlarGovernanceChatClientBuilderExtensions.UseAshlarGovernance"/>,
/// so an attempt that <see cref="PolicyGateChatClient"/> denies is still recorded.</para>
/// <para>Both overrides are deliberately not <c>async</c>: the decision is made when the call is made, and an exception
/// an inner layer throws synchronously still leaves this call synchronously, with unreadable failure data redacted.</para>
/// <para>A <see langword="null"/> request (the in-process LLamaSharp client under <c>local:onnx</c>) delegates without
/// deciding.</para>
/// <para><b>Ollama cloud models</b> (SPEC-007 PR 4.1). A model whose id ends in <c>-cloud</c> or <c>:cloud</c>, in any
/// case, runs on ollama.com: the local Ollama daemon relays the conversation there. A call with such a model is
/// recorded as an external model at <c>https://ollama.com</c>, under the target's site, whatever the target's own
/// destination. The model is the call's <see cref="ChatOptions.ModelId"/>, or, when that is blank, the inner client's
/// <see cref="ChatClientMetadata.DefaultModelId"/>, which is the model the default Ollama client sends. The rule is
/// unconditional: a wrong guess only records a model with that ending as leaving the host.</para>
/// <para><b>A response from an agent-backed target is a read</b> (SPEC-007 PR 4.5). A model's response is derived
/// from the prompt, which the caller already holds, so it is not a read. A <c>peer:</c> target, or any target whose key
/// names no model endpoint (neither <c>local:</c> nor <c>cloud:</c>), may be backed by an agent whose own data is not
/// derived from our prompt. PR 5 requires the caller's live policy clearance before a response or update is
/// exposed. Only an operator-trusted <see cref="IChatResponseLabelVerifier"/> can attest a lower data label;
/// absent or invalid verification means SystemHigh. Faults and abandoned streams are observed conservatively.
/// Host clients under local/cloud keys remain the trusted model boundary. This does not relax listener policy.</para>
/// </remarks>
public sealed class EgressGuardChatClient : DelegatingChatClient
{
    /// <summary>Where Ollama runs a <c>-cloud</c> or <c>:cloud</c> model.</summary>
    private static readonly Uri OllamaCloud = new("https://ollama.com");

    private readonly IEgressGuard _guard;
    private readonly string? _defaultModelId;
    private readonly bool _responsesAreReads;
    private readonly IChatInvocationAuditor? _auditor;
    private readonly string? _targetKey;
    private readonly SubjectClearanceAuthority _clearances = new(new Dictionary<string, string?>());
    private readonly IChatResponseLabelVerifier? _responseLabels;

    /// <summary>Creates the legacy targetless guard layer around an inner client.</summary>
    /// <remarks>This overload does not classify responses as reads. Governed target construction uses the
    /// four-argument overload so unknown keys are treated conservatively.</remarks>
    /// <param name="innerClient">The client this layer delegates to.</param>
    /// <param name="request">What each call is reported as, or <see langword="null"/> for an in-process target.</param>
    /// <param name="guard">The guard; <see langword="null"/> means <see cref="EgressGuard.ProcessDefault"/>.</param>
    public EgressGuardChatClient(IChatClient innerClient, EgressRequest? request, IEgressGuard? guard = null)
        : this(innerClient, request, guard, responsesAreReads: false)
    {
    }

    /// <summary>Creates the guard layer around the inner client of the target <paramref name="targetKey"/>.</summary>
    public EgressGuardChatClient(IChatClient innerClient, EgressRequest? request, IEgressGuard? guard, string? targetKey)
        : this(innerClient, request, guard, IsAgentBacked(targetKey))
    {
        _targetKey = targetKey;
    }

    /// <summary>Creates a governed target with an auditor that also receives refusals before inner middleware runs.</summary>
    public EgressGuardChatClient(IChatClient innerClient, EgressRequest? request, IEgressGuard? guard,
        string? targetKey, IChatInvocationAuditor? auditor)
        : this(innerClient, request, guard, targetKey)
    {
        _auditor = auditor;
    }

    /// <summary>Creates a governed target with trusted host clearance and response attestation services.</summary>
    public EgressGuardChatClient(IChatClient innerClient, EgressRequest? request, IEgressGuard? guard,
        string? targetKey, IChatInvocationAuditor? auditor, SubjectClearanceAuthority? clearances,
        IChatResponseLabelVerifier? responseLabels = null)
        : this(innerClient, request, guard, targetKey, auditor)
    {
        _clearances = clearances ?? _clearances;
        _responseLabels = responseLabels;
    }

    private EgressGuardChatClient(IChatClient innerClient, EgressRequest? request, IEgressGuard? guard, bool responsesAreReads)
        : base(innerClient)
    {
        Request = request;
        _guard = guard ?? EgressGuard.ProcessDefault;
        _defaultModelId = DefaultModelIdOf(innerClient);
        _responsesAreReads = responsesAreReads;
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
        if (!_responsesAreReads)
            return base.GetResponseAsync(messages, options, cancellationToken);

        Task<ChatResponse> call;
        try
        {
            call = base.GetResponseAsync(messages, options, cancellationToken);
        }
        catch (Exception error)
        {
            ObserveFailure(error, cancellationToken);
            throw;
        }

        return ObservedAsync(call, cancellationToken);
    }

    /// <inheritdoc />
    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        Decide(options);
        if (!_responsesAreReads)
            return base.GetStreamingResponseAsync(messages, options, cancellationToken);

        IAsyncEnumerable<ChatResponseUpdate> updates;
        try
        {
            updates = base.GetStreamingResponseAsync(messages, options, cancellationToken);
        }
        catch (Exception error)
        {
            ObserveFailure(error, cancellationToken);
            throw;
        }

        return ObservedAsync(updates, cancellationToken);
    }

    /// <summary>
    /// <see langword="true"/> unless <paramref name="targetKey"/> names a model endpoint (<c>local:</c> or
    /// <c>cloud:</c>, ignoring case): a <c>peer:</c> target, or one of no known kind, may be backed by an agent.
    /// </summary>
    internal static bool IsAgentBacked(string? targetKey)
    {
        var key = targetKey?.Trim();
        return key is null
            || !(key.StartsWith("local:", StringComparison.OrdinalIgnoreCase)
                || key.StartsWith("cloud:", StringComparison.OrdinalIgnoreCase));
    }

    // Faults and incomplete streams have no verified result label.
    private static void ObserveResponse() => EgressSubject.Observe(SecurityLabel.SystemHigh);

    private void ObserveFailure(Exception error, CancellationToken cancellationToken)
    {
        ObserveResponse();
        if (error is OperationCanceledException && cancellationToken.IsCancellationRequested)
            throw new OperationCanceledException(cancellationToken);
        RequireRead(SecurityLabel.SystemHigh);
    }

    private SecurityLabel VerifiedLabel(object response)
    {
        try
        {
            var text = response switch
            {
                ChatResponse complete => _responseLabels?.Verify(_targetKey, complete),
                ChatResponseUpdate update => _responseLabels?.Verify(_targetKey, update),
                _ => null,
            };
            return SecurityLabel.TryParse(text, out var label) ? label : SecurityLabel.SystemHigh;
        }
        catch (Exception)
        {
            return SecurityLabel.SystemHigh;
        }
    }

    private void RequireRead(SecurityLabel label)
    {
        var decision = ReferenceMonitor.CanRead(_clearances.ResolveCurrent(), label);
        if (decision.Allowed)
            return;

        var refusal = new ReadRefusedException(decision.Reason);
        var reference = refusal.Ref;
        try
        {
            _auditor?.Record(new ChatInvocationAuditRecord
            {
                TargetKey = _targetKey ?? string.Empty,
                Outcome = "denied",
                ReasonCode = "read_refused",
                CorrelationId = reference,
                ReadDecision = decision,
            });
        }
        catch (Exception) { /* Audit failure cannot release a refused response. */ }
        throw refusal;
    }

    private async Task<ChatResponse> ObservedAsync(Task<ChatResponse> call, CancellationToken cancellationToken)
    {
        ChatResponse response;
        try
        {
            response = await call.ConfigureAwait(false);
        }
        catch (Exception error)
        {
            ObserveFailure(error, cancellationToken);
            throw;
        }
        cancellationToken.ThrowIfCancellationRequested();
        var label = VerifiedLabel(response);
        RequireRead(label);
        cancellationToken.ThrowIfCancellationRequested();
        EgressSubject.Observe(label);
        return response;
    }

    // Each MoveNext executes on the consumer's live flow, so an expired or narrowed grant is checked again.
    private async IAsyncEnumerable<ChatResponseUpdate> ObservedAsync(
        IAsyncEnumerable<ChatResponseUpdate> updates,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        IAsyncEnumerator<ChatResponseUpdate>? iterator = null;
        bool completed = false;
        bool sawUpdate = false;
        try
        {
            try { iterator = updates.GetAsyncEnumerator(cancellationToken); }
            catch (Exception error) { ObserveFailure(error, cancellationToken); throw; }
            while (true)
            {
                bool moved;
                try { moved = await iterator.MoveNextAsync().ConfigureAwait(false); }
                catch (Exception error) { ObserveFailure(error, cancellationToken); throw; }
                if (!moved)
                {
                    if (!sawUpdate)
                    {
                        ObserveResponse();
                        RequireRead(SecurityLabel.SystemHigh);
                    }
                    completed = true;
                    break;
                }
                ChatResponseUpdate update;
                try { update = iterator.Current; }
                catch (Exception error) { ObserveFailure(error, cancellationToken); throw; }
                cancellationToken.ThrowIfCancellationRequested();
                var label = VerifiedLabel(update);
                RequireRead(label);
                cancellationToken.ThrowIfCancellationRequested();
                EgressSubject.Observe(label);
                sawUpdate = true;
                yield return update;
            }
        }
        finally
        {
            if (!completed) ObserveResponse();
            if (iterator is not null)
            {
                try { await iterator.DisposeAsync().ConfigureAwait(false); }
                catch
                {
                    ObserveResponse();
                    // Never replace a pending read refusal or cancellation with a peer's disposal payload.
                    if (completed)
                    {
                        RequireRead(SecurityLabel.SystemHigh);
                        throw;
                    }
                }
            }
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

    // Decide before asking the inner client for a task or stream, without enumerating the messages.
    private void Decide(ChatOptions? options)
    {
        if (Request is null)
            return;

        var model = string.IsNullOrWhiteSpace(options?.ModelId) ? _defaultModelId : options!.ModelId;
        var request = IsOllamaCloudModel(model)
            ? new EgressRequest(Request.Family, Request.Site, OllamaCloud)
            : Request;
        var decision = EgressGuard.EvaluateForRoute(_guard, request);
        if (decision.Refused)
        {
            try
            {
                _auditor?.Record(new ChatInvocationAuditRecord
                {
                    TargetKey = _targetKey ?? string.Empty,
                    ModelId = model,
                    Outcome = "denied",
                    ReasonCode = "egress_refused",
                    EgressDecision = decision,
                });
            }
            catch (Exception)
            {
                // A faulty audit sink must not replace or suppress the policy refusal.
            }
        }
        decision.ThrowIfRefused();
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
