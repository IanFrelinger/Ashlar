using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Ashlar.Abstractions;
using Ashlar.Abstractions.Agents;
using Ashlar.Orchestration.Architect.Models;
using Ashlar.Orchestration.Agents.Planning;
using Ashlar.Orchestration.Agents.Templates;
using Ashlar.Orchestration.Models;
using Ashlar.Core.Application.Orchestration.Ports;

namespace Ashlar.Orchestration.Agents;

/// <summary>
/// Factory for creating specialized agents from AgentSpawnSpec.
/// 
/// Responsibilities:
/// - Asks registered <see cref="IDomainAgentProvider"/>s first, so domains the kernel
///   does not know about (game domains, for instance) can be supplied by a package
/// - Creates appropriate agent type based on domain for the kernel's built-in domains
/// - Resolves dependencies from service provider
/// - Falls back to GenericAgent for unknown domains
/// 
/// Uses dependency injection to resolve agent dependencies (loggers, adapters, etc.).
/// </summary>
public sealed class AgentFactory : IAgentRuntimeFactory, IAgentCreationContext, IAgentCreator
{
    private readonly ILogger<AgentFactory> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly IReadOnlyList<IDomainAgentProvider> _providers;

    /// <summary>
    /// Initializes a new instance of the <see cref="AgentFactory"/> class.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="serviceProvider">The service provider for dependency injection.</param>
    /// <param name="domainAgentProviders">
    /// Providers supplying agents for domains the kernel does not know about — see
    /// <see cref="IDomainAgentProvider"/>. Optional so the many direct
    /// <c>new AgentFactory(logger, sp)</c> call sites keep compiling; DI resolves it to an
    /// empty sequence when nothing is registered.
    /// </param>
    public AgentFactory(
        ILogger<AgentFactory> logger,
        IServiceProvider serviceProvider,
        IEnumerable<IDomainAgentProvider>? domainAgentProviders = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _providers = domainAgentProviders?.ToList() ?? (IReadOnlyList<IDomainAgentProvider>)Array.Empty<IDomainAgentProvider>();
    }

    /// <inheritdoc />
    IServiceProvider IAgentCreationContext.Services => _serviceProvider;

    /// <inheritdoc />
    ILogger<BaseAgent> IAgentCreationContext.BaseLogger =>
        _serviceProvider.GetService(typeof(ILogger<BaseAgent>)) as ILogger<BaseAgent>
            ?? throw new InvalidOperationException("ILogger<BaseAgent> not registered");

    /// <inheritdoc />
    IModel IAgentCreationContext.ResolveModel(AgentSpawnSpec spec) =>
        WrapModel(spec, _serviceProvider.GetService<IModel>());

    /// <summary>
    /// Creates a BaseAgent instance from an AgentSpawnSpec.
    /// 
    /// Determines agent type based on domain and creates appropriate specialized agent:
    /// - Any domain claimed by a registered <see cref="IDomainAgentProvider"/> → that provider
    /// - Planning domains → PlanningAgent
    /// - Unknown domains → GenericAgent (fallback)
    /// </summary>
    /// <param name="spec">Agent spawn specification from Architect</param>
    /// <returns>Specialized agent instance</returns>
    /// <exception cref="ArgumentNullException">Thrown if spec is null</exception>
    public BaseAgent CreateAgent(AgentSpawnSpec spec)
    {
        if (spec == null)
        {
            throw new ArgumentNullException(nameof(spec));
        }

        _logger.LogInformation("Creating agent {AgentId} for domain {Domain}", spec.AgentId, spec.Domain);

        // Registered providers first, so a package can supply domains the kernel does not
        // know about (Playtest lives here now) and can also override a built-in if needed.
        foreach (var provider in _providers)
        {
            if (!provider.Handles(spec.Domain))
            {
                continue;
            }

            _logger.LogDebug(
                "Domain {Domain} claimed by provider {Provider}",
                spec.Domain,
                provider.GetType().Name);
            return provider.Create(spec, this);
        }


        // Check if this is a planning agent
        if (IsPlanningDomain(spec.Domain))
        {
            return CreatePlanningAgent(spec);
        }

        // Create domain-specific agent based on domain
        var agent = spec.Domain.ToLowerInvariant() switch
        {
            "infrastructure" => CreateInfrastructureAgent(spec),
            "security" => CreateSecurityAgent(spec),
            _ => CreateGenericAgent(spec)
        };

        return agent;
    }

    // TEMP P1.2 — delete in thin app PR
    // Creates a BaseAgent from AgentSpawnSpecDto (Application layer DTO).
    // Maps DTO to AgentSpawnSpec and delegates to CreateAgent(AgentSpawnSpec).
    /// <summary>
    /// Creates a BaseAgent from AgentSpawnSpecDto (Application layer DTO).
    /// Maps DTO to AgentSpawnSpec and delegates to CreateAgent(AgentSpawnSpec).
    /// </summary>
    public IAgent CreateAgent(AgentSpawnSpecDto dto)
    {
        if (dto == null)
        {
            throw new ArgumentNullException(nameof(dto));
        }

        // Map DTO to full AgentSpawnSpec
        var spec = new AgentSpawnSpec
        {
            AgentId = dto.AgentId,
            Name = dto.Name,
            Domain = dto.Domain,
            Goal = dto.Goal,
            Description = dto.Description,
            Dependencies = dto.Dependencies,
            OllamaModel = dto.OllamaModel
        };

        return CreateAgent(spec);
    }

    /// <summary>
    /// Creates an AgentContainer wrapping the agent.
    /// 
    /// Convenience method that creates both the agent and its container in one call.
    /// The container provides additional lifecycle management and execution coordination.
    /// </summary>
    /// <param name="spec">Agent spawn specification from Architect.</param>
    /// <returns>An AgentContainer wrapping the created agent.</returns>
    /// <exception cref="InvalidOperationException">Thrown if required services are not registered.</exception>
    public AgentContainer CreateContainer(AgentSpawnSpec spec)
    {
        var agent = CreateAgent(spec);
        var containerLogger = _serviceProvider.GetService(typeof(ILogger<AgentContainer>)) as ILogger<AgentContainer>
            ?? throw new InvalidOperationException("ILogger<AgentContainer> not registered");
        return new AgentContainer(agent, containerLogger);
    }

    /// <inheritdoc />
    public IAgentHandle Spawn(AgentSpawnSpec spec) => new InProcessAgentHandle(CreateContainer(spec));

    private BaseAgent CreateInfrastructureAgent(AgentSpawnSpec spec)
    {
        var logger = _serviceProvider.GetService(typeof(ILogger<InfrastructureAgent>)) as ILogger<InfrastructureAgent>
            ?? throw new InvalidOperationException("ILogger<InfrastructureAgent> not registered");
        var model = WrapModel(spec, _serviceProvider.GetService<IModel>());
        return new InfrastructureAgent(spec, logger, model);
    }

    private BaseAgent CreateSecurityAgent(AgentSpawnSpec spec)
    {
        var logger = _serviceProvider.GetService(typeof(ILogger<SecurityAgent>)) as ILogger<SecurityAgent>
            ?? throw new InvalidOperationException("ILogger<SecurityAgent> not registered");
        var model = WrapModel(spec, _serviceProvider.GetService<IModel>());
        return new SecurityAgent(spec, logger, model);
    }

    private BaseAgent CreateGenericAgent(AgentSpawnSpec spec)
    {
        var logger = _serviceProvider.GetService(typeof(ILogger<GenericAgent>)) as ILogger<GenericAgent>
            ?? throw new InvalidOperationException("ILogger<GenericAgent> not registered");
        return new GenericAgent(spec, logger);
    }

    private bool IsPlanningDomain(string domain)
    {
        var planningDomains = new[] { "planning", "guide", "assistant", "wizard" };
        return planningDomains.Contains(domain.ToLowerInvariant());
    }

    private BaseAgent CreatePlanningAgent(AgentSpawnSpec spec)
    {
        var baseLogger = _serviceProvider.GetService(typeof(ILogger<BaseAgent>)) as ILogger<BaseAgent>
            ?? throw new InvalidOperationException("ILogger<BaseAgent> not registered");
        var model = WrapModel(spec, _serviceProvider.GetService<IModel>());

        return new PlanningAgent(
            spec,
            baseLogger,
            model);
    }

    private IModel WrapModel(AgentSpawnSpec spec, IModel? model)
    {
        var logger = _serviceProvider.GetService(typeof(ILogger<OrchestrationHotSwappableModel>)) as ILogger<OrchestrationHotSwappableModel>
            ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<OrchestrationHotSwappableModel>.Instance;
        var baseModel = new OrchestrationHotSwappableModel(model, logger);

        var accessor = _serviceProvider.GetService(typeof(IOrchestrationRuntimeSpecAccessor)) as IOrchestrationRuntimeSpecAccessor;
        var runtime = accessor?.Current.Resolve(spec.AgentId, spec.Domain) ?? new ModelRuntimeSpec();
        if (!string.IsNullOrWhiteSpace(spec.OllamaModel) &&
            (string.IsNullOrWhiteSpace(runtime.Provider) || string.Equals(runtime.Provider, "ollama", StringComparison.OrdinalIgnoreCase)))
        {
            runtime = runtime with
            {
                Provider = "ollama",
                Model = spec.OllamaModel
            };
        }

        return new AgentScopedModel(baseModel, spec.AgentId, spec.Domain, runtime);
    }
}

