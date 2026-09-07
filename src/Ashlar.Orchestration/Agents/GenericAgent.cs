using Microsoft.Extensions.Logging;
using Ashlar.Orchestration.Architect.Models;

namespace Ashlar.Orchestration.Agents;

/// <summary>
/// Generic agent implementation for domains without specialized agents.
/// 
/// Provides a fallback agent for domains that don't have specialized implementations.
/// Returns placeholder results based on agent specifications.
/// 
/// Used by AgentFactory when no specialized agent matches the domain.
/// Inherits from BaseAgent for lifecycle management.
/// </summary>
public sealed class GenericAgent : BaseAgent
{
    public GenericAgent(AgentSpawnSpec spec, ILogger<GenericAgent> logger)
        : base(spec, logger)
    {
    }

    protected override Task OnInitializeAsync(CancellationToken cancellationToken)
    {
        // Generic initialization - can be overridden by subclasses
        return Task.CompletedTask;
    }

    protected override Task OnDependenciesResolvedAsync(
        IReadOnlyDictionary<string, object> dependencyOutputs,
        CancellationToken cancellationToken)
    {
        // Generic dependency handling
        return Task.CompletedTask;
    }

    protected override Task<object> OnExecuteAsync(
        IReadOnlyDictionary<string, object>? dependencyOutputs,
        CancellationToken cancellationToken)
    {
        // This is the AgentFactory fallback for a domain with no specialized agent. It performs
        // NO work. It used to return Output "…completed task: {Goal}", which reads as success —
        // telemetry and callers could not distinguish a real result from a domain that simply
        // had no handler. It now logs a warning and flags the result as a placeholder so that
        // is detectable. It does NOT throw: GenericAgent is a legitimate, widely-relied-on
        // fallback (fail-loud, if wanted, belongs as an opt-in policy at the AgentFactory
        // fallback, not in every generic execution).
        Logger.LogWarning(
            "No specialized agent matched domain '{Domain}' for agent {AgentId}; GenericAgent performed no work.",
            Spec.Domain,
            Spec.AgentId);

        // A NAMED type, not an anonymous one carrying a bool: the flag was already here and
        // nothing checked it, so an orchestration made entirely of these still reported success.
        // Callers now match on the type (see OrchestrationWorkReport in the CLI). Same property
        // names, so the serialized shape is unchanged.
        var result = new PlaceholderAgentResult(
            AgentId: Spec.AgentId,
            Domain: Spec.Domain,
            Goal: Spec.Goal,
            Placeholder: true,
            Output: $"No specialized agent for domain '{Spec.Domain}'; no work performed for goal: {Spec.Goal}");

        return Task.FromResult<object>(result);
    }

    protected override Task OnShutdownAsync(CancellationToken cancellationToken)
    {
        // Generic shutdown
        return Task.CompletedTask;
    }
}

