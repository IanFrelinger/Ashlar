using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ashlar.Core.Application.Adaptation.Models;
using Ashlar.Core.Application.Adaptation.Ports;
using Ashlar.Core.Application.SelfContext.Ports;
using Ashlar.Infrastructure.SelfContext;
using Ashlar.Tests.Infrastructure.Helpers;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.SelfContext;

/// <summary>
/// The container that ImmutableCoreTests, DogfoodBlock6Tests, DogfoodBlock10SharedAdaptationTests
/// and DogfoodClosedLoopTests build — <c>AddAdaptationInfrastructure(storePath)</c> +
/// <c>AddSelfContextInfrastructure(storePath, docsRoot)</c> — must document a promotion under the
/// root it was given and nowhere near the repository (#580).
///
/// <para>Those four classes never actually resolve <see cref="IDocumentationUpdater"/>, which is
/// why the leak they were blamed for could not be reproduced through them; the file came from the
/// CLI child process the E2E tests spawn. This test is what would have caught the registration
/// bug regardless of who called it: it drives the exact composition through a promotion and
/// looks at both roots. Only the repo root's state is compared before/after, because a developer's
/// tree may already hold residue from an older run and that must not fail this test.</para>
/// </summary>
public sealed class SelfContextDocsRootIsolationTests : TempDirTestBase
{
    public SelfContextDocsRootIsolationTests() : base("ashlar-docsroot-isolation") { }

    [Fact(Timeout = 30000)]
    public async Task Promotion_is_documented_under_the_injected_root_and_not_under_the_repo()
    {
        var storePath = Path.Combine(TempDir, "adapt.db");
        var services = new ServiceCollection()
            .AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning))
            .AddAdaptationInfrastructure(storePath)
            .AddSelfContextInfrastructure(storePath, docsRoot: TempDir)
            .BuildServiceProvider();

        var repoBricks = Path.Combine(TestPaths.FindRepoRoot(), "docs", "bricks");
        var repoBricksBefore = Snapshot(repoBricks);

        // BrickId null is the shape that leaked: a promoted fix to a file no brick claims documents
        // itself as "unknown", which is the file name that turned up in the checkout.
        var adaptationId = Guid.NewGuid().ToString("N");
        await services.GetRequiredService<IAdaptationLog>().LogAsync(new AdaptationRecord
        {
            Id = adaptationId,
            Timestamp = DateTimeOffset.UtcNow,
            BrickId = null,
            FailureType = "EmptyCatch",
            FixApplied = AdaptationFixType.Source,
            FilePath = Path.Combine(TempDir, "EmptyCatch.cs"),
            RegressionPassed = true,
            Promoted = true,
            Message = "docs-root isolation",
        });

        await services.GetRequiredService<IDocumentationUpdater>().UpdateForAdaptationAsync(adaptationId);

        File.Exists(Path.Combine(TempDir, "docs", "bricks", "unknown.md")).Should().BeTrue(
            "the root handed to AddSelfContextInfrastructure is where the promotion must be documented");
        Snapshot(repoBricks).Should().Equal(repoBricksBefore,
            "nothing a test container does may create or rewrite a file under the repository's docs/bricks/");
    }

    [Fact]
    public void Omitting_the_root_still_resolves_the_production_updater()
    {
        // The dogfooding default (null => ASHLAR_DOCS_ROOT, else repo root) is production
        // behaviour and must survive the factory registration; resolved, never invoked, because
        // invoking it here is exactly the leak.
        var storePath = Path.Combine(TempDir, "adapt.db");
        var services = new ServiceCollection()
            .AddLogging()
            .AddAdaptationInfrastructure(storePath)
            .AddSelfContextInfrastructure(storePath)
            .BuildServiceProvider();

        services.GetRequiredService<IDocumentationUpdater>().Should().BeOfType<DocumentationUpdater>();
    }

    /// <summary>Files under <paramref name="dir"/> with their last-write stamps; empty when it does not exist.</summary>
    private static IReadOnlyList<(string Path, DateTime Stamp)> Snapshot(string dir)
        => Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                .OrderBy(p => p, StringComparer.Ordinal)
                .Select(p => (p, File.GetLastWriteTimeUtc(p)))
                .ToList()
            : Array.Empty<(string, DateTime)>();
}
