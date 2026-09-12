using FluentAssertions;
using Ashlar.Infrastructure.Analysis.BrickAnalyzer;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Analysis;

/// <summary>
/// Tests for dot net regression test runner.
///
/// <para>These are hermetic on purpose. The test that used to live here drove
/// <c>RunAsync</c> at <c>Ashlar.sln</c> with <c>--no-build</c>, which re-entered this very test
/// assembly from a second process: measured in the devtest container it spawned a vstest host for
/// every test assembly and target framework in a 68-project solution, and on a tree that was not
/// fully built it printed <c>The argument .../Ashlar.Tests.Kernel.dll is invalid</c> for eight
/// assemblies and STILL PASSED, because <c>NotBeNull</c> / <c>&gt;= 0</c> / <c>&gt;= 0</c> /
/// <c>NotBeNullOrEmpty</c> are all satisfied by the runner's catch-all 0/0 result. It could not
/// fail on a product regression; it could only fail by hanging, and it was positioned to trip the
/// outer <c>--blame-hang-timeout</c> and abort the whole run. What is actually worth testing is
/// the transcript parser, and that is what these assert -- on OUTPUT, with exact counts.</para>
/// </summary>
public sealed class DotNetRegressionTestRunnerTests
{
    [Fact]
    public async Task RunAsync_WithNonExistentPath_ReturnsFailed()
    {
        var runner = new DotNetRegressionTestRunner(null);
        var result = await runner.RunAsync("/nonexistent/path/to/solution.sln");

        result.AllPassed.Should().BeFalse();
        result.Summary.Should().Contain("not found");
    }

    [Fact]
    public void ParseDotnetTestOutput_WithPassingSummary_ReadsPassedCount()
    {
        var output = string.Join(
            Environment.NewLine,
            "  Determining projects to restore...",
            "Passed!  - Failed:     0, Passed:    12, Skipped:     0, Total:    12, Duration: 42 ms");

        var (passed, failed) = DotNetRegressionTestRunner.ParseDotnetTestOutput(output);

        passed.Should().Be(12);
        failed.Should().Be(0);
    }

    [Fact]
    public void ParseDotnetTestOutput_WithFailingSummary_ReadsFailedCount()
    {
        var output = string.Join(
            Environment.NewLine,
            "  Failed BrickDecomposerTests.Decomposes [3 ms]",
            "Failed!  - Failed:     3, Passed:     9, Skipped:     1, Total:    13, Duration: 51 ms");

        var (passed, failed) = DotNetRegressionTestRunner.ParseDotnetTestOutput(output);

        failed.Should().Be(3);

        // Pinning current behaviour, and it is a wart: the loose fallback only runs when BOTH
        // counts came back zero, so a failing run reports its failures and loses its passes. The
        // count is not used for anything today beyond the Summary string, and AllPassed -- the one
        // field callers act on -- is already false here. Left alone rather than changed under a
        // test-hygiene pass; a test now says what it does.
        passed.Should().Be(0, "the fallback lane is gated on failed == 0 && passed == 0");
    }

    [Fact]
    public void ParseDotnetTestOutput_WithBareCounts_FallsBackToTheLooseForm()
    {
        // No "Passed!" / "Failed!" banner -- the shape older SDKs and some loggers emit.
        var output = "Total tests: 20. Passed: 18. Failed: 2. Skipped: 0.";

        var (passed, failed) = DotNetRegressionTestRunner.ParseDotnetTestOutput(output);

        passed.Should().Be(18);
        failed.Should().Be(2);
    }

    [Theory]
    [InlineData("")]
    [InlineData("MSBUILD : error MSB1003: Specify a project or solution file.")]
    [InlineData("No test is available in /tmp/x.dll. Make sure that test discovery is installed.")]
    public void ParseDotnetTestOutput_WithNoSummary_ReportsZeroZero(string output)
    {
        // Zero/zero is what the runner then reports as a *result*, which is exactly why
        // asserting `PassedCount >= 0` on a real run proves nothing.
        var (passed, failed) = DotNetRegressionTestRunner.ParseDotnetTestOutput(output);

        passed.Should().Be(0);
        failed.Should().Be(0);
    }
}
