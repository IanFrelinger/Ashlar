using System.Text.Json;
using Microsoft.Extensions.Logging;
using Ashlar.Abstractions.Security.Egress;
using Ashlar.Core.Application.Adaptation.Models;
using Ashlar.Core.Application.Adaptation.Ports;

namespace Ashlar.Infrastructure.Adaptation;

/// <summary>
/// Sneakernet transport: export shared adaptations to a single file, import and adopt.
/// P3.3: ashlar mesh export, ashlar mesh import.
/// </summary>
public sealed class SneakernetTransport : ISneakernetTransport
{
    private readonly ISharedAdaptationSync _sync;
    private readonly ILogger<SneakernetTransport>? _logger;

    /// <summary>Initializes a new sneakernet transport.</summary>
    public SneakernetTransport(ISharedAdaptationSync sync, ILogger<SneakernetTransport>? logger = null)
    {
        _sync = sync ?? throw new ArgumentNullException(nameof(sync));
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task ExportAsync(string outputPath, string? componentId = null, CancellationToken cancellationToken = default)
    {
        var entries = await _sync.PullAsync(cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(componentId))
            entries = entries.Where(e => e.Id.Equals(componentId, StringComparison.OrdinalIgnoreCase)).ToList();
        var path = outputPath;
        if (!path.EndsWith(".nxpkg", StringComparison.OrdinalIgnoreCase))
            path = Path.ChangeExtension(path, ".nxpkg") ?? path + ".nxpkg";

        // SPEC-007 EG-MESH-08, report-only: every shared adaptation leaves in this file, for physical transfer.
        _ = EgressGuard.ProcessDefault.Evaluate(new EgressRequest(EgressFamilies.FileExport, "EG-MESH-08", "file:" + path));
        var dto = new SneakernetExportDto
        {
            Version = 1,
            Format = "ashlar-mesh-adaptation",
            ExportedAt = DateTimeOffset.UtcNow,
            EntryCount = entries.Count,
            Entries = entries.Select(e => new SneakernetEntryDto
            {
                Id = e.Id,
                Record = e.Record,
                Files = e.Files.ToDictionary(kv => kv.Key, kv => Convert.ToBase64String(kv.Value)),
                SourcePeerId = e.SourcePeerId,
                BroadcastAt = e.BroadcastAt
            }).ToList()
        };
        var json = JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(path, json, cancellationToken).ConfigureAwait(false);
        _logger?.LogInformation("Exported {Count} adaptation(s) to {Path}", entries.Count, path);
    }

    /// <inheritdoc />
    public async Task<int> ImportAsync(string inputPath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(inputPath))
        {
            _logger?.LogWarning("Export file not found: {Path}", inputPath);
            return 0;
        }
        var json = await File.ReadAllTextAsync(inputPath, cancellationToken).ConfigureAwait(false);
        var dto = JsonSerializer.Deserialize<SneakernetExportDto>(json);
        if (dto?.Entries == null || dto.Entries.Count == 0)
        {
            _logger?.LogInformation("No entries in export file");
            return 0;
        }
        var adopted = 0;
        foreach (var e in dto.Entries)
        {
            var entry = new SharedAdaptationEntry
            {
                Id = e.Id,
                Record = e.Record,
                Files = e.Files.ToDictionary(kv => kv.Key, kv => Convert.FromBase64String(kv.Value)),
                SourcePeerId = e.SourcePeerId,
                BroadcastAt = e.BroadcastAt
            };
            var ok = await _sync.ValidateAndAdoptAsync(entry, cancellationToken).ConfigureAwait(false);
            if (ok) adopted++;
        }
        _logger?.LogInformation("Imported {Adopted}/{Total} adaptation(s) from {Path}", adopted, dto.Entries.Count, inputPath);
        return adopted;
    }

    private sealed class SneakernetExportDto
    {
        /// <summary>Version.</summary>
        public int Version { get; set; }
        /// <summary>Format.</summary>
        public string Format { get; set; } = "ashlar-mesh-adaptation";
        /// <summary>Exported at.</summary>
        public DateTimeOffset ExportedAt { get; set; }
        /// <summary>Entry count.</summary>
        public int EntryCount { get; set; }
        /// <summary>Entries.</summary>
        public List<SneakernetEntryDto> Entries { get; set; } = new();
    }

    private sealed class SneakernetEntryDto
    {
        /// <summary>Id.</summary>
        public string Id { get; set; } = string.Empty;
        /// <summary>Record.</summary>
        public AdaptationRecord Record { get; set; } = null!;
        /// <summary>Files.</summary>
        public Dictionary<string, string> Files { get; set; } = new();
        /// <summary>Source peer id.</summary>
        public string? SourcePeerId { get; set; }
        /// <summary>Broadcast at.</summary>
        public DateTimeOffset BroadcastAt { get; set; }
    }
}
