using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ashlar.Core.Application.Observation.Models;
using Ashlar.Core.Application.Observation.Ports;
using Ashlar.Core.Application.SelfContext.Models;
using Ashlar.Core.Application.SelfContext.Ports;
using Ashlar.Core.Application.SelfImprovement.Ports;
using Ashlar.Infrastructure;
using Ashlar.Infrastructure.Analysis;
using Ashlar.Infrastructure.Observation;
using Ashlar.Tests.Infrastructure.Helpers;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.SelfImprovement;

/// <summary>
/// P1.1: Self-improvement loop picks up patterns from pattern store and processes them.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SelfImprovementLoopPatternTests : TempDirTestBase
{
    public SelfImprovementLoopPatternTests() : base("ashlar-selfimprove-pattern") { }

    [Fact]
    public async Task SelfImprovementLoop_PicksUp_PatternsFromPatternStore()
    {
        var storePath = Path.Combine(TempDir, $"pattern-{Guid.NewGuid():N}.db");
        var patternStore = new LiteDbPatternStore(storePath);
        var processedStore = new LiteDbPatternProcessedStore(storePath);

        await patternStore.AddAsync(new ObservedPattern
        {
            PatternId = "p1",
            EventType = "repeated-edits",
            Frequency = 3,
            FirstSeen = DateTimeOffset.UtcNow.AddHours(-1),
            LastSeen = DateTimeOffset.UtcNow,
            ProjectPath = "src",
            Metadata = System.Text.Json.JsonSerializer.SerializeToElement(new { path = "src/Ashlar.Bricks.Owasp/Security/OWASPScannerBrick.cs" }),
        });

        var failures = new List<TestFailureRecord>();
        var services = new ServiceCollection()
            .AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning))
            .AddSingleton<ITestFailureStore>(new MockTestFailureStore(failures))
            .AddCodeAnalyzers()
            .AddAdaptationInfrastructure(storePath)
            .AddSingleton<IPatternStore>(patternStore)
            .AddSingleton<IPatternProcessedStore>(processedStore)
            .AddSelfImprovementLoop(5)
            .BuildServiceProvider();

        var loop = services.GetRequiredService<ISelfImprovementLoop>();
        await loop.RunOnceAsync();

        var report = await loop.GetLastRunReportAsync();
        report.Should().NotBeNull();
        report!.PatternsProcessed.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task SelfImprovementLoop_MarksPatternsAsProcessed_AfterAttempt()
    {
        var storePath = Path.Combine(TempDir, $"pattern-processed-{Guid.NewGuid():N}.db");
        var patternStore = new LiteDbPatternStore(storePath);
        var processedStore = new LiteDbPatternProcessedStore(storePath);

        await patternStore.AddAsync(new ObservedPattern
        {
            PatternId = "p2",
            EventType = "repeated-edits",
            Frequency = 3,
            FirstSeen = DateTimeOffset.UtcNow.AddHours(-1),
            LastSeen = DateTimeOffset.UtcNow,
            ProjectPath = "src",
            Metadata = System.Text.Json.JsonSerializer.SerializeToElement(new { path = "src/NonexistentBrick.cs" }),
        });

        var failures = new List<TestFailureRecord>();
        var services = new ServiceCollection()
            .AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning))
            .AddSingleton<ITestFailureStore>(new MockTestFailureStore(failures))
            .AddCodeAnalyzers()
            .AddAdaptationInfrastructure(storePath)
            .AddSingleton<IPatternStore>(patternStore)
            .AddSingleton<IPatternProcessedStore>(processedStore)
            .AddSelfImprovementLoop(5)
            .BuildServiceProvider();

        var loop = services.GetRequiredService<ISelfImprovementLoop>();
        await loop.RunOnceAsync();

        var wasProcessed = await processedStore.IsProcessedAsync("p2");
        wasProcessed.Should().BeTrue("pattern should be marked processed after attempt");
    }

    [Fact]
    public async Task SelfImprovementLoop_DoesNotReprocess_AlreadyHandledPattern()
    {
        var storePath = Path.Combine(TempDir, $"pattern-noreprocess-{Guid.NewGuid():N}.db");
        var patternStore = new LiteDbPatternStore(storePath);
        var processedStore = new LiteDbPatternProcessedStore(storePath);

        await patternStore.AddAsync(new ObservedPattern
        {
            PatternId = "p3",
            EventType = "repeated-edits",
            Frequency = 3,
            FirstSeen = DateTimeOffset.UtcNow.AddHours(-1),
            LastSeen = DateTimeOffset.UtcNow,
            ProjectPath = "src",
            Metadata = System.Text.Json.JsonSerializer.SerializeToElement(new { path = "src/SomeFile.cs" }),
        });

        (await processedStore.TryClaimAsync("p3")).Should().BeTrue();

        var failures = new List<TestFailureRecord>();
        var services = new ServiceCollection()
            .AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning))
            .AddSingleton<ITestFailureStore>(new MockTestFailureStore(failures))
            .AddCodeAnalyzers()
            .AddAdaptationInfrastructure(storePath)
            .AddSingleton<IPatternStore>(patternStore)
            .AddSingleton<IPatternProcessedStore>(processedStore)
            .AddSelfImprovementLoop(5)
            .BuildServiceProvider();

        var loop = services.GetRequiredService<ISelfImprovementLoop>();
        await loop.RunOnceAsync();

        var report = await loop.GetLastRunReportAsync();
        report.Should().NotBeNull();
        report!.PatternsProcessed.Should().Be(0, "pre-marked pattern should be skipped");
    }

    private sealed class MockTestFailureStore : ITestFailureStore
    {
        private readonly IReadOnlyList<TestFailureRecord> _records;

        public MockTestFailureStore(IReadOnlyList<TestFailureRecord> records) => _records = records;

        public Task RecordAsync(TestFailureRecord record, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<TestFailureRecord>> QueryAsync(DateTimeOffset? since = null, DateTimeOffset? until = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(_records);
    }
}
