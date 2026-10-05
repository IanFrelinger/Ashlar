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
/// <para>A <see langword="null"/> request (the in-process <c>local:onnx</c> target) delegates without deciding.</para>
/// </remarks>
public sealed class EgressGuardChatClient : DelegatingChatClient
{
    private readonly IEgressGuard _guard;

    /// <summary>Creates the guard layer around an inner client.</summary>
    /// <param name="innerClient">The client this layer delegates to.</param>
    /// <param name="request">What each call is reported as, or <see langword="null"/> for an in-process target.</param>
    /// <param name="guard">The guard; <see langword="null"/> means <see cref="EgressGuard.ProcessDefault"/>.</param>
    public EgressGuardChatClient(IChatClient innerClient, EgressRequest? request, IEgressGuard? guard = null)
        : base(innerClient)
    {
        Request = request;
        _guard = guard ?? EgressGuard.ProcessDefault;
    }

    /// <summary>The request each call is reported as; <see langword="null"/> when the target is not evaluated.</summary>
    public EgressRequest? Request { get; }

    /// <inheritdoc />
    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        Decide();
        return base.GetResponseAsync(messages, options, cancellationToken);
    }

    /// <inheritdoc />
    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        Decide();
        return base.GetStreamingResponseAsync(messages, options, cancellationToken);
    }

    // Report-only: the decision is discarded. EgressGuard.Evaluate never throws, but a host may register its own
    // IEgressGuard, so a fault is swallowed here as EgressGuardHandler does, and the call goes ahead unchanged.
    private void Decide()
    {
        if (Request is null)
            return;

        try
        {
            _ = _guard.Evaluate(Request);
        }
        catch (Exception)
        {
            // A host IEgressGuard threw; report-only, the call proceeds.
        }
    }
}
