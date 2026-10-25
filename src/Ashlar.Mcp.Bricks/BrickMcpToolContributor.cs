using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ashlar.Abstractions.Barriers;
using Ashlar.Core.Application.Bricks;
using Ashlar.Core.Domain.Bricks;
using Ashlar.Core.Domain.Execution;
using Ashlar.Infrastructure.Execution;
using Ashlar.Mcp.Server;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using DomainBrick = Ashlar.Core.Domain.Bricks.Brick;
using ExecutionContext = Ashlar.Infrastructure.Execution.ExecutionContext;

namespace Ashlar.Mcp.Bricks;

/// <summary>
/// Exposes allowlisted bricks from <see cref="IBrickRegistry"/> as MCP tools. Each brick input
/// becomes a JSON-schema property (required inputs are listed as required), a call converts the
/// arguments exactly as <c>POST /api/bricks/{id}/execute</c> does, applies input defaults, refuses
/// missing required inputs before the brick runs, and executes deterministically.
///
/// Every call that reaches a brick, and every refusal of one, writes one
/// <see cref="BarrierAuditEvent"/> (<see cref="EventType"/>) through <see cref="IBarrierAuditLog"/>
/// (registered by <see cref="AshlarMcpBricksServiceCollectionExtensions.AddAshlarMcpBricks"/> over the
/// configured audit sinks).
/// Its <see cref="BarrierAuditEvent.CorrelationId"/> is returned to the caller as <c>auditId</c>, and
/// its detail carries the SHA-256 of the arguments as received and of the text returned, so a
/// record can be matched to the exact exchange. Values themselves are never written to the record.
/// </summary>
public sealed class BrickMcpToolContributor : IMcpToolContributor
{
    /// <summary><see cref="BarrierAuditEvent.EventType"/> of the record written per brick call.</summary>
    public const string EventType = "MCP_BRICK_INVOKED";

    /// <summary><see cref="IExecutionContext.AgentId"/> of the context bricks execute under.</summary>
    public const string McpAgentId = "mcp";

    private static readonly JsonSerializerOptions PayloadJson = new() { WriteIndented = false };

    private readonly IBrickRegistry _registry;
    private readonly IOptions<AshlarMcpBrickOptions> _options;
    private readonly IBarrierAuditLog _audit;
    private readonly IBarrierContextAmbient? _barrier;
    private readonly TimeProvider _time;
    private readonly ILogger<BrickMcpToolContributor> _logger;

    /// <summary>Creates the contributor.</summary>
    public BrickMcpToolContributor(
        IBrickRegistry registry,
        IOptions<AshlarMcpBrickOptions> options,
        IBarrierAuditLog audit,
        ILogger<BrickMcpToolContributor> logger,
        IBarrierContextAmbient? barrier = null,
        TimeProvider? time = null)
    {
        _registry = registry;
        _options = options;
        _audit = audit;
        _logger = logger;
        _barrier = barrier;
        _time = time ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<AshlarMcpToolDescriptor>> DescribeAsync(CancellationToken ct)
    {
        var descriptors = new List<AshlarMcpToolDescriptor>();
        foreach (var id in _options.Value.ExposedBrickIds)
        {
            ct.ThrowIfCancellationRequested();
            var brick = _registry.GetBrick(id) ?? throw new InvalidOperationException(
                $"MCP brick allowlist entry '{id}' has no brick in IBrickRegistry. Register the brick " +
                "(AddAshlarSdk(sdk => sdk.RegisterBrick<T>()) or AddAshlarBrick<T>()) or remove it from " +
                $"{AshlarMcpBrickOptions.SectionPath}:{nameof(AshlarMcpBrickOptions.ExposedBrickIds)}.");

            descriptors.Add(new AshlarMcpToolDescriptor(
                McpName: McpToolNaming.Sanitize(brick.Id),
                CanonicalId: brick.Id,
                Description: string.IsNullOrWhiteSpace(brick.Description) ? brick.Name : brick.Description,
                InputSchema: BuildInputSchema(brick)));
        }

        return Task.FromResult<IReadOnlyList<AshlarMcpToolDescriptor>>(descriptors);
    }

    /// <inheritdoc />
    public async Task<AshlarMcpToolOutcome> InvokeAsync(string canonicalId, JsonElement arguments, CancellationToken ct)
    {
        var auditId = Guid.NewGuid().ToString("N");
        var inputSha256 = Sha256(arguments.ValueKind == JsonValueKind.Undefined ? "{}" : arguments.GetRawText());
        var stopwatch = Stopwatch.StartNew();

        var brick = _options.Value.ExposedBrickIds.Contains(canonicalId, StringComparer.Ordinal)
            ? _registry.GetBrick(canonicalId)
            : null;
        if (brick is null)
        {
            return await RefuseAsync(canonicalId, version: null, auditId, inputSha256, stopwatch,
                $"Brick '{canonicalId}' is not exposed.", ct).ConfigureAwait(false);
        }

        if (arguments.ValueKind is not (JsonValueKind.Object or JsonValueKind.Undefined))
        {
            return await RefuseAsync(brick.Id, brick.Version, auditId, inputSha256, stopwatch,
                "Arguments must be a JSON object.", ct).ConfigureAwait(false);
        }

        var input = BrickValueSerializer.FromWireToBrickInput(ToWire(arguments));
        BrickInputDefaults.Apply(brick, input);
        if (!input.TryValidateRequiredInputs(brick.Interface, out var missing))
        {
            return await RefuseAsync(brick.Id, brick.Version, auditId, inputSha256, stopwatch,
                $"Missing required input(s): {string.Join(", ", missing)}.", ct).ConfigureAwait(false);
        }

        var context = new ExecutionContext
        {
            AgentId = McpAgentId,
            BehaviorId = brick.Id,
            AuditMode = true,
        };

        BrickOutput output;
        try
        {
            output = await brick.ExecuteAsync(input, ImplementationType.Deterministic, context, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MCP brick call failed. Brick={BrickId}", brick.Id);
            return await RecordAsync(brick.Id, brick.Version, auditId, inputSha256, stopwatch, outcome: "failed",
                new AshlarMcpToolOutcome(true, $"Brick '{brick.Id}' failed: {ex.Message} (auditId {auditId})"), ct)
                .ConfigureAwait(false);
        }

        var payload = new Dictionary<string, object?>
        {
            ["brickId"] = brick.Id,
            ["brickVersion"] = brick.Version,
            ["auditId"] = auditId,
            ["summary"] = output.Summary,
            ["outputs"] = BrickValueSerializer.ToWireDictionary(new Dictionary<string, object>(output.ToDictionary())),
        };
        var text = JsonSerializer.Serialize(payload, PayloadJson);
        return await RecordAsync(brick.Id, brick.Version, auditId, inputSha256, stopwatch, outcome: "succeeded",
            new AshlarMcpToolOutcome(false, text, payload), ct).ConfigureAwait(false);
    }

    private Task<AshlarMcpToolOutcome> RefuseAsync(
        string brickId, string? version, string auditId, string inputSha256, Stopwatch stopwatch, string reason, CancellationToken ct)
        => RecordAsync(brickId, version, auditId, inputSha256, stopwatch, outcome: "refused",
            new AshlarMcpToolOutcome(true, $"{reason} (auditId {auditId})"), ct);

    private async Task<AshlarMcpToolOutcome> RecordAsync(
        string brickId,
        string? version,
        string auditId,
        string inputSha256,
        Stopwatch stopwatch,
        string outcome,
        AshlarMcpToolOutcome result,
        CancellationToken ct)
    {
        var detail = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["brickId"] = brickId,
            ["brickVersion"] = version,
            ["outcome"] = outcome,
            ["isError"] = result.IsError,
            ["durationMs"] = stopwatch.ElapsedMilliseconds,
            ["inputSha256"] = inputSha256,
            ["resultSha256"] = Sha256(result.Text),
        }, PayloadJson);

        var current = _barrier?.Current;
        var auditEvent = new BarrierAuditEvent(
            EventType: EventType,
            BarrierLevel: string.IsNullOrEmpty(current?.Level) ? "none" : current!.Level,
            AuthoritySource: string.IsNullOrEmpty(current?.AuthoritySource) ? "mcp" : current!.AuthoritySource,
            AgentName: brickId,
            CorrelationId: auditId,
            SpanId: string.Empty,
            OccurredAt: _time.GetUtcNow(),
            Detail: detail);

        try
        {
            await _audit.RecordAsync(auditEvent, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // What cannot be claimed is a record of the call. Say so rather than return a result the
            // caller would take as audited.
            _logger.LogError(ex, "MCP brick audit record failed. Brick={BrickId} AuditId={AuditId}", brickId, auditId);
            var what = outcome == "refused" ? "was refused" : "ran";
            return new AshlarMcpToolOutcome(true, $"Brick '{brickId}' {what} but its audit record could not be written (auditId {auditId}).");
        }

        return result;
    }

    internal static JsonElement BuildInputSchema(DomainBrick brick)
    {
        var properties = new Dictionary<string, object>(StringComparer.Ordinal);
        var required = new List<string>();
        foreach (var definition in brick.Interface.Inputs)
        {
            var property = new Dictionary<string, object>(StringComparer.Ordinal);
            if (JsonSchemaType(definition.Type) is { } type)
            {
                property["type"] = type;
            }

            if (!string.IsNullOrWhiteSpace(definition.Description))
            {
                property["description"] = definition.Description!;
            }

            properties[definition.Name] = property;
            if (definition.Required && definition.Default is null)
            {
                required.Add(definition.Name);
            }
        }

        var schema = new Dictionary<string, object> { ["type"] = "object", ["properties"] = properties };
        if (required.Count > 0)
        {
            schema["required"] = required;
        }

        return JsonSerializer.SerializeToElement(schema);
    }

    private static string? JsonSchemaType(string? brickType)
    {
        var t = (brickType ?? string.Empty).Trim().ToLowerInvariant();
        if (t.EndsWith("[]", StringComparison.Ordinal) || t is "array" or "list")
        {
            return "array";
        }

        return t switch
        {
            "string" or "text" or "url" or "path" => "string",
            "int" or "integer" or "long" or "int32" or "int64" => "integer",
            "double" or "float" or "decimal" or "number" => "number",
            "bool" or "boolean" => "boolean",
            "object" or "dictionary" or "map" or "json" => "object",
            _ => null,
        };
    }

    private static IReadOnlyDictionary<string, object> ToWire(JsonElement arguments)
    {
        var wire = new Dictionary<string, object>(StringComparer.Ordinal);
        if (arguments.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in arguments.EnumerateObject())
            {
                wire[property.Name] = property.Value.Clone();
            }
        }

        return wire;
    }

    private static string Sha256(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}
