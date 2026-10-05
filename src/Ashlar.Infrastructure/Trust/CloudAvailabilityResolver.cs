using System.Text.Json;
using Microsoft.Extensions.Logging;
using Ashlar.Abstractions.Security.Egress;
using Ashlar.Core.Application.Trust.Ports;

namespace Ashlar.Infrastructure.Trust;

/// <summary>
/// Resolves air-gap status from env var, config file, and optional network probe.
/// Priority: env var (ASHLAR_AIRGAP) → config file (~/.ashlar/config.json) → network probe.
/// </summary>
public sealed class CloudAvailabilityResolver : ICloudAvailabilityResolver
{
    private readonly ILogger<CloudAvailabilityResolver> _logger;
    private readonly string? _configPath;
    private readonly bool _enableNetworkProbe;
    private readonly HttpClient? _httpClient;
    private bool? _cached;
    private DateTimeOffset _cachedAt;

    private static readonly string DefaultConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".ashlar", "config.json");

    /// <summary>Initializes a new cloud availability resolver.</summary>
    public CloudAvailabilityResolver(
        ILogger<CloudAvailabilityResolver> logger,
        string? configPath = null,
        bool enableNetworkProbe = false,
        HttpClient? httpClient = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _configPath = configPath ?? DefaultConfigPath;
        _enableNetworkProbe = enableNetworkProbe;
        _httpClient = httpClient;
    }

    /// <inheritdoc />
    public async Task<bool> IsAirGappedAsync(bool refresh = false, CancellationToken ct = default)
    {
        if (!refresh && _cached.HasValue && (DateTimeOffset.UtcNow - _cachedAt).TotalSeconds < 30)
            return _cached.Value;

        // 1. Env var (highest priority)
        var envVal = Environment.GetEnvironmentVariable("ASHLAR_AIRGAP");
        if (!string.IsNullOrWhiteSpace(envVal))
        {
            var airGapped = string.Equals(envVal, "1", StringComparison.Ordinal) ||
                            string.Equals(envVal, "true", StringComparison.OrdinalIgnoreCase);
            _cached = airGapped;
            _cachedAt = DateTimeOffset.UtcNow;
            _logger.LogDebug("Air-gap from ASHLAR_AIRGAP env: {Value}", airGapped);
            return airGapped;
        }

        // 2. Config file
        if (!string.IsNullOrWhiteSpace(_configPath) && File.Exists(_configPath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(_configPath, ct);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("airGapped", out var prop))
                {
                    var airGapped = prop.ValueKind == JsonValueKind.True ||
                                    (prop.ValueKind == JsonValueKind.String && string.Equals(prop.GetString(), "true", StringComparison.OrdinalIgnoreCase));
                    _cached = airGapped;
                    _cachedAt = DateTimeOffset.UtcNow;
                    _logger.LogDebug("Air-gap from config {Path}: {Value}", _configPath, airGapped);
                    return airGapped;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read air-gap from config {Path}", _configPath);
            }
        }

        // 3. Network probe (optional)
        if (_enableNetworkProbe)
        {
            var unreachable = await ProbeCloudUnreachableAsync(ct);
            _cached = unreachable;
            _cachedAt = DateTimeOffset.UtcNow;
            _logger.LogDebug("Air-gap from network probe: {Value}", unreachable);
            return unreachable;
        }

        // Default: not air-gapped (cloud assumed available)
        _cached = false;
        _cachedAt = DateTimeOffset.UtcNow;
        return false;
    }

    private async Task<bool> ProbeCloudUnreachableAsync(CancellationToken ct)
    {
        var ownsClient = _httpClient == null;
        var client = _httpClient ?? EgressHttp.CreateClient(EgressFamilies.Http, "EG-MDL-12");
        try
        {
            client.Timeout = TimeSpan.FromSeconds(3);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            var response = await client.GetAsync("https://api.openai.com/v1/models", HttpCompletionOption.ResponseHeadersRead, cts.Token);
            return false; // Reachable
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Cloud probe failed; assuming air-gapped");
            return true; // Unreachable → air-gapped
        }
        finally
        {
            if (ownsClient) client.Dispose();
        }
    }
}
