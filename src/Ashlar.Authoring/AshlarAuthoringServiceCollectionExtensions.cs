using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Ashlar.Core.Application.Generation;
using Ashlar.Core.Application.Generation.Ports;
using Ashlar.Core.Domain.Bricks;
using Ashlar.Core.Domain.Bricks.Ports;

namespace Ashlar.Authoring;

/// <summary>
/// Stable registration helpers for code-authored Ashlar bricks and agent profiles.
/// </summary>
public static class AshlarAuthoringServiceCollectionExtensions
{
    /// <summary>
    /// Registers a code-authored brick for the host's brick registry, as a
    /// <see cref="BrickTypeRegistration"/>. The registry that <c>AddAshlar()</c> builds creates it, so this
    /// package does not reference the host.
    /// </summary>
    public static IServiceCollection AddAshlarBrick<TBrick>(this IServiceCollection services)
        where TBrick : DomainBrick
    {
        services.AddSingleton(new BrickTypeRegistration(typeof(TBrick)));
        return services;
    }

    /// <summary>
    /// Ensures <see cref="IAgentProfileRegistry"/> is available and registers
    /// the fixed <see cref="GenerativeArtifactBrick"/>.
    /// </summary>
    public static IServiceCollection AddAshlarGenerativeAgents(this IServiceCollection services)
    {
        services.TryAddSingleton<IAgentProfileRegistry>(sp =>
            new AgentProfileRegistry(sp.GetServices<IAgentProfileSource>(), sp));
        // These two lines register the brick for two different consumers, and the
        // container therefore holds TWO GenerativeArtifactBrick objects:
        //
        //   TryAddSingleton    the DI singleton, for anything resolving the brick
        //                      type directly.
        //   AddAshlarBrick       adds a BrickTypeRegistration, which the brick
        //                      registry feeds to ActivatorUtilities.CreateInstance
        //                      when it is built — constructing a fresh instance
        //                      rather than resolving the singleton above.
        //
        // Left as-is on purpose. The brick is stateless, so two instances are
        // harmless, and routing the registry through the container instead would
        // change instance identity for EVERY additional brick type, not just this
        // one — a behaviour change well outside a DI-composition cleanup.
        services.TryAddSingleton<GenerativeArtifactBrick>();
        return services.AddAshlarBrick<GenerativeArtifactBrick>();
    }

    /// <summary>
    /// Registers an agent profile factory (mirrors <see cref="AddAshlarBrick{TBrick}"/>).
    /// Profiles are loaded when the registry is constructed — zero edits to the fixed engine.
    /// </summary>
    public static IServiceCollection AddAshlarAgentProfile<TProfile>(this IServiceCollection services)
        where TProfile : class, IAgentProfileFactory
    {
        services.TryAddSingleton<IAgentProfileRegistry>(sp =>
            new AgentProfileRegistry(sp.GetServices<IAgentProfileSource>(), sp));
        services.AddSingleton<TProfile>();
        services.AddSingleton<IAgentProfileSource>(sp => new FactoryProfileSource<TProfile>(sp));
        return services;
    }

    private sealed class FactoryProfileSource<TProfile> : IAgentProfileSource
        where TProfile : class, IAgentProfileFactory
    {
        public FactoryProfileSource(IServiceProvider _) { }
        public AgentProfile Create(IServiceProvider services) =>
            services.GetRequiredService<TProfile>().Create(services);
    }
}
