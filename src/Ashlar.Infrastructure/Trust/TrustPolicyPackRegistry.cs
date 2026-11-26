using System.Text.Json;
using Microsoft.Extensions.Logging;
using Ashlar.Core.Application.Paths;
using Ashlar.Core.Application.Trust.Models;
using Ashlar.Core.Application.Trust.Ports;

namespace Ashlar.Infrastructure.Trust;

/// <summary>
/// Loads versioned trust policy packs and applies them to the access boundary.
/// </summary>
public sealed class TrustPolicyPackRegistry : ITrustPolicyPackRegistry
{
    private readonly string _packsRootPath;
    private readonly SemaphoreSlim _stateLock = new(1, 1);
    private readonly string _statePath;
    private readonly ILogger<TrustPolicyPackRegistry>? _logger;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true
    };
    private static readonly JsonSerializerOptions ReadJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private ActiveTrustPolicyPack? _activePack;
    private IReadOnlyDictionary<string, TrustPolicyPack> _packs = new Dictionary<string, TrustPolicyPack>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Initializes a new trust policy pack registry.</summary>
    public TrustPolicyPackRegistry(string? packsRootPath = null, ILogger<TrustPolicyPackRegistry>? logger = null)
    {
        _packsRootPath = ResolvePacksRootPath(packsRootPath);
        _statePath = ResolveStatePath(_packsRootPath);
        _logger = logger;
        Reload();
    }

    /// <inheritdoc />
    public IReadOnlyList<TrustPolicyPackInfo> ListPacks()
    {
        return _packs.Values
            .OrderBy(pack => pack.Id, StringComparer.OrdinalIgnoreCase)
            .Select(pack => new TrustPolicyPackInfo(pack.Id, pack.Version, pack.DisplayName, pack.Description))
            .ToList();
    }

    /// <inheritdoc />
    public TrustPolicyPack? GetById(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;
        return _packs.TryGetValue(id.Trim(), out var pack) ? pack : null;
    }

    /// <inheritdoc />
    public ActiveTrustPolicyPack? GetActivePack() => _activePack;

    /// <inheritdoc />
    public async Task<ActiveTrustPolicyPack> ActivateAsync(string id, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Trust policy pack id is required.", nameof(id));

        var normalizedId = id.Trim();
        if (!_packs.TryGetValue(normalizedId, out var pack))
            throw new InvalidOperationException($"Trust policy pack '{normalizedId}' was not found.");

        var active = new ActiveTrustPolicyPack(
            pack.Id,
            string.IsNullOrWhiteSpace(pack.Version) ? "1.0.0" : pack.Version.Trim(),
            DateTimeOffset.UtcNow);

        await _stateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _activePack = active;
            var json = JsonSerializer.Serialize(active, _jsonOptions);
            await File.WriteAllTextAsync(_statePath, json, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _stateLock.Release();
        }

        return active;
    }

    /// <summary>Reload.</summary>
    public void Reload()
    {
        Directory.CreateDirectory(_packsRootPath);
        _packs = LoadPacks(_packsRootPath);
        // Required deployments provision the initial selection before startup. Never
        // recover a missing pointer from a default: that could weaken a policy the
        // operator previously selected after data loss or a mount/configuration error.
        if (string.Equals(Environment.GetEnvironmentVariable("ASHLAR_TRUST_POLICY_REQUIRE_ACTIVE"),
            "true", StringComparison.OrdinalIgnoreCase))
        {
            var active = ReadStatus(_statePath);
            if (active is null || string.IsNullOrWhiteSpace(active.Id) ||
                !_packs.TryGetValue(active.Id, out var definition) ||
                string.IsNullOrWhiteSpace(active.Version) ||
                !string.Equals(active.Version, definition.Version, StringComparison.Ordinal))
                throw new InvalidOperationException("The active trust policy selection must identify an installed pack and its exact version.");
            _activePack = active;
        }
        else
        {
            _activePack = LoadStatus(_statePath);
        }
    }

    private static IReadOnlyDictionary<string, TrustPolicyPack> LoadPacks(string rootPath)
    {
        var packs = new Dictionary<string, TrustPolicyPack>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(rootPath, "*.json", SearchOption.TopDirectoryOnly))
        {
            if (string.Equals(Path.GetFileName(path), "active-pack.json", StringComparison.OrdinalIgnoreCase))
                continue;

            var text = File.ReadAllText(path);
            var pack = JsonSerializer.Deserialize<TrustPolicyPack>(text, ReadJsonOptions);
            if (pack == null || string.IsNullOrWhiteSpace(pack.Id))
                continue;
            packs[pack.Id] = pack;
        }

        return packs;
    }

    private static ActiveTrustPolicyPack? LoadStatus(string statePath)
    {
        if (!File.Exists(statePath))
            return null;

        try
        {
            return ReadStatus(statePath);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static ActiveTrustPolicyPack? ReadStatus(string statePath) =>
        JsonSerializer.Deserialize<ActiveTrustPolicyPack>(File.ReadAllText(statePath), ReadJsonOptions);

    private static string ResolvePacksRootPath(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
            return Path.GetFullPath(configuredPath);

        var envPath = Environment.GetEnvironmentVariable("ASHLAR_TRUST_POLICY_PACKS_PATH");
        if (!string.IsNullOrWhiteSpace(envPath))
            return Path.GetFullPath(envPath);

        var repoRoot = RepoPathResolver.FindRepoRoot();
        return Path.Combine(repoRoot, "config", "trust-packs");
    }

    private static string ResolveStatePath(string packsRootPath)
    {
        var envPath = Environment.GetEnvironmentVariable("ASHLAR_ACTIVE_TRUST_POLICY_PACK_PATH");
        if (!string.IsNullOrWhiteSpace(envPath))
            return Path.GetFullPath(envPath);
        return Path.Combine(packsRootPath, "active-pack.json");
    }
}
