namespace Ashlar.Mcp.Server;

/// <summary>
/// Present in DI only when the host chose MCP over HTTP via
/// <see cref="AshlarMcpServerServiceCollectionExtensions.WithAshlarHttpTransport"/>.
/// Stdio (<c>WithStdioServerTransport</c>) does not register it.
/// SecureWorkstation refuses an enabled MCP server when this marker is present; AirGapped refuses
/// an enabled server whether or not it is.
/// </summary>
public sealed class AshlarMcpHttpTransportMarker
{
    internal AshlarMcpHttpTransportMarker()
    {
    }
}
