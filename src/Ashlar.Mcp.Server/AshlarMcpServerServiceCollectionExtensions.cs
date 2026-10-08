using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Ashlar.Mcp.Server;

/// <summary>
/// DI composition for the Ashlar MCP server bridge.
///
/// Deliberately host-composed rather than kernel-registered: endpoint/protocol exposure is a
/// host decision (mirroring the gRPC server precedent), and keeping the ModelContextProtocol
/// dependency out of Ashlar.Hosting keeps it out of every AddAshlar consumer's package graph.
/// </summary>
public static class AshlarMcpServerServiceCollectionExtensions
{
    /// <summary>
    /// Registers the MCP server bridge and the underlying MCP server. The returned
    /// <see cref="IMcpServerBuilder"/> still needs a transport chosen by the host:
    /// <c>.WithAshlarHttpTransport()</c> (which registers the HTTP marker, then <c>WithHttpTransport()</c>) for web
    /// hosts followed by
    /// <see cref="AshlarMcpEndpointRouteBuilderExtensions.MapAshlarMcpEndpoint"/>, or
    /// <c>.WithStdioServerTransport()</c> for console hosts.
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <param name="configuration">Configuration root to bind <see cref="AshlarMcpServerOptions"/> from.</param>
    /// <param name="sectionPath">Configuration section (defaults to <c>Ashlar:Mcp:Server</c>).</param>
    public static IMcpServerBuilder AddAshlarMcpServer(
        this IServiceCollection services,
        IConfiguration configuration,
        string sectionPath = AshlarMcpServerOptions.SectionPath)
    {
        services.AddOptions<AshlarMcpServerOptions>()
            .Bind(configuration.GetSection(sectionPath))
            .ValidateOnStart();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<AshlarMcpServerOptions>, ValidateAshlarMcpServerOptions>());

        // TryAdd for the seams hosts may legitimately replace (world model, policy gate);
        // TryAddEnumerable for contributors so host-added contributors compose instead of clobber.
        services.TryAddSingleton<IMcpWorldSnapshotFactory, OptionsMcpWorldSnapshotFactory>();
        services.TryAddSingleton<IMcpInvocationGate, PolicyMcpInvocationGate>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IMcpToolContributor, ToolboxMcpToolContributor>());
        services.AddSingleton<AshlarMcpToolBridge>();
        services.AddHostedService<McpCatalogStartupValidator>();

        // ServerInfo depends on bound AshlarMcpServerOptions, so it is configured through the
        // options pipeline rather than captured at registration time.
        services.AddOptions<McpServerOptions>().Configure<IOptions<AshlarMcpServerOptions>>((mcp, ashlar) =>
        {
            mcp.ServerInfo = new Implementation
            {
                Name = ashlar.Value.ServerName,
                Version = ashlar.Value.ServerVersion ?? DefaultServerVersion(),
            };
        });

        // Dynamic list/call handlers instead of pre-materialized McpServerTool instances: the
        // catalog is derived from options + DI at runtime, and Ashlar tools carry hand-authored
        // JSON schemas that reflection-based tool factories would mangle.
        return services.AddMcpServer()
            .WithListToolsHandler(static async (ctx, ct) =>
                await ResolveBridge(ctx.Services ?? ctx.Server.Services).ListToolsAsync(ct).ConfigureAwait(false))
            .WithCallToolHandler(static async (ctx, ct) =>
            {
                var parameters = ctx.Params
                    ?? throw new McpProtocolException("Missing tools/call parameters.", McpErrorCode.InvalidParams);
                return await ResolveBridge(ctx.Services ?? ctx.Server.Services)
                    .CallToolAsync(parameters, ctx.User, ct).ConfigureAwait(false);
            });
    }

    private static AshlarMcpToolBridge ResolveBridge(IServiceProvider? services)
    {
        var provider = services
            ?? throw new InvalidOperationException(
                "No service provider available on the MCP request context; " +
                "AddAshlarMcpServer must be used with the hosting integration.");
        return provider.GetRequiredService<AshlarMcpToolBridge>();
    }

    /// <summary>
    /// HTTP transport for an Ashlar MCP server. Registers <see cref="AshlarMcpHttpTransportMarker"/> and then
    /// the ModelContextProtocol HTTP transport. The third-party <c>WithHttpTransport</c> cannot register an
    /// Ashlar marker; the validator also detects its underlying transport registration, so direct SDK callers are refused.
    /// </summary>
    public static IMcpServerBuilder WithAshlarHttpTransport(this IMcpServerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddSingleton(new AshlarMcpHttpTransportMarker());
        return builder.WithHttpTransport();
    }

    private static string DefaultServerVersion() =>
        typeof(AshlarMcpServerServiceCollectionExtensions).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "0.0.0";
}
