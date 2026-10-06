using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Ashlar.AI.Pipeline.Clients;
using Ashlar.AI.Pipeline.Embeddings;
using Ashlar.AI.Pipeline.Governance;
using Ashlar.AI.Pipeline.Rag;
using Ashlar.AI.Pipeline.Routing;
// MEAI 10.9.0 ships its own Microsoft.Extensions.AI.RoutingChatClient; alias the Ashlar router
// explicitly so the simple name keeps binding to ours (CS0104 otherwise).
using RoutingChatClient = Ashlar.AI.Pipeline.Routing.RoutingChatClient;

namespace Ashlar.AI.Pipeline;

/// <summary>
/// DI registration for the MEAI chat pipeline (feature-flagged).
/// </summary>
public static class MeaiPipelineServiceCollectionExtensions
{
    /// <summary>
    /// Returns true when the MEAI pipeline should be registered.
    /// Phase 6+: defaults to <c>true</c>. Opt out with config <c>false</c>/<c>0</c>
    /// or env <c>ASHLAR_USE_MEAI_PIPELINE=0|false</c>.
    /// </summary>
    public static bool IsMeaiPipelineEnabled(IConfiguration? configuration, bool? explicitEnable = null)
    {
        if (explicitEnable.HasValue)
        {
            return explicitEnable.Value;
        }

        if (configuration is not null)
        {
            var flagged = configuration[MeaiPipelineOptions.FeatureFlagKey];
            if (!string.IsNullOrWhiteSpace(flagged))
            {
                if (bool.TryParse(flagged, out var parsed))
                {
                    return parsed;
                }

                if (string.Equals(flagged, "1", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (string.Equals(flagged, "0", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
        }

        var env = Environment.GetEnvironmentVariable(MeaiPipelineOptions.FeatureFlagEnvVar);
        if (string.Equals(env, "0", StringComparison.OrdinalIgnoreCase)
            || string.Equals(env, "false", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.Equals(env, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(env, "true", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Phase 6 default: MEAI pipeline on.
        return true;
    }

    /// <summary>
    /// Registers keyed local (and optional Bedrock) governed pipelines plus an auditing router.
    /// Raw provider clients are never registered in DI.
    /// </summary>
    public static IServiceCollection AddAshlarMeaiPipeline(
        this IServiceCollection services,
        IConfiguration? configuration = null,
        Action<MeaiPipelineOptions>? configure = null,
        Func<IServiceProvider, IChatClient>? ollamaInnerFactory = null,
        Func<IServiceProvider, IChatClient>? onnxInnerFactory = null,
        Func<IServiceProvider, string, IChatClient>? bedrockInnerFactory = null,
        bool registerDefaultRouter = true)
    {
        var options = new MeaiPipelineOptions();
        if (configuration is not null)
        {
            BindSection(configuration.GetSection(MeaiPipelineOptions.SectionName), options);
        }

        configure?.Invoke(options);
        ApplyBedrockAllowListDefaults(options);
        // Options pipeline, not Options.Create: IValidateOptions and a later Configure must see this instance.
        // The local snapshot above is what RegisterGovernanceDefaults and the Bedrock tier registration capture.
        services.AddOptions<MeaiPipelineOptions>()
            .Configure(destination => CopyMeaiPipelineOptions(options, destination))
            .ValidateOnStart();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<MeaiPipelineOptions>, ValidateAirGappedMeaiBedrockOptions>());

        RegisterGovernanceDefaults(services, options);
        RegisterRoutingDefaults(services);
        services.TryAddSingleton<IBedrockChatClientFactory, AwsBedrockChatClientFactory>();

        Func<IServiceProvider, IChatClient> defaultOllama = sp =>
            new OllamaHttpChatClient(sp.GetRequiredService<IOptions<MeaiPipelineOptions>>());
        Func<IServiceProvider, IChatClient> defaultOnnx = sp =>
            new LlamaSharpChatClient(sp.GetRequiredService<IOptions<MeaiPipelineOptions>>());

        services.AddKeyedChatClient(
                MeaiTargetKeys.LocalOllama,
                sp => (ollamaInnerFactory ?? defaultOllama)(sp))
            .UseAshlarGovernance(MeaiTargetKeys.LocalOllama);

        services.AddKeyedChatClient(
                MeaiTargetKeys.LocalOnnx,
                sp => (onnxInnerFactory ?? defaultOnnx)(sp))
            .UseAshlarGovernance(MeaiTargetKeys.LocalOnnx);

        if (options.Bedrock.Enabled)
        {
            RegisterBedrockTier(services, options, bedrockInnerFactory);
        }

        RegisterVectorDataRag(services);

        if (registerDefaultRouter)
        {
            services.AddChatClient(sp =>
            {
                var router = ActivatorUtilities.CreateInstance<RoutingChatClient>(sp);
                var auditor = sp.GetRequiredService<IChatInvocationAuditor>();
                return new AuditingChatClient(router, auditor, RoutingChatClient.RouterTargetKey);
            });
        }

        return services;
    }

    /// <summary>
    /// Registers VectorData RAG (in-process store + governed embeddings). Legacy RAG remains until Phase 6.
    /// </summary>
    public static IServiceCollection RegisterVectorDataRag(this IServiceCollection services)
    {
        services.TryAddSingleton<InProcessVectorStore>();
        services.TryAddSingleton(sp =>
        {
            var store = sp.GetRequiredService<InProcessVectorStore>();
            return store.GetCollection<string, ChunkRecord>(VectorDataRagService.DefaultCollectionName);
        });
        services.TryAddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp =>
        {
            // Same stack as chat governance: Sanitizing → Auditing → provider.
            // Auditing must sit inside Sanitizing so SanitizationCallContext is visible
            // while Emit runs (AsyncLocal does not flow back to an outer awaiter).
            IEmbeddingGenerator<string, Embedding<float>> inner = new TokenHashEmbeddingGenerator();
            var sanitizer = sp.GetRequiredService<IChatMessageSanitizer>();
            var auditor = sp.GetRequiredService<IChatInvocationAuditor>();
            inner = new AuditingEmbeddingGenerator(inner, auditor);
            inner = new SanitizingEmbeddingGenerator(inner, sanitizer, SanitizeDisposition.Redact);
            return inner;
        });
        services.TryAddSingleton<VectorDataRagService>();
        return services;
    }

    /// <summary>
    /// Registers a governed keyed <see cref="IChatClient"/> for an additional target.
    /// </summary>
    public static ChatClientBuilder AddAshlarGovernedChatClient(
        this IServiceCollection services,
        string targetKey,
        Func<IServiceProvider, IChatClient> innerFactory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetKey);
        RegisterGovernanceDefaults(services);
        return services.AddKeyedChatClient(targetKey, innerFactory)
            .UseAshlarGovernance(targetKey);
    }

    /// <summary>Registers default governance services if not already present.</summary>
    public static IServiceCollection RegisterGovernanceDefaults(this IServiceCollection services) =>
        RegisterGovernanceDefaults(services, options: null);

    /// <summary>Registers default governance services, applying cloud allow-list from options.</summary>
    public static IServiceCollection RegisterGovernanceDefaults(
        this IServiceCollection services,
        MeaiPipelineOptions? options)
    {
        services.TryAddSingleton<IChatTargetAccessPolicy>(_ =>
        {
            var policy = new DefaultChatTargetAccessPolicy();
            if (options?.AllowedCloudTargets is { Count: > 0 } allowed)
            {
                foreach (var key in allowed)
                {
                    if (!string.IsNullOrWhiteSpace(key))
                    {
                        policy.AllowedCloudTargets.Add(key.Trim());
                    }
                }
            }

            return policy;
        });
        services.TryAddSingleton<IChatMessageSanitizer, DefaultChatMessageSanitizer>();
        services.TryAddSingleton<ITargetSanitizePolicy, DefaultTargetSanitizePolicy>();
        services.TryAddSingleton<IChatInvocationAuditor, InMemoryChatInvocationAuditor>();
        return services;
    }

    /// <summary>Registers default routing services if not already present.</summary>
    public static IServiceCollection RegisterRoutingDefaults(this IServiceCollection services)
    {
        services.TryAddSingleton<IRouteCandidateTable, DefaultRouteCandidateTable>();
        services.TryAddSingleton<ITargetAvailability, InMemoryTargetAvailability>();
        services.TryAddSingleton<IChatRouter, LocalFirstChatRouter>();
        return services;
    }

    private static void RegisterBedrockTier(
        IServiceCollection services,
        MeaiPipelineOptions options,
        Func<IServiceProvider, string, IChatClient>? bedrockInnerFactory)
    {
        RegisterTier(
            services,
            DefaultRouteCandidateTable.CloudBedrockFast,
            options.Bedrock.FastModelId,
            bedrockInnerFactory);
        RegisterTier(
            services,
            DefaultRouteCandidateTable.CloudBedrockBalanced,
            options.Bedrock.BalancedModelId,
            bedrockInnerFactory);
        RegisterTier(
            services,
            DefaultRouteCandidateTable.CloudBedrockHeavy,
            options.Bedrock.HeavyModelId,
            bedrockInnerFactory);
    }

    private static void RegisterTier(
        IServiceCollection services,
        string targetKey,
        string modelId,
        Func<IServiceProvider, string, IChatClient>? bedrockInnerFactory)
    {
        services.AddKeyedChatClient(
                targetKey,
                sp =>
                {
                    if (bedrockInnerFactory is not null)
                    {
                        return bedrockInnerFactory(sp, modelId);
                    }

                    return sp.GetRequiredService<IBedrockChatClientFactory>().Create(modelId);
                })
            .UseAshlarGovernance(targetKey);
    }

    private static void ApplyBedrockAllowListDefaults(MeaiPipelineOptions options)
    {
        if (!options.Bedrock.Enabled)
        {
            return;
        }

        void Add(string key)
        {
            if (!options.AllowedCloudTargets.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                options.AllowedCloudTargets.Add(key);
            }
        }

        Add(DefaultRouteCandidateTable.CloudBedrockFast);
        Add(DefaultRouteCandidateTable.CloudBedrockBalanced);
        Add(DefaultRouteCandidateTable.CloudBedrockHeavy);
    }

    private static void BindSection(IConfiguration section, MeaiPipelineOptions options)
    {
        var ollamaBase = section["OllamaBaseUrl"];
        if (!string.IsNullOrWhiteSpace(ollamaBase))
        {
            options.OllamaBaseUrl = ollamaBase;
        }

        var ollamaModel = section["OllamaModel"];
        if (!string.IsNullOrWhiteSpace(ollamaModel))
        {
            options.OllamaModel = ollamaModel;
        }

        var localPath = section["LocalModelPath"];
        if (!string.IsNullOrWhiteSpace(localPath))
        {
            options.LocalModelPath = localPath;
        }

        if (int.TryParse(section["LocalContextSize"], out var ctx) && ctx > 0)
        {
            options.LocalContextSize = ctx;
        }

        if (int.TryParse(section["LocalMaxTokens"], out var max) && max > 0)
        {
            options.LocalMaxTokens = max;
        }

        var bedrock = section.GetSection("Bedrock");
        if (bedrock.Exists())
        {
            if (bool.TryParse(bedrock["Enabled"], out var enabled))
            {
                options.Bedrock.Enabled = enabled;
            }

            var region = bedrock["Region"];
            if (!string.IsNullOrWhiteSpace(region))
            {
                options.Bedrock.Region = region;
            }

            var fast = bedrock["FastModelId"];
            if (!string.IsNullOrWhiteSpace(fast))
            {
                options.Bedrock.FastModelId = fast;
            }

            var balanced = bedrock["BalancedModelId"];
            if (!string.IsNullOrWhiteSpace(balanced))
            {
                options.Bedrock.BalancedModelId = balanced;
            }

            var heavy = bedrock["HeavyModelId"];
            if (!string.IsNullOrWhiteSpace(heavy))
            {
                options.Bedrock.HeavyModelId = heavy;
            }
        }

        var allowed = section.GetSection("AllowedCloudTargets").GetChildren();
        foreach (var child in allowed)
        {
            if (!string.IsNullOrWhiteSpace(child.Value))
            {
                options.AllowedCloudTargets.Add(child.Value.Trim());
            }
        }
    }

    private static void CopyMeaiPipelineOptions(MeaiPipelineOptions source, MeaiPipelineOptions destination)
    {
        destination.OllamaBaseUrl = source.OllamaBaseUrl;
        destination.OllamaModel = source.OllamaModel;
        destination.LocalModelPath = source.LocalModelPath;
        destination.LocalContextSize = source.LocalContextSize;
        destination.LocalMaxTokens = source.LocalMaxTokens;
        destination.Bedrock = source.Bedrock;
        destination.AllowedCloudTargets = source.AllowedCloudTargets;
    }
}
