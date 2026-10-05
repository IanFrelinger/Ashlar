using System.Text.Json;
using Microsoft.Extensions.Logging;
using Ashlar.Abstractions.Security.Egress;
using Ashlar.Core.Application.Adaptation.Models;
using Ashlar.Core.Application.Adaptation.Ports;
using Ashlar.Core.Application.Analysis.Ports;
using Ashlar.Core.Application.Paths;

namespace Ashlar.Infrastructure.Adaptation;

/// <summary>
/// File-based implementation of shared adaptation cache.
/// Broadcasts write to a shared directory; sync reads from it.
/// P2.3: Shared adaptation cache.
/// </summary>
public sealed class FileBasedSharedAdaptationStore : ISharedAdaptationBroadcaster, ISharedAdaptationSync
{
    private readonly string _basePath;
    private readonly IAdaptationLog _log;
    private readonly IRegressionTestRunner _regressionRunner;
    private readonly IImmutableCoreRegistry _immutableCore;
    private readonly ILogger<FileBasedSharedAdaptationStore>? _logger;
    private readonly string? _sourcePeerId;
    private readonly IReadOnlyCollection<string>? _trustedPeerIds;
    private readonly string? _repoRoot;

    /// <summary>Initializes a new file based shared adaptation store.</summary>
    public FileBasedSharedAdaptationStore(
        string? basePath,
        IAdaptationLog log,
        IRegressionTestRunner regressionRunner,
        IImmutableCoreRegistry immutableCore,
        ILogger<FileBasedSharedAdaptationStore>? logger = null,
        string? sourcePeerId = null,
        IReadOnlyCollection<string>? trustedPeerIds = null,
        string? repoRoot = null)
    {
        _basePath = basePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ashlar", "shared-adaptations");
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _regressionRunner = regressionRunner ?? throw new ArgumentNullException(nameof(regressionRunner));
        _immutableCore = immutableCore ?? throw new ArgumentNullException(nameof(immutableCore));
        _logger = logger;
        _sourcePeerId = sourcePeerId;
        _trustedPeerIds = trustedPeerIds;
        _repoRoot = repoRoot;
    }

    /// <inheritdoc />
    public async Task BroadcastAsync(SharedAdaptationEntry entry, CancellationToken cancellationToken = default)
    {
        var sourcePeerId = entry.SourcePeerId ?? _sourcePeerId;

        var dir = Path.Combine(_basePath, entry.Id);
        // SPEC-007 EG-MESH-07, report-only: the promoted source leaves through the directory peers sync.
        _ = EgressGuard.ProcessDefault.Evaluate(new EgressRequest(EgressFamilies.MeshPublish, "EG-MESH-07", "file:" + _basePath));
        Directory.CreateDirectory(dir);

        var metaPath = Path.Combine(dir, "meta.json");
        var meta = new
        {
            entry.Id,
            entry.Record,
            SourcePeerId = sourcePeerId,
            BroadcastAt = entry.BroadcastAt,
            FileCount = entry.Files.Count,
        };
        await File.WriteAllTextAsync(metaPath, JsonSerializer.Serialize(meta), cancellationToken).ConfigureAwait(false);

        var filesDir = Path.Combine(dir, "files");
        Directory.CreateDirectory(filesDir);
        foreach (var (path, content) in entry.Files)
        {
            // Even an outbound broadcast must not let a '..' or rooted entry escape the staging dir
            // into a sibling entry or ~/.ashlar state.
            if (!MediatedWritePath.IsSafeRelativePath(path))
                throw new InvalidOperationException(
                    $"Refusing to broadcast adaptation {entry.Id}: file path '{path}' is not a safe relative path.");
            var safePath = Path.Combine(filesDir, path.Replace('\\', Path.DirectorySeparatorChar));
            var parent = Path.GetDirectoryName(safePath);
            if (!string.IsNullOrEmpty(parent))
                Directory.CreateDirectory(parent);
            await File.WriteAllBytesAsync(safePath, content, cancellationToken).ConfigureAwait(false);
        }

        _logger?.LogDebug("Broadcast adaptation {Id} to shared store", entry.Id);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SharedAdaptationEntry>> PullAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_basePath))
            return Array.Empty<SharedAdaptationEntry>();

        var results = new List<SharedAdaptationEntry>();
        foreach (var dir in Directory.GetDirectories(_basePath))
        {
            try
            {
                var metaPath = Path.Combine(dir, "meta.json");
                if (!File.Exists(metaPath)) continue;

                var json = await File.ReadAllTextAsync(metaPath, cancellationToken).ConfigureAwait(false);
                var meta = JsonSerializer.Deserialize<JsonElement>(json);
                if (!meta.TryGetProperty("Record", out var recEl)) continue;

                var record = JsonSerializer.Deserialize<AdaptationRecord>(recEl.GetRawText());
                if (record == null) continue;

                var id = meta.TryGetProperty("Id", out var idEl) ? idEl.GetString() ?? record.Id : record.Id;
                var sourcePeer = meta.TryGetProperty("SourcePeerId", out var pEl) ? pEl.GetString() : null;
                var broadcastAt = DateTimeOffset.UtcNow;
                if (meta.TryGetProperty("BroadcastAt", out var btEl))
                {
                    try { broadcastAt = btEl.GetDateTimeOffset(); } catch { /* use default */ }
                }

                var filesDir = Path.Combine(dir, "files");
                var files = new Dictionary<string, byte[]>();
                if (Directory.Exists(filesDir))
                {
                    foreach (var f in Directory.GetFiles(filesDir, "*", SearchOption.AllDirectories))
                    {
                        var rel = Path.GetRelativePath(filesDir, f).Replace('\\', '/');
                        files[rel] = await File.ReadAllBytesAsync(f, cancellationToken).ConfigureAwait(false);
                    }
                }

                results.Add(new SharedAdaptationEntry
                {
                    Id = id,
                    Record = record,
                    Files = files,
                    SourcePeerId = sourcePeer,
                    BroadcastAt = broadcastAt,
                });
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to pull adaptation from {Dir}", dir);
            }
        }

        return results;
    }

    /// <inheritdoc />
    public async Task<bool> ValidateAndAdoptAsync(SharedAdaptationEntry entry, CancellationToken cancellationToken = default)
    {
        if (_trustedPeerIds != null && _trustedPeerIds.Count > 0)
        {
            var sourceId = entry.SourcePeerId ?? "local";
            if (!_trustedPeerIds.Contains(sourceId, StringComparer.OrdinalIgnoreCase))
            {
                _logger?.LogWarning("Rejecting shared adaptation {Id}: source {Source} not in trusted peers", entry.Id, sourceId);
                return false;
            }
        }

        var repoRoot = _repoRoot ?? RepoPathResolver.FindRepoRoot();
        var slnPath = Path.Combine(repoRoot, "Ashlar.sln");
        if (!File.Exists(slnPath))
        {
            _logger?.LogWarning("Cannot validate: not in Ashlar repo");
            return false;
        }

        // Validate EVERY file before writing ANY — a peer's adaptation is untrusted content, so it
        // is judged by the same MediatedWritePath floor as a forge apply or a package import: it may
        // not escape the repo, touch a governance/build-executed path, or run through a symlink. The
        // immutable-core registry is an additional, narrower engine-source guard. Nothing is written
        // until the whole batch passes, so a rejected adoption leaves the tree untouched — closing
        // the path that used to Path.Combine attacker input straight into File.WriteAllBytes and then
        // build it.
        foreach (var (path, _) in entry.Files)
        {
            if (_immutableCore.IsInImmutableCore(path))
            {
                _logger?.LogWarning("Rejecting shared adaptation: targets immutable core {Path}", path);
                return false;
            }
            var refusal = MediatedWritePath.Refuse(repoRoot, path);
            if (refusal is not null)
            {
                _logger?.LogWarning("Rejecting shared adaptation {Id}: {Reason}", entry.Id, refusal);
                return false;
            }
        }

        foreach (var (path, content) in entry.Files)
        {
            var fullPath = Path.Combine(repoRoot, path);
            var parent = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(parent))
                Directory.CreateDirectory(parent);
            await File.WriteAllBytesAsync(fullPath, content, cancellationToken).ConfigureAwait(false);
        }

        var result = await _regressionRunner.RunAsync(slnPath, filter: null, cancellationToken).ConfigureAwait(false);
        if (!result.AllPassed)
        {
            _logger?.LogWarning("Shared adaptation {Id} failed regression; rolling back", entry.Id);
            foreach (var (path, _) in entry.Files)
            {
                var fullPath = Path.Combine(repoRoot, path);
                if (File.Exists(fullPath))
                    File.Delete(fullPath);
            }
            return false;
        }

        var attribution = !string.IsNullOrEmpty(entry.SourcePeerId)
            ? $" (adapted from instance {entry.SourcePeerId})"
            : "";
        var recordToLog = entry.Record with
        {
            Promoted = true,
            Message = (entry.Record.Message ?? "") + attribution,
        };
        await _log.LogAsync(recordToLog, cancellationToken).ConfigureAwait(false);
        _logger?.LogInformation("Adopted shared adaptation {Id} from {Source}", entry.Id, entry.SourcePeerId ?? "local");
        return true;
    }
}
