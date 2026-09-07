using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Ashlar.Abstractions;
using Ashlar.BackgroundAgents.Configuration;
using Ashlar.BackgroundAgents.DataSensitivity;
using Ashlar.BackgroundAgents.Forge;
using Ashlar.BackgroundAgents.Logging;
using Ashlar.BackgroundAgents.Extending;
using Ashlar.BackgroundAgents.Objectives;
using Ashlar.BackgroundAgents.Observations;
using Ashlar.BackgroundAgents.Optimization;
using Ashlar.BackgroundAgents.Registry;
using Ashlar.BackgroundAgents.Testing;
using Ashlar.BackgroundAgents.RAG;
using Ashlar.BackgroundAgents.Scheduling;
using Ashlar.BackgroundAgents.Services;
using Ashlar.BackgroundAgents.Telemetry;
using Ashlar.BackgroundAgents.Tools;
using Ashlar.BackgroundAgents.Trust;
using Ashlar.BackgroundAgents.WebSearch;
using Ashlar.BackgroundAgents.Observation;
using Ashlar.Core.Application.Trust.Ports;
using Ashlar.Core.Application.Orchestration.Ports;
using Ashlar.Core.Application.Execution.Ports;
using Ashlar.Infrastructure.Observation;
using Ashlar.Infrastructure.Trust;
using Ashlar.Infrastructure.Trust.Sdk.Extensions;

namespace Ashlar.BackgroundAgents;

/// <summary>
/// Extension methods for registering background agent services.
/// </summary>
public static class ServiceCollectionExtensions
{
    private const string ObservationDegradedModeEnv = "ASHLAR_OBSERVATION_DEGRADED_MODE";

    /// <summary>
    /// Adds background agent services including scheduler, registry, and policy integration.
    /// Requires orchestration (AgentFactory, LifecycleManager for BackgroundAgentService) and configuration to be registered.
    /// </summary>
    /// <param name="registerHostedService">If true, registers BackgroundAgentService as a hosted service (default true). Set false for CLI-only usage.</param>
    public static IServiceCollection AddBackgroundAgents(this IServiceCollection services, bool registerHostedService = true)
    {
        services.TryAddSingleton<IDataSensitivityRegistry, DataSensitivityRegistry>();
        services.TryAddSingleton<IAggressivenessModeStore>(sp =>
        {
            var path = Environment.GetEnvironmentVariable("ASHLAR_AGENT_MODE_PATH");
            var logger = sp.GetService<ILogger<FileBasedAggressivenessModeStore>>();
            return string.IsNullOrWhiteSpace(path)
                ? new FileBasedAggressivenessModeStore(logger: logger)
                : new FileBasedAggressivenessModeStore(path.Trim(), logger);
        });
        services.TryAddSingleton<IBackgroundAgentLogStore, InMemoryAgentLogStore>();
        services.TryAddSingleton<IScheduleExecutor, ScheduleExecutor>();
        services.AddSingleton<IAgentScheduler, AgentScheduler>();
        services.TryAddSingleton<IApprovalGate, NoApprovalGate>();
        services.TryAddSingleton<CycleEventStore>(sp =>
        {
            // Default to <repoRoot>/.ashlar/runtime-studio/cycles.jsonl. Operators can override
            // by setting ASHLAR_CYCLE_EVENTS_PATH to an absolute or repo-relative path.
            var overridePath = Environment.GetEnvironmentVariable("ASHLAR_CYCLE_EVENTS_PATH");
            return string.IsNullOrWhiteSpace(overridePath)
                ? new CycleEventStore()
                : new CycleEventStore(overridePath.Trim());
        });
        services.TryAddSingleton<IObservationStore>(sp =>
        {
            // Companion to cycles.jsonl: structured facts agents publish for each other to read.
            // Override location with ASHLAR_OBSERVATIONS_PATH for tests / sandboxed runs.
            var overridePath = Environment.GetEnvironmentVariable("ASHLAR_OBSERVATIONS_PATH");
            return string.IsNullOrWhiteSpace(overridePath)
                ? new JsonlObservationStore()
                : new JsonlObservationStore(overridePath.Trim());
        });
        services.TryAddSingleton<IObjectiveStore>(sp =>
        {
            // Filesystem-backed backlog under .ashlar/runtime-studio/objectives/
            // by default. ASHLAR_OBJECTIVES_ROOT points at an absolute path for
            // sandboxed test runs so we don't pollute the working tree.
            var overridePath = Environment.GetEnvironmentVariable("ASHLAR_OBJECTIVES_ROOT");
            return string.IsNullOrWhiteSpace(overridePath)
                ? new ObjectiveStore()
                : new ObjectiveStore(overridePath.Trim());
        });
        services.TryAddSingleton<IChangeProposalStore>(sp =>
        {
            // Filesystem-backed forge proposal queue under .ashlar/runtime-studio/forge/
            // by default. ASHLAR_FORGE_ROOT lets sandboxed runs (and tests) point at
            // an isolated temp directory.
            var overridePath = Environment.GetEnvironmentVariable("ASHLAR_FORGE_ROOT");
            return string.IsNullOrWhiteSpace(overridePath)
                ? new ChangeProposalStore()
                : new ChangeProposalStore(overridePath.Trim());
        });
        services.AddSingleton<IBackgroundAgentRegistry>(sp =>
        {
            var scheduler = sp.GetRequiredService<IAgentScheduler>();
            var logger = sp.GetService<Microsoft.Extensions.Logging.ILogger<BackgroundAgentRegistry>>();
            var logStore = sp.GetService<IBackgroundAgentLogStore>();
            var codeAnalysisRunner = sp.GetService<ICodeAnalysisRunner>();
            var testRunRunner = sp.GetService<ITestRunRunner>();
            var selfExtendRunner = sp.GetService<ISelfExtendRunner>();
            var selfImprovementLoop = sp.GetService<Ashlar.Core.Application.SelfImprovement.Ports.ISelfImprovementLoop>();
            var modeStore = sp.GetService<IAggressivenessModeStore>();
            var approvalGate = sp.GetService<IApprovalGate>();
            var sensitivityRegistry = sp.GetService<IDataSensitivityRegistry>();
            var auditLog = sp.GetService<IDataDecisionAuditLog>();
            var cycleEvents = sp.GetService<CycleEventStore>();
            var observations = sp.GetService<IObservationStore>();
            // SX-AUDIT invariant D: a host may register a stricter ExtensionCeiling; absent one,
            // the registry resolves the defaults lowered by the environment.
            var extensionCeiling = sp.GetService<Extending.ExtensionCeiling>();
            return new BackgroundAgentRegistry(scheduler, logger, logStore, codeAnalysisRunner, testRunRunner, selfExtendRunner, selfImprovementLoop, modeStore, approvalGate, sensitivityRegistry, auditLog, cycleEvents, observations, extensionCeiling: extensionCeiling);
        });
        // Registered here, immediately beside IBackgroundAgentRegistry, so the two can
        // never be wired independently: wherever the registry exists, its deferred form
        // exists too. SelfExtendRunnerAdapter depends on the Lazy rather than the
        // registry to break a resolution cycle (see that constructor's remarks), and it
        // takes the dependency optionally — so a host that registered the registry but
        // not the Lazy would silently receive null and quietly lose the registry instead
        // of failing. Keeping them together removes that trap, and matters because three
        // separate hosts wire this adapter (Ashlar.API, the CLI daemon command, and the
        // CLI root).
        services.TryAddSingleton(sp => new Lazy<IBackgroundAgentRegistry>(
            sp.GetRequiredService<IBackgroundAgentRegistry>));

        services.TryAddSingleton<BackgroundAgentConfigLoader>();
        services.TryAddSingleton<BackgroundAgentSpecBuilder>();
        services.TryAddSingleton<AgentManagementToolbox>(sp =>
        {
            var registry = sp.GetRequiredService<IBackgroundAgentRegistry>();
            var configLoader = sp.GetRequiredService<BackgroundAgentConfigLoader>();
            var specBuilder = sp.GetRequiredService<BackgroundAgentSpecBuilder>();
            var agentCreator = sp.GetRequiredService<IAgentCreator>();
            return new AgentManagementToolbox(registry, configLoader, specBuilder, agentCreator);
        });
        if (registerHostedService)
            services.AddHostedService<BackgroundAgentService>();
        return services;
    }

    /// <summary>
    /// Adds RAG (Retrieval Augmented Generation) services for CLI and agent use.
    /// Registers in-memory vector store and token-based embeddings by default.
    /// </summary>
    public static IServiceCollection AddBackgroundAgentsRAG(this IServiceCollection services)
    {
        services.TryAddSingleton<IEmbeddingGenerator, TokenEmbeddingGenerator>();
        services.TryAddSingleton<IVectorStore>(sp =>
        {
            var registry = sp.GetService<IDataSensitivityRegistry>();
            return new InMemoryVectorStore(registry);
        });
        services.TryAddSingleton<IRAGService, RAGService>();
        services.TryAddSingleton<IKnowledgeBaseIndexer, KnowledgeBaseIndexer>();
        return services;
    }

    /// <summary>
    /// Adds Trust &amp; Information Architecture services: data taxonomy, cloud sanitization proxy, audit log.
    /// Call before registering IProviderFactory. When useSanitizingProviderFactory is true, IProviderFactory
    /// will be a SanitizingProviderFactory that wraps the inner ProviderFactory.
    /// </summary>
    /// <param name="useSanitizingProviderFactory">If true, IProviderFactory is registered as SanitizingProviderFactory wrapping ProviderFactory.</param>
    /// <param name="ephemeralLifecycle">If true, ProviderFactory will receive IEphemeralModelLifecycle for ephemeral Ollama (caller must register it).</param>
    /// <param name="skipProviderRegistration">If true, trust services are added but provider factory is not registered (caller builds chain).</param>
    public static IServiceCollection AddTrustServices(this IServiceCollection services, bool useSanitizingProviderFactory = false, bool ephemeralLifecycle = false, bool skipProviderRegistration = false)
    {
        services.TryAddSingleton<IDataTaxonomy, DataTaxonomy>();
        services.TryAddSingleton<ISensitiveContentFilter, SensitiveContentFilter>();
        services.AddUserKnowledgeLog(Environment.GetEnvironmentVariable("ASHLAR_KNOWLEDGE_LOG_PATH"));
        services.AddAccessBoundary(Environment.GetEnvironmentVariable("ASHLAR_ACCESS_BOUNDARY_CONFIG"));
        services.AddCloudAvailabilityResolver(
            configPath: Environment.GetEnvironmentVariable("ASHLAR_CONFIG_PATH"),
            enableNetworkProbe: string.Equals(Environment.GetEnvironmentVariable("ASHLAR_AIRGAP_PROBE"), "1", StringComparison.Ordinal));
        var auditDbPath = Environment.GetEnvironmentVariable("ASHLAR_TRUST_AUDIT_DB");
        object auditLogInstance = !string.IsNullOrWhiteSpace(auditDbPath)
            ? new Ashlar.BackgroundAgents.Trust.LiteDbDataDecisionAuditLog(auditDbPath)
            : new Ashlar.BackgroundAgents.Trust.DataDecisionAuditLog();
        services.TryAddSingleton<IDataDecisionAuditLog>(sp => (IDataDecisionAuditLog)auditLogInstance);
        services.TryAddSingleton<ISanitizationAuditLog>(sp => (ISanitizationAuditLog)auditLogInstance);
        services.TryAddSingleton<ICloudSanitizationProxy>(sp =>
        {
            var filter = sp.GetService<ISensitiveContentFilter>();
            var taxonomy = sp.GetService<IDataTaxonomy>();
            var audit = sp.GetService<ISanitizationAuditLog>();
            return new CloudSanitizationProxy(filter, taxonomy, audit);
        });
        services.TryAddSingleton<ITrustPolicyPackRegistry, TrustPolicyPackRegistry>();

        // NOTE: the Ashlar kernel never takes this branch — AshlarKernelRegistrar
        // always calls this method with skipProviderRegistration: true and owns
        // the whole provider-factory chain itself (Phase 15). The branch is kept
        // for SDK consumers that call AddTrustServices directly and want the
        // sanitizing factory wired for them; changing or deleting it would be a
        // behaviour break for those callers, not a kernel change.
        if (useSanitizingProviderFactory && !skipProviderRegistration)
        {
            services.TryAddSingleton<Ashlar.Core.Application.Resilience.Ports.IResilientExecutor, Ashlar.Infrastructure.Resilience.ResilientExecutor>();
            services.AddSingleton<Ashlar.Infrastructure.Execution.ProviderFactory>(sp =>
            {
                var logger = sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Ashlar.Infrastructure.Execution.ProviderFactory>>();
                var lifecycle = ephemeralLifecycle ? sp.GetService<Ashlar.Core.Application.Ephemeral.Ports.IEphemeralModelLifecycle>() : null;
                var resilient = sp.GetRequiredService<Ashlar.Core.Application.Resilience.Ports.IResilientExecutor>();
                return new Ashlar.Infrastructure.Execution.ProviderFactory(logger, lifecycle, resilient);
            });
            services.AddSingleton<IProviderFactory>(sp =>
            {
                var infraFactory = sp.GetRequiredService<Ashlar.Infrastructure.Execution.ProviderFactory>();
                var adapter = new Ashlar.Infrastructure.Adapters.ProviderFactoryAdapter(infraFactory);
                var proxy = sp.GetRequiredService<ICloudSanitizationProxy>();
                var logger = sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SanitizingProviderFactory>>();
                return new SanitizingProviderFactory(adapter, proxy, logger);
            });
        }

        return services;
    }

    /// <summary>
    /// Adds the observation pipeline: event sources, pattern detection, pattern store.
    /// Registers ObservationPipelineService as a hosted service when registerHostedService is true.
    /// </summary>
    /// <param name="configure">Optional configuration of pipeline options.</param>
    /// <param name="registerHostedService">If true, registers ObservationPipelineService (default true).</param>
    public static IServiceCollection AddObservationPipeline(this IServiceCollection services, Action<ObservationPipelineOptions>? configure = null, bool registerHostedService = true)
    {
        var degradedModeEnabled = string.Equals(
            Environment.GetEnvironmentVariable(ObservationDegradedModeEnv),
            "1",
            StringComparison.OrdinalIgnoreCase);
        services.Configure<ObservationPipelineOptions>(opts =>
        {
            configure?.Invoke(opts);
        });
        services.AddSingleton<Ashlar.Core.Application.Observation.Ports.IPatternStore>(sp =>
        {
            var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<ObservationPipelineOptions>>().Value;
            var repoRoot = opts.RepoRoot ?? Directory.GetCurrentDirectory();
            var storePath = Path.Combine(repoRoot, opts.StorePath);
            if (!degradedModeEnabled)
            {
                return new LiteDbPatternStore(storePath);
            }

            try
            {
                return new LiteDbPatternStore(storePath);
            }
            catch
            {
                return new NoOpPatternStore();
            }
        });
        services.AddSingleton<Ashlar.Core.Application.Observation.Ports.IPatternProcessedStore>(sp =>
        {
            var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<ObservationPipelineOptions>>().Value;
            var repoRoot = opts.RepoRoot ?? Directory.GetCurrentDirectory();
            var storePath = Path.Combine(repoRoot, opts.StorePath);
            return new Ashlar.Infrastructure.Observation.LiteDbPatternProcessedStore(storePath);
        });
        services.AddSingleton<Ashlar.Core.Application.Observation.Ports.IContextAssembler, ContextAssembler>();
        if (registerHostedService)
            services.AddHostedService<ObservationPipelineService>();
        return services;
    }
}
