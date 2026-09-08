using FluentAssertions;
using Ashlar.Core.Application.Adaptation.Models;
using Ashlar.Core.Application.Adaptation.Ports;
using Ashlar.Core.Application.Observation.Models;
using Ashlar.Core.Application.Observation.Ports;
using Ashlar.Commercial.Fleet.Infrastructure;
using Xunit;

namespace Ashlar.Commercial.Tests.Fleet;

/// <summary>Tests for mesh knowledge replication.</summary>
public sealed class MeshKnowledgeReplicationTests : IDisposable
{
    private readonly string _dir;

    public MeshKnowledgeReplicationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ashlar-mesh-knowledge-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // best effort
        }
    }

    [Fact]
    public async Task Export_then_import_twice_skips_duplicate_adaptations_and_patterns()
    {
        var logA = new InMemoryAdaptationLog();
        var storeA = new InMemoryPatternStore();
        var logB = new InMemoryAdaptationLog();
        var storeB = new InMemoryPatternStore();

        await logA.LogAsync(new AdaptationRecord
        {
            Id = "adapt-1",
            Timestamp = DateTimeOffset.UtcNow,
            BrickId = "brick-x",
            FailureType = "test",
            FixApplied = AdaptationFixType.Source,
            RegressionPassed = true,
            Promoted = false,
            Message = "seed"
        });

        await storeA.AddAsync(new ObservedPattern
        {
            PatternId = "pat-1",
            EventType = "repeated-edits",
            Frequency = 3,
            FirstSeen = DateTimeOffset.UtcNow.AddMinutes(-10),
            LastSeen = DateTimeOffset.UtcNow,
            ProjectPath = "/tmp"
        });

        var export = new MeshKnowledgeExportService(logA, storeA);
        var payload = await export.ExportAsync(since: null, maxAdaptations: 100, maxPatterns: 100);

        var import = new MeshKnowledgeImportService(logB, storeB);
        var r1 = await import.ImportAsync(payload);
        Assert.Equal(1, r1.AdaptationsApplied);
        Assert.Equal(1, r1.PatternsApplied);

        var r2 = await import.ImportAsync(payload);
        Assert.True(r2.AdaptationsSkipped > 0);
        Assert.True(r2.PatternsSkipped > 0);
    }

    private sealed class InMemoryAdaptationLog : IAdaptationLog
    {
        private readonly List<AdaptationRecord> _records = new();

        public Task LogAsync(AdaptationRecord record, CancellationToken cancellationToken = default)
        {
            _records.Add(record);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AdaptationRecord>> QueryAsync(DateTimeOffset? since = null, DateTimeOffset? until = null, string? brickId = null, CancellationToken cancellationToken = default)
        {
            var filtered = _records.AsEnumerable();
            if (since.HasValue)
                filtered = filtered.Where(r => r.Timestamp >= since.Value);
            if (until.HasValue)
                filtered = filtered.Where(r => r.Timestamp <= until.Value);
            if (!string.IsNullOrEmpty(brickId))
                filtered = filtered.Where(r => r.BrickId == brickId);
            return Task.FromResult<IReadOnlyList<AdaptationRecord>>(filtered.ToList());
        }
    }

    private sealed class InMemoryPatternStore : IPatternStore
    {
        private readonly List<ObservedPattern> _patterns = new();

        public Task AddAsync(ObservedPattern pattern, CancellationToken cancellationToken = default)
        {
            _patterns.Add(pattern);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ObservedPattern>> QueryAsync(PatternStoreQueryParams query, CancellationToken cancellationToken = default)
        {
            var filtered = _patterns.AsEnumerable();
            if (!string.IsNullOrEmpty(query.EventType))
                filtered = filtered.Where(p => p.EventType == query.EventType);
            if (query.Since.HasValue)
                filtered = filtered.Where(p => p.LastSeen >= query.Since.Value);
            return Task.FromResult<IReadOnlyList<ObservedPattern>>(filtered.ToList());
        }

        public Task PersistAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }
}
