using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ashlar.Core.Application.Analysis.Ports;
using Ashlar.Core.Application.Paths;
using Ashlar.Infrastructure;
using Ashlar.Infrastructure.Adaptation;
using Ashlar.Infrastructure.Analysis;
using Ashlar.Infrastructure.Observation;
using Ashlar.Infrastructure.SelfContext;
using Ashlar.Tests.Infrastructure.Helpers;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Dogfood;

/// <summary>
/// Phase F: Continuous self-improvement loop.
/// Validates the improve flow (analyze Block 1 path) completes on Ashlar.
/// Uses same services as ImproveCommand; avoids CLI assembly-loading issues.
/// </summary>
[Collection("Integration")]
[Trait("Category", "Integration")]
[Trait("Category", "Dogfood")]
[Trait("Category", "Adaptation")]
public sealed class DogfoodClosedLoopTests : TempDirTestBase
{
    public DogfoodClosedLoopTests() : base("ashlar-dogfood-closedloop") { }

    [Fact(Timeout = 30000)]
    public async Task ImproveFlow_AnalyzeBlock1Path_Completes()
    {
        var repoRoot = RepoPathResolver.FindRepoRoot();
        var slnPath = Path.Combine(repoRoot, "Ashlar.sln");
        if (!File.Exists(slnPath))
        {
            return; // Not in Ashlar repo
        }

        var block1Path = RepoPathResolver.FindBlock1ObservationPath(repoRoot);
        if (!Directory.Exists(block1Path))
        {
            return;
        }

        var storePath = Path.Combine(TempDir, "adapt.db");
        var services = new ServiceCollection()
            .AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning))
            .AddCodeAnalyzers()
            .AddAdaptationInfrastructure(storePath)
            .AddSelfContextInfrastructure(storePath, docsRoot: TempDir)
            .BuildServiceProvider();

        var analyzer = services.GetRequiredService<IBrickStaticAnalyzer>();
        var analysisResult = await analyzer.AnalyzeSourceAsync(block1Path);

        // Gate: closed-loop analyze completes on Ashlar Block 1 (violations or clean)
        Assert.NotNull(analysisResult);
    }
}
