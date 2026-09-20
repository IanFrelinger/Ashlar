using Microsoft.Extensions.Logging;
using Ashlar.Abstractions;

namespace Ashlar.Mcp.Server;

/// <summary>
/// Authorization gate for MCP-originated tool calls.
///
/// MCP requests bypass <c>AgentHost.StepAsync</c> — the only place the kernel applies
/// <see cref="IPolicy"/> approval today — so the bridge re-applies policy here. The allowlist in
/// <see cref="AshlarMcpServerOptions.ExposedToolIds"/> is the primary (deny-by-default) gate; this
/// interface layers policy decisions on top of it.
/// </summary>
public interface IMcpInvocationGate
{
    /// <summary>
    /// Authorizes one tool call. <paramref name="call"/> carries the canonical Ashlar tool id, so
    /// policies written against ids like <c>repo.fs.write</c> match exactly as they would for
    /// agent-originated calls.
    /// </summary>
    McpGateDecision Authorize(ToolCall call, WorldSnapshot snapshot);
}

/// <summary>Outcome of a gate check.</summary>
/// <param name="Allowed">Whether the call may proceed.</param>
/// <param name="Reason">Denial reason (present when <paramref name="Allowed"/> is false).</param>
public readonly record struct McpGateDecision(bool Allowed, string? Reason)
{
    /// <summary>An approval.</summary>
    public static McpGateDecision Allow() => new(true, null);

    /// <summary>A denial with a caller-visible reason.</summary>
    public static McpGateDecision Deny(string reason) => new(false, reason);
}

/// <summary>
/// Default gate: every DI-registered <see cref="IPolicy"/> must approve (logical AND), mirroring
/// the all-must-approve semantics of <c>PolicyEngine</c> in <c>AgentHost</c>.
///
/// <para>An empty policy set allows a READ and refuses a WRITE. No host registers an
/// <see cref="IPolicy"/> in DI today, so this gate's set is empty in every shipped composition; the
/// governance floor inside the write tools bounds WHAT such a call may write, but nothing then bounds
/// WHERE it lands. Rather than let a host that lists <c>repo.fs.write</c> in
/// <c>AshlarMcpServerOptions.ExposedToolIds</c> get an unbounded write edge silently, the gate refuses
/// the mutating ids until a write policy is registered — a configuration gap that announces itself.
/// Non-write ids keep the permissive behaviour: the allowlist has already constrained what is
/// reachable. Hosts opt into stricter behaviour by registering policies or replacing this gate
/// (TryAdd registration).</para>
/// </summary>
public sealed class PolicyMcpInvocationGate : IMcpInvocationGate
{
    /// <summary>
    /// The tool ids that mutate the repository. Literal here because this assembly references only
    /// <c>Ashlar.Abstractions</c>; the inventory of write tools is frozen by
    /// <c>WriteToolResolverConventionTests</c>.
    /// </summary>
    private static readonly HashSet<string> WriteToolIds = new(StringComparer.Ordinal)
    {
        "repo.fs.write",
        "repo.fs.search_replace",
        "repo.fs.ensure_file",
        "docs.update",
        "repo.git.commit",
    };

    private readonly IReadOnlyList<IPolicy> _policies;
    private readonly ILogger<PolicyMcpInvocationGate> _logger;

    /// <summary>Creates the gate over all DI-registered policies.</summary>
    public PolicyMcpInvocationGate(IEnumerable<IPolicy> policies, ILogger<PolicyMcpInvocationGate> logger)
    {
        _policies = policies.ToList();
        _logger = logger;
    }

    /// <inheritdoc />
    public McpGateDecision Authorize(ToolCall call, WorldSnapshot snapshot)
    {
        if (_policies.Count == 0 && WriteToolIds.Contains(call.Id))
        {
            _logger.LogWarning(
                "MCP tool call denied: no write policy is registered. Tool={ToolId}", call.Id);
            return McpGateDecision.Deny(
                $"Denied: '{call.Id}' mutates the repository and no IPolicy is registered to bound where it may write. "
                + "Register a write policy (for example PathAllowlist) or remove the id from ExposedToolIds.");
        }

        foreach (var policy in _policies)
        {
            if (!policy.Approve(call, snapshot, out var reason))
            {
                _logger.LogWarning(
                    "MCP tool call denied by policy. Tool={ToolId} Policy={Policy} Reason={Reason}",
                    call.Id, policy.GetType().Name, reason);
                return McpGateDecision.Deny($"Denied by policy {policy.GetType().Name}: {reason}");
            }
        }

        return McpGateDecision.Allow();
    }
}
