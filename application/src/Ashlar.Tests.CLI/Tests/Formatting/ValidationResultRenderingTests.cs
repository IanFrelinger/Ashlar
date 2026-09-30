using Ashlar.CLI.Formatting;
using Ashlar.Core.Application.Validation.Models;
using Ashlar.Infrastructure.Validation.Adapters;
using FluentAssertions;
using Xunit;

namespace Ashlar.Tests.CLI.Tests.Formatting;

[CollectionDefinition("ValidationConsole", DisableParallelization = true)]
public sealed class ValidationConsoleCollection;

[Collection("ValidationConsole")]
public sealed class ValidationResultRenderingTests
{
    // Each message below is one the validation adapter actually composes, which
    // Every_documented_message_is_one_the_adapter_composes pins, so these pairings cannot drift
    // from what `ashlar validate` prints. Since 2026-09-30 an UNFILTERED sweep fails a discovered
    // project that executed no test, so a passing "no tests selected" result is reachable only
    // under a caller's filter.
    private const string EvidenceFailure =
        "Validation failed (0/0 tests passed, but 1 project produced no passing evidence); "
        + "EvidenceTests.csproj: test results are missing, unreadable or inconsistent; "
        + "the run cannot be reported as passing.";

    private const string UnfilteredZeroExecution =
        "Validation failed (0/0 tests passed, but 1 project produced no passing evidence); "
        + "StressOnlyTests.csproj: executed no tests in an unfiltered sweep (it selected none, and "
        + "validate always excludes Category=Stress and Category=DockerOptional); a discovered test "
        + "project that runs nothing is not evidence of passing.";

    private const string FilteredEmptySelection =
        "Validation passed (0/0 tests); no tests selected: FilteredTests.csproj";

    [Theory]
    [InlineData(false, EvidenceFailure)]
    [InlineData(false, UnfilteredZeroExecution)]
    [InlineData(true, FilteredEmptySelection)]
    public void Human_output_preserves_project_evidence_and_empty_selection_reasons(bool passed, string message)
    {
        var previousOut = Console.Out;
        var previousError = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            new ConsoleRenderer().RenderValidationResult(new ValidationResult
            {
                Passed = passed, Message = message,
                TestsRun = 0, TestsPassed = 0, TestsFailed = 0,
            }, json: false);
            (passed ? stdout : stderr).ToString().Trim().Should().Be(message);
            (passed ? stderr : stdout).ToString().Should().BeEmpty();
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
        }
    }

    [Fact]
    public void Every_documented_message_is_one_the_adapter_composes()
    {
        ValidationServiceAdapter.DescribeOutcome(
                false, 0, 0, 0, 0,
                new[] { ValidationServiceAdapter.InvalidEvidenceError("EvidenceTests.csproj") },
                Array.Empty<string>())
            .Should().Be(EvidenceFailure);

        var zero = ValidationServiceAdapter.ZeroExecutionError(
            "StressOnlyTests.csproj", filter: null, executed: 0, skipped: 0);
        zero.Should().NotBeNull("an unfiltered sweep fails a project that executed nothing");
        ValidationServiceAdapter.DescribeOutcome(false, 0, 0, 0, 0, new[] { zero! }, Array.Empty<string>())
            .Should().Be(UnfilteredZeroExecution);

        ValidationServiceAdapter.ZeroExecutionError(
                "FilteredTests.csproj", filter: "Category=Smoke", executed: 0, skipped: 0)
            .Should().BeNull("only a caller's filter can still produce a passing empty selection");
        ValidationServiceAdapter.DescribeOutcome(
                true, 0, 0, 0, 0, Array.Empty<string>(), new[] { "FilteredTests.csproj" })
            .Should().Be(FilteredEmptySelection);
    }
}
