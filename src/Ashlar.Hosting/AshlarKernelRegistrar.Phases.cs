using FluentValidation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Ashlar.Abstractions;
using Ashlar.Abstractions.Routing;
using Ashlar.AI.Pipeline;
using Ashlar.BackgroundAgents;
using Ashlar.BackgroundAgents.Trust;
using Ashlar.Contracts;
using Ashlar.Core.Application.Adaptation.Ports;
using Ashlar.Core.Application.Analysis.UseCases.AnalyzeCode;
using Ashlar.Core.Application.Common.Ports;
using Ashlar.Core.Application.Common.Services;
using Ashlar.Core.Application.Copilot.Ports;
using Ashlar.Core.Application.Ephemeral.Ports;
using Ashlar.Core.Application.Knowledge.Ports;
using Ashlar.Core.Application.Execution.Ports;
using Ashlar.Core.Application.Resilience.Ports;
using Ashlar.Core.Application.Observation.Ports;
using Ashlar.Core.Application.Paths;
using Ashlar.Core.Application.Testing.UseCases.RunTests;
using Ashlar.Core.Application.Trust.Ports;
using Ashlar.Core.Application.Maintenance.Ports;
using Ashlar.Infrastructure.Copilot;
using Ashlar.Infrastructure.Environments;
using Ashlar.Infrastructure.Execution;
using Ashlar.Infrastructure.Execution.Ephemeral;
using Ashlar.Infrastructure.Execution.LoadPolicy;
using Ashlar.Infrastructure.Execution.Sandbox;
using Ashlar.Infrastructure.Execution.Scratch;
using Ashlar.Infrastructure.Resilience;
using Ashlar.Infrastructure.Scaling;
using Ashlar.Infrastructure.Knowledge;
using Ashlar.Infrastructure.MeshLab;
using Ashlar.Infrastructure.Persistence.Ephemeral;
using Ashlar.Orchestration;
using Ashlar.Orchestration.Models;
using Ashlar.Orchestration.Transport;
using Ashlar.Runtime;
using Ashlar.Runtime.Routing;
using Ashlar.Transport.Grpc;
using Ashlar.Tools.Assembly;
using Ashlar.Tools.Dev;

namespace Ashlar.Hosting;

/// <summary>
/// Ashlar kernel DI registration phases 01–20. Each method wires a focused service slice;
/// phases run in order during <c>AddAshlar</c> host bootstrap.
/// </summary>
internal static partial class AshlarKernelRegistrar
{
    /// <summary>Phase 01: configuration binding and node capability runtime.</summary>
    private static void RegisterPhase01_ConfigurationNodeCapabilityRuntime(AshlarKernelRegistrationContext ctx)
    {
        IServiceCollection services = ctx.Services;
        ModuleSelection modules = ctx.Modules;
        IConfiguration configuration = ctx.Configuration;

        // ── Configuration & Node Capability Runtime ────────────────────
        // Environment variables are the primary config source; appsettings
        // is intentionally NOT loaded here so that containerised deployments
        // stay 12-factor compliant.  RemoteCapabilitiesOptions binds from
        // the "Ashlar:RemoteCapabilities" section for RunPod/cloud routing.
        services.AddOptions<Ashlar.Infrastructure.Execution.RemoteCapabilitiesOptions>()
        .Bind(configuration.GetSection("Ashlar:RemoteCapabilities"));
        if (modules.IncludeNodeCapabilityRuntime)
        {
            services.AddRunPodCapabilityRouting(configuration);
            AshlarServiceCollectionExtensions.RegisterNodeCapabilityRuntime(services, configuration);
        }

    }

    /// <summary>Phase 02: MediatR, FluentValidation, and ingress pipeline behaviors.</summary>
    private static void RegisterPhase02_CQRSMediatRFluentValidation(AshlarKernelRegistrationContext ctx)
    {
        IServiceCollection services = ctx.Services;

        // ── CQRS (MediatR) & FluentValidation ─────────────────────────
        // MediatR handlers from both the Analysis and Testing assemblies
        // are registered in one pass.  The ValidationBehavior pipeline
        // behavior runs FluentValidation before each handler, so
        // validators must also be registered here.
        services.AddMediatR(cfg =>
        {
            cfg.RegisterServicesFromAssembly(typeof(AnalyzeCodeCommand).Assembly);
            cfg.RegisterServicesFromAssembly(typeof(RunTestsCommand).Assembly);
        });

        services.TryAddSingleton<ISmsIngressApprovalStore, UnsupportedSmsIngressApprovalStore>();

        services.AddValidatorsFromAssembly(typeof(AnalyzeCodeValidator).Assembly);
        services.AddTransient(typeof(MediatR.IPipelineBehavior<,>), typeof(Ashlar.Core.Application.Behaviors.IngressLoggingPipelineBehavior<,>));
        services.AddTransient(typeof(MediatR.IPipelineBehavior<,>), typeof(Ashlar.Core.Application.Behaviors.ValidationBehavior<,>));
        services.TryAddSingleton<Ashlar.Core.Application.Middleware.Ports.IAshlarIngressAccessor, Ashlar.Core.Application.Middleware.NoOpAshlarIngressAccessor>();

    }

    /// <summary>Phase 03: domain configuration service adapter.</summary>
    private static void RegisterPhase03_ConfigurationServiceAdapter(AshlarKernelRegistrationContext ctx)
    {
        IServiceCollection services = ctx.Services;

        // ── Configuration service adapter ──────────────────────────────
        // Bridges the domain-level IConfigurationService port to the
        // infrastructure adapter.  Strict mode controls whether config
        // warnings escalate to hard failures (useful in CI pipelines).
        services.AddSingleton<Ashlar.Core.Application.Configuration.Ports.IConfigurationService>(sp =>
        {
            Microsoft.Extensions.Logging.ILogger<Infrastructure.Configuration.ConfigurationServiceAdapter> logger = sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Ashlar.Infrastructure.Configuration.ConfigurationServiceAdapter>>();
            StrictModeOptions? strictMode = sp.GetService<StrictModeOptions>();
            return new Ashlar.Infrastructure.Configuration.ConfigurationServiceAdapter(logger, strictMode?.ShouldFailOnConfigurationWarnings ?? false);
        });

    }

    /// <summary>Phase 04: loop kernel decorator chain (sequential, parallel, instrumented).</summary>
    private static void RegisterPhase04_LoopKernelDecoratorChain(AshlarKernelRegistrationContext ctx)
    {
        IServiceCollection services = ctx.Services;

        // ── Loop kernel (decorator chain) ──────────────────────────────
        // The loop kernel runs brick-level iterations.  It is composed via
        // the decorator pattern:
        //   SequentialLoopKernel  (always present — baseline)
        //     → ParallelLoopKernel      (if ASHLAR_LOOP_PARALLEL=1)
        //       → InstrumentedLoopKernel (if ASHLAR_LOOP_INSTRUMENT=1)
        //
        // ASHLAR_LOOP_PARALLEL ("1"): wraps in a parallelising decorator for
        //   concurrent brick evaluation; useful on multi-core servers.
        // ASHLAR_LOOP_INSTRUMENT ("1"): adds timing/counter telemetry around
        //   each loop iteration; adds overhead, meant for dev profiling.
        services.AddSingleton<ILoopKernel>(sp =>
        {
            ILoopKernel k = new SequentialLoopKernel();
            bool enableParallel = string.Equals(Environment.GetEnvironmentVariable("ASHLAR_LOOP_PARALLEL"), "1", StringComparison.OrdinalIgnoreCase);
            if (enableParallel)
            {
                k = new ParallelLoopKernel(k);
            }

            bool instrument = string.Equals(Environment.GetEnvironmentVariable("ASHLAR_LOOP_INSTRUMENT"), "1", StringComparison.OrdinalIgnoreCase);
            if (instrument)
            {
                k = new InstrumentedLoopKernel(k, sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<InstrumentedLoopKernel>>());
            }

            return k;
        });

    }

    /// <summary>Phase 05: orchestration and optional runtime transport.</summary>
    private static void RegisterPhase05_OrchestrationTransport(AshlarKernelRegistrationContext ctx)
    {
        IServiceCollection services = ctx.Services;
        ModuleSelection modules = ctx.Modules;

        // ── Orchestration & transport ──────────────────────────────────
        // Orchestration is always registered (it owns the runtime spec
        // accessor used by the model decorator chain).  Transport is
        // optional: when present it registers gRPC channels plus the
        // dual in-process / gRPC agent transport pair used for peer
        // communication.  See Ashlar.Transport.Grpc for channel config.
        services.AddAshlarOrchestration();
        if (modules.IncludeRuntimeTransport)
        {
            services.AddOptions<GrpcTransportOptions>();
            services.AddOptions<RoutingOptions>();
            services.TryAddSingleton<IEndpointRegistry, InMemoryEndpointRegistry>();
            services.TryAddSingleton<IGrpcChannelFactory, DefaultGrpcChannelFactory>();
            services.AddAshlarRuntimeTransport<InProcessAgentTransport, GrpcAgentTransport>();
        }

    }

    /// <summary>Phase 06: persistence and Postgres isolated database provisioner.</summary>
    private static void RegisterPhase06_Persistence(AshlarKernelRegistrationContext ctx)
    {
        IServiceCollection services = ctx.Services;
        ModuleSelection modules = ctx.Modules;

        // ── Persistence ────────────────────────────────────────────────
        if (modules.IncludePersistence)
        {
            services.AddAshlarPersistence();
            services.AddPostgresIsolatedDatabaseProvisioner();
        }

    }

    /// <summary>Phase 07: adaptation infrastructure and federated brick mesh.</summary>
    private static void RegisterPhase07_Adaptation(AshlarKernelRegistrationContext ctx)
    {
        IServiceCollection services = ctx.Services;
        AshlarHostingOptions options = ctx.Options;
        ModuleSelection modules = ctx.Modules;
        IConfiguration configuration = ctx.Configuration;

        // ── Adaptation ─────────────────────────────────────────────────
        // Pattern store path is forwarded so the adaptation layer knows
        // where to persist learned patterns on disk.
        //
        // Observation core (IPatternStore / IPatternProcessedStore /
        // IContextAssembler) is registered by exactly one phase. Phase 12
        // owns it whenever the observation pipeline is active; adaptation
        // owns it otherwise. Previously BOTH registered it with AddSingleton
        // and Phase 12 silently won on last-wins wherever both ran — which
        // also meant the two disagreed about the store path, since Phase 12
        // combines it with the repo root and adaptation used it verbatim.
        // Adaptation's registration was therefore dead in Full/Server but
        // load-bearing in AirGapped and whenever the pipeline is disabled.
        if (modules.IncludeAdaptation)
        {
            bool observationPipelineOwnsObservationCore =
                modules.IncludeObservationPipeline && !options.DisableObservationPipeline;

            services.AddAdaptationInfrastructure(
                options.PatternStorePath,
                registerObservationCore: !observationPipelineOwnsObservationCore);
            services.AddAshlarFederatedBrickMesh(configuration);
        }

    }

    /// <summary>Phase 08: LiteDB copilot task store.</summary>
    private static void RegisterPhase08_CopilotTaskStore(AshlarKernelRegistrationContext ctx)
    {
        IServiceCollection services = ctx.Services;
        AshlarHostingOptions options = ctx.Options;

        // ── Copilot task store ──────────────────────────────────────────
        // LiteDB file is co-located with the pattern store directory
        // (or the resolved state directory — ASHLAR_STATE_DIR, else
        // <repo root>/.ashlar/state — as fallback) to keep all
        // Ashlar-generated state in one discoverable location.
        string copilotTasksBasePath = !string.IsNullOrEmpty(options.PatternStorePath)
            ? Path.GetDirectoryName(options.PatternStorePath) ?? "."
            : RepoPathResolver.ResolveStateDirectory();
        string copilotTasksDbPath = Path.Combine(copilotTasksBasePath, "ashlar-copilot-tasks.db");
        services.TryAddSingleton<ICopilotTaskStore>(_ => new LiteDbCopilotTaskStore(copilotTasksDbPath));

    }

    /// <summary>Phase 09: knowledge query service façade.</summary>
    private static void RegisterPhase09_KnowledgeQueryService(AshlarKernelRegistrationContext ctx)
    {
        IServiceCollection services = ctx.Services;

        // ── Knowledge query service ────────────────────────────────────
        // Aggregates adaptation logs, pattern store, and (optionally)
        // user-knowledge logs into a single query façade.  Falls back to
        // an in-memory knowledge log when the trust module is absent.
        services.TryAddSingleton<IKnowledgeQueryService>(sp =>
        {
            IAdaptationLog adaptationLog = sp.GetRequiredService<IAdaptationLog>();
            IPatternStore patternStore = sp.GetRequiredService<IPatternStore>();
            IUserKnowledgeLogStore userKnowledgeStore = sp.GetService<IUserKnowledgeLogStore>()
                ?? new Ashlar.Infrastructure.Trust.InMemoryUserKnowledgeLogStore();
            return new KnowledgeQueryService(adaptationLog, patternStore, userKnowledgeStore);
        });

    }

    /// <summary>Phase 10: pipeline composition layer.</summary>
    private static void RegisterPhase10_PipelineComposition(AshlarKernelRegistrationContext ctx)
    {
        IServiceCollection services = ctx.Services;
        ModuleSelection modules = ctx.Modules;

        // ── Pipeline composition ───────────────────────────────────────
        if (modules.IncludePipelineComposition)
        {
            services.AddPipelineCompositionLayer();
        }

    }

    /// <summary>Phase 11: background agents and RAG.</summary>
    private static void RegisterPhase11_BackgroundAgentsRAG(AshlarKernelRegistrationContext ctx)
    {
        IServiceCollection services = ctx.Services;
        AshlarHostingOptions options = ctx.Options;
        ModuleSelection modules = ctx.Modules;

        // ── Background agents & RAG ────────────────────────────────────
        if (modules.IncludeBackgroundAgents)
        {
            // Register adapters for BackgroundAgents to access Orchestration and Infrastructure
            // via Application ports (DIP - BackgroundAgents depends on ports, not concrete layers)
            services.TryAddSingleton<Ashlar.Core.Application.Orchestration.Ports.IAgentCreator>(sp =>
            {
                var agentFactory = sp.GetRequiredService<Ashlar.Orchestration.Agents.AgentFactory>();
                var logger = sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Ashlar.Orchestration.Adapters.AgentCreatorAdapter>>();
                return new Ashlar.Orchestration.Adapters.AgentCreatorAdapter(agentFactory, logger);
            });

            services.AddBackgroundAgents(registerHostedService: options.RegisterBackgroundAgentHostedService);
        }

        if (modules.IncludeBackgroundAgentRag)
        {
            services.AddBackgroundAgentsRAG();
        }

    }

    /// <summary>Phase 12: observation pipeline and web-search fallback.</summary>
    private static void RegisterPhase12_ObservationPipeline(AshlarKernelRegistrationContext ctx)
    {
        IServiceCollection services = ctx.Services;
        AshlarHostingOptions options = ctx.Options;
        ModuleSelection modules = ctx.Modules;

        // ── Observation pipeline ───────────────────────────────────────
        // Captures runtime telemetry and persists it alongside patterns.
        // ASHLAR_OBSERVATION_FAIL_OPEN ("1" / "true"): when set, store I/O
        //   errors are swallowed instead of failing the pipeline — safe
        //   for edge nodes with unreliable storage.
        if (modules.IncludeObservationPipeline && !options.DisableObservationPipeline)
        {
            string repoRoot = RepoPathResolver.FindRepoRoot();
            // Watch paths stay repo-relative; the LiteDB pattern store lives in the state
            // directory (ASHLAR_STATE_DIR, else <repo root>/.ashlar/state) unless PatternStorePath is set.
            string defaultPatternStorePath = Path.Combine(RepoPathResolver.ResolveStateDirectory(repoRoot), "ashlar-patterns.db");
            bool observationFailOpen = options.ObservationFailOpen ?? AshlarServiceCollectionExtensions.ParseBooleanEnvironmentVariable("ASHLAR_OBSERVATION_FAIL_OPEN");
            services.AddObservationPipeline(opts =>
            {
                opts.RepoRoot = repoRoot;
                opts.StorePath = options.PatternStorePath ?? defaultPatternStorePath;
                opts.FailOpenOnStoreErrors = observationFailOpen;
            }, registerHostedService: options.RegisterBackgroundAgentHostedService);
        }

        // Mock web-search provider is registered as a fallback so
        // background agents can be instantiated even when no real
        // provider is configured.
        if (modules.IncludeBackgroundAgents)
        {
            services.TryAddSingleton<Ashlar.BackgroundAgents.WebSearch.IWebSearchProvider, Ashlar.BackgroundAgents.WebSearch.MockWebSearchProvider>();
        }

    }

    /// <summary>Phase 13: IModel decorator chain (provider, hot-swap, orchestration spec).</summary>
    private static void RegisterPhase13_ModelDecoratorChain(AshlarKernelRegistrationContext ctx)
    {
        IServiceCollection services = ctx.Services;

        // ── Model decorator chain ──────────────────────────────────────
        // The IModel abstraction is built as a three-layer decorator:
        //
        //   1. MeaiBackedModel (default) or ProviderBackedModel (opt-out)
        //   2. HotSwappableModel       – allows runtime model switching
        //                                without restarting the host
        //   3. OrchestrationRuntimeModelDecorator
        //                              – injects orchestration-level
        //                                spec overrides (temperature,
        //                                token limits, etc.) per-call
        //
        // HotSwappableModel is registered as a concrete singleton so that
        // administrative endpoints can resolve it directly for hot-swap
        // operations, while IModel always returns the fully decorated chain.
        // The factory runs after Phase 13b so MEAI IChatClient is available when enabled.
        services.AddSingleton<Ashlar.Infrastructure.Execution.Models.HotSwappableModel>(sp =>
        {
            var logger = sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Ashlar.Infrastructure.Execution.Models.HotSwappableModel>>();
            bool meai = MeaiPipelineServiceCollectionExtensions.IsMeaiPipelineEnabled(
                ctx.Configuration,
                ctx.Options.UseMeaiPipeline);

            Ashlar.Abstractions.IModel agentic;
            if (meai)
            {
                agentic = new Ashlar.AI.Pipeline.Models.MeaiBackedModel(
                    sp.GetRequiredService<Microsoft.Extensions.AI.IChatClient>(),
                    sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Ashlar.AI.Pipeline.Models.MeaiBackedModel>>());
            }
            else
            {
                Ashlar.Infrastructure.Execution.IProviderFactory providerFactory = sp.GetRequiredService<Ashlar.Infrastructure.Execution.IProviderFactory>();
                agentic = new Ashlar.Infrastructure.Execution.Models.ProviderBackedModel(
                    providerFactory,
                    sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Ashlar.Infrastructure.Execution.Models.ProviderBackedModel>>());
            }

            return new Ashlar.Infrastructure.Execution.Models.HotSwappableModel(agentic, logger);
        });

        services.AddSingleton<Ashlar.Abstractions.IModel>(sp =>
        {
            IOrchestrationRuntimeSpecAccessor accessor = sp.GetRequiredService<IOrchestrationRuntimeSpecAccessor>();
            Infrastructure.Execution.Models.HotSwappableModel inner = sp.GetRequiredService<Ashlar.Infrastructure.Execution.Models.HotSwappableModel>();
            return new Ashlar.Orchestration.Models.OrchestrationRuntimeModelDecorator(
                inner,
                accessor,
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Ashlar.Orchestration.Models.OrchestrationRuntimeModelDecorator>>());
        });

    }

    /// <summary>
    /// Phase 13b: Microsoft.Extensions.AI pipeline (default on since Phase 6) + VectorData RAG as default IRAGService.
    /// Opt out of MEAI chat with <c>Ashlar:UseMeaiPipeline=false</c> / <c>ASHLAR_USE_MEAI_PIPELINE=0</c>.
    /// Legacy <c>IProviderFactory</c> remains registered for direct non-chat callers.
    /// </summary>
    private static void RegisterPhase13b_MeaiPipeline(AshlarKernelRegistrationContext ctx)
    {
        IServiceCollection services = ctx.Services;
        AshlarHostingOptions options = ctx.Options;
        IConfiguration configuration = ctx.Configuration;

        MeaiPipelineServiceCollectionExtensions.RegisterGovernanceDefaults(services);
        MeaiPipelineServiceCollectionExtensions.RegisterVectorDataRag(services);

        // INTENTIONAL override, not an accident of ordering. Phase 11 registers
        // IRAGService -> RAGService via AddBackgroundAgentsRAG; this registration
        // runs later and therefore wins on last-wins, so IRAGService always
        // resolves to the VectorData-backed adapter. It is deliberately NOT
        // TryAdd — TryAdd here would invert the intent and hand the contract back
        // to the legacy implementation. It is also deliberately not a Replace of
        // the Phase 11 descriptor: nothing enumerates IEnumerable<IRAGService>
        // today, but removing the legacy entry would change that enumeration for
        // anyone who starts.
        services.AddSingleton<Ashlar.BackgroundAgents.RAG.IRAGService>(sp =>
            new Ashlar.Hosting.Meai.MeaiVectorDataRagAdapter(
                sp.GetRequiredService<Ashlar.AI.Pipeline.Rag.VectorDataRagService>()));

        bool enabled = MeaiPipelineServiceCollectionExtensions.IsMeaiPipelineEnabled(
            configuration,
            options.UseMeaiPipeline);

        if (!enabled)
        {
            return;
        }

        services.AddAshlarMeaiPipeline(configuration);
    }

    /// <summary>Phase 14: ephemeral model and database lifecycle.</summary>
    private static void RegisterPhase14_EphemeralLifecycle(AshlarKernelRegistrationContext ctx)
    {
        IServiceCollection services = ctx.Services;
        ModuleSelection modules = ctx.Modules;

        // ── Ephemeral lifecycle ────────────────────────────────────────
        // "Ephemeral" means Ashlar can spin up and tear down backing
        // resources on demand (Ollama models, Postgres databases).
        //
        // ASHLAR_EPHEMERAL ("1"): master switch — enables ALL ephemeral
        //   subsystems.
        // ASHLAR_EPHEMERAL_MODELS ("1"): enables only ephemeral model
        //   lifecycle (Ollama pull/remove) without affecting databases.
        // ASHLAR_EPHEMERAL_DB ("postgres"): enables ephemeral Postgres
        //   database creation; only takes effect when persistence is on.
        if (EphemeralModelsEnabled())
        {
            services.AddSingleton<IEphemeralModelLifecycle, OllamaEphemeralLifecycle>();
        }

        string? ephemeralDb = Environment.GetEnvironmentVariable("ASHLAR_EPHEMERAL_DB")?.Trim();
        if (modules.IncludePersistence && string.Equals(ephemeralDb, "postgres", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<Ashlar.Core.Application.Persistence.Ports.IEphemeralDatabaseLifecycle, PostgresEphemeralLifecycle>();
        }

    }

    /// <summary>Phase 15: trust services and provider factory three-way branching.</summary>
    private static void RegisterPhase15_TrustProviderFactory3wayBranching(AshlarKernelRegistrationContext ctx)
    {
        IServiceCollection services = ctx.Services;
        AshlarHostingOptions options = ctx.Options;
        ModuleSelection modules = ctx.Modules;

        // ── Trust & provider factory (3-way branching) ─────────────────
        // The provider factory is the gateway through which every LLM
        // call flows.  It is composed as a decorator chain, built bottom-up
        // from two independent switches — sanitize and adaptive — rather
        // than as three hand-maintained alternatives:
        //
        //   ProviderFactory                         (always the innermost)
        //     → SanitizingProviderFactory           (when trust is on)
        //       → AdaptiveProviderFactory           (when a load pref is set)
        //
        // which yields the four combinations the kernel actually ships:
        //
        //   plain      ProviderFactory
        //   trust      Sanitizing → ProviderFactory
        //   adaptive   Adaptive → ProviderFactory
        //   both       Adaptive → Sanitizing → ProviderFactory
        //
        // This used to be three `if` branches, one of which lived in
        // Ashlar.BackgroundAgents.AddTrustServices rather than here, and the
        // two assemblies stayed consistent only because the flags passed
        // between them happened to be mutually exclusive. The wiring is now
        // owned entirely by this phase.
        //
        // ASHLAR_TRUST_ENABLED ("1"): activates the sanitization proxy
        //   that scrubs PII before LLM calls leave the trust boundary.
        // ASHLAR_LOAD_PREFERENCE (string, e.g. "latency" / "cost"):
        //   activates adaptive load balancing and selects the policy.
        bool ephemeralModels = EphemeralModelsEnabled();
        bool trustEnabledByConfig = options.TrustEnabled ?? string.Equals(Environment.GetEnvironmentVariable("ASHLAR_TRUST_ENABLED"), "1", StringComparison.OrdinalIgnoreCase);
        bool trustEnabled = modules.IncludeTrustServices && trustEnabledByConfig;
        string? loadPref = Environment.GetEnvironmentVariable("ASHLAR_LOAD_PREFERENCE")?.Trim();
        bool useAdaptive = options.UseAdaptiveLoadBalancing ?? !string.IsNullOrEmpty(loadPref);

        // IResilientExecutor is registered here and nowhere else in the kernel.
        // AddTrustServices also TryAdds it, but only inside the provider branch the
        // kernel no longer takes; even when it did, this registration ran first and
        // won, so the two never disagreed. TryAdd is kept so a host can substitute
        // its own executor before calling AddAshlar.
        services.TryAddSingleton<IResilientExecutor, ResilientExecutor>();
        services.TryAddSingleton<IProcessCommandRunner, ProcessCommandRunner>();
        services.TryAddSingleton<ISandboxedCommandRunner, DockerSandboxedCommandRunner>();
        services.TryAddSingleton<IScratchSpace, FileScratchSpace>();
        services.TryAddSingleton<IWorkspacePathPolicy, WorkspacePathPolicy>();
        services.TryAddSingleton<IManagedFileSet, SnapshotManagedFileSet>();
        services.TryAddSingleton<ISingleFlightGuard, SingleFlightGuard>();

        if (modules.IncludeTrustServices)
        {
            // Trust registers taxonomy, sanitization proxy, and audit log only.
            // skipProviderRegistration is ALWAYS true: the kernel never delegates
            // provider-factory wiring to the trust module, so all three paths are
            // decided in the single block below instead of being split across two
            // assemblies and kept consistent by the flags happening to agree.
            // (AddTrustServices keeps its own provider branch for SDK consumers
            // that call it directly; the kernel simply does not use it.)
            services.AddTrustServices(useSanitizingProviderFactory: false, ephemeralLifecycle: ephemeralModels, skipProviderRegistration: true);
        }

        // ── The one place the provider-factory chain is decided ─────────
        // sanitize implies the trust module is present: trustEnabled already
        // requires modules.IncludeTrustServices, so ICloudSanitizationProxy is
        // guaranteed resolvable wherever it is used below.
        bool sanitize = trustEnabled;

        // Path A and Path B both wrap the bare factory, so they need it resolvable
        // as a concrete service. Path C does not register it — a host asking for
        // ProviderFactory (rather than IProviderFactory) gets null there, and that
        // asymmetry is preserved deliberately.
        if (useAdaptive || sanitize)
        {
            services.AddSingleton<ProviderFactory>(sp => CreateProviderFactory(sp, useAdaptive, sanitize, ephemeralModels));
        }

        if (useAdaptive)
        {
            services.TryAddSingleton<ILoadPolicy, PreferenceLoadPolicy>();
        }

        services.AddSingleton<Ashlar.Infrastructure.Execution.IProviderFactory>(sp =>
        {
            // Innermost: the bare factory. Resolved when it was registered above so
            // wrapper and wrapped share one instance; constructed inline on Path C.
            Ashlar.Infrastructure.Execution.IProviderFactory chain = useAdaptive || sanitize
                ? sp.GetRequiredService<ProviderFactory>()
                : CreateProviderFactory(sp, useAdaptive, sanitize, ephemeralModels);

            // Middle: PII scrubbing when trust is enabled (stays in Infrastructure layer)
            if (sanitize)
            {
                chain = new SanitizingProviderFactory(
                    chain,
                    sp.GetRequiredService<ICloudSanitizationProxy>(),
                    sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SanitizingProviderFactory>>());
            }

            // Outermost: load-balancing across providers (stays in Infrastructure layer)
            if (useAdaptive)
            {
                chain = new AdaptiveProviderFactory(
                    chain,
                    sp.GetRequiredService<ILoadPolicy>(),
                    sp.GetService<Microsoft.Extensions.Logging.ILogger<AdaptiveProviderFactory>>());
            }

            return chain;
        });

        // Register Application port adapter for IProviderFactory (DIP - BackgroundAgents depends on ports)
        services.AddSingleton<Ashlar.Core.Application.Execution.Ports.IProviderFactory>(sp =>
        {
            var infraFactory = sp.GetRequiredService<Ashlar.Infrastructure.Execution.IProviderFactory>();
            Ashlar.Core.Application.Execution.Ports.IProviderFactory appChain = new Ashlar.Infrastructure.Adapters.ProviderFactoryAdapter(infraFactory);
            
            // Apply PII scrubbing at the Application port level (SanitizingProviderFactory uses Application ports)
            if (sanitize)
            {
                appChain = new SanitizingProviderFactory(
                    appChain,
                    sp.GetRequiredService<ICloudSanitizationProxy>(),
                    sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SanitizingProviderFactory>>());
            }
            
            return appChain;
        });

    }

    /// <summary>
    /// Builds the bare <see cref="ProviderFactory"/> that sits at the bottom of the
    /// chain in every wiring path.
    /// </summary>
    /// <remarks>
    /// The lifecycle probe is not uniform, and that is preserved rather than tidied.
    /// Before consolidation the sanitizing path (registered inside AddTrustServices)
    /// only asked for <see cref="IEphemeralModelLifecycle"/> when ephemeral models
    /// were enabled, while the adaptive and plain paths always asked. Those forms
    /// are identical in practice — the kernel registers the lifecycle only when
    /// ephemeral models are on — but they differ if a host pre-registers its own
    /// lifecycle, so each path keeps the probe it had.
    /// </remarks>
    private static ProviderFactory CreateProviderFactory(
        IServiceProvider sp,
        bool useAdaptive,
        bool sanitize,
        bool ephemeralModels)
    {
        bool probeLifecycle = useAdaptive || !sanitize || ephemeralModels;
        return new ProviderFactory(
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ProviderFactory>>(),
            probeLifecycle ? sp.GetService<IEphemeralModelLifecycle>() : null,
            sp.GetRequiredService<IResilientExecutor>());
    }

    /// <summary>Phase 16: execution core, workflow integrations, and behavior pipeline.</summary>
    private static void RegisterPhase16_ExecutionCoreWorkflow(AshlarKernelRegistrationContext ctx)
    {
        IServiceCollection services = ctx.Services;
        ModuleSelection modules = ctx.Modules;

        // ── Execution core & workflow ──────────────────────────────────
        services.AddSingleton<Ashlar.Core.Application.Common.Ports.ITextFileSystem, Ashlar.Infrastructure.IO.LocalTextFileSystem>();
        services.AddMapDataProviderRouting();

        // Workflow integrations (PDF export, webhooks, DB read/write,
        // cluster store) are only available in Full/Server profiles.
        // WorkflowExecutor resolves them as optional dependencies so it
        // can still execute pure in-memory workflows without them.
        if (modules.IncludeWorkflowIntegrations)
        {
            services.AddSingleton<Ashlar.Core.Application.Common.Ports.IWorkflowPdfExporter, Ashlar.Infrastructure.Workflows.QuestPdfWorkflowExporter>();
            services.AddSingleton<Ashlar.Core.Application.Common.Ports.IWorkflowWebhookClient, Ashlar.Infrastructure.Workflows.HttpWorkflowWebhookClient>();
            services.AddSingleton<Ashlar.Core.Application.Common.Ports.IWorkflowDatabaseReader, Ashlar.Infrastructure.Workflows.DapperWorkflowDatabaseReader>();
            services.AddSingleton<Ashlar.Core.Application.Common.Ports.IWorkflowDatabaseWriter, Ashlar.Infrastructure.Workflows.DapperWorkflowDatabaseWriter>();
            services.AddSingleton<Ashlar.Infrastructure.Execution.IClusterRegistry, Ashlar.Infrastructure.Execution.ClusterRegistry>();
            services.AddSingleton<Ashlar.Core.Application.Common.Ports.IClusterStore, Ashlar.Infrastructure.Workflows.ClusterStoreAdapter>();
        }

        // Semantic cache, behavior registry, step mode, and behavior
        // executor form the brick execution pipeline.  TryAddSingleton
        // is used so that test hosts or SDK consumers can substitute
        // any of these before calling AddAshlar.
        services.TryAddSingleton<Ashlar.Infrastructure.Execution.ISemanticCache>(sp =>
            new Ashlar.Infrastructure.Execution.SemanticCache(sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Ashlar.Infrastructure.Execution.SemanticCache>>()));
        services.TryAddSingleton<Ashlar.Core.Domain.Execution.IBehaviorRegistry>(_ =>
            new Ashlar.Infrastructure.Execution.BehaviorRegistry([]));
        services.TryAddSingleton<Ashlar.Core.Application.Execution.Ports.IStepExecutionMode>(sp =>
            new Ashlar.Infrastructure.Execution.StepExecutionModeStore(
                null,
                sp.GetService<Microsoft.Extensions.Logging.ILogger<Ashlar.Infrastructure.Execution.StepExecutionModeStore>>()));
        services.TryAddSingleton<Ashlar.Core.Domain.Execution.IBehaviorExecutor>(sp =>
            new Ashlar.Infrastructure.Execution.BehaviorExecutor(
                sp.GetRequiredService<Ashlar.Core.Domain.Execution.IBrickRegistry>(),
                sp.GetRequiredService<Ashlar.Infrastructure.Execution.IProviderFactory>(),
                sp.GetRequiredService<Ashlar.Infrastructure.Execution.ISemanticCache>(),
                sp.GetRequiredService<ILoopKernel>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Ashlar.Infrastructure.Execution.BehaviorExecutor>>(),
                sp.GetService<Ashlar.Core.Application.Execution.Ports.IAgenticBrickEngine>(),
                sp.GetService<Ashlar.Core.Application.Execution.Ports.IStepExecutionMode>(),
                sp.GetService<IMetricsCollector>()));
        // Agent registry is populated from SDK-provided AgentCards; if
        // none are supplied the registry starts empty and agents can be
        // registered later at runtime.
        services.TryAddSingleton<Ashlar.Core.Domain.Execution.IAgentRegistry>(sp =>
        {
            AshlarSdkOptions? sdkOptions = sp.GetService<AshlarSdkOptions>();
            List<Core.Domain.Agents.AgentCard> cards = sdkOptions?.AgentCards?.ToList() ?? [];
            return new Ashlar.Infrastructure.Execution.AgentRegistry(cards);
        });

    }

    /// <summary>Phase 17: workflow executor and agent adapters.</summary>
    private static void RegisterPhase17_WorkflowExecutor(AshlarKernelRegistrationContext ctx)
    {
        IServiceCollection services = ctx.Services;

        // ── Workflow executor ──────────────────────────────────────────
        // Scoped because a single workflow execution may accumulate
        // state (e.g. cluster affinity) that should not leak across
        // independent request scopes.
        services.AddScoped<Ashlar.Core.Application.Workflows.WorkflowExecutor>(sp =>
            new Ashlar.Core.Application.Workflows.WorkflowExecutor(
                sp.GetRequiredService<Ashlar.Core.Domain.Execution.IAgentRegistry>(),
                sp.GetRequiredService<Ashlar.Core.Domain.Execution.IBrickRegistry>(),
                sp.GetRequiredService<Ashlar.Core.Domain.Execution.IBehaviorRegistry>(),
                sp.GetRequiredService<Ashlar.Core.Domain.Execution.IBehaviorExecutor>(),
                sp.GetRequiredService<ILoopKernel>(),
                sp.GetRequiredService<Ashlar.Core.Application.Common.Ports.ITextFileSystem>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Ashlar.Core.Application.Workflows.WorkflowExecutor>>(),
                pdfExporter: sp.GetService<Ashlar.Core.Application.Common.Ports.IWorkflowPdfExporter>(),
                webhookClient: sp.GetService<Ashlar.Core.Application.Common.Ports.IWorkflowWebhookClient>(),
                databaseReader: sp.GetService<Ashlar.Core.Application.Common.Ports.IWorkflowDatabaseReader>(),
                databaseWriter: sp.GetService<Ashlar.Core.Application.Common.Ports.IWorkflowDatabaseWriter>(),
                clusterStore: sp.GetService<Ashlar.Core.Application.Common.Ports.IClusterStore>()));

        services.AddScoped<Ashlar.Core.Application.Agent.Ports.IAgentRegistry, Ashlar.Infrastructure.Agent.Adapters.AgentRegistryAdapter>();
        services.AddScoped<Ashlar.Core.Application.Agent.Ports.IAgentExecutor, Ashlar.Infrastructure.Agent.Adapters.AgentExecutorAdapter>();

    }

    /// <summary>Phase 18: cached analysis and validation services.</summary>
    private static void RegisterPhase18_AnalysisValidation(AshlarKernelRegistrationContext ctx)
    {
        IServiceCollection services = ctx.Services;

        // ── Analysis & validation ──────────────────────────────────────
        // Both services use a caching decorator (CachedAnalysis/
        // CachedValidation) to avoid re-running expensive analysis
        // or parsing when the same input appears within a scope.
        services.AddScoped<Ashlar.Core.Application.Analysis.Ports.IAnalysisService>(sp =>
        {
            var inner = new Ashlar.Infrastructure.Analysis.Adapters.AnalysisServiceAdapter(
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Ashlar.Infrastructure.Analysis.Adapters.AnalysisServiceAdapter>>(),
                sp.GetRequiredService<Ashlar.Infrastructure.Analysis.Rules.AnalysisRuleEngine>());
            ICacheStrategy cache = sp.GetRequiredService<ICacheStrategy>();
            Microsoft.Extensions.Logging.ILogger<Infrastructure.Analysis.Adapters.CachedAnalysisServiceAdapter> logger = sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Ashlar.Infrastructure.Analysis.Adapters.CachedAnalysisServiceAdapter>>();
            return new Ashlar.Infrastructure.Analysis.Adapters.CachedAnalysisServiceAdapter(inner, cache, logger);
        });

        services.AddScoped<Ashlar.Core.Application.Validation.Ports.IValidationService>(sp =>
        {
            var inner = new Ashlar.Infrastructure.Validation.Adapters.ValidationServiceAdapter(
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Ashlar.Infrastructure.Validation.Adapters.ValidationServiceAdapter>>(),
                sp.GetRequiredService<Ashlar.Infrastructure.Validation.Parsers.ITestResultParser>());
            ICacheStrategy cache = sp.GetRequiredService<ICacheStrategy>();
            Microsoft.Extensions.Logging.ILogger<Infrastructure.Validation.Adapters.CachedValidationServiceAdapter> logger = sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Ashlar.Infrastructure.Validation.Adapters.CachedValidationServiceAdapter>>();
            return new Ashlar.Infrastructure.Validation.Adapters.CachedValidationServiceAdapter(inner, cache, logger);
        });

        services.AddSingleton<ICacheStrategy, Ashlar.Infrastructure.Caching.MemoryCacheStrategy>();
        services.AddSingleton<IMetricsCollector, Ashlar.Infrastructure.Metrics.MemoryMetricsCollector>();
    }

    /// <summary>Phase 19: testing adapters and remote/docker execution platform.</summary>
    private static void RegisterPhase19_TestingAdapters(AshlarKernelRegistrationContext ctx)
    {
        IServiceCollection services = ctx.Services;
        AshlarHostingOptions options = ctx.Options;
        ModuleSelection modules = ctx.Modules;

        // ── Testing adapters ───────────────────────────────────────────
        services.AddScoped<Ashlar.Core.Application.Testing.Ports.ITestRunner, Ashlar.Infrastructure.Testing.TestRunnerAdapter>();

        // ASHLAR_EXECUTION_REMOTE_URL (URL string): when set, test
        //   execution is delegated to a remote execution service via
        //   HTTP instead of the local Docker-based platform.  Useful in
        //   CI environments where Docker-in-Docker is unavailable.
        if (modules.IncludeTestingAdapters)
        {
            string? executionRemoteUrl = options.ExecutionRemoteUrl ?? Environment.GetEnvironmentVariable("ASHLAR_EXECUTION_REMOTE_URL")?.Trim();
            if (!string.IsNullOrEmpty(executionRemoteUrl))
            {
                string baseUrl = executionRemoteUrl.TrimEnd('/') + "/";
                services.AddHttpClient("AshlarExecution", c => c.BaseAddress = new Uri(baseUrl));
                services.AddSingleton<Ashlar.Infrastructure.Testing.ExecutionPlatform.IExecutionPlatform>(sp =>
                {
                    IHttpClientFactory factory = sp.GetRequiredService<IHttpClientFactory>();
                    HttpClient client = factory.CreateClient("AshlarExecution");
                    Microsoft.Extensions.Logging.ILogger<Infrastructure.Testing.ExecutionPlatform.RemoteExecutionPlatform>? logger = sp.GetService<Microsoft.Extensions.Logging.ILogger<Ashlar.Infrastructure.Testing.ExecutionPlatform.RemoteExecutionPlatform>>();
                    return new Ashlar.Infrastructure.Testing.ExecutionPlatform.RemoteExecutionPlatform(client, logger);
                });
            }
            else
            {
                services.AddSingleton<Ashlar.Infrastructure.Testing.ExecutionPlatform.IExecutionPlatform>(sp =>
                    new Ashlar.Infrastructure.Testing.ExecutionPlatform.DockerExecutionPlatform(sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Ashlar.Infrastructure.Testing.ExecutionPlatform.DockerExecutionPlatform>>()));
            }

            services.AddSingleton<Ashlar.Infrastructure.Testing.Docker.IDockerService>(sp =>
                new Ashlar.Infrastructure.Testing.Docker.DockerService(sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Ashlar.Infrastructure.Testing.Docker.DockerService>>()));
            services.AddSingleton<Ashlar.Infrastructure.Testing.CodeAnalysis.ICodeAnalysisService>(sp =>
                new Ashlar.Infrastructure.Testing.CodeAnalysis.RoslynCodeAnalysisService(sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Ashlar.Infrastructure.Testing.CodeAnalysis.RoslynCodeAnalysisService>>()));
            services.AddArtifactCleanup();
        }

        // Swappable container workload scaler (null | kubernetes | compose). See docs/WorkloadScaling.md.
        Ashlar.Infrastructure.Scaling.Sdk.Extensions.WorkloadScalingServiceCollectionExtensions
            .AddAshlarWorkloadScaling(services, ctx.Configuration);
    }

    /// <summary>Phase 20: analysis rule engine and mesh-lab worker executor.</summary>
    private static void RegisterPhase20_AnalysisRuleEngine(AshlarKernelRegistrationContext ctx)
    {
        IServiceCollection services = ctx.Services;
        IConfiguration configuration = ctx.Configuration;

        // ── Tools registration ─────────────────────────────────────────
        // Tools from Tools.Assembly and Tools.Dev are registered here so
        // Infrastructure can use them via DI without direct ProjectReference.
        // This breaks the Infrastructure ↔ Tools circular dependency.
        services.AddSingleton<ITool, AssemblyAnalyzeTool>();
        services.AddSingleton<ITool, AssemblyDecompileTool>();
        services.AddSingleton<ITool, AssemblySecurityScanTool>();
        services.AddSingleton<ITool, DotnetTestTool>();
        
        // CleanArtifactsTool requires IArtifactCleanupService which may not be registered
        // in all scenarios (e.g., minimal test hosts). Check if registered before adding.
        if (services.Any(d => d.ServiceType == typeof(IArtifactCleanupService)))
        {
            services.AddSingleton<ITool>(sp => new CleanArtifactsTool(sp.GetRequiredService<IArtifactCleanupService>()));
        }

        // ── Analysis rule engine ───────────────────────────────────────
        // Rules are collected via DI multi-registration and fed into
        // the engine.  Add new IAnalysisRule implementations to extend
        // the static analysis suite without touching this file.
        // Rules now receive their tools via constructor injection.
        services.AddScoped<Ashlar.Infrastructure.Validation.Parsers.ITestResultParser, Ashlar.Infrastructure.Validation.Parsers.TrxTestResultParser>();
        
        services.AddScoped<Ashlar.Infrastructure.Analysis.Rules.IAnalysisRule>(sp =>
        {
            var logger = sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Ashlar.Infrastructure.Analysis.Rules.SecurityAnalysisRule>>();
            var securityTool = sp.GetServices<ITool>().First(t => t.Id == "assembly.security_scan");
            return new Ashlar.Infrastructure.Analysis.Rules.SecurityAnalysisRule(logger, securityTool);
        });
        
        services.AddScoped<Ashlar.Infrastructure.Analysis.Rules.IAnalysisRule>(sp =>
        {
            var logger = sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Ashlar.Infrastructure.Analysis.Rules.CodeQualityRule>>();
            var analyzeTool = sp.GetServices<ITool>().First(t => t.Id == "assembly.analyze");
            return new Ashlar.Infrastructure.Analysis.Rules.CodeQualityRule(logger, analyzeTool);
        });
        
        services.AddScoped<Ashlar.Infrastructure.Analysis.Rules.AnalysisRuleEngine>(sp =>
        {
            IEnumerable<Infrastructure.Analysis.Rules.IAnalysisRule> rules = sp.GetServices<Ashlar.Infrastructure.Analysis.Rules.IAnalysisRule>();
            Microsoft.Extensions.Logging.ILogger<Infrastructure.Analysis.Rules.AnalysisRuleEngine> logger = sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Ashlar.Infrastructure.Analysis.Rules.AnalysisRuleEngine>>();
            return new Ashlar.Infrastructure.Analysis.Rules.AnalysisRuleEngine(rules, logger);
        });

        services.AddAshlarMeshLabWorkerExecutor(configuration);

    }

}
