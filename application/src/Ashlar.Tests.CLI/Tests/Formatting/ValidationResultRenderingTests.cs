using Ashlar.CLI.Formatting;
using Ashlar.Core.Application.Validation.Models;
using FluentAssertions;
using Xunit;

namespace Ashlar.Tests.CLI.Tests.Formatting;

[CollectionDefinition("ValidationConsole", DisableParallelization = true)]
public sealed class ValidationConsoleCollection;

[Collection("ValidationConsole")]
public sealed class ValidationResultRenderingTests
{
    [Theory]
    [InlineData(false, "Validation failed (0/0 tests failed); EvidenceTests.csproj: test results are missing, unreadable or inconsistent")]
    [InlineData(true, "Validation passed (0/0 tests); no tests selected: FilteredTests.csproj")]
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
}
