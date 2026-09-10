using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ashlar.Core.Application.Adaptation.Models;
using Ashlar.Core.Application.Adaptation.Ports;
using Ashlar.Core.Application.Analysis.Models;
using Ashlar.Core.Application.Analysis.Ports;
using Ashlar.Core.Application.Paths;
using Ashlar.Infrastructure;
using Ashlar.Infrastructure.Adaptation;
using Ashlar.Tests.Infrastructure.Helpers;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Dogfood;

/// <summary>
/// Block 10: Shared adaptation cache dogfood.
/// Verifies broadcast and pull work in Ashlar repo context.
/// </summary>
[Collection("Integration")]
[Trait("Category", "Integration")]
[Trait("Category", "Dogfood")]
public sealed class DogfoodBlock10SharedAdaptationTests : TempDirTestBase
{
    private readonly string _sharedPath;
    private readonly string _storePath;

    public DogfoodBlock10SharedAdaptationTests() : base("ashlar-dogfood-block10")
    {
        _sharedPath = Path.Combine(TempDir, "shared");
        _storePath = Path.Combine(TempDir, "adapt.db");
    }

    [Fact(Timeout = 30000)]
    public async Task ImproveFlow_OnPromotion_BroadcastsToSharedStore()
    {
        var repoRoot = RepoPathResolver.FindRepoRoot();
        var slnPath = Path.Combine(repoRoot, "Ashlar.sln");
        if (!File.Exists(slnPath))
            return;

        var mockRegression = new StubRegressionTestRunner(allPassed: true);
        var services = new ServiceCollection()
            .AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning))
            .AddAdaptationInfrastructure(_storePath)
            .AddSelfContextInfrastructure(_storePath, docsRoot: TempDir)
            .AddSharedAdaptationCache(_sharedPath, mockRegression)
            .BuildServiceProvider();

        var broadcaster = services.GetRequiredService<ISharedAdaptationBroadcaster>();
        var sync = services.GetRequiredService<ISharedAdaptationSync>();

        var record = new AdaptationRecord
        {
            Id = "dogfood-broadcast-test",
            Timestamp = DateTimeOffset.UtcNow,
            BrickId = "test.brick",
            FailureType = "EmptyCatch",
            FixApplied = AdaptationFixType.Source,
            FilePath = "src/Ashlar.Bricks.Owasp/SomeFile.cs",
            RegressionPassed = true,
            Promoted = true,
            Message = "Dogfood broadcast test",
        };
        var entry = new SharedAdaptationEntry
        {
            Id = record.Id,
            Record = record,
            Files = new Dictionary<string, byte[]> { ["src/Ashlar.Bricks.Owasp/SomeFile.cs"] = System.Text.Encoding.UTF8.GetBytes("// test") },
            BroadcastAt = record.Timestamp,
        };

        await broadcaster.BroadcastAsync(entry);

        var pulled = await sync.PullAsync();
        pulled.Should().HaveCount(1);
        pulled[0].Id.Should().Be(entry.Id);
    }

    private sealed class StubRegressionTestRunner : IRegressionTestRunner
    {
        private readonly bool _allPassed;

        public StubRegressionTestRunner(bool allPassed) => _allPassed = allPassed;

        public Task<RegressionTestResult> RunAsync(string projectOrSolutionPath, string? filter = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new RegressionTestResult { AllPassed = _allPassed, PassedCount = 1, FailedCount = 0, Summary = "Stub" });
    }
}
