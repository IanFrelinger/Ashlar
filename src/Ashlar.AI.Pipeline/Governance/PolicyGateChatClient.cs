using Microsoft.Extensions.AI;

namespace Ashlar.AI.Pipeline.Governance;

/// <summary>
/// Outermost enforcing middleware, just inside the report-only <see cref="EgressGuardChatClient"/>: denies targets not
/// permitted by the access policy.
/// </summary>
public sealed class PolicyGateChatClient : DelegatingChatClient
{
    private readonly IChatTargetAccessPolicy _policy;
    private readonly IChatInvocationAuditor _auditor;
    private readonly string _targetKey;
    private readonly Func<string?>? _callerIdentityAccessor;

    /// <summary>Creates a policy gate around an inner client.</summary>
    public PolicyGateChatClient(
        IChatClient innerClient,
        IChatTargetAccessPolicy policy,
        IChatInvocationAuditor auditor,
        string targetKey,
        Func<string?>? callerIdentityAccessor = null)
        : base(innerClient)
    {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _auditor = auditor ?? throw new ArgumentNullException(nameof(auditor));
        _targetKey = targetKey ?? throw new ArgumentNullException(nameof(targetKey));
        _callerIdentityAccessor = callerIdentityAccessor;
    }

    /// <summary>Target key this gate protects.</summary>
    public string TargetKey => _targetKey;

    /// <inheritdoc />
    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        EnsureAllowed(options);
        return base.GetResponseAsync(messages, options, cancellationToken);
    }

    /// <inheritdoc />
    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        EnsureAllowed(options);
        return base.GetStreamingResponseAsync(messages, options, cancellationToken);
    }

    private void EnsureAllowed(ChatOptions? options)
    {
        var caller = _callerIdentityAccessor?.Invoke();
        if (_policy.IsAllowed(caller, _targetKey, options?.ModelId, out var reason))
        {
            return;
        }

        _auditor.Record(new ChatInvocationAuditRecord
        {
            TargetKey = _targetKey,
            ModelId = options?.ModelId,
            Outcome = "denied",
            PolicyDecisions = new[] { $"target={_targetKey}", "decision=deny" },
            ReasonCode = "target_denied",
        });

        throw new PolicyViolationException(
            code: "target_denied",
            message: reason ?? $"Target '{_targetKey}' is not permitted.",
            targetKey: _targetKey,
            modelId: options?.ModelId);
    }
}
