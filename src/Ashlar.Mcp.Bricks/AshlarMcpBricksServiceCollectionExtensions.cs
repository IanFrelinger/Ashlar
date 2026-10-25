using Ashlar.Abstractions.Barriers;
using Ashlar.Mcp.Server;
using Ashlar.Runtime.Barriers;
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
    /// <see cref="Ashlar.Core.Domain.Execution.IBrickRegistry"/>, which <c>AddAshlar()</c> registers.
    /// </summary>
    /// <remarks>
    /// Records are written to every registered <see cref="IBarrierAuditSink"/> (configure them with
    /// <c>AddBarrierAuditSinks</c> and <c>Ashlar:Audit:Sinks</c>), not through the container's
    /// <see cref="IBarrierAuditLog"/>: the <c>AddAshlar()</c> composition registers a no-op one, which would
    /// discard them however the sinks are configured. With no sink registered the runtime logs a warning that
    /// events are discarded.
    /// </remarks>
    public static IServiceCollection AddAshlarMcpBricks(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<AshlarMcpBrickOptions>().Bind(configuration.GetSection(AshlarMcpBrickOptions.SectionPath));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IMcpToolContributor, BrickMcpToolContributor>(sp =>
            ActivatorUtilities.CreateInstance<BrickMcpToolContributor>(sp, new StructuredBarrierAuditLog(
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<StructuredBarrierAuditLog>>(),
                sp.GetServices<IBarrierAuditSink>()))));
        return services;
    }
}
