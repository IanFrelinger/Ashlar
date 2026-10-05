using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using Ashlar.Abstractions;
using Ashlar.Abstractions.Security.Egress;

namespace Ashlar.Mcp.Client;

/// <summary>
/// Owns the lifecycle of external MCP server connections and exposes their tools as
/// <see cref="McpToolProxy"/> instances through <see cref="IToolSource"/>.
///
/// Lifecycle and trust model:
/// <list type="bullet">
/// <item><description><b>Connect + pin at startup.</b> Each configured server is dialed once;
/// its advertised tools (optionally narrowed by an allowlist) are pinned — name, description,
/// and raw JSON schema captured verbatim. The pinned definition is the contract for the rest of
/// the process lifetime.</description></item>
/// <item><description><b>Drift faults, never follows.</b> A periodic re-list compares against
/// the pins; a changed or vanished definition marks that tool faulted (calls fail with a clear
/// reason, the tool disappears from <see cref="GetTools"/>). Remote servers do not get to
/// redefine what an agent's tool does mid-flight — restart to re-pin.</description></item>
/// <item><description><b>Failures contribute nothing.</b> A server that cannot be reached at
/// startup contributes zero tools (logged loudly); it does not fail host boot, because a remote
/// outage should degrade capability, not availability.</description></item>
/// </list>
/// </summary>
public sealed class McpClientConnectionManager : IHostedService, IToolSource, IAsyncDisposable
{
    private sealed class PinnedTool
    {
        public required string RemoteName { get; init; }
        public required string Description { get; init; }
        public required string SchemaRaw { get; init; }
        public volatile bool Faulted;
        public string? FaultReason;
    }

    private sealed class ServerConnection
    {
        public required McpServerEndpointOptions Options { get; init; }
        public required McpClient Client { get; init; }
        public required Dictionary<string, PinnedTool> Pinned { get; init; }
        public required List<McpToolProxy> Proxies { get; init; }
    }

    private readonly IOptions<AshlarMcpClientOptions> _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<McpClientConnectionManager> _logger;
    private readonly Dictionary<string, ServerConnection> _connections = new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _refreshCts;
    private Task? _refreshTask;

    /// <summary>
    /// Test seam: replaces the HTTP transport + <see cref="McpClient.CreateAsync"/> pipeline so
    /// protocol round-trip tests can hand in clients connected over in-memory streams.
    /// </summary>
    internal Func<McpServerEndpointOptions, CancellationToken, Task<McpClient>>? ClientFactoryOverride { get; set; }

    /// <summary>Creates the manager over bound options.</summary>
    public McpClientConnectionManager(
        IOptions<AshlarMcpClientOptions> options,
        ILoggerFactory loggerFactory,
        ILogger<McpClientConnectionManager> logger)
    {
        _options = options;
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var options = _options.Value;
        if (!options.Enabled)
        {
            _logger.LogDebug("MCP client disabled; no external servers dialed.");
            return;
        }

        foreach (var server in options.Servers)
        {
            try
            {
                // Per-server budget so one slow server cannot eat the whole host-start window.
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(options.ConnectTimeout);
                await ConnectAndPinAsync(server, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                _logger.LogError(ex,
                    "MCP server '{Server}' could not be connected; it contributes no tools this process lifetime.",
                    server.Name);
            }
        }

        var toolCount = _connections.Values.Sum(c => c.Proxies.Count);
        _logger.LogInformation(
            "MCP client started. Servers={ConnectedServers}/{ConfiguredServers} Tools={ToolCount}",
            _connections.Count, options.Servers.Count, toolCount);

        if (_connections.Count > 0 && options.ToolListRefreshInterval > TimeSpan.Zero)
        {
            _refreshCts = new CancellationTokenSource();
            _refreshTask = Task.Run(() => RefreshLoopAsync(_refreshCts.Token), CancellationToken.None);
        }
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_refreshCts is not null)
        {
            _refreshCts.Cancel();
            if (_refreshTask is not null)
            {
                try
                {
                    await _refreshTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Expected on shutdown.
                }
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<ITool> GetTools()
    {
        var tools = new List<ITool>();
        foreach (var connection in _connections.Values)
        {
            foreach (var proxy in connection.Proxies)
            {
                if (!connection.Pinned[proxy.RemoteToolName].Faulted)
                {
                    tools.Add(proxy);
                }
            }
        }

        return tools;
    }

    /// <summary>Invokes a proxied tool; called only by <see cref="McpToolProxy"/>.</summary>
    internal async Task<ToolResult> InvokeProxiedToolAsync(
        McpToolProxy proxy, JsonElement arguments, int tick, CancellationToken ct)
    {
        if (!_connections.TryGetValue(proxy.ServerName, out var connection))
        {
            return Failure(proxy.Id, tick, $"MCP server '{proxy.ServerName}' is not connected.");
        }

        var pinned = connection.Pinned[proxy.RemoteToolName];
        if (pinned.Faulted)
        {
            return Failure(proxy.Id, tick, $"Tool is faulted: {pinned.FaultReason}");
        }

        try
        {
            var result = await connection.Client
                .CallToolAsync(proxy.RemoteToolName, McpJsonBridge.ToArgumentDictionary(arguments), cancellationToken: ct)
                .ConfigureAwait(false);
            return McpJsonBridge.ToToolResult(result, proxy.Id, tick);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Transport/protocol failures surface as error payloads (repo tool convention) so the
            // calling agent sees them; details go to the host log.
            _logger.LogWarning(ex, "Proxied MCP tool call failed. Tool={ToolId}", proxy.Id);
            return Failure(proxy.Id, tick, $"Remote call failed: {ex.Message}");
        }
    }

    /// <summary>One drift-detection pass over every connected server; exposed for tests.</summary>
    internal async Task RefreshOnceAsync(CancellationToken ct)
    {
        foreach (var connection in _connections.Values)
        {
            IList<McpClientTool> listed;
            try
            {
                listed = await connection.Client
                    .ListToolsAsync((ModelContextProtocol.RequestOptions?)null, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Transient unreachability is not drift; existing pins stay valid.
                _logger.LogWarning(ex, "MCP drift check could not list server '{Server}'.", connection.Options.Name);
                continue;
            }

            var byName = listed.ToDictionary(t => t.Name, StringComparer.Ordinal);
            foreach (var pinned in connection.Pinned.Values)
            {
                if (pinned.Faulted)
                {
                    continue;
                }

                if (!byName.TryGetValue(pinned.RemoteName, out var current))
                {
                    Fault(connection, pinned, "the server no longer advertises this tool");
                }
                else if (!string.Equals(current.JsonSchema.GetRawText(), pinned.SchemaRaw, StringComparison.Ordinal) ||
                         !string.Equals(current.Description ?? string.Empty, pinned.Description, StringComparison.Ordinal))
                {
                    Fault(connection, pinned, "the server changed this tool's definition after it was pinned");
                }
            }

            foreach (var name in byName.Keys)
            {
                if (!connection.Pinned.ContainsKey(name))
                {
                    _logger.LogInformation(
                        "MCP server '{Server}' advertises new tool '{Tool}'; restart to pick it up (tool sets are pinned per process).",
                        connection.Options.Name, name);
                }
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _refreshCts?.Dispose();
        foreach (var connection in _connections.Values)
        {
            try
            {
                await connection.Client.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error disposing MCP client for server '{Server}'.", connection.Options.Name);
            }
        }
    }

    private async Task ConnectAndPinAsync(McpServerEndpointOptions server, CancellationToken ct)
    {
        var client = ClientFactoryOverride is not null
            ? await ClientFactoryOverride(server, ct).ConfigureAwait(false)
            : await CreateHttpClientAsync(server, ct).ConfigureAwait(false);

        var listed = await client.ListToolsAsync((ModelContextProtocol.RequestOptions?)null, ct).ConfigureAwait(false);

        var allow = server.AllowedTools.Count == 0
            ? null
            : new HashSet<string>(server.AllowedTools, StringComparer.Ordinal);

        var pinned = new Dictionary<string, PinnedTool>(StringComparer.Ordinal);
        var proxies = new List<McpToolProxy>();
        foreach (var tool in listed)
        {
            if (allow is not null && !allow.Contains(tool.Name))
            {
                continue;
            }

            var description = tool.Description ?? string.Empty;
            var schemaRaw = tool.JsonSchema.GetRawText();
            pinned[tool.Name] = new PinnedTool
            {
                RemoteName = tool.Name,
                Description = description,
                SchemaRaw = schemaRaw,
            };
            proxies.Add(new McpToolProxy(
                this,
                server.Name,
                tool.Name,
                new ToolSchema($"mcp:{server.Name}:{tool.Name}", description, schemaRaw)));
        }

        _connections[server.Name] = new ServerConnection
        {
            Options = server,
            Client = client,
            Pinned = pinned,
            Proxies = proxies,
        };

        _logger.LogInformation(
            "MCP server '{Server}' connected; pinned {ToolCount} tool(s){Narrowed}.",
            server.Name, proxies.Count, allow is null ? string.Empty : " (allowlist-narrowed)");
    }

    private async Task<McpClient> CreateHttpClientAsync(McpServerEndpointOptions server, CancellationToken ct)
    {
        var transportOptions = new HttpClientTransportOptions
        {
            Endpoint = new Uri(server.Url!, UriKind.Absolute),
        };

        if (!string.IsNullOrWhiteSpace(server.ApiKeyHeader))
        {
            // Secret comes from the environment at connect time; it never sits in bound config.
            var secret = Environment.GetEnvironmentVariable(server.ApiKeyEnvVar!)
                ?? throw new InvalidOperationException(
                    $"Environment variable '{server.ApiKeyEnvVar}' for MCP server '{server.Name}' is unset.");
            transportOptions.AdditionalHeaders = new Dictionary<string, string>
            {
                [server.ApiKeyHeader!] = secret,
            };
        }

        // The same client the two-argument constructor builds (new HttpClient(), owned by the transport), with the
        // egress guard in front of it.
        var transport = new HttpClientTransport(
            transportOptions,
            EgressHttp.CreateClient(EgressFamilies.Mcp, "EG-XPT-06"),
            _loggerFactory,
            ownsHttpClient: true);
        return await McpClient.CreateAsync(transport, clientOptions: null, loggerFactory: _loggerFactory, cancellationToken: ct)
            .ConfigureAwait(false);
    }

    private async Task RefreshLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_options.Value.ToolListRefreshInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                await RefreshOnceAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown path.
        }
    }

    private void Fault(ServerConnection connection, PinnedTool pinned, string reason)
    {
        pinned.FaultReason = reason;
        pinned.Faulted = true;
        _logger.LogError(
            "MCP tool 'mcp:{Server}:{Tool}' faulted: {Reason}. It is withdrawn from toolboxes until restart.",
            connection.Options.Name, pinned.RemoteName, reason);
    }

    private static ToolResult Failure(string toolId, int tick, string error)
        => new(new McpProxyDelta(tick, $"{toolId} error"), new McpToolFailure(error));
}
