using Ashlar.Mcp.Server;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ashlar.Mcp.Bricks;

/// <summary>Registration for <see cref="BrickMcpToolContributor"/>.</summary>
public static class AshlarMcpBricksServiceCollectionExtensions
{
    /// <summary>
    /// Adds the brick contributor to the MCP catalog and binds <see cref="AshlarMcpBrickOptions"/>
    /// from <see cref="AshlarMcpBrickOptions.SectionPath"/>. Call alongside
    /// <c>AddAshlarMcpServer(configuration)</c>; the contributor needs an
    /// <see cref="Ashlar.Core.Domain.Execution.IBrickRegistry"/> and an
    /// <see cref="Ashlar.Abstractions.Barriers.IBarrierAuditLog"/>, both of which <c>AddAshlar()</c> registers.
    /// Records reach disk only when an audit sink is configured (<c>Ashlar:Audit:Sinks</c>, see
    /// <c>AddBarrierAuditSinks</c>); with none, the runtime logs that events are discarded.
    /// </summary>
    public static IServiceCollection AddAshlarMcpBricks(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<AshlarMcpBrickOptions>().Bind(configuration.GetSection(AshlarMcpBrickOptions.SectionPath));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IMcpToolContributor, BrickMcpToolContributor>());
        return services;
    }
}
