using Ashlar.Abstractions.Security.Egress;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ashlar.Abstractions;

namespace Ashlar.Mcp.Server;

/// <summary>
/// Built-in contributor that bridges DI-registered <see cref="ITool"/> implementations into the
/// MCP catalog, filtered by the <see cref="AshlarMcpServerOptions.ExposedToolIds"/> allowlist.
///
/// Hosts opt tools in twice: register the <see cref="ITool"/> in DI (Ashlar tools are not
/// DI-registered by the kernel today — they are constructed by toolbox factories) AND allowlist
/// its id in options. Registered-but-not-allowlisted tools stay invisible; allowlisted-but-not-
/// registered ids fail startup validation loudly, because a silently missing tool is the kind of
/// misconfiguration that otherwise surfaces days later as a confusing client error.
/// </summary>
public sealed class ToolboxMcpToolContributor : IMcpToolContributor
{
    private static readonly JsonElement DefaultObjectSchema = JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone();

    private readonly IReadOnlyDictionary<string, ITool> _tools;
    private readonly IOptions<AshlarMcpServerOptions> _options;
    private readonly IMcpWorldSnapshotFactory _snapshots;
    private readonly ILogger<ToolboxMcpToolContributor> _logger;

    /// <summary>Creates the contributor over all DI-registered tools.</summary>
    public ToolboxMcpToolContributor(
        IEnumerable<ITool> tools,
        IOptions<AshlarMcpServerOptions> options,
        IMcpWorldSnapshotFactory snapshots,
        ILogger<ToolboxMcpToolContributor> logger)
    {
        // Last registration wins on duplicate ids, matching CapabilityRegistry.Register semantics.
        var map = new Dictionary<string, ITool>(StringComparer.OrdinalIgnoreCase);
        foreach (var tool in tools)
        {
            map[tool.Id] = tool;
        }

        _tools = map;
        _options = options;
        _snapshots = snapshots;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<AshlarMcpToolDescriptor>> DescribeAsync(CancellationToken ct)
    {
        var descriptors = new List<AshlarMcpToolDescriptor>();
        foreach (var id in _options.Value.ExposedToolIds)
        {
            ct.ThrowIfCancellationRequested();

            if (!_tools.TryGetValue(id, out var tool))
            {
                throw new InvalidOperationException(
                    $"MCP allowlist entry '{id}' has no matching ITool registered in DI. " +
                    "Register the tool (services.AddSingleton<ITool, ...>()) or remove it from " +
                    $"{AshlarMcpServerOptions.SectionPath}:{nameof(AshlarMcpServerOptions.ExposedToolIds)}.");
            }

            descriptors.Add(new AshlarMcpToolDescriptor(
                McpName: McpToolNaming.Sanitize(tool.Id),
                CanonicalId: tool.Id,
                Description: tool.Schema.Description,
                InputSchema: ParseInputSchema(tool.Id, tool.Schema.InputJsonSchema)));
        }

        return Task.FromResult<IReadOnlyList<AshlarMcpToolDescriptor>>(descriptors);
    }

    /// <inheritdoc />
    public async Task<AshlarMcpToolOutcome> InvokeAsync(string canonicalId, JsonElement arguments, CancellationToken ct)
    {
        if (!_tools.TryGetValue(canonicalId, out var tool))
        {
            return new AshlarMcpToolOutcome(IsError: true, Text: $"Tool '{canonicalId}' is not registered.");
        }

        var snapshot = _snapshots.Create(canonicalId);
        try
        {
            var result = await tool.InvokeAsync(new ToolCall(canonicalId, arguments), snapshot, ct).ConfigureAwait(false);

            // Deltas describe world-state changes for the simulation loop; they are meaningless to
            // a remote MCP caller, so their log lines go to the host log instead of the wire.
            foreach (var line in result.Delta.Log)
            {
                _logger.LogInformation("MCP tool delta. Tool={ToolId} {DeltaLog}", canonicalId, line);
            }

            return result.Payload switch
            {
                null => new AshlarMcpToolOutcome(IsError: false, Text: "(no output)"),
                string text => new AshlarMcpToolOutcome(IsError: false, Text: text),
                var payload => new AshlarMcpToolOutcome(IsError: false, Text: JsonSerializer.Serialize(payload), Payload: payload),
            };
        }
        catch (Exception ex) when (EgressRefusal.Find(ex) is { } refusal)
        {
            return new AshlarMcpToolOutcome(IsError: true, Text: EgressRefusal.RemoteMessage(refusal.Ref));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Tool exceptions (including argument-shape JsonExceptions thrown inside tools) are
            // business-level failures the calling model can correct — report them as isError
            // results, not protocol errors, per the MCP spec's tool-error guidance.
            _logger.LogWarning(ex, "MCP tool invocation failed. Tool={ToolId}", canonicalId);
            return new AshlarMcpToolOutcome(IsError: true, Text: $"Tool '{canonicalId}' failed: {ex.Message}");
        }
    }

    private static JsonElement ParseInputSchema(string toolId, string? inputJsonSchema)
    {
        if (string.IsNullOrWhiteSpace(inputJsonSchema))
        {
            return DefaultObjectSchema;
        }

        JsonElement parsed;
        try
        {
            using var doc = JsonDocument.Parse(inputJsonSchema);
            parsed = doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Tool '{toolId}' has an InputJsonSchema that is not valid JSON and cannot be exposed over MCP.", ex);
        }

        // MCP requires an object schema at the root; surface the offending tool id here instead of
        // letting the SDK's Tool.InputSchema setter throw a generic ArgumentException later.
        if (parsed.ValueKind != JsonValueKind.Object ||
            !parsed.TryGetProperty("type", out var type) ||
            type.ValueKind != JsonValueKind.String ||
            !string.Equals(type.GetString(), "object", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Tool '{toolId}' has an InputJsonSchema whose root is not '\"type\":\"object\"'; MCP requires object schemas.");
        }

        return parsed;
    }
}
