using Ashlar.Abstractions.Barriers;
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
    /// <see cref="Ashlar.Core.Domain.Execution.IBrickRegistry"/>, which <c>AddAshlar()</c> registers.
    /// </summary>
    /// <remarks>
    /// Records are written to every registered <see cref="IBarrierAuditSink"/> (configure them with
    /// <c>AddBarrierAuditSinks</c> and <c>Ashlar:Audit:Sinks</c>), not through the container's
    /// <see cref="IBarrierAuditLog"/>: the <c>AddAshlar()</c> composition registers a no-op one, which would
    /// discard them however the sinks are configured. A record any sink fails to write turns the call into a tool
    /// error that withholds the result (the File sink is awaited until the record is flushed). With no sink
    /// registered a warning is logged that records are discarded.
    /// </remarks>
    public static IServiceCollection AddAshlarMcpBricks(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<AshlarMcpBrickOptions>().Bind(configuration.GetSection(AshlarMcpBrickOptions.SectionPath));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IMcpToolContributor, BrickMcpToolContributor>(sp =>
            ActivatorUtilities.CreateInstance<BrickMcpToolContributor>(sp, new ConfirmedBarrierAuditLog(
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ConfirmedBarrierAuditLog>>(),
                sp.GetServices<IBarrierAuditSink>()))));
        return services;
    }
}
