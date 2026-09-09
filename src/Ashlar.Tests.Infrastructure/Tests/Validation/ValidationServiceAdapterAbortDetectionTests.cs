using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Ashlar.Core.Application.Common.Models;
using Ashlar.Infrastructure.Validation.Adapters;
using Ashlar.Infrastructure.Validation.Parsers;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Validation;

/// <summary>
/// #566: <c>ashlar validate</c> judged a project on its TRX alone. When the Blame collector
/// killed a hung host, the results captured before the kill were all green, the tests
/// scheduled after it never ran, and the project was reported PASSED. These tests feed the
/// exact console output <c>dotnet test</c> prints in that case (and its neighbours) through
/// <see cref="ValidationServiceAdapter.DetectAbortedTestRun"/> and pin the verdict.
///
/// The synthetic console text lives in constants rather than Theory data on purpose: xunit
/// prints Theory display names — parameters included — to the console, and validate scans
/// that console when it runs this very project.
/// </summary>
[Collection("ProcessCwd")]
public class ValidationServiceAdapterAbortDetectionTests
{
    // Verbatim from run 33998995522 (Ashlar.Tests.Infrastructure, hang-killed on all three
    // platforms, reported green).
    private const string HangKilledOutput = """
          Passed Ashlar.Tests.Infrastructure.Tests.Validation.Some.Test [12 ms]
          Passed Ashlar.Tests.Infrastructure.Tests.Validation.Some.Other [3 ms]
        Data collector 'Blame' message: The specified inactivity time of 2 minutes has elapsed. Collecting hang dumps from testhost and its child processes.
        Data collector 'Blame' message: Hang dump collection is disabled (--blame-hang-dump-type none).
        The active test run was aborted. Reason: Test host process crashed
        Test Run Aborted.
        Total tests: Unknown
             Passed: 2
         Total time: 2.4 Minutes
        C:\Program Files\dotnet\sdk\10.0.100\Microsoft.TestPlatform.targets(48,5): error MSB4181: The "VSTestTask" task returned false but did not log an error.

        Build FAILED.
        """;

    private const string CleanPassingOutput = """
        Test run for /work/src/Ashlar.Tests.Domain/bin/Debug/net8.0/Ashlar.Tests.Domain.dll (.NETCoreApp,Version=v8.0)
        VSTest version 17.12.0 (x64)
        Starting test execution, please wait...
        A total of 1 test files matched the specified pattern.
        Results File: /work/src/Ashlar.Tests.Domain/TestResults/runner_2026-09-09.trx

        Passed!  - Failed:     0, Passed:   412, Skipped:     3, Total:   415, Duration: 31 s - Ashlar.Tests.Domain.dll (net8.0)
        """;

    private const string WarningsOnlyOutput = """
        Node.js 20 actions are deprecated. For more information see: https://github.blog/changelog/
        /work/src/Ashlar.Tests.Domain/Ashlar.Tests.Domain.csproj : warning NU1603: Ashlar depends on X (>= 1.0.0) but X 1.0.0 was not found.
        warning MSB3277: Found conflicts between different versions of "System.Text.Json" that could not be resolved.
        Passed!  - Failed:     0, Passed:    12, Skipped:     0, Total:    12, Duration: 1 s
        """;

    private const string OrdinaryFailureOutput = """
          Failed FailTests.Boom [5 ms]
          Error Message:
           Assert.True() Failure
        Results File: /work/tests/TestResults/x.trx

        Failed!  - Failed:     1, Passed:     0, Skipped:     0, Total:     1, Duration: 5 ms
        """;

    private const string NoTestsMatchedOutput = """
        Starting test execution, please wait...
        A total of 1 test files matched the specified pattern.
        No test matches the given testcase filter `Category!=DockerOptional&Category!=Stress` in /work/tests/bin/Debug/net8.0/Empty.dll
        """;

    private const string CrashWithNamedTestOutput = """
        The active test run was aborted. Reason: Test host process crashed : Unhandled exception. System.AccessViolationException
        The test running when the crash occurred:
        Ashlar.Tests.Infrastructure.Tests.Adaptation.MutantLoadTests.Unloads_collectible_context
        Test Run Aborted.
        """;

    private static readonly IReadOnlyList<TestResult> ThreeGreen = new[]
    {
        new TestResult { Name = "A", Passed = true },
        new TestResult { Name = "B", Passed = true },
        new TestResult { Name = "C", Passed = true },
    };

    [Fact]
    public void Hang_killed_run_is_a_failure_that_names_the_last_running_test()
    {
        using var sequence = SequenceFile.Write(
            "Ashlar.Tests.Infrastructure.Tests.Validation.Some.Test",
            "Ashlar.Tests.Infrastructure.Tests.Validation.Some.Other",
            "Ashlar.Tests.Infrastructure.Tests.Adaptation.Hanging.ForeverTest");

        var abort = ValidationServiceAdapter.DetectAbortedTestRun(
            exitCode: 1, HangKilledOutput, trxFound: true, ThreeGreen, sequence.File);

        abort.Should().NotBeNull("a green TRX from a run the Blame collector cut short is not a pass");
        abort!.Reason.Should().Contain("The specified inactivity time of 2 minutes has elapsed",
            "the first abort line the console printed is the reason");
        abort.LastRunningTest.Should().Be("Ashlar.Tests.Infrastructure.Tests.Adaptation.Hanging.ForeverTest",
            "the last <Test> in the Blame Sequence file is the one that hung");
        abort.ResultsRecorded.Should().Be(3);
        abort.Describe().Should().Contain("last running test: Ashlar.Tests.Infrastructure.Tests.Adaptation.Hanging.ForeverTest");
        abort.Describe().Should().Contain("3 result(s) were recorded before the run ended");
    }

    [Fact]
    public void Clean_exit_is_trusted_even_when_the_console_carries_second_hand_abort_lines()
    {
        // vstest fails the VSTestTask whenever it aborts, so a 0 exit cannot follow a real
        // abort — but abort lines can: this project's own validate tests drive a nested
        // `dotnet test` and echo its stderr. Those must not fail the outer run.
        ValidationServiceAdapter.DetectAbortedTestRun(
                exitCode: 0, HangKilledOutput, trxFound: true, ThreeGreen, sequenceFile: null)
            .Should().BeNull();
    }

    [Fact]
    public void Hang_killed_run_without_a_named_test_still_fails_with_the_console_reason()
    {
        var abort = ValidationServiceAdapter.DetectAbortedTestRun(
            exitCode: 1, HangKilledOutput, trxFound: true, ThreeGreen, sequenceFile: null);

        abort.Should().NotBeNull();
        abort!.LastRunningTest.Should().BeNull("nothing named the test");
        abort.Describe().Should().NotContain("last running test");
        abort.Describe().Should().Contain("3 result(s) were recorded before the run ended");
    }

    [Fact]
    public void Console_named_crash_test_wins_over_the_sequence_file()
    {
        using var sequence = SequenceFile.Write("Ignored.Because.Console.Said");

        var abort = ValidationServiceAdapter.DetectAbortedTestRun(
            exitCode: 1, CrashWithNamedTestOutput, trxFound: true, ThreeGreen, sequence.File);

        abort.Should().NotBeNull();
        abort!.LastRunningTest.Should().Be("Ashlar.Tests.Infrastructure.Tests.Adaptation.MutantLoadTests.Unloads_collectible_context");
    }

    [Fact]
    public void Crash_test_named_on_the_same_line_is_also_read()
    {
        var lines = new[]
        {
            "The test running when the crash occurred: Ns.Cls.Method",
            "Test Run Aborted.",
        };
        ValidationServiceAdapter.ExtractLastRunningTest(lines).Should().Be("Ns.Cls.Method");
        ValidationServiceAdapter.ExtractLastRunningTest(new[] { "Passed! - Failed: 0" }).Should().BeNull();
    }

    [Fact]
    public void Clean_exit_with_a_green_trx_is_a_pass()
    {
        ValidationServiceAdapter.DetectAbortedTestRun(
                exitCode: 0, CleanPassingOutput, trxFound: true, ThreeGreen, sequenceFile: null)
            .Should().BeNull();
    }

    [Fact]
    public void Warnings_never_trip_the_detector()
    {
        ValidationServiceAdapter.DetectAbortedTestRun(
                exitCode: 0, WarningsOnlyOutput, trxFound: true, ThreeGreen, sequenceFile: null)
            .Should().BeNull("Node.js deprecation and MSB/NU warnings are noise, not aborts");
    }

    [Fact]
    public void Ordinary_failing_tests_are_left_to_the_trx()
    {
        var oneRed = new[]
        {
            new TestResult { Name = "FailTests.Boom", Passed = false, Message = "Assert.True() Failure" },
        };

        ValidationServiceAdapter.DetectAbortedTestRun(
                exitCode: 1, OrdinaryFailureOutput, trxFound: true, oneRed, sequenceFile: null)
            .Should().BeNull("the TRX already names the failure; a second entry would double-report it");
    }

    [Fact]
    public void Filter_matching_no_tests_stays_a_pass_whatever_the_exit_code()
    {
        var none = Array.Empty<TestResult>();

        ValidationServiceAdapter.DetectAbortedTestRun(
                exitCode: 1, NoTestsMatchedOutput, trxFound: false, none, sequenceFile: null)
            .Should().BeNull("a project with nothing to run under the filter is not a failed project");
        ValidationServiceAdapter.DetectAbortedTestRun(
                exitCode: 0, NoTestsMatchedOutput, trxFound: true, none, sequenceFile: null)
            .Should().BeNull();
    }

    [Fact]
    public void Nonzero_exit_with_a_green_trx_and_no_console_evidence_is_still_a_failure()
    {
        // The run ended before it could fail a test, and vstest said nothing we recognise:
        // the exit code is the only witness, and it says the run did not complete.
        var abort = ValidationServiceAdapter.DetectAbortedTestRun(
            exitCode: 1, "Build FAILED.", trxFound: true, ThreeGreen, sequenceFile: null);

        abort.Should().NotBeNull();
        abort!.Reason.Should().Be("dotnet test exited 1 but the TRX records no failed test");
        abort.ResultsRecorded.Should().Be(3);
    }

    [Fact]
    public void Nonzero_exit_without_a_trx_is_a_failure_that_says_so()
    {
        var abort = ValidationServiceAdapter.DetectAbortedTestRun(
            exitCode: 1, "error: test source file was not found", trxFound: false, Array.Empty<TestResult>(), sequenceFile: null);

        abort.Should().NotBeNull();
        abort!.Reason.Should().Be("dotnet test exited 1 and wrote no TRX for this run");
    }

    [Fact]
    public void Abort_phrases_quoted_inside_a_test_line_do_not_count()
    {
        // A Theory display name or a test's own output may quote the phrases the detector looks
        // for; only a line the console logger itself starts with one of them is an abort. The
        // run here is red for an ordinary reason (exit 1, one failed test in the TRX), so the
        // quoted phrases are the only thing that could add an abort entry.
        const string quoted = """
              Passed Detector_flags(line: "Test Run Aborted.") [1 ms]
              Passed Detector_flags(line: "The active test run was aborted. Reason: x") [1 ms]
              Passed Detector_flags(line: "Data collector 'Blame' message: The specified inactivity time of 2 minutes") [1 ms]
              Failed FailTests.Boom [5 ms]
            Failed!  - Failed:     1, Passed:     3, Skipped:     0, Total:     4, Duration: 8 ms
            """;
        var oneRed = new[]
        {
            new TestResult { Name = "FailTests.Boom", Passed = false, Message = "Assert.True() Failure" },
        };

        ValidationServiceAdapter.DetectAbortedTestRun(
                exitCode: 1, quoted, trxFound: true, oneRed, sequenceFile: null)
            .Should().BeNull();
    }

    [Fact]
    public void Sequence_file_that_is_missing_or_malformed_yields_no_name()
    {
        ValidationServiceAdapter.ReadLastTestFromSequenceFile(null).Should().BeNull();
        ValidationServiceAdapter.ReadLastTestFromSequenceFile(
            new FileInfo(Path.Combine(Path.GetTempPath(), "Sequence_" + Guid.NewGuid() + ".xml"))).Should().BeNull();

        var broken = Path.Combine(Path.GetTempPath(), "Sequence_" + Guid.NewGuid() + ".xml");
        File.WriteAllText(broken, "<TestSequence><Test Name=\"unterminated");
        try
        {
            ValidationServiceAdapter.ReadLastTestFromSequenceFile(new FileInfo(broken)).Should().BeNull();
        }
        finally
        {
            File.Delete(broken);
        }

        using var empty = SequenceFile.Write();
        ValidationServiceAdapter.ReadLastTestFromSequenceFile(empty.File).Should().BeNull();
    }

    [Fact]
    public async Task ValidateAsync_reports_a_project_whose_host_dies_mid_run_as_failed_not_N_of_N()
    {
        // The TRX parser is stubbed to what a killed run leaves behind: the one result that
        // reported before the host went down, green. Before #566 this project was PASSED 1/1.
        var parser = new Mock<ITestResultParser>();
        parser.Setup(p => p.ParseAsync(It.IsAny<FileInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new TestResult { Name = "CrashTests.Ok", Passed = true } });

        var adapter = new ValidationServiceAdapter(NullLogger<ValidationServiceAdapter>.Instance, parser.Object);
        var original = Directory.GetCurrentDirectory();
        var originalError = Console.Error;
        var temp = CreateHostKillingTestProjectDir();

        try
        {
            // The adapter echoes the nested run's stderr; keep its abort lines out of this
            // host's console so an outer validate scanning it does not read them as its own.
            Console.SetError(TextWriter.Null);
            Directory.SetCurrentDirectory(temp);
            var result = await adapter.ValidateAsync(null, progress: null, CancellationToken.None);

            result.Passed.Should().BeFalse("the host died before every test ran");
            result.TestsFailed.Should().BeGreaterThanOrEqualTo(1);
            result.TestsRun.Should().Be(result.TestsPassed + result.TestsFailed,
                "the aggregate must not read as N/N passed");
            result.TestResults.Should().Contain(r =>
                !r.Passed &&
                r.Name.Contains("(test run aborted)") &&
                r.Message != null &&
                (r.Message.Contains("did not finish the run") || r.Message.Contains("dotnet test exited")));
            result.Message.Should().StartWith("Validation failed");
        }
        finally
        {
            Console.SetError(originalError);
            Directory.SetCurrentDirectory(original);
            if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
        }
    }

    /// <summary>
    /// A test project with one green test and one that takes the host down with it, the way a
    /// hang-kill or an unhandled native fault does — <c>dotnet test</c> exits non-zero and the
    /// console carries the vstest abort lines.
    /// </summary>
    private static string CreateHostKillingTestProjectDir()
    {
        var temp = Path.Combine(Path.GetTempPath(), "ashlar-validate-hostkill-" + Guid.NewGuid());
        var testsDir = Path.Combine(temp, "tests");
        Directory.CreateDirectory(testsDir);
        File.WriteAllText(Path.Combine(testsDir, "CrashTests.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
                <IsPackable>false</IsPackable>
                <ImplicitUsings>enable</ImplicitUsings>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.12.0" />
                <PackageReference Include="xunit" Version="2.9.3" />
                <PackageReference Include="xunit.runner.visualstudio" Version="3.0.0">
                  <PrivateAssets>all</PrivateAssets>
                  <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
                </PackageReference>
              </ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(testsDir, "CrashTests.cs"), """
            using Xunit;
            /// <summary>Tests whose host dies mid-run.</summary>
            public class CrashTests
            {
                /// <summary>Ok.</summary>
                [Fact] public void Ok() { }
                /// <summary>Takes the test host down.</summary>
                [Fact] public void TakesHostDown() => System.Environment.Exit(134);
            }
            """);
        return temp;
    }

    /// <summary>A Blame-collector <c>Sequence_*.xml</c> in the temp directory, deleted on dispose.</summary>
    private sealed class SequenceFile : IDisposable
    {
        public FileInfo File { get; }

        private SequenceFile(FileInfo file) => File = file;

        public static SequenceFile Write(params string[] testNames)
        {
            var path = Path.Combine(Path.GetTempPath(), "Sequence_" + Guid.NewGuid() + ".xml");
            var tests = string.Join("\n", testNames.Select(n => $"  <Test Name=\"{n}\" Source=\"/work/tests.dll\" />"));
            System.IO.File.WriteAllText(path, $"<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<TestSequence>\n{tests}\n</TestSequence>\n");
            return new SequenceFile(new FileInfo(path));
        }

        public void Dispose()
        {
            try { System.IO.File.Delete(File.FullName); } catch { /* best effort */ }
        }
    }
}
