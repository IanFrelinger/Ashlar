using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Ashlar.Abstractions;

namespace Ashlar.Mcp.Server;

/// <summary>
/// Central orchestrator between the MCP server handlers and Ashlar tool contributors.
///
/// Every MCP <c>tools/list</c> and <c>tools/call</c> flows through here so that security controls
/// live in exactly one place: allowlist-derived catalog, operator argument overrides, policy
/// gating via <see cref="IMcpInvocationGate"/>, a concurrency ceiling, and audit logging.
/// Contributors are never invoked except through this bridge.
/// </summary>
public sealed class AshlarMcpToolBridge
{
    private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement.Clone();

    private readonly IReadOnlyList<IMcpToolContributor> _contributors;
    private readonly IMcpInvocationGate _gate;
    private readonly IMcpWorldSnapshotFactory _snapshots;
    private readonly IOptions<AshlarMcpServerOptions> _options;
    private readonly ILogger<AshlarMcpToolBridge> _logger;
    private readonly IMcpRefusalAuditor? _refusalAuditor;
    private readonly SemaphoreSlim _concurrency;

    // Volatile snapshot of the last-built catalog, used to route tools/call without a rebuild.
    // tools/list always rebuilds, so a client that re-lists sees contributor changes.
    private volatile IReadOnlyDictionary<string, CatalogEntry>? _catalog;

    private sealed record CatalogEntry(AshlarMcpToolDescriptor Descriptor, IMcpToolContributor Contributor);

    /// <summary>Creates the bridge over all registered contributors.</summary>
    /// <param name="contributors">Every registered contributor.</param>
    /// <param name="gate">The policy gate.</param>
    /// <param name="snapshots">World snapshots for the gate.</param>
    /// <param name="options">Server options.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="refusalAuditors">Records the refusals made here; the last registered one is used. None: they are only logged.</param>
    public AshlarMcpToolBridge(
        IEnumerable<IMcpToolContributor> contributors,
        IMcpInvocationGate gate,
        IMcpWorldSnapshotFactory snapshots,
        IOptions<AshlarMcpServerOptions> options,
        ILogger<AshlarMcpToolBridge> logger,
        IEnumerable<IMcpRefusalAuditor>? refusalAuditors = null)
    {
        _contributors = contributors.ToList();
        _gate = gate;
        _snapshots = snapshots;
        _options = options;
        _logger = logger;
        _refusalAuditor = refusalAuditors?.LastOrDefault();
        _concurrency = new SemaphoreSlim(Math.Max(1, options.Value.MaxConcurrentToolCalls));
    }

    /// <summary>
    /// Builds the catalog once and throws on invalid definitions. Called from the startup
    /// validator so misconfiguration (missing tools, bad schemas, name collisions) prevents boot
    /// instead of surfacing on the first client request.
    /// </summary>
    public async Task ValidateCatalogAsync(CancellationToken ct)
    {
        var catalog = await BuildCatalogAsync(ct).ConfigureAwait(false);
        _logger.LogInformation("MCP tool catalog validated. ToolCount={ToolCount}", catalog.Count);
    }

    /// <summary>Handles MCP <c>tools/list</c>.</summary>
    public async ValueTask<ListToolsResult> ListToolsAsync(CancellationToken ct)
    {
        var catalog = await BuildCatalogAsync(ct).ConfigureAwait(false);
        var tools = new List<Tool>(catalog.Count);
        foreach (var entry in catalog.Values)
        {
            var descriptor = entry.Descriptor;
            tools.Add(new Tool
            {
                Name = descriptor.McpName,
                // Surface the canonical id to humans when sanitization changed it (e.g. repo.fs.read
                // is advertised as repo_fs_read); Title is display-only per spec.
                Title = descriptor.CanonicalId == descriptor.McpName ? null : descriptor.CanonicalId,
                Description = descriptor.Description,
                InputSchema = descriptor.InputSchema,
            });
        }

        return new ListToolsResult { Tools = tools };
    }

    /// <summary>Handles MCP <c>tools/call</c>.</summary>
    public async ValueTask<CallToolResult> CallToolAsync(CallToolRequestParams parameters, ClaimsPrincipal? user, CancellationToken ct)
    {
        var caller = user?.Identity?.Name ?? "anonymous";
        var name = parameters.Name;

        var catalog = _catalog ?? await BuildCatalogAsync(ct).ConfigureAwait(false);
        if (!catalog.TryGetValue(name, out var entry))
        {
            // One rebuild covers the "listed before a contributor refresh" race; a second miss is
            // a genuinely unknown tool, which the spec treats as a protocol-level error.
            catalog = await BuildCatalogAsync(ct).ConfigureAwait(false);
            if (!catalog.TryGetValue(name, out entry))
            {
                var shown = Shown(name);
                _logger.LogWarning("MCP call to unknown tool. Tool={ToolName} Caller={Caller}", shown, caller);
                var text = await AuditRefusalAsync(name, canonicalId: null, RawArguments(parameters), $"Unknown tool '{shown}'.", ct)
                    .ConfigureAwait(false);
                throw new McpProtocolException(text, McpErrorCode.InvalidParams);
            }
        }

        var canonicalId = entry.Descriptor.CanonicalId;
        var arguments = BuildArguments(parameters, canonicalId);

        var decision = _gate.Authorize(new ToolCall(canonicalId, arguments), _snapshots.Create(canonicalId));
        if (!decision.Allowed)
        {
            _logger.LogWarning(
                "MCP tool call denied. Tool={ToolId} Caller={Caller} Reason={Reason}",
                canonicalId, caller, decision.Reason);
            return ErrorResult(await AuditRefusalAsync(
                name, canonicalId, arguments, $"Call to '{name}' was denied: {decision.Reason}", ct).ConfigureAwait(false));
        }

        await _concurrency.WaitAsync(ct).ConfigureAwait(false);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var outcome = await entry.Contributor.InvokeAsync(canonicalId, arguments, ct).ConfigureAwait(false);

            _logger.LogInformation(
                "MCP tool call completed. Tool={ToolId} Caller={Caller} IsError={IsError} DurationMs={DurationMs}",
                canonicalId, caller, outcome.IsError, stopwatch.ElapsedMilliseconds);

            var result = new CallToolResult
            {
                IsError = outcome.IsError,
                Content = [new TextContentBlock { Text = outcome.Text }],
            };
            if (outcome.Payload is not null and not string)
            {
                result.StructuredContent = JsonSerializer.SerializeToElement(outcome.Payload);
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (McpProtocolException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Contributor contract says business failures come back as outcomes; anything thrown
            // is unexpected. Keep the wire message generic — internals stay in the host log.
            _logger.LogError(ex,
                "MCP tool call crashed. Tool={ToolId} Caller={Caller} DurationMs={DurationMs}",
                canonicalId, caller, stopwatch.ElapsedMilliseconds);
            return ErrorResult($"Tool '{name}' failed with an internal error.");
        }
        finally
        {
            _concurrency.Release();
        }
    }

    private async Task<IReadOnlyDictionary<string, CatalogEntry>> BuildCatalogAsync(CancellationToken ct)
    {
        var catalog = new Dictionary<string, CatalogEntry>(StringComparer.Ordinal);
        foreach (var contributor in _contributors)
        {
            foreach (var descriptor in await contributor.DescribeAsync(ct).ConfigureAwait(false))
            {
                if (catalog.TryGetValue(descriptor.McpName, out var existing))
                {
                    // Sanitization is not injective; refuse ambiguous catalogs instead of letting
                    // one tool silently shadow another (mirrors the fail-closed posture everywhere
                    // else on this surface).
                    throw new InvalidOperationException(
                        $"MCP tool name collision: '{descriptor.McpName}' maps to both " +
                        $"'{existing.Descriptor.CanonicalId}' and '{descriptor.CanonicalId}'. " +
                        "Rename one of the tools or narrow the allowlist.");
                }

                catalog.Add(descriptor.McpName, new CatalogEntry(descriptor, contributor));
            }
        }

        _catalog = catalog;
        return catalog;
    }

    private async Task<string> AuditRefusalAsync(
        string requestedName, string? canonicalId, JsonElement arguments, string message, CancellationToken ct)
    {
        if (_refusalAuditor is null)
        {
            return message;
        }

        try
        {
            return await _refusalAuditor.RecordRefusalAsync(requestedName, canonicalId, arguments, message, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "MCP refusal audit record failed. Tool={ToolName}", Shown(requestedName));
            return message + " Its audit record could not be written.";
        }
    }

    /// <summary>A caller-sent tool name as it may appear in a log line or an error: at most
    /// <see cref="McpToolNaming.MaxNameLength"/> characters, control characters replaced.</summary>
    private static string Shown(string name)
    {
        var cut = name.Length <= McpToolNaming.MaxNameLength ? name : name[..McpToolNaming.MaxNameLength] + "…";
        return string.Create(cut.Length, cut, static (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                span[i] = char.IsControl(source[i]) ? '?' : source[i];
            }
        });
    }

    private static JsonElement RawArguments(CallToolRequestParams parameters) => parameters.Arguments is null
        ? EmptyObject
        : JsonSerializer.SerializeToElement(parameters.Arguments);

    private JsonElement BuildArguments(CallToolRequestParams parameters, string canonicalId)
    {
        var argumentsElement = RawArguments(parameters);

        if (!_options.Value.ArgumentOverrides.TryGetValue(canonicalId, out var overrides) || overrides.Count == 0)
        {
            return argumentsElement;
        }

        // Operator-pinned arguments beat caller-supplied ones: this is what stops a remote client
        // from re-rooting filesystem tools (repo.fs.* take 'root' as an argument, not from the
        // world snapshot).
        var node = argumentsElement.ValueKind == JsonValueKind.Object
            ? (JsonObject)JsonNode.Parse(argumentsElement.GetRawText())!
            : new JsonObject();
        foreach (var pair in overrides)
        {
            node[pair.Key] = JsonValue.Create(pair.Value);
        }

        return JsonSerializer.SerializeToElement(node);
    }

    private static CallToolResult ErrorResult(string message) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = message }],
    };
}
