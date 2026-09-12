using Ashlar.Core.Application.Common.Models;

namespace Ashlar.Core.Application.Validation.Models;

/// <summary>
/// Result of a validation operation.
/// 
/// Contains:
/// - Whether validation passed
/// - Summary message
/// - Test execution statistics (run, passed, failed)
/// - Optional list of individual test results
/// 
/// Produced by IValidationService after running validation tests.
/// Used by CLI commands to display validation results.
/// </summary>
public record ValidationResult
{
    /// <summary>Whether all validation checks passed.</summary>
    public required bool Passed { get; init; }

    /// <summary>Human-readable summary of the validation outcome.</summary>
    public required string Message { get; init; }

    /// <summary>Total number of tests executed.</summary>
    public required int TestsRun { get; init; }

    /// <summary>Number of tests that passed.</summary>
    public required int TestsPassed { get; init; }

    /// <summary>Number of tests that failed.</summary>
    public required int TestsFailed { get; init; }

    /// <summary>
    /// Number of tests that were skipped (not executed). Skipped tests are not counted in
    /// <see cref="TestsRun"/> and never count as failures.
    /// </summary>
    public int TestsSkipped { get; init; }

    /// <summary>Per-test results when detailed output is available.</summary>
    public IReadOnlyList<TestResult>? TestResults { get; init; }

    /// <summary>
    /// Project runs whose result evidence could not be read or substantiated. These make validation
    /// fail but do not contribute fictional tests to TestsRun, TestsPassed or TestsFailed.
    /// </summary>
    public IReadOnlyList<string> EvidenceErrors { get; init; } = Array.Empty<string>();
}
