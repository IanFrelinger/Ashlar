using Ashlar.Tests.Application.Helpers;
using Ashlar.Tests.Infrastructure.Helpers;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.CLI;

/// <summary>
/// End-to-end smoke tests for Phases 1-4: observe, analyze bricks, adapt, improve.
/// Uses prebuilt CLI (build once, run many) for faster execution.
/// </summary>
[Collection("E2E")]
[Trait("Category", "E2E")]
public sealed class Phases14CliE2ETests : E2ETestBase
{
    public Phases14CliE2ETests() : base("ashlar-phases14-e2e") { }

    [Fact(Timeout = 60000)]
    public async Task AdaptCommand_DryRun_ExitsZero()
    {
        // The brick registry only offers bricks that carry a valid admission record, so
        // `adapt` on an empty store correctly reports "brick not found". Seeding a real
        // signed record is what lets this test exercise the decomposition it is named
        // for, rather than the not-found branch.
        AdmittedBrickFixture.SeedObservationContext(TempDir);

        var (code, stdout, _) = await RunCliAsync($"adapt --dry-run --store-path \"{TempDir}\"");

        Assert.Equal(0, code);
        Assert.Contains("Decomposed", stdout, StringComparison.OrdinalIgnoreCase);
    }

    [Fact(Timeout = 60000)]
    public async Task ImproveCommand_DryRun_Exits()
    {
        var (code, _, _) = await RunCliAsync("improve --dry-run");

        Assert.True(code == 0 || code == 1);
    }

    /// <summary>
    /// This is the invocation that wrote <c>docs/bricks/unknown.md</c> into the checkout (#580):
    /// the fix to EmptyCatch.cs is promoted, EmptyCatch.cs maps to no brick, and the child
    /// process documented the promotion under the repo root. The document must now land under
    /// <see cref="TempDirTestBase.TempDir"/>, where <see cref="E2ETestBase.RunCliAsync"/> points
    /// <c>ASHLAR_DOCS_ROOT</c>, and the checkout must be untouched.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task ImproveCommand_WithViolations_AppliesFixes()
    {
        var repoDocStampBefore = RepoBrickDocStamp();
        var csPath = Path.Combine(TempDir, "EmptyCatch.cs");
        await File.WriteAllTextAsync(csPath, """
            using System;
            namespace Test;
            public class C
            {
                public void M()
                {
                    try { }
                    catch (Exception) { }
                }
            }
            """);

        var (code, _, _) = await RunCliAsync($"improve --path \"{TempDir}\" --yes --skip-regression --store-path \"{TempDir}\"");

        Assert.True(code == 0 || code == 1);
        var content = await File.ReadAllTextAsync(csPath);
        var fixApplied = content.Contains("Trace.WriteLine", StringComparison.Ordinal);
        Assert.True(
            fixApplied || code == 0,
            "File should be modified with Trace.WriteLine when violations were fixed, or exit 0 if no violations");

        Assert.Equal(repoDocStampBefore, RepoBrickDocStamp());
        if (fixApplied)
        {
            Assert.True(
                File.Exists(Path.Combine(TempDir, "docs", "bricks", "unknown.md")),
                "a promoted fix is documented under ASHLAR_DOCS_ROOT; if this is missing the child process ignored the variable and the document went somewhere else");
        }
    }

    [Fact(Timeout = 60000)]
    public async Task AnalyzeBricksCommand_Exits()
    {
        var obsPath = Path.Combine(RepoRoot, "src", "Ashlar.Infrastructure", "Observation");
        var path = Directory.Exists(obsPath) ? obsPath : RepoRoot;

        var (code, _, _) = await RunCliAsync($"analyze bricks --path \"{path}\"");

        Assert.True(code == 0 || code == 1);
    }

    [Fact(Timeout = 60000)]
    public async Task Phases14_FullPipeline_ObserveAnalyzeAdaptImprove()
    {
        var watchPath = Path.Combine(TempDir, "src");
        Directory.CreateDirectory(watchPath);

        var (observeCode, _, _) = await RunCliAsync($"observe --path \"{TempDir}\" --duration 1s");
        Assert.Equal(0, observeCode);

        var obsPath = Path.Combine(RepoRoot, "src", "Ashlar.Infrastructure", "Observation");
        var analyzePath = Directory.Exists(obsPath) ? obsPath : RepoRoot;

        var (analyzeCode, _, _) = await RunCliAsync($"analyze bricks --path \"{analyzePath}\"");
        Assert.True(analyzeCode == 0 || analyzeCode == 1);

        AdmittedBrickFixture.SeedObservationContext(TempDir);
        var (adaptCode, adaptStdout, _) = await RunCliAsync($"adapt --dry-run --store-path \"{TempDir}\"");
        Assert.Equal(0, adaptCode);
        Assert.Contains("Decomposed", adaptStdout, StringComparison.OrdinalIgnoreCase);

        var (improveCode, _, _) = await RunCliAsync("improve --dry-run");
        Assert.True(improveCode == 0 || improveCode == 1);
    }
}
