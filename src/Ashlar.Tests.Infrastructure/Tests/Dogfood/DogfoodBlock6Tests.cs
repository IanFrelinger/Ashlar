using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ashlar.Core.Application.Adaptation.Models;
using Ashlar.Core.Application.Adaptation.Ports;
using Ashlar.Core.Application.Observation.Models;
using Ashlar.Core.Application.Observation.Ports;
using Ashlar.Core.Application.SelfContext.Ports;
using Ashlar.Infrastructure;
using Ashlar.Infrastructure.SelfContext;
using Ashlar.Tests.Application.Helpers;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Dogfood;

/// <summary>
/// Block 6 dogfood gate: SelfContextAssembler answers "what did I change in 24h?".
/// Validates AssembleAsync(lookback: 24h) returns adaptations, executions, patterns.
/// </summary>
[Collection("Integration")]
[Trait("Category", "Integration")]
[Trait("Category", "Dogfood")]
[Trait("Category", "Adaptation")]
public sealed class DogfoodBlock6Tests : IDisposable
{
    private readonly string _tempDir;
    private readonly IDisposable _tempDirCleanup;

    public DogfoodBlock6Tests()
    {
        (_tempDir, _tempDirCleanup) = TestHelpers.CreateTempDirectoryWithCleanup("ashlar-dogfood-block6");
    }

    public void Dispose() => _tempDirCleanup.Dispose();

    [Fact(Timeout = 15000)]
    public async Task SelfContextAssembler_With24hLookback_ReturnsWhatChangedIn24h()
    {
        var storePath = Path.Combine(_tempDir, "patterns.db");
        var services = new ServiceCollection()
            .AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning))
            .AddAdaptationInfrastructure(storePath)
            .AddSelfContextInfrastructure(storePath, docsRoot: _tempDir)
            .BuildServiceProvider();

        var adaptationLog = services.GetRequiredService<IAdaptationLog>();
        var executionTracer = services.GetRequiredService<IExecutionTracer>();
        var patternStore = services.GetRequiredService<IPatternStore>();
        var assembler = services.GetRequiredService<ISelfContextAssembler>();

        await adaptationLog.LogAsync(new AdaptationRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = DateTimeOffset.UtcNow,
            BrickId = "observation.context",
            FailureType = "EmptyCatch",
            FixApplied = AdaptationFixType.Source,
            FilePath = Path.Combine(_tempDir, "ContextAssembler.cs"),
            RegressionPassed = true,
            Promoted = true,
            Message = "Dogfood: 24h change",
        });

        await executionTracer.TraceAsync("adapt.start", new Dictionary<string, object> { ["brickId"] = "observation.context" }, _tempDir, null);
        await executionTracer.TraceAsync("adapt.end", null, _tempDir, "recompiled");

        await patternStore.AddAsync(new ObservedPattern
        {
            PatternId = "p1",
            EventType = "repeated-edits",
            Frequency = 2,
            FirstSeen = DateTimeOffset.UtcNow.AddHours(-1),
            LastSeen = DateTimeOffset.UtcNow,
        });
        await patternStore.PersistAsync();

        var result = await assembler.AssembleAsync(TimeSpan.FromHours(24));

        Assert.NotNull(result.Summary);
        Assert.Contains("Recent Activity Summary", result.Summary);
        Assert.Contains("Adaptations", result.Summary);
        Assert.Contains("Executions", result.Summary);
        Assert.Contains("Patterns", result.Summary);
        Assert.NotEmpty(result.RecentAdaptations);
        Assert.NotEmpty(result.RecentExecutions);
        Assert.NotEmpty(result.RecentPatterns);
        Assert.Contains("EmptyCatch", result.Summary);
        Assert.Contains("repeated-edits", result.Summary);
    }
}
