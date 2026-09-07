using System.CommandLine;
using System.CommandLine.Invocation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ashlar.Core.Application.Adaptation.Models;
using Ashlar.Core.Application.Adaptation.Ports;
using Ashlar.Core.Application.Analysis.Models;
using Ashlar.Core.Application.Analysis.Ports;
using Ashlar.Core.Application.Observation.Ports;
using Ashlar.Core.Application.Paths;
using Ashlar.Core.Domain.Bricks;
using Ashlar.Core.Domain.Execution;
using Ashlar.Bricks.Owasp.Security;
using Ashlar.Core.Application.Rollback.Ports;
using Ashlar.Infrastructure;
using Ashlar.Core.Application.SelfContext.Ports;
using Ashlar.Core.Application.Adaptation;
using Ashlar.BackgroundAgents.Observation;
using Ashlar.Infrastructure.Adaptation;
using Ashlar.Infrastructure.Analysis;
using Ashlar.Infrastructure.Observation;
using Ashlar.Infrastructure.SelfContext;

namespace Ashlar.CLI.Commands;

/// <summary>
/// Block 4: Closed-loop improve. Runs analyze bricks → adapt from violations.
/// Dogfood flow is sequential: run observe separately, then this command for analyze → adapt.
/// </summary>
public sealed class ImproveCommand : Command
{
    /// <summary>Creates a new ImproveCommand instance.</summary>
    public ImproveCommand() : base("improve", "Analyze brick code, then run adaptation for each violation (Block 4 closed-loop).")
    {
        var pathOpt = new Option<string?>("--path", "Path to analyze. Default: Block 1 Observation folders.");
        var dryRunOpt = new Option<bool>("--dry-run", () => false, "Only report; do not run adaptation");
        var autonomyOpt = new Option<string>("--autonomy", () => "supervised", "Autonomy level: supervised | semi | full");
        var yesOpt = new Option<bool>("--yes", () => false, "Auto-approve all prompts (non-interactive, for CI/tests)");
        var skipRegressionOpt = new Option<bool>("--skip-regression", () => false, "Skip regression test after source fix (for CI/tests when fix is outside solution)");
        var storePathOpt = new Option<string?>("--store-path", "Directory for ashlar dbs (default: ASHLAR_STATE_DIR, else <repo root>/.ashlar/state)");
        var selfOpt = new Option<bool>("--self", () => false, "Run one cycle of the self-improvement loop (test failures → fix → validate → promote)");
        var holdoutFilterOpt = new Option<string?>("--holdout-filter", "xUnit filter for holdout tests (e.g. Category=Holdout). Excluded from per-fix regression; run at end (P3.4)");
        var fromObservationOpt = new Option<bool>("--from-observation", () => false, "Query recent observation patterns and prioritize analysis on affected file paths");
        var observationDaysOpt = new Option<int>("--observation-days", () => 7, "Number of days of observation history to consider (default: 7)");
        var continuousOpt = new Option<bool>("--continuous", () => false, "Run observe → improve loop continuously until Ctrl+C");
        var intervalMinutesOpt = new Option<int>("--interval-minutes", () => 5, "Minutes between continuous loop iterations (default: 5)");
        var observeMinutesOpt = new Option<int>("--observe-minutes", () => 5, "Minutes to observe per continuous iteration (default: 5)");

        AddOption(pathOpt);
        AddOption(dryRunOpt);
        AddOption(autonomyOpt);
        AddOption(yesOpt);
        AddOption(skipRegressionOpt);
        AddOption(storePathOpt);
        AddOption(selfOpt);
        AddOption(holdoutFilterOpt);
        AddOption(fromObservationOpt);
        AddOption(observationDaysOpt);
        AddOption(continuousOpt);
        AddOption(intervalMinutesOpt);
        AddOption(observeMinutesOpt);

        this.SetHandler(async (InvocationContext ctx) =>
        {
            var path = ctx.ParseResult.GetValueForOption(pathOpt);
            var dryRun = ctx.ParseResult.GetValueForOption(dryRunOpt);
            var autonomy = ctx.ParseResult.GetValueForOption(autonomyOpt) ?? "supervised";
            var yes = ctx.ParseResult.GetValueForOption(yesOpt);
            var skipRegression = ctx.ParseResult.GetValueForOption(skipRegressionOpt);
            var storePathOverride = ctx.ParseResult.GetValueForOption(storePathOpt);
            var self = ctx.ParseResult.GetValueForOption(selfOpt);
            var holdoutFilter = ctx.ParseResult.GetValueForOption(holdoutFilterOpt);
            var fromObservation = ctx.ParseResult.GetValueForOption(fromObservationOpt);
            var observationDays = ctx.ParseResult.GetValueForOption(observationDaysOpt);
            var continuous = ctx.ParseResult.GetValueForOption(continuousOpt);
            var intervalMinutes = ctx.ParseResult.GetValueForOption(intervalMinutesOpt);
            var observeMinutes = ctx.ParseResult.GetValueForOption(observeMinutesOpt);

            if (continuous)
            {
                await ExecuteContinuousAsync(path, dryRun, autonomy, yes, skipRegression, storePathOverride, self, holdoutFilter, observationDays, intervalMinutes, observeMinutes);
            }
            else
            {
                await ExecuteAsync(path, dryRun, autonomy, yes, skipRegression, storePathOverride, self, holdoutFilter, fromObservation, observationDays);
            }
        });
    }

    private static async Task ExecuteAsync(string? path, bool dryRun, string autonomy = "supervised", bool yes = false, bool skipRegression = false, string? storePathOverride = null, bool self = false, string? holdoutFilter = null, bool fromObservation = false, int observationDays = 7)
    {
        var repoRoot = RepoPathResolver.FindRepoRoot();
        var storePath = !string.IsNullOrWhiteSpace(storePathOverride)
            ? Path.Combine(Path.GetFullPath(storePathOverride), "ashlar-patterns.db")
            : Path.Combine(RepoPathResolver.ResolveStateDirectory(repoRoot), "ashlar-patterns.db");
        var targetPath = path ?? RepoPathResolver.FindBlock1ObservationPath(repoRoot);

        var trustEnabled = string.Equals(
            Environment.GetEnvironmentVariable("ASHLAR_TRUST_ENABLED"), "1",
            StringComparison.OrdinalIgnoreCase);

        var serviceCollection = new ServiceCollection()
            .AddLogging(b => b.AddConsole())
            .AddCodeAnalyzers()
            .AddAdaptationInfrastructure(storePath)
            .AddAdaptationBricks(typeof(OWASPScannerBrick))
            .AddSelfContextInfrastructure(storePath)
            .AddSharedAdaptationCache();

        serviceCollection.AddSingleton<Ashlar.Infrastructure.Execution.ProviderFactory>();
        if (trustEnabled)
        {
            serviceCollection.AddSingleton<Ashlar.BackgroundAgents.Trust.ICloudSanitizationProxy,
                Ashlar.BackgroundAgents.Trust.CloudSanitizationProxy>();
            serviceCollection.AddSingleton<Ashlar.Infrastructure.Execution.IProviderFactory>(sp =>
                new Ashlar.BackgroundAgents.Trust.SanitizingProviderFactory(
                    sp.GetRequiredService<Ashlar.Infrastructure.Execution.ProviderFactory>(),
                    sp.GetRequiredService<Ashlar.BackgroundAgents.Trust.ICloudSanitizationProxy>(),
                    sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Ashlar.BackgroundAgents.Trust.SanitizingProviderFactory>>()));
        }
        else
        {
            serviceCollection.AddSingleton<Ashlar.Infrastructure.Execution.IProviderFactory>(
                sp => sp.GetRequiredService<Ashlar.Infrastructure.Execution.ProviderFactory>());
        }

        if (self)
        {
            var holdoutOptions = !string.IsNullOrWhiteSpace(holdoutFilter)
                ? new Ashlar.Core.Application.SelfImprovement.Models.HoldoutTestOptions { HoldoutFilter = holdoutFilter }
                : null;
            serviceCollection.AddSelfImprovementLoop(5, holdoutOptions);
        }

        if (yes)
            serviceCollection.AddSingleton<IUserFeedbackCapture>(new AutoApproveUserFeedbackCapture());

        if (fromObservation)
        {
            serviceCollection.AddSingleton<Ashlar.Core.Application.Observation.Ports.IPatternStore>(
                _ => new Ashlar.Infrastructure.Observation.LiteDbPatternStore(storePath));
        }

        var services = serviceCollection.BuildServiceProvider();

        if (fromObservation && string.IsNullOrWhiteSpace(path))
        {
            var patternStore = services.GetService<Ashlar.Core.Application.Observation.Ports.IPatternStore>();
            if (patternStore != null)
            {
                var since = DateTimeOffset.UtcNow.AddDays(-observationDays);
                var patternTypes = new[] { "repeated-edits", "edit-then-build" };
                var affectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var eventType in patternTypes)
                {
                    var patterns = await patternStore.QueryAsync(
                        new Ashlar.Core.Application.Observation.Models.PatternStoreQueryParams
                        {
                            Since = since,
                            EventType = eventType,
                            MaxCount = 50
                        }).ConfigureAwait(false);

                    foreach (var pattern in patterns)
                    {
                        if (!string.IsNullOrWhiteSpace(pattern.ProjectPath))
                            affectedPaths.Add(pattern.ProjectPath);
                        if (pattern.Metadata?.ValueKind == System.Text.Json.JsonValueKind.Object &&
                            pattern.Metadata.Value.TryGetProperty("path", out var pathProp) &&
                            pathProp.ValueKind == System.Text.Json.JsonValueKind.String)
                        {
                            var p = pathProp.GetString();
                            if (!string.IsNullOrWhiteSpace(p))
                                affectedPaths.Add(p);
                        }
                    }
                }

                if (affectedPaths.Count > 0)
                {
                    Console.WriteLine($"Observation-driven improve: found {affectedPaths.Count} affected path(s) from last {observationDays} day(s):");
                    foreach (var ap in affectedPaths.OrderBy(x => x))
                        Console.WriteLine($"  - {ap}");
                    Console.WriteLine();
                    targetPath = affectedPaths.First();
                }
                else
                {
                    Console.WriteLine($"No observation patterns found in last {observationDays} day(s). Falling back to default path.");
                }
            }
        }

        if (self)
        {
            var loop = services.GetRequiredService<Ashlar.Core.Application.SelfImprovement.Ports.ISelfImprovementLoop>();
            await loop.RunOnceAsync().ConfigureAwait(false);
            var report = await loop.GetLastRunReportAsync().ConfigureAwait(false);
            if (report != null)
            {
                Console.WriteLine($"Self-improvement: {report.FailuresProcessed} processed, {report.FixesPromoted} promoted, {report.FixesRejected} rejected");
            }
            Environment.ExitCode = 0;
            return;
        }

        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger<ImproveCommand>();
        var analyzer = services.GetRequiredService<IBrickStaticAnalyzer>();
        var decomposer = services.GetRequiredService<IBrickDecomposer>();
        var fixGenerator = services.GetRequiredService<IFixGenerator>();
        var recompiler = services.GetRequiredService<IBrickRecompiler>();
        var sourceFixer = services.GetRequiredService<ISourceCodeFixer>();
        var regressionRunner = services.GetRequiredService<IRegressionTestRunner>();
        var adaptationLog = services.GetRequiredService<IAdaptationLog>();
        var adaptationPromoter = services.GetRequiredService<IAdaptationPromoter>();
        var rollbackHelper = services.GetRequiredService<AdaptationRollbackHelper>();
        var rollbackManager = services.GetRequiredService<IRollbackManager>();
        var immutableCoreRegistry = services.GetRequiredService<IImmutableCoreRegistry>();
        var documentationUpdater = services.GetService<Ashlar.Core.Application.SelfContext.Ports.IDocumentationUpdater>();
        var sharedBroadcaster = services.GetService<ISharedAdaptationBroadcaster>();
        var peerIdProvider = services.GetService<IPeerIdProvider>();
        var userFeedback = services.GetRequiredService<IUserFeedbackCapture>();
        var executionTracer = services.GetRequiredService<IExecutionTracer>();
        var auditLog = services.GetRequiredService<IAdaptationAuditLog>();
        var envelope = new PermissionEnvelope(autonomy);

        Console.WriteLine("Block 4: Closed-loop improve");
        Console.WriteLine($"  Path: {targetPath}");
        Console.WriteLine($"  Mode: {(dryRun ? "dry-run (report only)" : "analyze + adapt")}");
        Console.WriteLine($"  Autonomy: {autonomy}");
        Console.WriteLine();

        await executionTracer.TraceAsync("improve.start", new Dictionary<string, object> { ["path"] = targetPath, ["dryRun"] = dryRun }, path: targetPath).ConfigureAwait(false);

        var analysisResult = await analyzer.AnalyzeSourceAsync(targetPath).ConfigureAwait(false);

        if (analysisResult.Passed)
        {
            Console.WriteLine("No violations found.");
            Environment.ExitCode = 0;
            return;
        }

        var violationsByBrick = new Dictionary<string, List<(string Rule, string FilePath, int? Line)>>(StringComparer.OrdinalIgnoreCase);

        foreach (var v in analysisResult.Violations)
        {
            var brickId = ViolationToBrickMapper.GetBrickIdForFile(v.FilePath);
            if (brickId == null) continue;

            if (!violationsByBrick.TryGetValue(brickId, out var list))
            {
                list = new List<(string, string, int?)>();
                violationsByBrick[brickId] = list;
            }
            list.Add((v.Rule, v.FilePath, v.LineNumber));
        }

        Console.WriteLine($"Found {analysisResult.TotalViolations} violation(s). {violationsByBrick.Sum(x => x.Value.Count)} in known bricks.");
        foreach (var kv in violationsByBrick)
        {
            Console.WriteLine($"  {kv.Key}: {kv.Value.Count} violation(s)");
            foreach (var (rule, filePath, line) in kv.Value.DistinctBy(x => (x.Rule, x.FilePath, x.Line)))
                Console.WriteLine($"    [{rule}] {Path.GetFileName(filePath)}{(line.HasValue ? $":{line}" : "")}");
        }
        Console.WriteLine();

        // Source-level fixes (e.g. EmptyCatch) with validate-before-promote
        int sourceFixes = 0;
        if (!dryRun)
        {
            var solutionPath = Path.Combine(repoRoot, "Ashlar.sln");
            foreach (var v in analysisResult.Violations)
            {
                if (immutableCoreRegistry.IsInImmutableCore(v.FilePath))
                {
                    logger.LogWarning("Rejecting adaptation of immutable core: {FilePath}", v.FilePath);
                    await auditLog.LogAsync(new AdaptationAuditEntry
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        Timestamp = DateTimeOffset.UtcNow,
                        AutonomyLevel = autonomy,
                        Outcome = "Rejected",
                        BrickId = ViolationToBrickMapper.GetBrickIdForFile(v.FilePath),
                        FailureType = v.Rule,
                        FilePath = v.FilePath,
                        RegressionPassed = false,
                        Promoted = false,
                        Message = $"Immutable core violation: cannot adapt {v.FilePath}",
                    }).ConfigureAwait(false);
                    throw new ImmutableCoreViolationException(v.FilePath);
                }

                var suggestion = $"Fix {v.Rule} in {Path.GetFileName(v.FilePath)}";
                if (!envelope.CanApplySourceFix && !await userFeedback.ApproveAsync(suggestion).ConfigureAwait(false))
                    continue;

                rollbackHelper.Snapshot(v.FilePath);
                if (!await sourceFixer.TryFixAsync(v).ConfigureAwait(false))
                {
                    rollbackHelper.Clear();
                    continue;
                }
                var regResult = skipRegression
                    ? new RegressionTestResult { AllPassed = true, PassedCount = 0, FailedCount = 0, Summary = "Skipped" }
                    : await regressionRunner.RunAsync(solutionPath).ConfigureAwait(false);
                if (regResult.AllPassed)
                {
                    var promoted = envelope.CanPromoteWithoutApproval || await userFeedback.ApproveAsync($"Regression passed. Promote: {suggestion}?").ConfigureAwait(false);
                    if (promoted)
                    {
                        sourceFixes++;
                        var record = new AdaptationRecord
                        {
                            Id = Guid.NewGuid().ToString("N"),
                            Timestamp = DateTimeOffset.UtcNow,
                            BrickId = ViolationToBrickMapper.GetBrickIdForFile(v.FilePath),
                            FailureType = v.Rule,
                            FixApplied = AdaptationFixType.Source,
                            FilePath = v.FilePath,
                            RegressionPassed = true,
                            Promoted = true,
                            Message = $"Fixed {v.Rule} in {Path.GetFileName(v.FilePath)}",
                        };
                        try
                        {
                            rollbackManager.PrepareForInherit(record.Id, new[] { v.FilePath });
                            await rollbackManager.BeforeInheritAsync(record.Id).ConfigureAwait(false);
                            await adaptationPromoter.PromoteAsync(record).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            logger.LogWarning(ex, "Promotion failed, rolling back adaptation {Id}", record.Id);
                            await rollbackManager.RollbackAsync(record.Id).ConfigureAwait(false);
                            rollbackHelper.Rollback(v.FilePath);
                            throw;
                        }
                        if (documentationUpdater != null)
                        {
                            try { await documentationUpdater.UpdateForAdaptationAsync(record.Id).ConfigureAwait(false); }
                            catch (Exception ex) { logger.LogWarning(ex, "Documentation update failed for {Id}", record.Id); }
                        }
                        if (sharedBroadcaster != null && !string.IsNullOrEmpty(record.FilePath))
                        {
                            try
                            {
                                var fullPath = Path.IsPathRooted(record.FilePath) ? record.FilePath : Path.Combine(repoRoot, record.FilePath);
                                if (File.Exists(fullPath))
                                {
                                    var relPath = Path.GetRelativePath(repoRoot, fullPath).Replace('\\', '/');
                                    var content = await File.ReadAllBytesAsync(fullPath).ConfigureAwait(false);
                                    var entry = new Ashlar.Core.Application.Adaptation.Models.SharedAdaptationEntry
                                    {
                                        Id = record.Id,
                                        Record = record,
                                        Files = new Dictionary<string, byte[]> { [relPath] = content },
                                        BroadcastAt = record.Timestamp,
                                        SourcePeerId = peerIdProvider?.GetPeerId(),
                                    };
                                    await sharedBroadcaster.BroadcastAsync(entry).ConfigureAwait(false);
                                }
                            }
                            catch (Exception ex) { logger.LogWarning(ex, "Shared adaptation broadcast failed for {Id}", record.Id); }
                        }
                        await auditLog.LogAsync(new AdaptationAuditEntry
                        {
                            Id = record.Id,
                            Timestamp = record.Timestamp,
                            AutonomyLevel = autonomy,
                            Outcome = "Promoted",
                            BrickId = record.BrickId,
                            FailureType = record.FailureType,
                            FilePath = record.FilePath,
                            RegressionPassed = true,
                            Promoted = true,
                            Message = record.Message,
                        }).ConfigureAwait(false);
                    }
                    else
                    {
                        rollbackHelper.Rollback(v.FilePath);
                        await auditLog.LogAsync(new AdaptationAuditEntry
                        {
                            Id = Guid.NewGuid().ToString("N"),
                            Timestamp = DateTimeOffset.UtcNow,
                            AutonomyLevel = autonomy,
                            Outcome = "Rejected",
                            BrickId = ViolationToBrickMapper.GetBrickIdForFile(v.FilePath),
                            FailureType = v.Rule,
                            FilePath = v.FilePath,
                            RegressionPassed = true,
                            Promoted = false,
                            Message = "User rejected promotion",
                        }).ConfigureAwait(false);
                    }
                    rollbackHelper.Clear();
                }
                else
                {
                    rollbackHelper.Rollback(v.FilePath);
                    var record = new AdaptationRecord
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        Timestamp = DateTimeOffset.UtcNow,
                        BrickId = ViolationToBrickMapper.GetBrickIdForFile(v.FilePath),
                        FailureType = v.Rule,
                        FixApplied = AdaptationFixType.Source,
                        FilePath = v.FilePath,
                        RegressionPassed = false,
                        Promoted = false,
                        Message = $"Rollback: regression failed after {v.Rule} fix",
                    };
                    await adaptationLog.LogAsync(record).ConfigureAwait(false);
                    await auditLog.LogAsync(new AdaptationAuditEntry
                    {
                        Id = record.Id,
                        Timestamp = record.Timestamp,
                        AutonomyLevel = autonomy,
                        Outcome = "Rollback",
                        BrickId = record.BrickId,
                        FailureType = record.FailureType,
                        FilePath = record.FilePath,
                        RegressionPassed = false,
                        Promoted = false,
                        Message = record.Message,
                    }).ConfigureAwait(false);
                }
            }
            if (sourceFixes > 0)
            {
                Console.WriteLine($"Applied {sourceFixes} source-level fix(s).");
                await executionTracer.TraceAsync("improve.end", null, targetPath, "source_fixes").ConfigureAwait(false);
                Environment.ExitCode = 0;
                return;
            }
        }

        if (violationsByBrick.Count == 0)
        {
            Console.WriteLine(dryRun
                ? "(dry-run: skip source fixes)"
                : "No violations in known bricks. Add brick mapping in ViolationToBrickMapper to enable brick adaptation.");
            Environment.ExitCode = 1;
            return;
        }

        if (dryRun)
        {
            Console.WriteLine("(dry-run: skip brick adaptation)");
            Environment.ExitCode = 1;
            return;
        }

        var brickRegistry = services.GetRequiredService<IBrickRegistry>();
        int brickAdapted = 0;
        foreach (var (brickId, violations) in violationsByBrick)
        {
            var immutableViolation = violations.FirstOrDefault(v => immutableCoreRegistry.IsInImmutableCore(v.FilePath));
            if (!string.IsNullOrEmpty(immutableViolation.FilePath))
            {
                logger.LogWarning("Rejecting brick adaptation for immutable core: {BrickId} ({FilePath})", brickId, immutableViolation.FilePath);
                await auditLog.LogAsync(new AdaptationAuditEntry
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Timestamp = DateTimeOffset.UtcNow,
                    AutonomyLevel = autonomy,
                    Outcome = "Rejected",
                    BrickId = brickId,
                    FailureType = immutableViolation.Rule,
                    FilePath = immutableViolation.FilePath,
                    RegressionPassed = false,
                    Promoted = false,
                    Message = $"Immutable core violation: cannot adapt {immutableViolation.FilePath}",
                }).ConfigureAwait(false);
                throw new ImmutableCoreViolationException(immutableViolation.FilePath);
            }

            var brick = brickRegistry.GetBrick(brickId);
            if (brick == null) continue;

            var manifest = await decomposer.DecomposeAsync(brick).ConfigureAwait(false);
            var uniqueRules = violations.Select(x => x.Rule).Distinct().ToList();

            foreach (var rule in uniqueRules)
            {
                var context = new FailureContext
                {
                    FailureType = rule,
                    TargetId = brickId,
                    Message = $"From analysis: {rule}",
                };
                var fixes = await fixGenerator.GenerateFixesAsync(context, manifest).ConfigureAwait(false);
                if (fixes.Count == 0) continue;

                var recompiled = await recompiler.RecompileAsync(fixes[0]).ConfigureAwait(false);
                if (recompiled != null)
                {
                    brickAdapted++;
                    logger.LogInformation("Adapted {DomainBrick} for {Rule}", brickId, rule);
                }
            }
        }

        if (brickAdapted > 0)
            Console.WriteLine($"Applied {brickAdapted} brick adaptation(s).");

        var outcome = analysisResult.Passed ? "passed" : "violations";
        await executionTracer.TraceAsync("improve.end", null, targetPath, outcome).ConfigureAwait(false);
        Environment.ExitCode = analysisResult.Passed ? 0 : 1;
    }

    private static async Task ExecuteContinuousAsync(
        string? path, bool dryRun, string autonomy, bool yes, bool skipRegression,
        string? storePathOverride, bool self, string? holdoutFilter,
        int observationDays, int intervalMinutes, int observeMinutes)
    {
        var repoRoot = path ?? RepoPathResolver.FindRepoRoot();
        var storePath = !string.IsNullOrWhiteSpace(storePathOverride)
            ? Path.Combine(Path.GetFullPath(storePathOverride), "ashlar-patterns.db")
            : Path.Combine(RepoPathResolver.ResolveStateDirectory(repoRoot), "ashlar-patterns.db");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        Console.WriteLine("Continuous self-improvement mode");
        Console.WriteLine($"  Root: {repoRoot}");
        Console.WriteLine($"  Observe: {observeMinutes}m per cycle, Interval: {intervalMinutes}m between cycles");
        Console.WriteLine("  Press Ctrl+C to stop.");
        Console.WriteLine();

        var iteration = 0;
        while (!cts.IsCancellationRequested)
        {
            iteration++;
            Console.WriteLine($"=== Continuous iteration {iteration} ({DateTimeOffset.UtcNow:HH:mm:ss}) ===");

            try
            {
                await RunObservationPhaseAsync(repoRoot, storePath, TimeSpan.FromMinutes(observeMinutes), cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }

            if (cts.IsCancellationRequested) break;

            Console.WriteLine();
            Console.WriteLine("Running improve --from-observation...");
            try
            {
                await ExecuteAsync(path, dryRun, autonomy, yes, skipRegression, storePathOverride, self, holdoutFilter, true, observationDays).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Console.WriteLine($"Improve cycle error: {ex.Message}");
            }

            if (cts.IsCancellationRequested) break;

            Console.WriteLine();
            Console.WriteLine($"Sleeping {intervalMinutes}m before next cycle...");
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(intervalMinutes), cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
        }

        Console.WriteLine();
        Console.WriteLine($"Continuous mode stopped after {iteration} iteration(s).");
        Environment.ExitCode = 0;
    }

    private static async Task RunObservationPhaseAsync(string repoRoot, string storePath, TimeSpan duration, CancellationToken ct)
    {
        var services = new ServiceCollection()
            .AddLogging(b => b.AddConsole())
            .AddOptions()
            .AddAdaptationInfrastructure(storePath)
            .AddSelfContextInfrastructure(storePath)
            .Configure<ObservationPipelineOptions>(opts =>
            {
                opts.RepoRoot = repoRoot;
                opts.StorePath = Path.GetFileName(storePath);
                opts.WatchPaths = new[] { "src", ".github", "tools" };
                opts.ProcessFilters = new[] { "dotnet", "msbuild", "ashlar" };
            })
            .AddSingleton<IPatternStore>(_ => new LiteDbPatternStore(storePath))
            .AddSingleton<IContextAssembler, ContextAssembler>()
            .BuildServiceProvider();

        var loggerFactory = services.GetRequiredService<ILoggerFactory>();

        var watchPaths = new[] { "src", ".github", "tools" }
            .Select(p => Path.Combine(repoRoot, p.TrimStart('/', '\\')))
            .Where(Directory.Exists)
            .ToList();

        if (watchPaths.Count == 0)
        {
            Console.WriteLine("No watch paths exist. Skipping observation phase.");
            return;
        }

        Console.WriteLine($"Observing for {duration.TotalMinutes}m: {string.Join(", ", watchPaths.Select(Path.GetFileName))}");

        var patternStore = services.GetRequiredService<IPatternStore>();
        var patternDetector = new PatternDetector(
            TimeSpan.FromMinutes(5),
            3,
            patternStore,
            loggerFactory.CreateLogger<PatternDetector>());

        var fileSource = new FileSystemEventSource(
            watchPaths,
            repoRoot,
            new[] { "*" },
            loggerFactory.CreateLogger<FileSystemEventSource>());
        var processSource = new ProcessEventSource(
            new[] { "dotnet", "msbuild", "ashlar" },
            repoRoot,
            TimeSpan.FromSeconds(2),
            loggerFactory.CreateLogger<ProcessEventSource>());
        var compositeSource = new CompositeEventSource(new IObservableEventSource[] { fileSource, processSource });

        using var observeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        observeCts.CancelAfter(duration);

        var eventCount = 0;
        try
        {
            await foreach (var evt in compositeSource.SubscribeAsync(observeCts.Token).WithCancellation(observeCts.Token))
            {
                eventCount++;
                await patternDetector.ProcessAsync(evt, observeCts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Observation time elapsed (not the outer cancellation)
        }

        Console.WriteLine($"Observation phase complete: {eventCount} event(s) captured.");
    }
}
