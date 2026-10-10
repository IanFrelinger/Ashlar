using Ashlar.Abstractions.Security.Egress;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ashlar.Brick.Contracts;
using Ashlar.Brick.Contracts.Capabilities;

namespace Ashlar.Infrastructure.Execution;

/// <summary>
/// Fetches brick catalog from a remote host via GET /api/bricks and GET /api/bricks/{id}.
/// </summary>
public sealed class HttpRemoteBrickCatalog : IRemoteBrickCatalog, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly EgressRefusalWarnings _refusalWarnings;
    private readonly ILogger<HttpRemoteBrickCatalog>? _logger;
    private readonly StaleCapabilitiesSnapshotStore? _staleCapabilities;
    private readonly TimeSpan _capabilityTtl;
    private readonly TimeSpan _maxStaleCapabilityAge;
    private readonly object _capabilityGate = new();
    private NodeCapabilityManifestDto? _cachedCapabilities;
    private DateTimeOffset _capabilitiesFetchedAt = DateTimeOffset.MinValue;

    /// <summary>Initializes a new http remote brick catalog.</summary>
    public HttpRemoteBrickCatalog(
        HttpClient httpClient,
        ILogger<HttpRemoteBrickCatalog>? logger = null,
        StaleCapabilitiesSnapshotStore? staleCapabilities = null,
        TimeSpan? capabilityTtl = null,
        TimeSpan? maxStaleCapabilityAge = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger;
        _refusalWarnings = new EgressRefusalWarnings((refusal, suppressed, summary) =>
            _logger?.LogWarning(new EventId(7307, "EgressRefusalHandled"),
                "Egress operation refused. Site={Site} Reason={Reason} Ref={Ref} Suppressed={Suppressed} Summary={Summary}",
                refusal.Site, refusal.Reason, refusal.Ref, suppressed, summary));
        _staleCapabilities = staleCapabilities;
        _capabilityTtl = capabilityTtl ?? TimeSpan.FromSeconds(30);
        _maxStaleCapabilityAge = maxStaleCapabilityAge ?? TimeSpan.FromMinutes(5);
    }

    /// <summary>Base url.</summary>
    public string BaseUrl => _httpClient.BaseAddress?.ToString() ?? string.Empty;

    /// <summary>Get all asynchronously.</summary>
    public async Task<IReadOnlyList<BrickCatalogEntryDto>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _httpClient.GetAsync("/api/bricks", cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var bricks = await ReadBrickEntriesCompatAsync(stream, cancellationToken).ConfigureAwait(false);
            var capabilities = (await GetCapabilitiesWithStalenessAsync(cancellationToken).ConfigureAwait(false)).Capabilities;
            if (capabilities is not null)
            {
                foreach (var entry in bricks)
                {
                    entry.HostCapabilities ??= capabilities;
                }
            }

            return bricks;
        }
        catch (Exception ex) when (EgressRefusal.Find(ex) is { } refusal)
        {
            _refusalWarnings.Report(refusal);
            return [];
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to fetch brick catalog from {BaseUrl}", BaseUrl);
            return [];
        }
    }

    /// <summary>Get by id asynchronously.</summary>
    public async Task<BrickCatalogEntryDto?> GetByIdAsync(string brickId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(brickId)) return null;
        try
        {
            var url = $"/api/bricks/{Uri.EscapeDataString(brickId)}";
            var entry = await _httpClient.GetFromJsonAsync<BrickCatalogEntryDto>(url, cancellationToken).ConfigureAwait(false);
            if (entry is null)
            {
                return null;
            }

            entry.HostCapabilities ??= (await GetCapabilitiesWithStalenessAsync(cancellationToken).ConfigureAwait(false)).Capabilities;
            return entry;
        }
        catch (Exception ex) when (EgressRefusal.Find(ex) is { } refusal)
        {
            _refusalWarnings.Report(refusal);
            return null;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to fetch brick {BrickId} from {BaseUrl}", brickId, BaseUrl);
            return null;
        }
    }

    /// <summary>Get capabilities asynchronously.</summary>
    public async Task<NodeCapabilityManifestDto?> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
        => (await GetCapabilitiesWithStalenessAsync(cancellationToken).ConfigureAwait(false)).Capabilities;

    /// <summary>Get capabilities with staleness asynchronously.</summary>
    public async Task<CapabilitiesFetchResult> GetCapabilitiesWithStalenessAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_capabilityGate)
        {
            if (_cachedCapabilities is not null && now - _capabilitiesFetchedAt <= _capabilityTtl)
            {
                return new CapabilitiesFetchResult
                {
                    Capabilities = _cachedCapabilities,
                    IsStale = false
                };
            }
        }

        try
        {
            var capabilities = await FetchCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
            if (capabilities is not null)
            {
                lock (_capabilityGate)
                {
                    _cachedCapabilities = capabilities;
                    _capabilitiesFetchedAt = DateTimeOffset.UtcNow;
                }

                _staleCapabilities?.Put(BaseUrl, capabilities);
            }

            return new CapabilitiesFetchResult
            {
                Capabilities = capabilities,
                IsStale = false
            };
        }
        catch (Exception ex) when (EgressRefusal.Find(ex) is { } refusal)
        {
            _refusalWarnings.Report(refusal);
            return new CapabilitiesFetchResult();
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            _logger?.LogDebug("Capabilities endpoint not found at {BaseUrl}; using cached value when available.", BaseUrl);
            lock (_capabilityGate)
            {
                var fallback = SelectEligibleStaleCapabilities();
                return new CapabilitiesFetchResult
                {
                    Capabilities = fallback,
                    IsStale = fallback is not null
                };
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to fetch capabilities from {BaseUrl}", BaseUrl);
            lock (_capabilityGate)
            {
                var fallback = SelectEligibleStaleCapabilities();
                return new CapabilitiesFetchResult
                {
                    Capabilities = fallback,
                    IsStale = fallback is not null
                };
            }
        }
    }

    private NodeCapabilityManifestDto? SelectEligibleStaleCapabilities()
    {
        var stale = _cachedCapabilities ?? _staleCapabilities?.Get(BaseUrl);
        if (stale is null)
        {
            return null;
        }

        var staleAge = DateTimeOffset.UtcNow - stale.GeneratedAt;
        if (stale.GeneratedAt > DateTimeOffset.UtcNow.AddMinutes(1))
        {
            _logger?.LogWarning(
                "Discarding stale capabilities for {BaseUrl}; manifest generatedAt is in the future ({GeneratedAt}).",
                BaseUrl,
                stale.GeneratedAt);
            return null;
        }
        if (staleAge > _maxStaleCapabilityAge)
        {
            _logger?.LogWarning(
                "Discarding stale capabilities for {BaseUrl}; age {AgeSeconds}s exceeds max {MaxAgeSeconds}s.",
                BaseUrl,
                staleAge.TotalSeconds,
                _maxStaleCapabilityAge.TotalSeconds);
            return null;
        }

        return stale;
    }

    private async Task<NodeCapabilityManifestDto?> FetchCapabilitiesAsync(CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync("/api/capabilities", cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            throw new HttpRequestException("Capabilities endpoint not found.", null, System.Net.HttpStatusCode.NotFound);
        }

        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new NodeCapabilityManifestDto
        {
            NodeId = ReadString(doc.RootElement, "nodeId") ?? string.Empty,
            Tier = ReadEnum(doc.RootElement, "tier", NodeTierDto.Nano),
            Platform = ReadEnum(doc.RootElement, "platform", PlatformTypeDto.Unknown),
            HotModelIds = ReadStringArray(doc.RootElement, "hotModelIds"),
            AvailableModelIds = ReadStringArray(doc.RootElement, "availableModelIds"),
            SupportedCapabilities = ReadEnumArray<TaskCapabilityDto>(doc.RootElement, "supportedCapabilities"),
            AcceptingRemoteWork = ReadBool(doc.RootElement, "acceptingRemoteWork"),
            GeneratedAt = ReadDateTimeOffset(doc.RootElement, "generatedAt", DateTimeOffset.UtcNow)
        };
    }

    private static string? ReadString(JsonElement root, string propertyName)
    {
        if (!TryGetPropertyIgnoreCase(root, propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    private static bool ReadBool(JsonElement root, string propertyName)
    {
        if (!TryGetPropertyIgnoreCase(root, propertyName, out var value))
        {
            return false;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(value.GetString(), out var parsed) && parsed,
            JsonValueKind.Number => value.TryGetInt32(out var numeric) && numeric != 0,
            _ => false
        };
    }

    private static DateTimeOffset ReadDateTimeOffset(JsonElement root, string propertyName, DateTimeOffset fallback)
    {
        if (!TryGetPropertyIgnoreCase(root, propertyName, out var value))
        {
            return fallback;
        }

        if (value.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(value.GetString(), out var parsed))
        {
            return parsed;
        }

        return fallback;
    }

    private static string[] ReadStringArray(JsonElement root, string propertyName)
    {
        if (!TryGetPropertyIgnoreCase(root, propertyName, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var items = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            var text = item.ValueKind == JsonValueKind.String ? item.GetString() : item.GetRawText();
            if (!string.IsNullOrWhiteSpace(text))
            {
                items.Add(text);
            }
        }

        return items.ToArray();
    }

    private bool IsStaleWithinAge(NodeCapabilityManifestDto manifest, DateTimeOffset now)
    {
        if (manifest.GeneratedAt == default)
        {
            return true;
        }
        var age = now - manifest.GeneratedAt;
        return age <= _maxStaleCapabilityAge;
    }

    private static TEnum[] ReadEnumArray<TEnum>(JsonElement root, string propertyName)
        where TEnum : struct, Enum
    {
        if (!TryGetPropertyIgnoreCase(root, propertyName, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var items = new List<TEnum>();
        foreach (var item in value.EnumerateArray())
        {
            if (TryParseEnum(item, out TEnum parsed))
            {
                items.Add(parsed);
            }
        }

        return items.ToArray();
    }

    private static TEnum ReadEnum<TEnum>(JsonElement root, string propertyName, TEnum fallback)
        where TEnum : struct, Enum
    {
        if (!TryGetPropertyIgnoreCase(root, propertyName, out var value))
        {
            return fallback;
        }

        return TryParseEnum(value, out TEnum parsed) ? parsed : fallback;
    }

    private static bool TryParseEnum<TEnum>(JsonElement value, out TEnum parsed)
        where TEnum : struct, Enum
    {
        if (value.ValueKind == JsonValueKind.String &&
            Enum.TryParse(value.GetString(), ignoreCase: true, out parsed))
        {
            return true;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var numeric) &&
            Enum.IsDefined(typeof(TEnum), numeric))
        {
            parsed = (TEnum)Enum.ToObject(typeof(TEnum), numeric);
            return true;
        }

        parsed = default;
        return false;
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement root, string propertyName, out JsonElement value)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static async Task<List<BrickCatalogEntryDto>> ReadBrickEntriesCompatAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (doc.RootElement.ValueKind != JsonValueKind.Object ||
            !TryGetPropertyIgnoreCase(doc.RootElement, "bricks", out var bricksNode) ||
            bricksNode.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var entries = new List<BrickCatalogEntryDto>();
        foreach (var brick in bricksNode.EnumerateArray())
        {
            if (brick.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var interfaceNode = TryGetPropertyIgnoreCase(brick, "interface", out var iface)
                ? iface
                : default;
            var metadataNode = TryGetPropertyIgnoreCase(brick, "metadata", out var meta)
                ? meta
                : default;

            entries.Add(new BrickCatalogEntryDto
            {
                WireFormatVersion = ReadString(brick, "wireFormatVersion") ?? Ashlar.Brick.Contracts.WireFormatVersion.Current,
                Id = ReadString(brick, "id") ?? string.Empty,
                Name = ReadString(brick, "name") ?? string.Empty,
                Version = ReadString(brick, "version") ?? "1.0.0",
                Icon = ReadString(brick, "icon") ?? "pkg",
                Category = ReadString(brick, "category") ?? string.Empty,
                Description = ReadString(brick, "description") ?? string.Empty,
                HostBaseUrl = ReadString(brick, "hostBaseUrl"),
                HasDeterministic = ReadBool(brick, "hasDeterministic"),
                HasAgentic = ReadBool(brick, "hasAgentic"),
                Interface = new BrickInterfaceDto
                {
                    Inputs = ReadInterfaceInputs(interfaceNode),
                    Outputs = ReadInterfaceOutputs(interfaceNode)
                },
                Metadata = new BrickMetadataDto
                {
                    Author = ReadString(metadataNode, "author"),
                    License = ReadString(metadataNode, "license"),
                    Repository = ReadString(metadataNode, "repository"),
                    UsageCount = ReadLong(metadataNode, "usageCount"),
                    LastUpdated = ReadNullableDateTime(metadataNode, "lastUpdated")
                }
            });
        }

        return entries;
    }

    private static IReadOnlyList<BrickInputDefinitionDto> ReadInterfaceInputs(JsonElement node)
    {
        if (node.ValueKind != JsonValueKind.Object ||
            !TryGetPropertyIgnoreCase(node, "inputs", out var inputsNode) ||
            inputsNode.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var list = new List<BrickInputDefinitionDto>();
        foreach (var item in inputsNode.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            list.Add(new BrickInputDefinitionDto
            {
                Name = ReadString(item, "name") ?? string.Empty,
                Type = ReadString(item, "type") ?? string.Empty,
                Description = ReadString(item, "description"),
                Required = ReadBool(item, "required"),
                Default = ReadString(item, "default")
            });
        }

        return list;
    }

    private static IReadOnlyList<BrickOutputDefinitionDto> ReadInterfaceOutputs(JsonElement node)
    {
        if (node.ValueKind != JsonValueKind.Object ||
            !TryGetPropertyIgnoreCase(node, "outputs", out var outputsNode) ||
            outputsNode.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var list = new List<BrickOutputDefinitionDto>();
        foreach (var item in outputsNode.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            list.Add(new BrickOutputDefinitionDto
            {
                Name = ReadString(item, "name") ?? string.Empty,
                Type = ReadString(item, "type") ?? string.Empty,
                Description = ReadString(item, "description")
            });
        }

        return list;
    }

    private static long ReadLong(JsonElement root, string propertyName)
    {
        if (!TryGetPropertyIgnoreCase(root, propertyName, out var value))
        {
            return 0;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var parsed))
        {
            return parsed;
        }

        if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out parsed))
        {
            return parsed;
        }

        return 0;
    }

    private static DateTime? ReadNullableDateTime(JsonElement root, string propertyName)
    {
        if (!TryGetPropertyIgnoreCase(root, propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.String && DateTime.TryParse(value.GetString(), out var parsed))
        {
            return parsed;
        }

        return null;
    }
    /// <summary>Flushes suppressed refusal warnings and releases their timers.</summary>
    public void Dispose()
    {
        _refusalWarnings.Dispose();
    }

}
