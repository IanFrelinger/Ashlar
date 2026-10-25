using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Ashlar.Abstractions;
using Ashlar.Abstractions.Security;

#pragma warning disable ASHLAREXP002 // ResponseReadScope and SecurityLabel enforce the reviewed MCP response boundary.

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
    private readonly SemaphoreSlim _concurrency;

    // Volatile snapshot of the last-built catalog, used to route tools/call without a rebuild.
    // tools/list always rebuilds, so a client that re-lists sees contributor changes.
    private volatile IReadOnlyDictionary<string, CatalogEntry>? _catalog;

    private sealed record CatalogEntry(AshlarMcpToolDescriptor Descriptor, IMcpToolContributor Contributor);

    /// <summary>Creates the bridge over all registered contributors.</summary>
    public AshlarMcpToolBridge(
        IEnumerable<IMcpToolContributor> contributors,
        IMcpInvocationGate gate,
        IMcpWorldSnapshotFactory snapshots,
        IOptions<AshlarMcpServerOptions> options,
        ILogger<AshlarMcpToolBridge> logger)
    {
        _contributors = contributors.ToList();
        _gate = gate;
        _snapshots = snapshots;
        _options = options;
        _logger = logger;
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
        var catalog = await BuildRequestCatalogAsync(ct).ConfigureAwait(false);
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

        var catalog = _catalog ?? await BuildRequestCatalogAsync(ct).ConfigureAwait(false);
        if (!catalog.TryGetValue(name, out var entry))
        {
            // One rebuild covers the "listed before a contributor refresh" race; a second miss is
            // a genuinely unknown tool, which the spec treats as a protocol-level error.
            catalog = await BuildRequestCatalogAsync(ct).ConfigureAwait(false);
            if (!catalog.TryGetValue(name, out entry))
            {
                _logger.LogWarning("MCP call to unknown tool. Tool={ToolName} Caller={Caller}", name, caller);
                throw new McpProtocolException($"Unknown tool '{name}'.", McpErrorCode.InvalidParams);
            }
        }

        var canonicalId = entry.Descriptor.CanonicalId;
        var arguments = BuildArguments(parameters, canonicalId);

        // The authenticated SDK principal and operator configuration are the only clearance inputs. A tool
        // result cannot label itself public, and request arguments cannot grant the caller a higher clearance.
        var options = _options.Value;
        options.ResponseLabels.TryGetValue(canonicalId, out var responseLabel);
        using var response = new ResponseReadScope("mcp-response:" + canonicalId,
            ResolveCallerClearance(user, options), SecurityLabel.ParseOrSystemHigh(responseLabel));
        if (!response.CanRead().Allowed)
            return ResponseDenied(canonicalId, caller);

        McpGateDecision decision;
        try
        {
            decision = _gate.Authorize(new ToolCall(canonicalId, arguments), _snapshots.Create(canonicalId));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }
        catch (Exception ex)
        {
            // Host policies and snapshot providers can throw messages containing data. The SDK must not
            // receive those exceptions to format as a protocol response.
            _logger.LogError(ex, "MCP policy evaluation failed. Tool={ToolId} Caller={Caller}", canonicalId, caller);
            return ErrorResult($"Tool '{name}' failed with an internal error.");
        }
        if (!decision.Allowed)
        {
            _logger.LogWarning(
                "MCP tool call denied. Tool={ToolId} Caller={Caller} Reason={Reason}",
                canonicalId, caller, decision.Reason);
            return response.CanRead().Allowed
                ? ErrorResult($"Call to '{name}' was denied: {decision.Reason}")
                : ResponseDenied(canonicalId, caller);
        }

        await _concurrency.WaitAsync(ct).ConfigureAwait(false);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var outcome = await entry.Contributor.InvokeAsync(canonicalId, arguments, ct).ConfigureAwait(false);

            if (!response.CanRead().Allowed)
                return ResponseDenied(canonicalId, caller);

            JsonElement? structured = outcome.Payload is not null and not string
                ? JsonSerializer.SerializeToElement(outcome.Payload)
                : null;

            // Getters/converters can perform additional labelled reads. Never disclose either representation
            // if serialization raised the high-water mark above the recipient's clearance.
            if (!response.CanRead().Allowed)
                return ResponseDenied(canonicalId, caller);

            _logger.LogInformation(
                "MCP tool call completed. Tool={ToolId} Caller={Caller} IsError={IsError} DurationMs={DurationMs}",
                canonicalId, caller, outcome.IsError, stopwatch.ElapsedMilliseconds);

            var result = new CallToolResult
            {
                IsError = outcome.IsError,
                Content = [new TextContentBlock
                {
                    Text = outcome.SerializePayloadAsText && structured.HasValue
                        ? structured.Value.GetRawText()
                        : outcome.Text,
                }],
                StructuredContent = structured,
            };

            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The transport may format exception messages. Preserve cancellation without forwarding text
            // supplied by a contributor, which can contain the same private data as a normal result.
            throw new OperationCanceledException(ct);
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

    private CallToolResult ResponseDenied(string canonicalId, string caller)
    {
        _logger.LogWarning("MCP response denied by read policy. Tool={ToolId} Caller={Caller}", canonicalId, caller);
        return ErrorResult("Response denied by data access policy.");
    }

    private static SecurityLabel ResolveCallerClearance(ClaimsPrincipal? user, AshlarMcpServerOptions options)
    {
        if (user is null || string.IsNullOrWhiteSpace(options.CallerIdentityIssuer))
            return SecurityLabel.Public;

        var subjects = user.Identities.Where(identity => identity.IsAuthenticated)
            .SelectMany(identity => identity.FindAll(ClaimTypes.NameIdentifier))
            .Where(claim => string.Equals(claim.Issuer, options.CallerIdentityIssuer, StringComparison.Ordinal))
            .Select(claim => claim.Value).Distinct(StringComparer.Ordinal).Take(2).ToArray();

        if (subjects.Length != 1 || !options.CallerClearances.TryGetValue(subjects[0], out var clearance))
            return SecurityLabel.Public;

        return SecurityLabel.ParseOrPublic(clearance);
    }

    private async Task<IReadOnlyDictionary<string, CatalogEntry>> BuildRequestCatalogAsync(CancellationToken ct)
    {
        try
        {
            return await BuildCatalogAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }
        catch (Exception ex)
        {
            // Catalog metadata is host-published, but discovery failures can carry private backend details.
            // In particular, the SDK forwards McpProtocolException.Message to the remote caller. Keep
            // detailed diagnostics in startup validation and host logs, never in request error messages.
            _logger.LogError(ex, "MCP tool catalog discovery failed.");
            throw new McpProtocolException("Tool catalog is unavailable.", McpErrorCode.InternalError);
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

    private JsonElement BuildArguments(CallToolRequestParams parameters, string canonicalId)
    {
        var argumentsElement = parameters.Arguments is null
            ? EmptyObject
            : JsonSerializer.SerializeToElement(parameters.Arguments);

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
