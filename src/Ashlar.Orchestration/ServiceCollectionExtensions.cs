using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ashlar.Abstractions;
using Ashlar.Core.Application.Common.Ports;
using Ashlar.Core.Application.Common.Services;
using Ashlar.Core.Application.Resilience.Ports;
using Ashlar.Orchestration.Agents;
using Ashlar.Orchestration.Architect;
using Ashlar.Orchestration.Architect.Parsers;
using Ashlar.Orchestration.Architect.Prompts;
using Ashlar.Orchestration.Communication;
using Ashlar.Orchestration.Coordination;
using Ashlar.Orchestration.Coordination.Conflicts;
using Ashlar.Orchestration.Coordination.ErrorRecovery;
using Ashlar.Orchestration.Metrics;
using Ashlar.Orchestration.Negotiation;
using Ashlar.Orchestration.Barriers;
using Ashlar.Orchestration.Routing;
using Ashlar.Orchestration.Resources;
using Ashlar.Orchestration.Transport;
using Ashlar.Orchestration.Validation;
using Ashlar.Orchestration.Models;
using Ashlar.Orchestration.Resilience;
using Ashlar.Abstractions.Barriers;
using Ashlar.Abstractions.Routing;
using Ashlar.Abstractions.Transport;

namespace Ashlar.Orchestration;

/// <summary>
/// Extension methods for registering orchestration services.
/// 
/// Provides dependency injection registration for all orchestration components:
/// - Architect services (decomposition, validation)
/// - Agent services (factory, lifecycle, health)
/// - Communication services (message bus, channels)
/// - Coordination services (dependency resolution, conflict detection, etc.)
/// - Negotiation services (schema adapter, Pareto optimizer, etc.)
/// - Metrics and orchestrator
/// 
/// Call AddAshlarOrchestration() to register all orchestration services.
/// Register optional <c>IAgentTransportInvocationHook</c> implementations (singleton or scoped) to extend transport invocations.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds all orchestration services to the service collection.
    /// </summary>
    public static IServiceCollection AddAshlarOrchestration(this IServiceCollection services)
    {
        services.AddSingleton<IOrchestrationRuntimeSpecAccessor, OrchestrationRuntimeSpecAccessor>();

        // Loop kernel (hot paths use this; default is sequential unless host decorates).
        services.TryAddSingleton<ILoopKernel, SequentialLoopKernel>();

        // Architect
        services.AddSingleton<DomainRecognizer>();
        services.AddSingleton<DecompositionRetriever>();
        services.AddSingleton<DecompositionPromptBuilder>();
        services.AddSingleton<DecompositionJsonParser>();
        services.AddSingleton<IArchitectAgent, ArchitectAgent>();
        services.TryAddSingleton<IEndpointRegistry, EmptyEndpointRegistry>();
        services.AddSingleton<IEndpointRouter, CompositeEndpointRouter>();
        services.AddSingleton<IRoutingPolicy, CapabilityRoutingPolicy>();
        services.AddSingleton<IRoutingPolicy, BarrierRoutingPolicy>();
        services.AddSingleton<IRoutingPolicy, GeographicAffinityPolicy>();
        services.AddSingleton<IRoutingPolicy, HealthFilterPolicy>();
        services.AddSingleton<IRoutingPolicy, PrioritySelectionPolicy>();
        services.TryAddSingleton<IBarrierAuditLog, NoopBarrierAuditLog>();

        // Validation
        services.AddSingleton<IValidator, SchemaValidator>();
        services.AddSingleton<IValidator, DependencyAnalyzer>();
        services.AddSingleton<IValidator, CoverageChecker>();
        services.AddSingleton<IValidator, ConstraintSolver>();

        // Agents
        services.AddSingleton<AgentFactory>();
        services.AddSingleton<IAgentRuntimeFactory>(sp => sp.GetRequiredService<AgentFactory>());
        services.AddSingleton<LifecycleManager>();
        services.AddSingleton<HealthMonitor>();

        // Unified provisioned resources (scoped per orchestration scope / request)
        services.AddOptions<OrchestrationResourceOptions>();
        services.AddScoped(sp => new OrchestrationResourceScope(
            sp.GetRequiredService<IOptions<OrchestrationResourceOptions>>().Value.TeardownPolicy,
            sp.GetService<ILogger<OrchestrationResourceScope>>()));

        // Communication
        services.AddSingleton<IAgentBus, AgentBus>();
        services.AddSingleton<ChannelManager>();
        services.AddSingleton<MessageSchemaValidator>();
        // Coordination
        services.AddSingleton<DependencyResolver>();
        services.AddSingleton<ConflictDetector>();
        services.AddSingleton<ResourceAllocator>();
        services.AddSingleton<ProgressTracker>();
        services.AddSingleton<EscalationManager>();
        services.AddSingleton<OutputIntegrator>();
        services.AddSingleton<TimeoutManager>();
        services.AddSingleton<ErrorRecoveryManager>();

        // Negotiation
        services.AddSingleton<SchemaAdapter>();
        services.AddSingleton<ParetoOptimizer>();
        services.AddSingleton<ConstraintRelaxer>();
        services.AddSingleton<SynthesisEngine>();
        services.AddSingleton<NegotiationProtocol>();

        // Resilience (ports from Core.Application)
        // Note: IResilientExecutor is registered by the Hosting layer.
        // ICircuitBreaker implementation is in Orchestration.Resilience.
        services.AddSingleton<ICircuitBreaker>(sp => new CircuitBreaker(
            name: "orchestration-agent-transport",
            failureThreshold: 3,
            timeout: TimeSpan.FromSeconds(30),
            logger: sp.GetService<ILogger<CircuitBreaker>>()));

        // Metrics
        services.AddSingleton<OrchestrationMetrics>();

        // Orchestrator (resolve invocation hooks from DI so hosts can add policy/metadata pipelines)
        services.AddScoped(sp => new Orchestrator(
            sp.GetRequiredService<IArchitectAgent>(),
            sp.GetRequiredService<AgentFactory>(),
            sp.GetRequiredService<LifecycleManager>(),
            sp.GetRequiredService<DependencyResolver>(),
            sp.GetRequiredService<ConflictDetector>(),
            sp.GetRequiredService<ResourceAllocator>(),
            sp.GetRequiredService<ProgressTracker>(),
            sp.GetRequiredService<EscalationManager>(),
            sp.GetRequiredService<OutputIntegrator>(),
            sp.GetRequiredService<IAgentBus>(),
            sp.GetRequiredService<IAgentTransport>(),
            sp.GetRequiredService<ILoopKernel>(),
            sp.GetRequiredService<ILogger<Orchestrator>>(),
            negotiationProtocol: sp.GetService<NegotiationProtocol>(),
            metrics: sp.GetService<OrchestrationMetrics>(),
            runtimeSpecAccessor: sp.GetService<IOrchestrationRuntimeSpecAccessor>(),
            barrierContextAccessor: sp.GetService<IBarrierContextAccessor>(),
            barrierHierarchy: sp.GetService<BarrierHierarchy>(),
            barrierAuditLog: sp.GetService<IBarrierAuditLog>(),
            barrierOptions: sp.GetService<IOptions<BarrierOptions>>(),
            invocationHooks: sp.GetServices<IAgentTransportInvocationHook>(),
            resilientExecutor: sp.GetRequiredService<IResilientExecutor>(),
            circuitBreaker: sp.GetRequiredService<ICircuitBreaker>()));

        return services;
    }

    private sealed class EmptyEndpointRegistry : IEndpointRegistry
    {
        public IReadOnlyList<EndpointDescriptor> GetAll() => Array.Empty<EndpointDescriptor>();
        public void Register(EndpointDescriptor descriptor) { }
        public void UpdateHealth(string endpoint, bool isHealthy) { }
    }

    private sealed class NoopBarrierAuditLog : IBarrierAuditLog
    {
        public ValueTask RecordAsync(BarrierAuditEvent auditEvent, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
    }


    /// <summary>
    /// Adds asset generation services to the service collection.
    /// Asset adapters (3D models, images, audio) can be added later via extension packages.
    /// </summary>
    public static IServiceCollection AddAssetGenerators(this IServiceCollection services)
    {
        // Placeholder implementations are registered in the Adapters project
        // This method is kept for backward compatibility
        return services;
    }
}

