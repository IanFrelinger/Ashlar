using Ashlar.Infrastructure.Validation.Adapters;
using FluentAssertions;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>Distinguishes an empty selection from missing or unreadable test evidence.</summary>
[Trait("Category", "Certification")]
public sealed class ValidationEvidenceCertificationTests
{
    private const string EmptyTrx = """
        <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
          <Results />
          <ResultSummary outcome="Completed"><Counters total="0" executed="0" failed="0" error="0" aborted="0" /></ResultSummary>
        </TestRun>
        """;

    [Theory]
    [InlineData("", false)]
    [InlineData("all good", false)]
    [InlineData("No test matches the given testcase filter 'Category!=Stress' in /x/tests.dll", true)]
    [InlineData("No test is available in /x/tests.dll", true)]
    public void A_missing_file_requires_an_explicit_empty_selection(string output, bool empty)
        => ValidationServiceAdapter.ClassifyCompletedRun(null, 0, output).Should().Be(empty
            ? ValidationServiceAdapter.CompletedRunEvidence.NoTestsSelected
            : ValidationServiceAdapter.CompletedRunEvidence.InvalidEvidence);

    [Fact]
    public void A_valid_empty_result_file_proves_an_empty_selection()
        => Classify(EmptyTrx, 0).Should().Be(ValidationServiceAdapter.CompletedRunEvidence.NoTestsSelected);

    [Theory]
    [InlineData("<broken")]
    [InlineData("<not-a-test-run />")]
    [InlineData("<TestRun xmlns=\"http://microsoft.com/schemas/VisualStudio/TeamTest/2010\"><Results /></TestRun>")]
    public void An_unreadable_or_incomplete_file_is_not_an_empty_selection(string text)
        => Classify(text, 0, "No test is available in /x/tests.dll").Should().Be(
            ValidationServiceAdapter.CompletedRunEvidence.InvalidEvidence);

    [Theory]
    [InlineData("total")]
    [InlineData("executed")]
    [InlineData("failed")]
    [InlineData("error")]
    [InlineData("aborted")]
    public void A_nonzero_counter_is_not_an_empty_selection(string counter)
        => Classify(EmptyTrx.Replace($"{counter}=\"0\"", $"{counter}=\"1\"", StringComparison.Ordinal), 0)
            .Should().Be(ValidationServiceAdapter.CompletedRunEvidence.InvalidEvidence);

    [Fact]
    public void A_failed_summary_is_not_an_empty_selection()
        => Classify(EmptyTrx.Replace("Completed", "Failed", StringComparison.Ordinal), 0)
            .Should().Be(ValidationServiceAdapter.CompletedRunEvidence.InvalidEvidence);

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    public void Parsed_results_must_match_the_file_rows(int parsedRows, bool matches)
    {
        var text = OnePassingTrx;
        Classify(text, parsedRows).Should().Be(matches
            ? ValidationServiceAdapter.CompletedRunEvidence.ResultsRecorded
            : ValidationServiceAdapter.CompletedRunEvidence.InvalidEvidence);
    }

    private static string OnePassingTrx => EmptyTrx
        .Replace("<Results />", "<Results><UnitTestResult testName=\"one\" outcome=\"Passed\" /></Results>", StringComparison.Ordinal)
        .Replace("total=\"0\" executed=\"0\"", "total=\"1\" executed=\"1\"", StringComparison.Ordinal);

    [Theory]
    [InlineData("total=\"1\"", "total=\"2\"")]
    [InlineData("executed=\"1\"", "executed=\"2\"")]
    [InlineData("failed=\"0\"", "failed=\"1\"")]
    [InlineData("error=\"0\"", "error=\"1\"")]
    [InlineData("aborted=\"0\"", "aborted=\"1\"")]
    [InlineData("outcome=\"Completed\"", "outcome=\"Failed\"")]
    [InlineData("outcome=\"Completed\"", "outcome=\"Aborted\"")]
    public void Populated_results_require_a_consistent_summary(string before, string after)
        => Classify(OnePassingTrx.Replace(before, after, StringComparison.Ordinal), 1)
            .Should().Be(ValidationServiceAdapter.CompletedRunEvidence.InvalidEvidence);

    [Fact]
    public void A_recorded_failure_is_valid_evidence_of_a_failed_test()
    {
        var text = OnePassingTrx.Replace("outcome=\"Passed\"", "outcome=\"Failed\"", StringComparison.Ordinal)
            .Replace("outcome=\"Completed\"", "outcome=\"Failed\"", StringComparison.Ordinal)
            .Replace("failed=\"0\"", "failed=\"1\"", StringComparison.Ordinal);
        Classify(text, 1).Should().Be(ValidationServiceAdapter.CompletedRunEvidence.ResultsRecorded);
    }

    [Theory]
    [InlineData("NotExecuted", 0)]
    [InlineData("Inconclusive", 0)]
    public void Skipped_rows_are_recorded_without_becoming_an_empty_selection(string outcome, int executed)
    {
        var text = OnePassingTrx.Replace("outcome=\"Passed\"", $"outcome=\"{outcome}\"", StringComparison.Ordinal)
            .Replace("executed=\"1\"", $"executed=\"{executed}\"", StringComparison.Ordinal);
        Classify(text, 1).Should().Be(ValidationServiceAdapter.CompletedRunEvidence.ResultsRecorded);
    }

    // VSTest 17.12 counts the parent as well; 18.9 counts only its two data rows.
    [Theory]
    [InlineData(3, 3, true)]
    [InlineData(2, 3, true)]
    [InlineData(3, 1, false)]
    [InlineData(2, 1, false)]
    [InlineData(4, 3, false)]
    public void Nested_results_preserve_both_supported_counter_layouts_and_require_every_parsed_row(
        int countedRows, int parsedRows, bool valid)
    {
        var text = EmptyTrx.Replace("<Results />", """
            <Results>
              <UnitTestResult testName="parent" outcome="Passed" resultType="DataDrivenTest">
                <InnerResults>
                  <UnitTestResult testName="row1" outcome="Passed" />
                  <UnitTestResult testName="row2" outcome="Passed" />
                </InnerResults>
              </UnitTestResult>
            </Results>
            """, StringComparison.Ordinal)
            .Replace("total=\"0\" executed=\"0\"", $"total=\"{countedRows}\" executed=\"{countedRows}\"", StringComparison.Ordinal);
        Classify(text, parsedRows).Should().Be(valid
            ? ValidationServiceAdapter.CompletedRunEvidence.ResultsRecorded
            : ValidationServiceAdapter.CompletedRunEvidence.InvalidEvidence);
    }

    [Fact]
    public void A_file_that_disappeared_after_discovery_is_not_an_empty_selection()
    {
        var missing = new FileInfo(Path.Combine(Path.GetTempPath(), $"missing-evidence-{Guid.NewGuid():N}.trx"));
        ValidationServiceAdapter.ClassifyCompletedRun(missing, 0, "").Should().Be(
            ValidationServiceAdapter.CompletedRunEvidence.InvalidEvidence);
    }

    private static ValidationServiceAdapter.CompletedRunEvidence Classify(string text, int rows, string output = "")
    {
        var path = Path.Combine(Path.GetTempPath(), $"validation-evidence-{Guid.NewGuid():N}.trx");
        try
        {
            File.WriteAllText(path, text);
            return ValidationServiceAdapter.ClassifyCompletedRun(new FileInfo(path), rows, output);
        }
        finally { File.Delete(path); }
    }
}
