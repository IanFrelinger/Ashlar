using Ashlar.Abstractions.Security.Egress;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Ashlar.AI.Pipeline.Governance;

/// <summary>
/// Fixed-order governance composition for Ashlar chat clients.
/// </summary>
public static class AshlarGovernanceChatClientBuilderExtensions
{
    /// <summary>
    /// Applies EgressGuard → PolicyGate → Sanitizing → Auditing around the inner provider client.
    /// Hosts must use this extension so middleware cannot be mis-ordered.
    /// </summary>
    /// <remarks>
    /// EgressGuard (<see cref="EgressGuardChatClient"/>, SPEC-007) is report-only: it records one egress decision per
    /// call for the destination the inner client names (<c>MeaiEgressDestination</c>; none for the in-process LLamaSharp
    /// client under <c>local:onnx</c>; when the inner client names none, the configured region's runtime endpoint, or
    /// <c>aws-bedrock</c>, for <c>cloud:bedrock:*</c>, else <c>meai:&lt;key&gt;</c>) and never refuses.
    /// It is outermost, so an attempt that PolicyGate denies is still recorded.
    /// </remarks>
    public static ChatClientBuilder UseAshlarGovernance(this ChatClientBuilder builder, string targetKey)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetKey);

        // First Use = outermost → the egress guard records the attempt before PolicyGate decides it.
        builder.Use((inner, sp) =>
            new EgressGuardChatClient(inner, MeaiEgressDestination.Resolve(targetKey, sp, inner), ResolveEgressGuard(sp), targetKey,
                sp.GetRequiredService<IChatInvocationAuditor>()));

        builder.Use((inner, sp) =>
        {
            var policy = sp.GetRequiredService<IChatTargetAccessPolicy>();
            var auditor = sp.GetRequiredService<IChatInvocationAuditor>();
            return new PolicyGateChatClient(inner, policy, auditor, targetKey);
        });

        builder.Use((inner, sp) =>
        {
            var sanitizer = sp.GetRequiredService<IChatMessageSanitizer>();
            var sanitizePolicy = sp.GetRequiredService<ITargetSanitizePolicy>();
            var auditor = sp.GetRequiredService<IChatInvocationAuditor>();
            return new SanitizingChatClient(inner, sanitizer, sanitizePolicy, auditor, targetKey);
        });

        builder.Use((inner, sp) =>
        {
            var auditor = sp.GetRequiredService<IChatInvocationAuditor>();
            return new AuditingChatClient(inner, auditor, targetKey);
        });

        return builder;
    }

    // Report-only: nothing here may stop the keyed client being built, so a failure leaves the guard at
    // EgressGuard.ProcessDefault (a null guard), as the factory handler in AddAshlarEgressGuard does.
    private static IEgressGuard? ResolveEgressGuard(IServiceProvider services)
    {
        try
        {
            return services.GetService<IEgressGuard>();
        }
        catch (Exception)
        {
            return null;
        }
    }
}
