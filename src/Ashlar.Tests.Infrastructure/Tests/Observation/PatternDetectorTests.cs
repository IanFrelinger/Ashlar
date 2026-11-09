using System.Collections.Concurrent;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Ashlar.Core.Application.Observation.Models;
using Ashlar.Core.Application.Observation.Ports;
using Ashlar.Infrastructure.Observation;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Observation;

/// <summary>Tests for pattern detector.</summary>
public class PatternDetectorTests
{
    /// <summary>Tests for in memory pattern store.</summary>
    private sealed class InMemoryPatternStore : IPatternStore
    {
        private readonly ConcurrentBag<ObservedPattern> _patterns = new();

        public Func<Task>? BeforeAdd { get; init; }

        public async Task AddAsync(ObservedPattern pattern, CancellationToken cancellationToken = default)
        {
            if (BeforeAdd is not null) await BeforeAdd();
            _patterns.Add(pattern);
        }

        public Task<IReadOnlyList<ObservedPattern>> QueryAsync(PatternStoreQueryParams query, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<ObservedPattern>>(_patterns.ToList());
        }

        /// <summary>Persist async.</summary>
        /// <param name="default">Default.</param>
        public Task PersistAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        /// <summary>Gets all.</summary>
        public IReadOnlyList<ObservedPattern> GetAll() => _patterns.ToList();
    }

    [Fact]
    public async Task ProcessAsync_RepeatedEditsToSamePath_EmitsRepeatedEditsPattern()
    {
        var store = new InMemoryPatternStore();
        var detector = new PatternDetector(TimeSpan.FromMinutes(5), 3, store, null);

        var path = "/tmp/proj/src/foo.cs";
        var payload = JsonSerializer.SerializeToElement(new { fullPath = path, changeType = "changed" });

        for (var i = 0; i < 3; i++)
        {
            await detector.ProcessAsync(new NormalizedEvent
            {
                EventId = $"e{i}",
                Timestamp = DateTimeOffset.UtcNow,
                SourceId = "file-system",
                Category = "file-paths",
                Payload = payload,
            });
        }

        var patterns = store.GetAll();
        patterns.Should().ContainSingle(p => p.EventType == "repeated-edits");
        patterns.First(p => p.EventType == "repeated-edits").Frequency.Should().Be(3);
    }

    [Fact]
    public async Task ProcessAsync_BelowThreshold_DoesNotEmit()
    {
        var store = new InMemoryPatternStore();
        var detector = new PatternDetector(TimeSpan.FromMinutes(5), 3, store, null);

        var path = "/tmp/proj/src/foo.cs";
        var payload = JsonSerializer.SerializeToElement(new { fullPath = path, changeType = "changed" });

        for (var i = 0; i < 2; i++)
        {
            await detector.ProcessAsync(new NormalizedEvent
            {
                EventId = $"e{i}",
                Timestamp = DateTimeOffset.UtcNow,
                SourceId = "file-system",
                Category = "file-paths",
                Payload = payload,
            });
        }

        store.GetAll().Should().BeEmpty();
    }

    [Fact]
    public async Task ProcessAsync_ProcessEvents_DoesNotCrash()
    {
        var store = new InMemoryPatternStore();
        var detector = new PatternDetector(TimeSpan.FromMinutes(5), 3, store, null);

        var payload = JsonSerializer.SerializeToElement(new { processName = "dotnet", changeType = "started" });
        await detector.ProcessAsync(new NormalizedEvent
        {
            EventId = "pe1",
            Timestamp = DateTimeOffset.UtcNow,
            SourceId = "process-monitor",
            Category = "process-names",
            Payload = payload,
        });
    }

    [Fact]
    public async Task Repeated_edits_consume_evidence_once_and_ignore_unrelated_paths()
    {
        var store = new InMemoryPatternStore();
        var detector = new PatternDetector(TimeSpan.FromMinutes(5), 3, store);
        for (var i = 0; i < 3; i++) await detector.ProcessAsync(FileEvent($"edit-{i}", "a.cs"));
        await detector.ProcessAsync(FileEvent("unrelated", "b.cs"));
        await detector.ProcessAsync(FileEvent("edit-3", "a.cs"));
        store.GetAll().Should().ContainSingle();
        for (var i = 4; i < 6; i++) await detector.ProcessAsync(FileEvent($"edit-{i}", "a.cs"));
        store.GetAll().Should().HaveCount(2);
        store.GetAll().SelectMany(p => p.RelatedEventIds).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task Duplicate_or_concurrent_deliveries_do_not_amplify_evidence()
    {
        var writing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new InMemoryPatternStore { BeforeAdd = async () => { writing.TrySetResult(); await release.Task; } };
        var detector = new PatternDetector(TimeSpan.FromMinutes(5), 3, store);
        var events = Enumerable.Range(0, 9).Select(i => FileEvent($"edit-{i}", "a.cs")).ToArray();
        var tasks = events.Concat(events).Select(e => Task.Run(() => detector.ProcessAsync(e))).ToArray();
        try
        {
            await writing.Task.WaitAsync(TimeSpan.FromSeconds(5));
            tasks.Count(t => !t.IsCompleted).Should().BeGreaterThan(1);
        }
        finally { release.TrySetResult(); }
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5));
        store.GetAll().Should().HaveCount(3);
        store.GetAll().SelectMany(p => p.RelatedEventIds).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task Process_stop_and_unrelated_process_do_not_replay_an_earlier_build()
    {
        var store = new InMemoryPatternStore();
        var detector = new PatternDetector(TimeSpan.FromMinutes(5), 10, store);
        await detector.ProcessAsync(FileEvent("edit", "a.cs") with { Timestamp = DateTimeOffset.UtcNow.AddSeconds(-1) });
        var build = new NormalizedEvent
        {
            EventId = "build", Timestamp = DateTimeOffset.UtcNow, SourceId = "process-monitor", Category = "process-names",
            Payload = JsonSerializer.SerializeToElement(new { processName = "dotnet", changeType = "started" })
        };
        await detector.ProcessAsync(build);
        await detector.ProcessAsync(build);
        await detector.ProcessAsync(build with { EventId = "stop", Payload = JsonSerializer.SerializeToElement(new { processName = "dotnet", changeType = "stopped" }) });
        await detector.ProcessAsync(build with { EventId = "other", Payload = JsonSerializer.SerializeToElement(new { processName = "editor", changeType = "started" }) });
        store.GetAll().Should().ContainSingle(p => p.EventType == "edit-then-build");
    }

    private static NormalizedEvent FileEvent(string id, string path) => new()
    {
        EventId = id, Timestamp = DateTimeOffset.UtcNow, SourceId = "file-system", Category = "file-paths",
        Payload = JsonSerializer.SerializeToElement(new { fullPath = path, changeType = "changed" })
    };
}
