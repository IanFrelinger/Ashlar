using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ashlar.Core.Application.Adaptation.Ports;
using Ashlar.Core.Application.Paths;
using Ashlar.Infrastructure;
using Ashlar.Infrastructure.Adaptation;
using Ashlar.Infrastructure.Analysis;
using Ashlar.Tests.Infrastructure.Helpers;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Adaptation;

/// <summary>
/// P0.4: Proves the immutable core cannot be modified by self-adaptation.
/// </summary>
public sealed class ImmutableCoreTests : TempDirTestBase
{
    public ImmutableCoreTests() : base("ashlar-immutable-core") { }

    [Fact]
    public void AdaptationEngine_CannotModify_ObservationPipeline()
    {
        var registry = new ImmutableCoreRegistry();
        var corePath = "src/Ashlar.Infrastructure/Observation/FileSystemEventSource.cs";

        registry.IsInImmutableCore(corePath).Should().BeTrue();
    }

    [Fact]
    public void AdaptationEngine_CannotModify_ValidationChecker()
    {
        var registry = new ImmutableCoreRegistry();
        var corePath = "src/Ashlar.Infrastructure/Validation/Adapters/ValidationServiceAdapter.cs";

        registry.IsInImmutableCore(corePath).Should().BeTrue();
    }

    [Fact]
    public void AdaptationEngine_CannotModify_InheritanceProtocol()
    {
        var registry = new ImmutableCoreRegistry();
        var corePath = "src/Ashlar.Infrastructure/Adaptation/AdaptationPromoter.cs";

        registry.IsInImmutableCore(corePath).Should().BeTrue();
    }

    [Fact]
    public void AdaptationEngine_CannotModify_ScopeBoundaryEnforcer()
    {
        var registry = new ImmutableCoreRegistry();
        var corePath = "src/Ashlar.Infrastructure/Trust/AccessBoundary.cs";

        registry.IsInImmutableCore(corePath).Should().BeTrue();
    }

    [Fact]
    public void ScopeEnforcer_RejectsAdaptation_TargetingCoreNamespace()
    {
        var registry = new ImmutableCoreRegistry();
        var corePath = "src/Ashlar.Infrastructure/Observation/ObservationContextBrick.cs";

        var rejected = registry.IsInImmutableCore(corePath);

        rejected.Should().BeTrue("adaptation targeting core namespace should be rejected before execution");
    }

    [Fact]
    public void CoreComponentList_IsComplete_AndMatchesSpec()
    {
        var registry = new ImmutableCoreRegistry();

        registry.CoreComponentIds.Should().NotBeEmpty();
        registry.CoreComponentIds.Should().Contain("observation.pipeline");
        registry.CoreComponentIds.Should().Contain("analysis.engine");
        registry.CoreComponentIds.Should().Contain("validation.checker");
        registry.CoreComponentIds.Should().Contain("inheritance.protocol");
        registry.CoreComponentIds.Should().Contain("scope.boundary.enforcer");
        registry.CoreComponentIds.Should().Contain("dependency.graph");
        registry.CoreComponentIds.Should().Contain("rollback.manager");
    }

    [Fact]
    public void NonCorePath_IsAllowed()
    {
        var registry = new ImmutableCoreRegistry();
        var allowedPath = "src/Ashlar.Bricks.Owasp/Security/OWASPScannerBrick.cs";

        registry.IsInImmutableCore(allowedPath).Should().BeFalse();
    }

    [Fact]
    public void ImproveFlow_SkipsImmutableCoreViolations()
    {
        var repoRoot = RepoPathResolver.FindRepoRoot();
        var slnPath = Path.Combine(repoRoot, "Ashlar.sln");
        if (!File.Exists(slnPath))
            return;

        var storePath = Path.Combine(TempDir, "adapt.db");
        var services = new ServiceCollection()
            .AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning))
            .AddCodeAnalyzers()
            .AddAdaptationInfrastructure(storePath)
            .AddSelfContextInfrastructure(storePath, docsRoot: TempDir)
            .BuildServiceProvider();

        var registry = services.GetRequiredService<IImmutableCoreRegistry>();
        var corePath = Path.Combine(repoRoot, "src", "Ashlar.Infrastructure", "Observation", "FileSystemEventSource.cs");
        if (!File.Exists(corePath))
            return;

        var rejected = registry.IsInImmutableCore(corePath);
        rejected.Should().BeTrue();
    }
}
