using System.Text.Json;
using Microsoft.Extensions.Logging;
using Ashlar.Core.Application.Observation.Models;
using Ashlar.Core.Application.Observation.Ports;

namespace Ashlar.Infrastructure.Observation;
/// <summary>
/// Detects patterns in the normalized event stream using sliding-window heuristics.
/// Emits ObservedPattern when thresholds are met (e.g. repeated-edits, edit-then-build).
/// </summary>
public sealed class PatternDetector
{
    private readonly TimeSpan _windowSize;
    private readonly int _repeatedEditThreshold;
    private readonly IPatternStore _store;
    private readonly ILogger<PatternDetector>? _logger;
    private readonly List<NormalizedEvent> _window = new();
    private readonly HashSet<string> _consumedEdits = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);
    /// <summary>
    /// Creates a pattern detector.
    /// </summary>
    /// <param name = "windowSize">Sliding window duration (e.g. 5 min).</param>
    /// <param name = "repeatedEditThreshold">Minimum file changes to same path to emit repeated-edits.</param>
    /// <param name = "store">Pattern store to write detected patterns to.</param>
    /// <param name = "logger">Optional logger.</param>
    public PatternDetector(TimeSpan windowSize, int repeatedEditThreshold, IPatternStore store, ILogger<PatternDetector>? logger = null)
    {
        if (windowSize <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(windowSize));
        if (repeatedEditThreshold <= 0) throw new ArgumentOutOfRangeException(nameof(repeatedEditThreshold));
        _windowSize = windowSize;
        _repeatedEditThreshold = repeatedEditThreshold;
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _logger = logger;
    }

    /// <summary>
    /// Process an event and potentially emit a pattern.
    /// </summary>
    public async Task ProcessAsync(NormalizedEvent evt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evt);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var cutoff = DateTimeOffset.UtcNow - _windowSize;
            _window.RemoveAll(e => e.Timestamp < cutoff);
            _consumedEdits.IntersectWith(_window.Select(e => e.EventId));
            if (evt.Timestamp < cutoff || _window.Any(e => e.EventId == evt.EventId))
                return;
            _window.Add(evt);
            if (evt.SourceId == "file-system" && evt.Category == "file-paths")
                await DetectRepeatedEditsAsync(evt, cancellationToken).ConfigureAwait(false);
            if (evt.SourceId == "process-monitor" && evt.Category == "process-names")
                await DetectEditThenBuildAsync(evt, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // A failed write must remain retryable when the same observation is redelivered.
            _window.RemoveAll(e => e.EventId == evt.EventId);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task DetectRepeatedEditsAsync(NormalizedEvent incoming, CancellationToken ct)
    {
        var path = GetPathFromPayload(incoming);
        if (string.IsNullOrEmpty(path)) return;
        // Each observation contributes to one repeated-edits burst, never every later event.
        var events = _window.Where(e => e.SourceId == "file-system" && e.Category == "file-paths"
                && e.ProjectPath == incoming.ProjectPath && !_consumedEdits.Contains(e.EventId)
                && string.Equals(GetPathFromPayload(e), path, StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.Timestamp).ToList();
        if (events.Count < _repeatedEditThreshold) return;
        var first = events[0];
        var last = events[^1];
        var pattern = new ObservedPattern
        {
            PatternId = Guid.NewGuid().ToString("N"),
            EventType = "repeated-edits",
            Frequency = events.Count,
            FirstSeen = first.Timestamp,
            LastSeen = last.Timestamp,
            ProjectPath = first.ProjectPath,
            RelatedEventIds = events.Select(e => e.EventId).ToList(),
            Metadata = JsonSerializer.SerializeToElement(new { path, intervalSeconds = (last.Timestamp - first.Timestamp).TotalSeconds }),
        };
        await _store.AddAsync(pattern, ct).ConfigureAwait(false);
        _consumedEdits.UnionWith(pattern.RelatedEventIds);
        _logger?.LogInformation("Detected pattern: {Type} for {Path} ({Count} edits)", pattern.EventType, path, events.Count);
    }

    private async Task DetectEditThenBuildAsync(NormalizedEvent proc, CancellationToken ct)
    {
        // Only the incoming process start can create a new pattern. A later unrelated event,
        // repeated delivery, or process stop must not replay every earlier build in the window.
        if (proc.Payload is not { } payload || payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("changeType", out var changeType) || changeType.ValueKind != JsonValueKind.String || changeType.GetString() != "started")
            return;
        var procName = GetProcessNameFromPayload(proc);
        if (string.IsNullOrEmpty(procName) || (procName != "dotnet" && procName != "msbuild" && !procName.Contains("ashlar")))
            return;
        var recentFileEvents = _window.Where(e => e.SourceId == "file-system" && e.Category == "file-paths"
                && (proc.ProjectPath == null || e.ProjectPath == proc.ProjectPath)
                && e.Timestamp < proc.Timestamp && (proc.Timestamp - e.Timestamp).TotalSeconds < 120)
            .OrderBy(e => e.Timestamp).ToList();
        if (recentFileEvents.Count == 0) return;
        var first = recentFileEvents[0];
        var pattern = new ObservedPattern
        {
            PatternId = Guid.NewGuid().ToString("N"),
            EventType = "edit-then-build",
            Frequency = 1,
            FirstSeen = first.Timestamp,
            LastSeen = proc.Timestamp,
            ProjectPath = first.ProjectPath ?? proc.ProjectPath,
            RelatedEventIds = recentFileEvents.Take(5).Select(e => e.EventId).Append(proc.EventId).ToList(),
            Metadata = JsonSerializer.SerializeToElement(new { processName = procName, fileCount = recentFileEvents.Count }),
        };
        await _store.AddAsync(pattern, ct).ConfigureAwait(false);
        _logger?.LogInformation("Detected pattern: {Type} ({Process})", pattern.EventType, procName);
    }

    private static string? GetPathFromPayload(NormalizedEvent evt)
    {
        if (!evt.Payload.HasValue)
            return null;
        try
        {
            var doc = evt.Payload.Value;
            if (doc.TryGetProperty("fullPath", out var pathProp))
                return pathProp.GetString();
        }
        catch
        {
            // Ignore malformed payload
        }

        return null;
    }

    private static string? GetProcessNameFromPayload(NormalizedEvent evt)
    {
        if (!evt.Payload.HasValue)
            return null;
        try
        {
            var doc = evt.Payload.Value;
            if (doc.TryGetProperty("processName", out var prop))
                return prop.GetString();
        }
        catch
        {
            // Ignore malformed payload
        }

        return null;
    }
}