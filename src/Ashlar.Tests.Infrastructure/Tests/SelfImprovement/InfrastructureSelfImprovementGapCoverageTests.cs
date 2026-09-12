using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Ashlar.Core.Application.Adaptation.Models;
using Ashlar.Core.Application.Adaptation.Ports;
using Ashlar.Core.Domain.Values;
using Ashlar.Core.Application.Analysis.Models;
using Ashlar.Core.Application.Analysis.Ports;
using Ashlar.Core.Application.Observation.Models;
using Ashlar.Core.Application.Observation.Ports;
using Ashlar.Core.Application.Rollback.Ports;
using Ashlar.Core.Application.SelfContext.Models;
using Ashlar.Core.Application.SelfContext.Ports;
using Ashlar.Core.Application.SelfImprovement.Models;
using Ashlar.Core.Application.SelfImprovement.Ports;
using Ashlar.Core.Application.Trust.Ports;
using Ashlar.Infrastructure.Adaptation;
using Ashlar.Infrastructure.SelfImprovement;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.SelfImprovement;

/// <summary>Tests for infrastructure self improvement gap coverage.</summary>
public class InfrastructureSelfImprovementGapCoverageTests
{
    private const string AdaptableFile = "src/Ashlar.Bricks/ObservationContextBrick.cs";

    [Fact]
    public async Task RunOnceAsync_with_no_failures_writes_empty_report()
    {
        var metrics = new Mock<ISelfImprovementMetricsStore>();
        metrics.Setup(m => m.SaveAsync(It.IsAny<SelfImprovementReport>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var loop = BuildLoop(
            failures: [],
            metrics: metrics.Object);

        await loop.RunOnceAsync(CancellationToken.None);

        var report = await loop.GetLastRunReportAsync();
        report.Should().NotBeNull();
        report!.FailuresProcessed.Should().Be(0);
        report.FixesPromoted.Should().Be(0);
        metrics.Verify(m => m.SaveAsync(It.IsAny<SelfImprovementReport>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunOnceAsync_promotes_fix_when_regression_passes()
    {
        var promoter = new Mock<IAdaptationPromoter>();
        promoter.Setup(p => p.PromoteAsync(It.IsAny<AdaptationRecord>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var loop = BuildLoop(
            failures: [SampleFailure()],
            analyzer: FailedAnalysis(),
            fixer: SuccessfulFixer(),
            regression: PassedRegression(),
            promoter: promoter.Object);

        await loop.RunOnceAsync(CancellationToken.None);

        var report = await loop.GetLastRunReportAsync();
        report!.FixesPromoted.Should().Be(1);
        report.PromotedAdaptationIds.Should().ContainSingle();
        promoter.Verify(p => p.PromoteAsync(It.IsAny<AdaptationRecord>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunOnceAsync_rejects_when_source_fix_fails()
    {
        var fixer = new Mock<ISourceCodeFixer>();
        fixer.Setup(f => f.TryFixAsync(It.IsAny<Violation>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var loop = BuildLoop(
            failures: [SampleFailure()],
            analyzer: FailedAnalysis(),
            fixer: fixer.Object,
            regression: PassedRegression());

        await loop.RunOnceAsync(CancellationToken.None);

        var report = await loop.GetLastRunReportAsync();
        report!.FixesRejected.Should().BeGreaterThan(0);
        report.RejectedReasons.Should().Contain(r => r.Contains("Fix failed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RunOnceAsync_rolls_back_when_regression_fails()
    {
        var loop = BuildLoop(
            failures: [SampleFailure()],
            analyzer: FailedAnalysis(),
            fixer: SuccessfulFixer(),
            regression: FailedRegression());

        await loop.RunOnceAsync(CancellationToken.None);

        var report = await loop.GetLastRunReportAsync();
        report!.FixesRejected.Should().BeGreaterThan(0);
        report.RejectedReasons.Should().Contain(r => r.Contains("Regression failed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RunOnceAsync_rolls_back_when_promotion_fails()
    {
        var promoter = new Mock<IAdaptationPromoter>();
        promoter.Setup(p => p.PromoteAsync(It.IsAny<AdaptationRecord>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("promote failed"));

        var loop = BuildLoop(
            failures: [SampleFailure()],
            analyzer: FailedAnalysis(),
            fixer: SuccessfulFixer(),
            regression: PassedRegression(),
            promoter: promoter.Object);

        await loop.RunOnceAsync(CancellationToken.None);

        var report = await loop.GetLastRunReportAsync();
        report!.FixesRejected.Should().BeGreaterThan(0);
        report.RejectedReasons.Should().Contain("promote failed");
    }

    [Fact]
    public async Task RunOnceAsync_skips_failures_without_brick_mapping()
    {
        var loop = BuildLoop(
            failures:
            [
                new TestFailureRecord
                {
                    Id = "f1",
                    Timestamp = DateTimeOffset.UtcNow,
                    TestName = "UnknownTest",
                    FilePath = "src/Unknown/UnmappedFile.cs",
                    ErrorMessage = "fail",
                },
            ]);

        await loop.RunOnceAsync(CancellationToken.None);

        var report = await loop.GetLastRunReportAsync();
        report!.FailuresProcessed.Should().Be(0);
    }

    [Fact]
    public async Task RunOnceAsync_skips_failures_with_empty_file_path()
    {
        var loop = BuildLoop(
            failures:
            [
                new TestFailureRecord
                {
                    Id = "empty-path",
                    Timestamp = DateTimeOffset.UtcNow,
                    TestName = "EmptyPathTest",
                    FilePath = "",
                    ErrorMessage = "fail",
                },
            ]);

        await loop.RunOnceAsync(CancellationToken.None);

        var report = await loop.GetLastRunReportAsync();
        report!.FailuresProcessed.Should().Be(0);
    }

    [Fact]
    public async Task RunOnceAsync_records_holdout_failure_when_validation_fails()
    {
        var promoter = new Mock<IAdaptationPromoter>();
        promoter.Setup(p => p.PromoteAsync(It.IsAny<AdaptationRecord>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var regression = new Mock<IRegressionTestRunner>();
        regression.SetupSequence(r => r.RunAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RegressionTestResult { AllPassed = true, PassedCount = 1, FailedCount = 0 })
            .ReturnsAsync(new RegressionTestResult { AllPassed = false, PassedCount = 0, FailedCount = 2 });

        var loop = new SelfImprovementLoop(
            Mock.Of<ITestFailureStore>(s => s.QueryAsync(It.IsAny<DateTimeOffset?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<CancellationToken>()) ==
                Task.FromResult<IReadOnlyList<TestFailureRecord>>(new[] { SampleFailure() })),
            FailedAnalysis(),
            Mock.Of<IImmutableCoreRegistry>(r => r.IsInImmutableCore(It.IsAny<string>()) == false),
            Mock.Of<IAccessBoundary>(b => b.IsObservationPaused == false),
            regression.Object,
            promoter.Object,
            Mock.Of<IAdaptationAuditLog>(a => a.LogAsync(It.IsAny<AdaptationAuditEntry>(), It.IsAny<CancellationToken>()) == Task.CompletedTask),
            Mock.Of<IRollbackManager>(r =>
                r.BeforeInheritAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()) == Task.FromResult("snap") &&
                r.RollbackAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()) == Task.CompletedTask),
            SuccessfulFixer(),
            new AdaptationRollbackHelper(),
            NullLogger<SelfImprovementLoop>.Instance,
            holdoutOptions: new HoldoutTestOptions { ValidateHoldoutAtEnd = true, HoldoutFilter = "Category=Holdout" });

        await loop.RunOnceAsync(CancellationToken.None);

        var report = await loop.GetLastRunReportAsync();
        report!.FixesPromoted.Should().Be(1);
        report.RejectedReasons.Should().Contain("Holdout validation failed");
        report.HoldoutTotal.Should().Be(2);
    }

    [Fact]
    public async Task RunOnceAsync_processes_pattern_and_marks_processed()
    {
        var pattern = new ObservedPattern
        {
            PatternId = "pat-1",
            EventType = "repeated-edits",
            Frequency = 2,
            FirstSeen = DateTimeOffset.UtcNow.AddMinutes(-5),
            LastSeen = DateTimeOffset.UtcNow,
            ProjectPath = "src",
            Metadata = System.Text.Json.JsonSerializer.SerializeToElement(new { path = AdaptableFile }),
        };

        var patternStore = new Mock<IPatternStore>();
        patternStore.Setup(s => s.QueryAsync(
                It.Is<PatternStoreQueryParams>(q => q.EventType == "repeated-edits"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { pattern });
        patternStore.Setup(s => s.QueryAsync(
                It.Is<PatternStoreQueryParams>(q => q.EventType == "edit-then-build"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ObservedPattern>());

        var processed = new Mock<IPatternProcessedStore>();
        processed.Setup(s => s.TryClaimAsync("pat-1", It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var loop = BuildLoop(
            failures: [],
            analyzer: FailedAnalysis(),
            fixer: SuccessfulFixer(),
            regression: PassedRegression(),
            promoter: Mock.Of<IAdaptationPromoter>(p => p.PromoteAsync(It.IsAny<AdaptationRecord>(), It.IsAny<CancellationToken>()) == Task.CompletedTask),
            patternStore: patternStore.Object,
            patternProcessedStore: processed.Object);

        await loop.RunOnceAsync(CancellationToken.None);

        var report = await loop.GetLastRunReportAsync();
        report!.PatternsProcessed.Should().Be(1);
        // The claim is taken once, up front, instead of marked at whichever of eight exits the
        // iteration happened to take.
        processed.Verify(s => s.TryClaimAsync("pat-1", It.IsAny<CancellationToken>()), Times.Once);
        processed.Verify(s => s.IsProcessedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>Sample failure.</summary>
    private static TestFailureRecord SampleFailure() => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Timestamp = DateTimeOffset.UtcNow,
        TestName = "ObservationContextBrickTests",
        FilePath = AdaptableFile,
        ErrorMessage = "assert failed",
    };

    private static IBrickStaticAnalyzer FailedAnalysis()
    {
        var analyzer = new Mock<IBrickStaticAnalyzer>();
        analyzer.Setup(a => a.AnalyzeSourceAsync(It.IsAny<string>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BrickStaticAnalysisResult
            {
                Passed = false,
                Violations = new[]
                {
                    new Violation
                    {
                        Rule = "quality",
                        Message = "issue",
                        FilePath = AdaptableFile,
                        Severity = RiskLevel.Medium,
                    },
                },
            });
        return analyzer.Object;
    }

    private static ISourceCodeFixer SuccessfulFixer()
    {
        var fixer = new Mock<ISourceCodeFixer>();
        fixer.Setup(f => f.TryFixAsync(It.IsAny<Violation>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        return fixer.Object;
    }

    private static IRegressionTestRunner PassedRegression()
    {
        var runner = new Mock<IRegressionTestRunner>();
        runner.Setup(r => r.RunAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RegressionTestResult { AllPassed = true, PassedCount = 1, FailedCount = 0 });
        return runner.Object;
    }

    private static IRegressionTestRunner FailedRegression()
    {
        var runner = new Mock<IRegressionTestRunner>();
        runner.Setup(r => r.RunAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RegressionTestResult { AllPassed = false, PassedCount = 0, FailedCount = 1 });
        return runner.Object;
    }

    private static SelfImprovementLoop BuildLoop(
        IReadOnlyList<TestFailureRecord> failures,
        IBrickStaticAnalyzer? analyzer = null,
        ISourceCodeFixer? fixer = null,
        IRegressionTestRunner? regression = null,
        IAdaptationPromoter? promoter = null,
        ISelfImprovementMetricsStore? metrics = null,
        IPatternStore? patternStore = null,
        IPatternProcessedStore? patternProcessedStore = null)
    {
        var failureStore = new Mock<ITestFailureStore>();
        failureStore.Setup(s => s.QueryAsync(It.IsAny<DateTimeOffset?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(failures);

        var immutable = new Mock<IImmutableCoreRegistry>();
        immutable.Setup(r => r.IsInImmutableCore(It.IsAny<string>())).Returns(false);

        var boundary = new Mock<IAccessBoundary>();
        boundary.Setup(b => b.IsObservationPaused).Returns(false);

        var audit = new Mock<IAdaptationAuditLog>();
        audit.Setup(a => a.LogAsync(It.IsAny<AdaptationAuditEntry>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var rollback = new Mock<IRollbackManager>();
        rollback.Setup(r => r.PrepareForInherit(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>()));
        rollback.Setup(r => r.BeforeInheritAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("snap-1");
        rollback.Setup(r => r.RollbackAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        promoter ??= Mock.Of<IAdaptationPromoter>(p =>
            p.PromoteAsync(It.IsAny<AdaptationRecord>(), It.IsAny<CancellationToken>()) == Task.CompletedTask);

        return new SelfImprovementLoop(
            failureStore.Object,
            analyzer ?? Mock.Of<IBrickStaticAnalyzer>(),
            immutable.Object,
            boundary.Object,
            regression ?? PassedRegression(),
            promoter,
            audit.Object,
            rollback.Object,
            fixer ?? SuccessfulFixer(),
            new AdaptationRollbackHelper(),
            NullLogger<SelfImprovementLoop>.Instance,
            maxIterationsPerRun: 5,
            holdoutOptions: null,
            patternStore: patternStore,
            patternProcessedStore: patternProcessedStore,
            metricsStore: metrics);
    }
}
