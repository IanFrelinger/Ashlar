using Microsoft.Extensions.Logging;
using Ashlar.Core.Application.Validation.Models;
using Ashlar.Core.Application.Validation.Ports;
using Ashlar.Core.Application.Common.Models;
using Ashlar.Infrastructure.Validation.Parsers;
using System.Diagnostics;

namespace Ashlar.Infrastructure.Validation.Adapters;

/// <summary>
/// Infrastructure adapter for running validation tests.
/// 
/// Implements IValidationService port from Application layer. Provides:
/// - Test project discovery
/// - Test execution via dotnet test
/// - Test result parsing using ITestResultParser
/// - Progress tracking for test execution
/// 
/// Part of the Infrastructure layer, implementing the hexagonal architecture pattern.
/// </summary>
public class ValidationServiceAdapter : IValidationService
{
    private readonly ILogger<ValidationServiceAdapter> _logger;
    private readonly ITestResultParser _testResultParser;

    /// <summary>Initializes a new validation service adapter.</summary>
    public ValidationServiceAdapter(
        ILogger<ValidationServiceAdapter> logger,
        ITestResultParser testResultParser)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _testResultParser = testResultParser ?? throw new ArgumentNullException(nameof(testResultParser));
    }

    /// <summary>Validate asynchronously.</summary>
    public async Task<ValidationResult> ValidateAsync(
        string? filter,
        IProgress<ProgressReport>? progress = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Running validation with filter: {Filter}",
            filter ?? "none");

        progress?.Report(new ProgressReport
        {
            Percentage = 0,
            Message = $"Starting validation with filter: {filter ?? "none"}",
            CurrentStep = 0,
            TotalSteps = null
        });

        try
        {
            ValidateFilterGrouping(filter);

            // Find test projects in current directory
            var currentDir = new DirectoryInfo(Directory.GetCurrentDirectory());
            var testProjects = currentDir.GetFiles("*.csproj", SearchOption.AllDirectories)
                .Where(f => IsDiscoverableTestProject(f, currentDir))
                .ToList();

            if (testProjects.Count == 0)
            {
                _logger.LogInformation("No test projects found - validation skipped");
                progress?.Report(new ProgressReport
                {
                    Percentage = 100,
                    Message = "No test projects found - validation skipped"
                });
                return new ValidationResult
                {
                    Passed = true,
                    Message = "No test projects found - validation skipped",
                    TestsRun = 0,
                    TestsPassed = 0,
                    TestsFailed = 0
                };
            }

            progress?.Report(new ProgressReport
            {
                Percentage = 5,
                Message = $"Found {testProjects.Count} test project(s)",
                CurrentStep = 0,
                TotalSteps = testProjects.Count
            });

            var allTestResults = new List<TestResult>();
            var evidenceErrors = new List<string>();
            var emptyProjects = new List<string>();
            int totalTestsRun = 0;
            int totalTestsPassed = 0;
            int totalTestsFailed = 0;
            int totalTestsSkipped = 0;
            var totalProjects = testProjects.Count;
            var currentProject = 0;

            foreach (var testProject in testProjects)
            {
                cancellationToken.ThrowIfCancellationRequested();

                currentProject++;
                var percentage = 5 + (int)((currentProject / (double)totalProjects) * 90);

                progress?.Report(new ProgressReport
                {
                    Percentage = percentage,
                    Message = $"Running tests in {Path.GetFileName(testProject.FullName)} ({currentProject}/{totalProjects})",
                    CurrentStep = currentProject,
                    TotalSteps = totalProjects,
                    Metadata = new Dictionary<string, object>
                    {
                        ["Project"] = testProject.FullName
                    }
                });

                try
                {
                    var projectDir = testProject.Directory?.FullName ?? currentDir.FullName;
                    var csprojPath = testProject.FullName;

                    // `dotnet test --no-build` requires outputs on disk. Multi-target test projects
                    // often have no DLL until an explicit build; CI `ashlar validate` previously failed
                    // with "test source file ... was not found" when only the CLI had been built.
                    var buildExit = await RunDotnetBuildProjectAsync(csprojPath, cancellationToken).ConfigureAwait(false);
                    if (buildExit != 0)
                    {
                        _logger.LogWarning(
                            "Skipping tests for {Project}: dotnet build exited {ExitCode}",
                            testProject.Name,
                            buildExit);
                        totalTestsFailed++;
                        totalTestsRun++;
                        allTestResults.Add(new TestResult
                        {
                            Name = testProject.Name,
                            Passed = false,
                            Message = $"dotnet build failed (exit {buildExit})"
                        });
                        continue;
                    }

                    // One framework per project: a multi-target project runs a single TFM (net8.0
                    // when it has it) so validate does not double the hosts under load, and a
                    // single-target project runs the TFM it actually declares — forcing net8.0 on
                    // a net9.0-only project produced "The argument <dll> is invalid" from VSTest
                    // for an output that was never built.
                    var framework = SelectTestFramework(csprojPath);
                    var runStartedUtc = DateTime.UtcNow;
                    var run = await RunDotnetTestForValidateAsync(
                        csprojPath,
                        framework,
                        filter,
                        streamOutput: progress != null,
                        cancellationToken).ConfigureAwait(false);

                    // Only artifacts this run wrote count. The most recent TRX under the project
                    // used to be taken regardless of age, so a run that died before writing one
                    // was judged on a stale file from an earlier run.
                    var trxFile = FindArtifactWrittenSince(projectDir, "*.trx", runStartedUtc);
                    var sequenceFile = FindArtifactWrittenSince(projectDir, "Sequence_*.xml", runStartedUtc);

                    var executed = new List<TestResult>();
                    var recordedResults = 0;
                    if (trxFile is not null)
                    {
                        // Parse TRX file for detailed results
                        var parsedResults = await _testResultParser.ParseAsync(trxFile, cancellationToken);
                        recordedResults = parsedResults.Count;

                        // Skipped tests were not run: they are neither passes nor failures and
                        // stay out of the per-test list (consumers list `!Passed` as failures).
                        // Their count is still reported.
                        executed = parsedResults.Where(r => !r.Skipped).ToList();
                        var skipped = parsedResults.Count - executed.Count;
                        if (skipped > 0)
                        {
                            _logger.LogInformation(
                                "{Project}: {Skipped} test(s) skipped (not counted as failures)",
                                testProject.Name,
                                skipped);
                        }

                        allTestResults.AddRange(executed);
                        totalTestsRun += executed.Count;
                        totalTestsPassed += executed.Count(r => r.Passed);
                        totalTestsFailed += executed.Count(r => !r.Passed);
                        totalTestsSkipped += skipped;
                    }

                    // A TRX only records the tests that reported before the run ended. When the
                    // Blame collector kills a hung host (#566) the results captured so far are
                    // all green, every test scheduled after the kill never runs, and `dotnet test`
                    // exits non-zero — the exit code and console output are the only evidence,
                    // so the run is judged on them too, not on the TRX alone.
                    var abort = DetectAbortedTestRun(run.ExitCode, run.Output, trxFile is not null, executed, sequenceFile);
                    if (abort is not null)
                    {
                        _logger.LogWarning(
                            "{Project}: test run did not complete: {Reason}",
                            testProject.Name,
                            abort.Describe());
                        allTestResults.Add(new TestResult
                        {
                            Name = $"{testProject.Name} (test run aborted)",
                            Passed = false,
                            Message = abort.Describe()
                        });
                        totalTestsFailed++;
                        totalTestsRun++;
                    }
                    else
                    {
                        switch (ClassifyCompletedRun(trxFile, recordedResults, run.Output))
                        {
                            case CompletedRunEvidence.NoTestsSelected:
                                emptyProjects.Add(testProject.Name);
                                _logger.LogInformation("{Project}: no tests selected (0 run)", testProject.Name);
                                break;
                            case CompletedRunEvidence.InvalidEvidence:
                                var error = $"{testProject.Name}: test results are missing, unreadable or inconsistent; "
                                    + "the run cannot be reported as passing.";
                                evidenceErrors.Add(error);
                                _logger.LogWarning("{Error}", error);
                                break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Failed to run tests for project: {Project}",
                        testProject.Name);

                    totalTestsFailed++;
                    allTestResults.Add(new TestResult
                    {
                        Name = testProject.Name,
                        Passed = false,
                        Message = $"Error: {ex.Message}"
                    });
                }
            }

            // Pass if no tests failed (even if no tests were run)
            var passed = totalTestsFailed == 0 && evidenceErrors.Count == 0;

            progress?.Report(new ProgressReport
            {
                Percentage = 100,
                Message = $"Validation completed. Passed: {passed}, Tests: {totalTestsPassed}/{totalTestsRun}",
                CurrentStep = totalProjects,
                TotalSteps = totalProjects
            });

            var skippedSuffix = totalTestsSkipped > 0 ? $", {totalTestsSkipped} skipped" : string.Empty;
            var evidenceSuffix = evidenceErrors.Count > 0 ? "; " + string.Join("; ", evidenceErrors) : string.Empty;
            var emptySuffix = emptyProjects.Count > 0 ? "; no tests selected: " + string.Join(", ", emptyProjects) : string.Empty;
            return new ValidationResult
            {
                Passed = passed,
                Message = (passed
                    ? $"Validation passed ({totalTestsPassed}/{totalTestsRun} tests{skippedSuffix})"
                    : $"Validation failed ({totalTestsFailed}/{totalTestsRun} tests failed{skippedSuffix})")
                    + evidenceSuffix + emptySuffix,
                TestsRun = totalTestsRun,
                TestsPassed = totalTestsPassed,
                TestsFailed = totalTestsFailed,
                TestsSkipped = totalTestsSkipped,
                TestResults = allTestResults,
                EvidenceErrors = evidenceErrors
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during validation");
            return new ValidationResult
            {
                Passed = false,
                Message = $"Validation error: {ex.Message}",
                TestsRun = 0,
                TestsPassed = 0,
                TestsFailed = 0
            };
        }
    }

    /// <summary>
    /// Whether a <c>.csproj</c> under the validation root is a test project we should build and
    /// run. A project qualifies by name or directory ("Test"), and is excluded when it is a
    /// build helper, lives under a hidden directory (<c>.git</c>, <c>.claude</c> worktrees — a
    /// full second checkout would double every run), lives under a <c>templates</c> directory,
    /// or is itself a template whose name still carries a <c>__Placeholder__</c> token — those
    /// exist to be stamped by <c>ashlar new</c>, not compiled in place.
    /// </summary>
    internal static bool IsDiscoverableTestProject(FileInfo project, DirectoryInfo root)
    {
        var name = project.Name;
        var directory = project.DirectoryName ?? string.Empty;

        var looksLikeTests =
            name.Contains("Test", StringComparison.OrdinalIgnoreCase) ||
            directory.Contains("test", StringComparison.OrdinalIgnoreCase);
        if (!looksLikeTests)
            return false;

        if (name.Equals("copy-assemblies.csproj", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Ashlar.Agents.TestKit.csproj", StringComparison.OrdinalIgnoreCase))
            return false;

        if (PlaceholderToken.IsMatch(name))
            return false;

        // Only the segments below the validation root matter: the root itself may legitimately
        // sit under a hidden or "templates" directory.
        var relative = Path.GetRelativePath(root.FullName, directory);
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (segment.Length == 0 || segment == ".")
                continue;
            if (segment.StartsWith('.'))
                return false;
            if (segment.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("obj", StringComparison.OrdinalIgnoreCase))
                return false;
            if (segment.Equals("templates", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("template", StringComparison.OrdinalIgnoreCase))
                return false;
            // Author-shaped fixture projects for the cert-gate corpus, not runnable tests.
            if (segment.Equals("adversarial-corpus", StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    internal enum CompletedRunEvidence
    {
        ResultsRecorded,
        NoTestsSelected,
        InvalidEvidence,
    }

    // A parser can return no rows after a read/parse failure. Empty results alone therefore do
    // not establish an empty selection. Validate the TRX envelope and row count as independent
    // evidence, and never turn a project-level evidence failure into a fictional executed test.
    internal static CompletedRunEvidence ClassifyCompletedRun(FileInfo? trx, int recordedResults, string output)
    {
        if (trx == null)
        {
            return recordedResults == 0 && SplitLines(output).Any(line => NoTestsMatchedLinePrefixes.Any(
                prefix => line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                ? CompletedRunEvidence.NoTestsSelected : CompletedRunEvidence.InvalidEvidence;
        }

        try
        {
            var document = System.Xml.Linq.XDocument.Load(trx.FullName);
            System.Xml.Linq.XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
            if (document.Root?.Name != ns + "TestRun")
                return CompletedRunEvidence.InvalidEvidence;
            var rows = document.Root.Element(ns + "Results")?.Descendants(ns + "UnitTestResult").ToList()
                ?? new List<System.Xml.Linq.XElement>();
            if (rows.Count != recordedResults)
                return CompletedRunEvidence.InvalidEvidence;

            var summary = document.Root.Element(ns + "ResultSummary");
            var outcome = (string?)summary?.Attribute("outcome");
            var counters = summary?.Element(ns + "Counters");
            bool CounterEquals(string name, int expected) =>
                int.TryParse((string?)counters?.Attribute(name), out var count) && count == expected;
            bool MatchesSummary(IReadOnlyList<System.Xml.Linq.XElement> countedRows)
            {
                var outcomes = countedRows.Select(row => (string?)row.Attribute("outcome")).ToList();
                var knownOutcomes = outcomes.All(value => value is "Passed" or "Completed" or "Failed" or "NotExecuted" or "Inconclusive");
                var failures = outcomes.Count(value => value == "Failed");
                var executed = outcomes.Count(value => value is "Passed" or "Completed" or "Failed");
                var validOutcome = failures > 0
                    ? outcome == "Failed"
                    : outcome is "Completed" or "Passed" || outcome == "NotExecuted" && executed == 0;
                return knownOutcomes && validOutcome && CounterEquals("total", countedRows.Count)
                    && CounterEquals("executed", executed) && CounterEquals("failed", failures)
                    && CounterEquals("error", 0) && CounterEquals("aborted", 0);
            }

            // VSTest 17.12 counts data-driven parents and their children. VSTest 18.9 excludes
            // DataDrivenTest containers from counters, while retaining them in the TRX tree.
            // The parser still returns every row; reconcile both documented counter layouts.
            var withoutContainers = rows.Where(row => (string?)row.Attribute("resultType") != "DataDrivenTest"
                || !row.Descendants(ns + "UnitTestResult").Any()).ToList();
            if (!MatchesSummary(rows) && !MatchesSummary(withoutContainers))
                return CompletedRunEvidence.InvalidEvidence;
            return rows.Count == 0 ? CompletedRunEvidence.NoTestsSelected : CompletedRunEvidence.ResultsRecorded;
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or IOException or UnauthorizedAccessException)
        {
            return CompletedRunEvidence.InvalidEvidence;
        }
    }

    private static readonly System.Text.RegularExpressions.Regex PlaceholderToken =
        new("__[A-Za-z0-9]+__", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// The single target framework validate should run for a project, read from the project
    /// file: a multi-target project (<c>TargetFrameworks</c>) runs <c>net8.0</c> when it lists it
    /// (one host, not two, under load), otherwise its first framework; a single-target project
    /// runs the framework it declares. Null when the project declares neither, in which case
    /// <c>dotnet test</c> is left to its own resolution.
    /// </summary>
    internal static string? SelectTestFramework(string csprojPath)
    {
        try
        {
            var doc = System.Xml.Linq.XDocument.Load(csprojPath);
            var multi = doc.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "TargetFrameworks")?.Value;
            if (!string.IsNullOrWhiteSpace(multi))
            {
                var frameworks = multi.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (frameworks.Length == 0)
                    return null;
                return frameworks.FirstOrDefault(f => f.Equals("net8.0", StringComparison.OrdinalIgnoreCase))
                       ?? frameworks[0];
            }

            var single = doc.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "TargetFramework")?.Value?.Trim();
            return string.IsNullOrEmpty(single) ? null : single;
        }
        catch (Exception)
        {
            // An unreadable project file surfaces at build; the framework choice is not the
            // place to fail it.
            return null;
        }
    }

    private static async Task<int> RunDotnetBuildProjectAsync(string csprojPath, CancellationToken ct)
    {
        var p = Process.Start(CreateDotnetBuildStartInfo(csprojPath));
        if (p is null)
            return -1;

        using (p)
        {
            p.OutputDataReceived += (_, _) => { };
            p.ErrorDataReceived += (_, e) => { if (e.Data is not null) Console.Error.WriteLine(e.Data); };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            try
            {
                await p.WaitForExitAsync(ct).ConfigureAwait(false);
                return p.ExitCode;
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return -1;
            }
        }
    }

    /// <summary>
    /// Console lines from the <c>vstest</c> console logger and the Blame collector that mean
    /// the run ended before every scheduled test ran. Matched at the start of a trimmed line
    /// (the Blame one after its collector prefix) so a test's own display name or output that
    /// happens to quote one of them — a Theory case, say — cannot trip the detector.
    /// </summary>
    private static readonly string[] AbortedRunLinePrefixes =
    {
        "The active test run was aborted",
        "Test Run Aborted",
        "Test host process crashed",
        "Total tests: Unknown",
    };

    private const string BlameCollectorPrefix = "Data collector 'Blame' message:";
    private const string BlameInactivityMarker = "The specified inactivity time of";
    private const string LastRunningTestMarker = "The test running when the crash occurred:";

    /// <summary>
    /// Console lines that mean the run legitimately executed nothing. <c>dotnet test</c> may
    /// exit non-zero for these depending on SDK version and runsettings; a project whose
    /// filter matches no test is not a failed project, so they keep the exit-code verdict off.
    /// </summary>
    private static readonly string[] NoTestsMatchedLinePrefixes =
    {
        "No test matches the given testcase filter",
        "No test is available",
    };

    /// <summary>
    /// Why one <c>dotnet test</c> run cannot be trusted as complete: the reason line, the test
    /// that was running when the host went down (when the Blame collector or the console said),
    /// and how many results the TRX had captured before that point.
    /// </summary>
    internal sealed record TestRunAbort(string Reason, string? LastRunningTest, int ResultsRecorded)
    {
        /// <summary>One line suitable for the failure entry and the log.</summary>
        public string Describe()
        {
            var text = Reason;
            if (!string.IsNullOrWhiteSpace(LastRunningTest))
                text += $"; last running test: {LastRunningTest}";
            text += $"; {ResultsRecorded} result(s) were recorded before the run ended and tests scheduled after it never ran";
            return text;
        }
    }

    /// <summary>
    /// Judges one <c>dotnet test</c> invocation on everything it left behind, not on the TRX
    /// alone. Returns null when the run completed — it exited 0, or every failure it exited
    /// non-zero for is recorded in the TRX — and a <see cref="TestRunAbort"/> when a non-zero
    /// exit is not explained by the TRX:
    /// <list type="bullet">
    /// <item>the console carries a vstest abort line or a Blame inactivity (hang-kill) message,
    /// in which case the reason names the test that was running if the console or the Blame
    /// <c>Sequence_*.xml</c> did;</item>
    /// <item>the TRX records no failed test — the run stopped before it could fail one, or
    /// wrote no TRX at all — unless the console says the filter simply matched nothing, which
    /// stays a pass.</item>
    /// </list>
    /// A non-zero exit with failed tests in the TRX is an ordinary red run: the TRX already
    /// names each failure, so nothing is added. Warnings never count.
    /// </summary>
    internal static TestRunAbort? DetectAbortedTestRun(
        int exitCode,
        string consoleOutput,
        bool trxFound,
        IReadOnlyList<TestResult> executedResults,
        FileInfo? sequenceFile)
    {
        // A clean exit is trusted. vstest fails the VSTestTask (MSB4181) whenever it aborts a
        // run, so `dotnet test` cannot exit 0 after a hang-kill or a host crash; and abort lines
        // can reach this console second-hand — a test that itself drives `dotnet test` echoes
        // its child's stderr — so they are evidence only when the exit code agrees.
        if (exitCode == 0)
            return null;

        var lines = SplitLines(consoleOutput);
        var recorded = executedResults.Count;

        var abortLine = lines.FirstOrDefault(IsAbortedRunLine);
        if (abortLine is not null)
        {
            var lastTest = ExtractLastRunningTest(lines) ?? ReadLastTestFromSequenceFile(sequenceFile);
            return new TestRunAbort($"test host did not finish the run: {abortLine}", lastTest, recorded);
        }

        // Ordinary failing tests: the TRX names them and the exit code merely agrees.
        if (executedResults.Any(r => !r.Passed))
            return null;

        if (lines.Any(l => NoTestsMatchedLinePrefixes.Any(p => l.StartsWith(p, StringComparison.OrdinalIgnoreCase))))
            return null;

        var lastRunning = ExtractLastRunningTest(lines) ?? ReadLastTestFromSequenceFile(sequenceFile);
        var reason = trxFound
            ? $"dotnet test exited {exitCode} but the TRX records no failed test"
            : $"dotnet test exited {exitCode} and wrote no TRX for this run";
        return new TestRunAbort(reason, lastRunning, recorded);
    }

    private static bool IsAbortedRunLine(string line)
    {
        foreach (var prefix in AbortedRunLinePrefixes)
        {
            if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        if (line.StartsWith(BlameCollectorPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var rest = line.Substring(BlameCollectorPrefix.Length).TrimStart();
            return rest.StartsWith(BlameInactivityMarker, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    /// <summary>
    /// The test named after "The test running when the crash occurred:" — on the same line
    /// after the colon, or on the next non-empty line. Null when the console never said.
    /// </summary>
    internal static string? ExtractLastRunningTest(IReadOnlyList<string> lines)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (!line.StartsWith(LastRunningTestMarker, StringComparison.OrdinalIgnoreCase))
                continue;

            var inline = line.Substring(LastRunningTestMarker.Length).Trim();
            if (inline.Length > 0)
                return inline;

            for (var j = i + 1; j < lines.Count; j++)
            {
                if (lines[j].Length > 0)
                    return lines[j];
            }
        }

        return null;
    }

    /// <summary>
    /// The last <c>&lt;Test&gt;</c> the Blame collector appended to its <c>Sequence_*.xml</c>
    /// before the host went down — the test that was running at that moment. Null when there is
    /// no file, it is unreadable, or it lists no test.
    /// </summary>
    internal static string? ReadLastTestFromSequenceFile(FileInfo? sequenceFile)
    {
        if (sequenceFile is null || !sequenceFile.Exists)
            return null;

        try
        {
            var doc = System.Xml.Linq.XDocument.Load(sequenceFile.FullName);
            var last = doc.Descendants().LastOrDefault(e => e.Name.LocalName == "Test");
            if (last is null)
                return null;

            foreach (var attribute in new[] { "Name", "name", "FullyQualifiedName", "DisplayName" })
            {
                var value = last.Attribute(attribute)?.Value?.Trim();
                if (!string.IsNullOrEmpty(value))
                    return value;
            }

            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static IReadOnlyList<string> SplitLines(string text)
    {
        if (string.IsNullOrEmpty(text))
            return Array.Empty<string>();
        return text.Split('\n').Select(l => l.Trim()).ToList();
    }

    /// <summary>
    /// The newest file under <paramref name="directory"/> matching <paramref name="pattern"/>
    /// that was written by the run that started at <paramref name="runStartedUtc"/> (a small
    /// tolerance covers coarse filesystem timestamps). Null when the run wrote none.
    /// </summary>
    private static FileInfo? FindArtifactWrittenSince(string directory, string pattern, DateTime runStartedUtc)
    {
        var notBefore = runStartedUtc - TimeSpan.FromSeconds(10);
        return Directory.GetFiles(directory, pattern, SearchOption.AllDirectories)
            .Select(f => new FileInfo(f))
            .Where(f => f.LastWriteTimeUtc >= notBefore)
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault();
    }

    /// <summary>Exit code and captured console output of one <c>dotnet test</c> run.</summary>
    internal sealed record DotnetTestRun(int ExitCode, string Output);

    /// <summary>
    /// Runs <c>dotnet test</c> for validate: one framework (see <see cref="SelectTestFramework"/>),
    /// TRX for parsing, optional console streaming. Stdout and stderr are also retained (the
    /// last <see cref="MaxRetainedOutputLines"/> lines) so the run can be judged on what vstest
    /// and the Blame collector said, not only on the exit code.
    /// </summary>
    private static async Task<DotnetTestRun> RunDotnetTestForValidateAsync(
        string csprojPath, string? framework, string? filter, bool streamOutput, CancellationToken ct)
    {
        var p = Process.Start(CreateDotnetTestStartInfo(csprojPath, framework, filter, streamOutput));
        if (p is null)
            return new DotnetTestRun(-1, string.Empty);

        var retained = new Queue<string>();
        var retainedLock = new object();
        void Retain(string line)
        {
            lock (retainedLock)
            {
                retained.Enqueue(line);
                while (retained.Count > MaxRetainedOutputLines)
                    retained.Dequeue();
            }
        }

        string Captured()
        {
            lock (retainedLock)
                return string.Join('\n', retained);
        }

        using (p)
        {
            p.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                Retain(e.Data);
                if (streamOutput) Console.Out.WriteLine(e.Data);
            };
            p.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                Retain(e.Data);
                Console.Error.WriteLine(e.Data);
            };

            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            try
            {
                await p.WaitForExitAsync(ct).ConfigureAwait(false);
                return new DotnetTestRun(p.ExitCode, Captured());
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return new DotnetTestRun(-1, Captured());
            }
        }
    }

    internal static ProcessStartInfo CreateDotnetBuildStartInfo(string csprojPath)
    {
        var startInfo = CreateDotnetStartInfo(csprojPath);
        foreach (var argument in new[] { "build", csprojPath, "--verbosity", "quiet" })
            startInfo.ArgumentList.Add(argument);
        return startInfo;
    }

    // Caller filters narrow the normal sweep; they cannot opt excluded categories back in.
    // Each value is one argv element, including paths and filters containing spaces or quotes.
    internal static ProcessStartInfo CreateDotnetTestStartInfo(
        string csprojPath, string? framework, string? filter, bool streamOutput)
    {
        ValidateFilterGrouping(filter);
        const string exclusions = "Category!=DockerOptional&Category!=Stress";
        var effectiveFilter = string.IsNullOrWhiteSpace(filter) ? exclusions : $"({exclusions})&({filter})";
        var startInfo = CreateDotnetStartInfo(csprojPath);
        startInfo.ArgumentList.Add("test");
        startInfo.ArgumentList.Add(csprojPath);
        if (framework is not null)
        {
            startInfo.ArgumentList.Add("--framework");
            startInfo.ArgumentList.Add(framework);
        }
        foreach (var argument in new[]
        {
            "--no-build", "--filter", effectiveFilter, "--logger", "trx",
            "--blame-hang-timeout", $"{ValidateBlameHangTimeoutSeconds}s",
            "--blame-hang-dump-type", "none", "--verbosity", streamOutput ? "normal" : "minimal"
        })
            startInfo.ArgumentList.Add(argument);
        return startInfo;
    }

    private static void ValidateFilterGrouping(string? filter)
    {
        // VSTest uses backslash escapes for literal parentheses and backslashes. Check the
        // caller's grouping before adding our own: an unmatched ')' could otherwise escape
        // the AND and turn an excluded category back on through an outer OR.
        var depth = 0;
        var escaped = false;
        foreach (var character in filter ?? string.Empty)
        {
            if (escaped)
            {
                escaped = false;
                continue;
            }
            if (character == '\\') escaped = true;
            else if (character == '(') depth++;
            else if (character == ')' && --depth < 0)
                throw new ArgumentException("The test filter must have balanced, unescaped parentheses.", nameof(filter));
        }
        if (depth != 0 || escaped)
            throw new ArgumentException("The test filter must have balanced, unescaped parentheses and complete escapes.", nameof(filter));
    }

    private static ProcessStartInfo CreateDotnetStartInfo(string csprojPath) => new("dotnet")
    {
        WorkingDirectory = Path.GetDirectoryName(csprojPath) ?? Directory.GetCurrentDirectory(),
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false
    };

    /// <summary>
    /// Blame's inactivity window for the <c>validate</c> sweep, in seconds. It must stay ABOVE
    /// every per-test timeout in the suites this runs; <c>TimeoutConventionTests</c> fails when
    /// it does not.
    /// </summary>
    /// <remarks>
    /// This was 120s, while tests this lane selects are bounded at up to 600s — five times
    /// larger. A test bounded above the window can never fail as a timeout here: any stall past
    /// two minutes is a host kill instead, which discards the ~1900 results already recorded,
    /// names the in-flight test only as one that "may, or may not be the source of the crash",
    /// and leaves no per-test failure to diagnose. That is what
    /// <c>FileSystemEventSourceTests.SubscribeAsync_FileCreated_EmitsEvent</c> produced twice on
    /// the macOS lane, and the 480s bound it tripped over had itself been raised to stop an
    /// earlier false red (docs/production-readiness/KernelCoverageGate-Findings.md).
    ///
    /// 900s restores the relation docs/Testing.md already documents — blame-hang-timeout at 1.5x
    /// the widest per-test timeout — so the per-test net fires first and the failure names
    /// itself. It is 1.5x 600s, not 1.5x <c>TestTimeouts.HostTouching</c>: the widest deadline
    /// this filter selects is the 600_000 ms literal on
    /// <c>RuntimeStudioBlackBoxPlaygroundTests</c>, and the first attempt at this fix sized the
    /// window against the <c>TestTimeouts</c> constants alone — which is exactly the blind spot
    /// <c>LaneBlameWindowConventionTests</c> now removes by reflecting over the real
    /// <c>[Fact(Timeout = ...)]</c> values and evaluating each lane's own filter against them.
    ///
    /// The cost is that a host which is genuinely wedged, with no per-test timeout above it, now
    /// takes fifteen minutes to abort rather than two. That is the right trade: the old window
    /// never shortened a hang, it only converted diagnosable test failures into lost runs.
    /// </remarks>
    internal const int ValidateBlameHangTimeoutSeconds = 900;

    private const int MaxRetainedOutputLines = 10_000;
}
