using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Ashlar.Core.Application.Analysis.Models;
using Ashlar.Core.Application.Analysis.Ports;

namespace Ashlar.Infrastructure.Analysis.BrickAnalyzer;

/// <summary>
/// Runs regression tests via dotnet test.
/// </summary>
public sealed class DotNetRegressionTestRunner : IRegressionTestRunner
{
    private readonly ILogger<DotNetRegressionTestRunner>? _logger;

    /// <summary>Initializes a new dot net regression test runner.</summary>
    public DotNetRegressionTestRunner(ILogger<DotNetRegressionTestRunner>? logger = null)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<RegressionTestResult> RunAsync(
        string projectOrSolutionPath,
        string? filter = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(projectOrSolutionPath))
        {
            return new RegressionTestResult
            {
                AllPassed = false,
                PassedCount = 0,
                FailedCount = 0,
                Summary = $"Path not found: {projectOrSolutionPath}",
            };
        }

        var args = $"test \"{projectOrSolutionPath}\" --no-build --verbosity minimal";
        if (!string.IsNullOrWhiteSpace(filter))
            args += $" --filter \"{filter}\"";

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = args,
            WorkingDirectory = Path.GetDirectoryName(projectOrSolutionPath) ?? Environment.CurrentDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        try
        {
            using var proc = Process.Start(psi);
            if (proc == null)
                return new RegressionTestResult { AllPassed = false, PassedCount = 0, FailedCount = 0, Summary = "Failed to start dotnet test" };

            var stdout = await proc.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            var stderr = await proc.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await proc.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            var output = stdout + stderr;
            var (passed, failed) = ParseDotnetTestOutput(output);

            return new RegressionTestResult
            {
                AllPassed = proc.ExitCode == 0 && failed == 0,
                PassedCount = passed,
                FailedCount = failed,
                Summary = $"Passed: {passed}, Failed: {failed}",
            };
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Regression test run failed");
            return new RegressionTestResult
            {
                AllPassed = false,
                PassedCount = 0,
                FailedCount = 0,
                Summary = ex.Message,
            };
        }
    }

    /// <summary>
    /// Extracts the pass/fail counts from a <c>dotnet test</c> transcript.
    /// </summary>
    /// <remarks>
    /// Internal rather than private so it can be unit tested against captured transcripts. The
    /// alternative — driving the whole runner at a real solution — spawns a vstest host per test
    /// assembly per target framework, and every assertion such a test can make is already
    /// satisfied by the catch-all in <see cref="RunAsync"/>, which returns 0/0 for a
    /// <c>dotnet</c> that is missing, a tree that is not built, or a filter matching nothing.
    /// </remarks>
    internal static (int Passed, int Failed) ParseDotnetTestOutput(string output)
    {
        var passedMatch = Regex.Match(output, @"Passed!\s*-\s*Failed:\s*\d+,\s*Passed:\s*(\d+)");
        var failedMatch = Regex.Match(output, @"Failed!\s*-\s*Failed:\s*(\d+)");
        var passed = passedMatch.Success ? int.Parse(passedMatch.Groups[1].Value) : 0;
        var failed = failedMatch.Success ? int.Parse(failedMatch.Groups[1].Value) : 0;
        if (failed == 0 && passed == 0)
        {
            passedMatch = Regex.Match(output, @"Passed:\s*(\d+)");
            failedMatch = Regex.Match(output, @"Failed:\s*(\d+)");
            if (passedMatch.Success) passed = int.Parse(passedMatch.Groups[1].Value);
            if (failedMatch.Success) failed = int.Parse(failedMatch.Groups[1].Value);
        }
        return (passed, failed);
    }
}
