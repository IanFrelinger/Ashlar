using System.Text.Json;
using Microsoft.Extensions.Logging;
using Ashlar.Core.Application.Adaptation;
using Ashlar.Core.Application.Adaptation.Models;
using Ashlar.Core.Application.Adaptation.Ports;
using Ashlar.Core.Application.Analysis.Ports;
using Ashlar.Core.Application.Observation.Models;
using Ashlar.Core.Application.Observation.Ports;
using Ashlar.Core.Application.Paths;
using Ashlar.Core.Application.SelfContext.Ports;
using Ashlar.Core.Application.SelfImprovement.Models;
using Ashlar.Core.Application.Rollback.Ports;
using Ashlar.Core.Application.SelfImprovement.Ports;
using Ashlar.Core.Application.Trust.Ports;
using Ashlar.Core.Application.Analysis.Models;
using Ashlar.Infrastructure.Adaptation;

namespace Ashlar.Infrastructure.SelfImprovement;

/// <summary>
/// Self-improvement loop: query test failures, run adaptation, validate, promote.
/// </summary>
public sealed class SelfImprovementLoop : ISelfImprovementLoop
{
    private readonly ITestFailureStore _testFailureStore;
    private readonly IBrickStaticAnalyzer _analyzer;
    private readonly IImmutableCoreRegistry _immutableCoreRegistry;
    private readonly IAccessBoundary _accessBoundary;
    private readonly IRegressionTestRunner _regressionRunner;
    private readonly IAdaptationPromoter _promoter;
    private readonly IAdaptationAuditLog _auditLog;
    private readonly IRollbackManager _rollbackManager;
    private readonly ISourceCodeFixer _sourceFixer;
    private readonly AdaptationRollbackHelper _rollbackHelper;
    private readonly ILogger<SelfImprovementLoop>? _logger;
    private readonly int _maxIterationsPerRun;
    private readonly Ashlar.Core.Application.SelfImprovement.Models.HoldoutTestOptions? _holdoutOptions;
    private readonly IPatternStore? _patternStore;
    private readonly IPatternProcessedStore? _patternProcessedStore;
    private readonly ISelfImprovementMetricsStore? _metricsStore;
    private SelfImprovementReport? _lastReport;

    /// <summary>Initializes a new self-improvement loop.</summary>
    public SelfImprovementLoop(
        ITestFailureStore testFailureStore,
        IBrickStaticAnalyzer analyzer,
        IImmutableCoreRegistry immutableCoreRegistry,
        IAccessBoundary accessBoundary,
        IRegressionTestRunner regressionRunner,
        IAdaptationPromoter promoter,
        IAdaptationAuditLog auditLog,
        IRollbackManager rollbackManager,
        ISourceCodeFixer sourceFixer,
        AdaptationRollbackHelper rollbackHelper,
        ILogger<SelfImprovementLoop>? logger = null,
        int maxIterationsPerRun = 5,
        Ashlar.Core.Application.SelfImprovement.Models.HoldoutTestOptions? holdoutOptions = null,
        IPatternStore? patternStore = null,
        IPatternProcessedStore? patternProcessedStore = null,
        ISelfImprovementMetricsStore? metricsStore = null)
    {
        _testFailureStore = testFailureStore ?? throw new ArgumentNullException(nameof(testFailureStore));
        _analyzer = analyzer ?? throw new ArgumentNullException(nameof(analyzer));
        _immutableCoreRegistry = immutableCoreRegistry ?? throw new ArgumentNullException(nameof(immutableCoreRegistry));
        _accessBoundary = accessBoundary ?? throw new ArgumentNullException(nameof(accessBoundary));
        _regressionRunner = regressionRunner ?? throw new ArgumentNullException(nameof(regressionRunner));
        _promoter = promoter ?? throw new ArgumentNullException(nameof(promoter));
        _auditLog = auditLog ?? throw new ArgumentNullException(nameof(auditLog));
        _rollbackManager = rollbackManager ?? throw new ArgumentNullException(nameof(rollbackManager));
        _sourceFixer = sourceFixer ?? throw new ArgumentNullException(nameof(sourceFixer));
        _rollbackHelper = rollbackHelper ?? throw new ArgumentNullException(nameof(rollbackHelper));
        _logger = logger;
        _maxIterationsPerRun = maxIterationsPerRun;
        _holdoutOptions = holdoutOptions;
        _patternStore = patternStore;
        _patternProcessedStore = patternProcessedStore;
        _metricsStore = metricsStore;
    }

    /// <summary>Run once asynchronously.</summary>
    public async Task RunOnceAsync(CancellationToken ct = default)
    {
        if (_accessBoundary.IsObservationPaused)
        {
            _logger?.LogInformation("Self-improvement loop skipped: kill switch engaged (trust pause)");
            return;
        }

        var since = DateTimeOffset.UtcNow.AddHours(-24);
        var failures = await _testFailureStore.QueryAsync(since: since, until: null, cancellationToken: ct).ConfigureAwait(false);

        var promoted = new List<string>();
        var rejected = new List<string>();
        var patternPromoted = new List<string>();
        var failuresProcessed = 0;
        var fixesGenerated = 0;
        var fixesValidated = 0;
        var fixesPromoted = 0;
        var fixesRejected = 0;
        var patternsProcessed = 0;
        var patternSourcedPromoted = 0;

        var block1Path = RepoPathResolver.FindBlock1ObservationPath();
        var solutionPath = Path.Combine(RepoPathResolver.FindRepoRoot(), "Ashlar.sln");

        foreach (var failure in failures.Take(_maxIterationsPerRun))
        {
            if (_accessBoundary.IsObservationPaused) break;
            ct.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(failure.FilePath))
                continue;

            if (_immutableCoreRegistry.IsInImmutableCore(failure.FilePath))
            {
                await _auditLog.LogAsync(new AdaptationAuditEntry
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Timestamp = DateTimeOffset.UtcNow,
                    AutonomyLevel = "self",
                    Outcome = "Rejected",
                    BrickId = ViolationToBrickMapper.GetBrickIdForFile(failure.FilePath),
                    FailureType = failure.ErrorMessage ?? "TestFailure",
                    FilePath = failure.FilePath,
                    RegressionPassed = false,
                    Promoted = false,
                    Message = $"Immutable core violation: cannot adapt {failure.FilePath}",
                }, ct).ConfigureAwait(false);
                throw new ImmutableCoreViolationException(failure.FilePath);
            }

            var brickId = ViolationToBrickMapper.GetBrickIdForFile(failure.FilePath);
            if (brickId == null)
                continue;

            failuresProcessed++;

            var failureDir = Path.GetDirectoryName(failure.FilePath) ?? block1Path;
            var analysisResult = await _analyzer.AnalyzeSourceAsync(failureDir, includeAnalyzers: false, ct).ConfigureAwait(false);
            if (analysisResult.Passed)
                continue;

            var violation = analysisResult.Violations.FirstOrDefault(v =>
                string.Equals(v.FilePath, failure.FilePath, StringComparison.OrdinalIgnoreCase));
            if (violation == null)
                continue;

            fixesGenerated++;

            _rollbackHelper.Snapshot(failure.FilePath);
            if (!await _sourceFixer.TryFixAsync(violation, ct).ConfigureAwait(false))
            {
                _rollbackHelper.Clear();
                rejected.Add($"Fix failed: {failure.TestName}");
                fixesRejected++;
                continue;
            }

            var regressionFilter = _holdoutOptions?.GetRegressionExclusionFilter();
            var regResult = await _regressionRunner.RunAsync(solutionPath, filter: regressionFilter, ct).ConfigureAwait(false);
            if (!regResult.AllPassed)
            {
                _rollbackHelper.Rollback(failure.FilePath);
                _rollbackHelper.Clear();
                rejected.Add($"Regression failed: {failure.TestName}");
                fixesRejected++;
                continue;
            }

            fixesValidated++;
            _rollbackHelper.Clear();

            var record = new AdaptationRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                Timestamp = DateTimeOffset.UtcNow,
                BrickId = brickId,
                FailureType = failure.ErrorMessage ?? "TestFailure",
                FixApplied = AdaptationFixType.Source,
                FilePath = failure.FilePath,
                RegressionPassed = true,
                Promoted = true,
                Message = $"Self-improvement: fixed {failure.TestName}",
            };

            try
            {
                _rollbackManager.PrepareForInherit(record.Id, new[] { failure.FilePath });
                await _rollbackManager.BeforeInheritAsync(record.Id, ct).ConfigureAwait(false);
                await _promoter.PromoteAsync(record, ct).ConfigureAwait(false);
                promoted.Add(record.Id);
                fixesPromoted++;

                await _auditLog.LogAsync(new AdaptationAuditEntry
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Timestamp = record.Timestamp,
                    AutonomyLevel = "self",
                    Outcome = "Promoted",
                    BrickId = record.BrickId,
                    FailureType = record.FailureType,
                    FilePath = record.FilePath,
                    RegressionPassed = true,
                    Promoted = true,
                    Message = record.Message,
                }, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Promotion failed for {Id}, rolling back", record.Id);
                await _rollbackManager.RollbackAsync(record.Id, ct).ConfigureAwait(false);
                rejected.Add(ex.Message);
                fixesRejected++;
            }
        }

        if (_patternStore != null && _patternProcessedStore != null)
        {
            var patternTypes = new[] { "repeated-edits", "edit-then-build" };
            foreach (var eventType in patternTypes)
            {
                var patterns = await _patternStore.QueryAsync(
                    new PatternStoreQueryParams { Since = since, EventType = eventType, MaxCount = _maxIterationsPerRun },
                    ct).ConfigureAwait(false);

                foreach (var pattern in patterns)
                {
                    if (_accessBoundary.IsObservationPaused) break;
                    ct.ThrowIfCancellationRequested();

                    // Claim BEFORE the work, not after it. What follows is a source edit, a
                    // solution-wide regression run and a promotion; the old shape asked
                    // IsProcessedAsync here and marked at each of eight exits, so two overlapping
                    // cycles -- which BackgroundAgentRegistry.TrackedExecuteAgentAsync says in its
                    // own remarks the scheduler may start, and which `ashlar improve` starts in a
                    // whole second process -- both read false, both edited the same file, both ran
                    // the suite against a tree the other was editing, and both promoted. A false
                    // here means another cycle owns this pattern.
                    //
                    // The trade this makes: a pattern whose iteration throws part way through stays
                    // claimed and is not retried. That is deliberate. Releasing it would hand the
                    // same pattern straight back to a cycle that is already failing on it, and the
                    // rollback helper has already restored the file.
                    if (!await _patternProcessedStore.TryClaimAsync(pattern.PatternId, ct).ConfigureAwait(false))
                        continue;

                    patternsProcessed++;
                    var filePath = GetFilePathFromPattern(pattern);
                    if (string.IsNullOrWhiteSpace(filePath))
                    {
                        continue;
                    }

                    if (_immutableCoreRegistry.IsInImmutableCore(filePath))
                    {
                        await _auditLog.LogAsync(new AdaptationAuditEntry
                        {
                            Id = Guid.NewGuid().ToString("N"),
                            Timestamp = DateTimeOffset.UtcNow,
                            AutonomyLevel = "self",
                            Outcome = "Rejected",
                            BrickId = ViolationToBrickMapper.GetBrickIdForFile(filePath),
                            FailureType = pattern.EventType,
                            FilePath = filePath,
                            RegressionPassed = false,
                            Promoted = false,
                            Message = $"Immutable core violation: cannot adapt {filePath} (pattern {pattern.EventType})",
                        }, ct).ConfigureAwait(false);
                        continue;
                    }

                    var brickId = ViolationToBrickMapper.GetBrickIdForFile(filePath);
                    if (brickId == null)
                    {
                        continue;
                    }

                    var analysisDir = Path.GetDirectoryName(filePath) ?? block1Path;
                    var analysisResult = await _analyzer.AnalyzeSourceAsync(analysisDir, includeAnalyzers: false, ct).ConfigureAwait(false);
                    if (analysisResult.Passed)
                    {
                        continue;
                    }

                    var violation = analysisResult.Violations.FirstOrDefault(v =>
                        string.Equals(v.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
                    if (violation == null)
                    {
                        continue;
                    }

                    fixesGenerated++;
                    _rollbackHelper.Snapshot(filePath);
                    if (!await _sourceFixer.TryFixAsync(violation, ct).ConfigureAwait(false))
                    {
                        _rollbackHelper.Clear();
                        rejected.Add($"Fix failed: {pattern.EventType} {filePath}");
                        fixesRejected++;
                        continue;
                    }

                    var regressionFilter = _holdoutOptions?.GetRegressionExclusionFilter();
                    var regResult = await _regressionRunner.RunAsync(solutionPath, filter: regressionFilter, ct).ConfigureAwait(false);
                    if (!regResult.AllPassed)
                    {
                        _rollbackHelper.Rollback(filePath);
                        _rollbackHelper.Clear();
                        rejected.Add($"Regression failed: {pattern.EventType} {filePath}");
                        fixesRejected++;
                        continue;
                    }

                    fixesValidated++;
                    _rollbackHelper.Clear();

                    var record = new AdaptationRecord
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        Timestamp = DateTimeOffset.UtcNow,
                        BrickId = brickId,
                        FailureType = pattern.EventType,
                        FixApplied = AdaptationFixType.Source,
                        FilePath = filePath,
                        RegressionPassed = true,
                        Promoted = true,
                        Message = $"Self-improvement (pattern {pattern.EventType}): fixed {Path.GetFileName(filePath)}",
                    };

                    try
                    {
                        _rollbackManager.PrepareForInherit(record.Id, new[] { filePath });
                        await _rollbackManager.BeforeInheritAsync(record.Id, ct).ConfigureAwait(false);
                        await _promoter.PromoteAsync(record, ct).ConfigureAwait(false);
                        promoted.Add(record.Id);
                        patternPromoted.Add(record.Id);
                        fixesPromoted++;
                        patternSourcedPromoted++;

                        await _auditLog.LogAsync(new AdaptationAuditEntry
                        {
                            Id = Guid.NewGuid().ToString("N"),
                            Timestamp = record.Timestamp,
                            AutonomyLevel = "self",
                            Outcome = "Promoted",
                            BrickId = record.BrickId,
                            FailureType = record.FailureType,
                            FilePath = record.FilePath,
                            RegressionPassed = true,
                            Promoted = true,
                            Message = record.Message,
                        }, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, "Promotion failed for {Id}, rolling back", record.Id);
                        await _rollbackManager.RollbackAsync(record.Id, ct).ConfigureAwait(false);
                        rejected.Add(ex.Message);
                        fixesRejected++;
                    }

                }
            }
        }

        int? holdoutPassed = null;
        int? holdoutTotal = null;
        double? holdoutPassRate = null;

        if (failures.Count == 0 && patternsProcessed == 0)
        {
            _lastReport = new SelfImprovementReport(DateTimeOffset.UtcNow, 0, 0, 0, 0, 0, Array.Empty<string>(), Array.Empty<string>(), 0, 0, Array.Empty<string>(), holdoutPassed, holdoutTotal, holdoutPassRate);
            await SaveMetricsAsync(_lastReport, ct).ConfigureAwait(false);
            return;
        }

        if (_holdoutOptions is { ValidateHoldoutAtEnd: true, HoldoutFilter: { Length: > 0 } holdoutFilter } && fixesPromoted > 0)
        {
            var holdoutResult = await _regressionRunner.RunAsync(solutionPath, filter: holdoutFilter, ct).ConfigureAwait(false);
            holdoutPassed = holdoutResult.PassedCount;
            holdoutTotal = holdoutResult.PassedCount + holdoutResult.FailedCount;
            holdoutPassRate = holdoutTotal > 0 ? (double)holdoutPassed.Value / holdoutTotal.Value : null;

            if (!holdoutResult.AllPassed)
            {
                _logger?.LogWarning("Holdout tests failed after self-improvement: {Failed} failed", holdoutResult.FailedCount);
                _lastReport = new SelfImprovementReport(
                    DateTimeOffset.UtcNow,
                    failuresProcessed,
                    fixesGenerated,
                    fixesValidated,
                    fixesPromoted,
                    fixesRejected,
                    promoted,
                    rejected.Concat(new[] { "Holdout validation failed" }).ToArray(),
                    patternsProcessed,
                    patternSourcedPromoted,
                    patternPromoted,
                    holdoutPassed,
                    holdoutTotal,
                    holdoutPassRate);
                await SaveMetricsAsync(_lastReport, ct).ConfigureAwait(false);
                return;
            }
        }

        _lastReport = new SelfImprovementReport(
            DateTimeOffset.UtcNow,
            failuresProcessed,
            fixesGenerated,
            fixesValidated,
            fixesPromoted,
            fixesRejected,
            promoted,
            rejected,
            patternsProcessed,
            patternSourcedPromoted,
            patternPromoted,
            holdoutPassed,
            holdoutTotal,
            holdoutPassRate);
        await SaveMetricsAsync(_lastReport, ct).ConfigureAwait(false);
    }

    private async Task SaveMetricsAsync(SelfImprovementReport report, CancellationToken ct)
    {
        if (_metricsStore != null)
        {
            try { await _metricsStore.SaveAsync(report, ct).ConfigureAwait(false); }
            catch (Exception ex) { _logger?.LogWarning(ex, "Failed to save self-improvement metrics"); }
        }
    }

    private static string? GetFilePathFromPattern(ObservedPattern pattern)
    {
        if (pattern.Metadata.HasValue && pattern.Metadata.Value.TryGetProperty("path", out var pathProp))
        {
            var path = pathProp.GetString();
            if (!string.IsNullOrWhiteSpace(path))
                return path;
        }
        return pattern.ProjectPath;
    }

    /// <summary>Start continuous asynchronously.</summary>
    public Task StartContinuousAsync(CancellationToken ct = default)
    {
        return Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested && !_accessBoundary.IsObservationPaused)
            {
                await RunOnceAsync(ct).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromMinutes(5), ct).ConfigureAwait(false);
            }
        }, ct);
    }

    /// <summary>Get last run report asynchronously.</summary>
    public Task<SelfImprovementReport?> GetLastRunReportAsync(CancellationToken ct = default) =>
        Task.FromResult(_lastReport);
}
