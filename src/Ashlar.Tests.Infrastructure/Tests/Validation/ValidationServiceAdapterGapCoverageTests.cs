using Ashlar.Tests.Infrastructure.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Ashlar.Core.Application.Common.Models;
using Ashlar.Core.Application.Validation.Models;
using Ashlar.Infrastructure.Validation.Adapters;
using Ashlar.Infrastructure.Validation.Parsers;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Validation;

/// <summary>Tests for validation service adapter gap coverage.</summary>
[Collection("ProcessCwd")]
public class ValidationServiceAdapterGapCoverageTests
{
    [Fact]
    public async Task ValidateAsync_returns_skipped_result_when_no_test_projects()
    {
        var adapter = CreateAdapter();
        var original = Directory.GetCurrentDirectory();
        var temp = Path.Combine(Path.GetTempPath(), "ashlar-validate-" + Guid.NewGuid());
        Directory.CreateDirectory(temp);

        try
        {
            Directory.SetCurrentDirectory(temp);
            var result = await adapter.ValidateAsync("filter", progress: null, CancellationToken.None);
            result.Passed.Should().BeTrue();
            result.Message.Should().Contain("skipped");
        }
        finally
        {
            Directory.SetCurrentDirectory(original);
            if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public async Task ValidateAsync_reports_progress_when_no_projects_found()
    {
        var adapter = CreateAdapter();
        var original = Directory.GetCurrentDirectory();
        var temp = Path.Combine(Path.GetTempPath(), "ashlar-validate-progress-" + Guid.NewGuid());
        Directory.CreateDirectory(temp);
        var reports = new List<ProgressReport>();

        try
        {
            Directory.SetCurrentDirectory(temp);
            await adapter.ValidateAsync(null, new SyncProgress<ProgressReport>(r => reports.Add(r)), CancellationToken.None);
            reports.Should().NotBeEmpty();
            reports.Should().Contain(r => r.Percentage == 100 || r.Message.Contains("skipped", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.SetCurrentDirectory(original);
            if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public async Task ValidateAsync_counts_build_failure_as_failed_test()
    {
        var adapter = CreateAdapter();
        var original = Directory.GetCurrentDirectory();
        var temp = Path.Combine(Path.GetTempPath(), "ashlar-validate-buildfail-" + Guid.NewGuid());
        Directory.CreateDirectory(temp);
        var csprojDir = Path.Combine(temp, "tests");
        Directory.CreateDirectory(csprojDir);
        await File.WriteAllTextAsync(
            Path.Combine(csprojDir, "BrokenTests.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
              <ItemGroup><Compile Include="Missing.cs" /></ItemGroup>
            </Project>
            """);

        try
        {
            Directory.SetCurrentDirectory(temp);
            var result = await adapter.ValidateAsync("filter", progress: null, CancellationToken.None);
            result.Passed.Should().BeFalse();
            result.TestsFailed.Should().BeGreaterThan(0);
            result.TestResults.Should().Contain(r => !r.Passed && r.Message != null && r.Message.Contains("build failed", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.SetCurrentDirectory(original);
            if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public void Constructor_throws_for_null_dependencies()
    {
        var parser = Mock.Of<ITestResultParser>();
        var act = () => new ValidationServiceAdapter(null!, parser);
        act.Should().Throw<ArgumentNullException>();

        var act2 = () => new ValidationServiceAdapter(NullLogger<ValidationServiceAdapter>.Instance, null!);
        act2.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task ValidateAsync_runs_passing_test_project_and_aggregates_trx_results()
    {
        var parser = new Mock<ITestResultParser>();
        parser.Setup(p => p.ParseAsync(It.IsAny<FileInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new TestResult { Name = "PassTests.Ok", Passed = true },
                new TestResult { Name = "PassTests.Other", Passed = true },
            });

        var adapter = new ValidationServiceAdapter(NullLogger<ValidationServiceAdapter>.Instance, parser.Object);
        var original = Directory.GetCurrentDirectory();
        var temp = CreatePassingTestProjectDir();

        try
        {
            Directory.SetCurrentDirectory(temp);
            var reports = new List<ProgressReport>();
            var result = await adapter.ValidateAsync(
                null,
                new SyncProgress<ProgressReport>(r => reports.Add(r)),
                CancellationToken.None);

            result.Passed.Should().BeTrue();
            result.TestsRun.Should().Be(2);
            result.TestsPassed.Should().Be(2);
            result.TestsFailed.Should().Be(0);
            reports.Should().Contain(r => r.Percentage == 100);
            parser.Verify(p => p.ParseAsync(It.IsAny<FileInfo>(), It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            Directory.SetCurrentDirectory(original);
            if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public async Task ValidateAsync_does_not_count_skipped_tests_as_failures()
    {
        var parser = new Mock<ITestResultParser>();
        parser.Setup(p => p.ParseAsync(It.IsAny<FileInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new TestResult { Name = "PassTests.Ok", Passed = true },
                new TestResult { Name = "PassTests.Gap", Passed = false, Skipped = true, Message = "GAP: not yet" },
            });

        var adapter = new ValidationServiceAdapter(NullLogger<ValidationServiceAdapter>.Instance, parser.Object);
        var original = Directory.GetCurrentDirectory();
        var temp = CreatePassingTestProjectDir();

        try
        {
            Directory.SetCurrentDirectory(temp);
            var result = await adapter.ValidateAsync(null, progress: null, CancellationToken.None);

            result.Passed.Should().BeTrue("a documented skip is not a red test");
            result.TestsRun.Should().Be(1, "skipped tests were not executed");
            result.TestsPassed.Should().Be(1);
            result.TestsFailed.Should().Be(0);
            result.TestsSkipped.Should().Be(1);
            result.Message.Should().Contain("1 skipped");
            result.TestResults.Should().NotContain(r => r.Name == "PassTests.Gap",
                "consumers list every !Passed result as a failure; a skip must not appear there");
        }
        finally
        {
            Directory.SetCurrentDirectory(original);
            if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
        }
    }

    [Theory]
    [InlineData("src/Ashlar.Tests.Domain/Ashlar.Tests.Domain.csproj", true)]
    [InlineData("src/Ashlar.Transport.A2A.Server.Tests/Ashlar.Transport.A2A.Server.Tests.csproj", true)]
    [InlineData("tests/anything/Whatever.csproj", true)]
    [InlineData("tests/adversarial-corpus/fixtures/b2-pinvoke-exit/project/Brick.csproj", false)]
    [InlineData("src/Ashlar.Runtime/Ashlar.Runtime.csproj", false)]
    [InlineData("tools/copy-assemblies.csproj", false)]
    [InlineData("src/Ashlar.Agents.TestKit/Ashlar.Agents.TestKit.csproj", false)]
    [InlineData("samples/templates/brick/__BrickName__Brick.Tests/__BrickName__Brick.Tests.csproj", false)]
    [InlineData("samples/other/__Token__Tests/__Token__Tests.csproj", false)]
    [InlineData(".claude/worktrees/x/src/Ashlar.Tests.Domain/Ashlar.Tests.Domain.csproj", false)]
    [InlineData(".git/some/Tests.csproj", false)]
    [InlineData("src/Foo.Tests/bin/Debug/Foo.Tests.csproj", false)]
    public void Discovery_excludes_templates_placeholders_and_hidden_trees(string relativePath, bool expected)
    {
        // Pure predicate: no files need to exist. Root is a fixed absolute path; the candidate
        // is placed relative to it exactly as GetFiles(AllDirectories) would report it.
        var root = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "ashlar-discovery-root"));
        var candidate = new FileInfo(Path.Combine(root.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar)));

        ValidationServiceAdapter.IsDiscoverableTestProject(candidate, root).Should().Be(expected);
    }

    [Fact]
    public void Discovery_ignores_hidden_or_template_segments_ABOVE_the_root()
    {
        // Running validate from inside a hidden worktree is legitimate; only segments below the
        // root are subject to the hidden/template rules.
        var root = new DirectoryInfo(Path.Combine(Path.GetTempPath(), ".claude", "worktrees", "wt1"));
        var candidate = new FileInfo(Path.Combine(root.FullName, "src", "Ashlar.Tests.Domain", "Ashlar.Tests.Domain.csproj"));

        ValidationServiceAdapter.IsDiscoverableTestProject(candidate, root).Should().BeTrue();
    }

    [Theory]
    [InlineData("<Project><PropertyGroup><TargetFrameworks>net8.0;net9.0</TargetFrameworks></PropertyGroup></Project>", "net8.0")]
    [InlineData("<Project><PropertyGroup><TargetFrameworks>net9.0;net10.0</TargetFrameworks></PropertyGroup></Project>", "net9.0")]
    [InlineData("<Project><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>", "net9.0")]
    [InlineData("<Project><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>", "net8.0")]
    [InlineData("<Project><PropertyGroup><Nullable>enable</Nullable></PropertyGroup></Project>", null)]
    public void SelectTestFramework_runs_one_framework_the_project_actually_declares(string csproj, string? expected)
    {
        // The A2A server test project is net9.0-only (TestHost 8 lacks PipeWriter.UnflushedBytes);
        // forcing --framework net8.0 on it asked VSTest for an output that was never built.
        var path = Path.Combine(Path.GetTempPath(), "ashlar-tfm-" + Guid.NewGuid() + ".csproj");
        File.WriteAllText(path, csproj);
        try
        {
            ValidationServiceAdapter.SelectTestFramework(path).Should().Be(expected);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task ValidateAsync_reports_failed_tests_from_trx_parser()
    {
        var parser = new Mock<ITestResultParser>();
        parser.Setup(p => p.ParseAsync(It.IsAny<FileInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new TestResult { Name = "FailTests.Boom", Passed = false, Message = "assert failed" },
            });

        var adapter = new ValidationServiceAdapter(NullLogger<ValidationServiceAdapter>.Instance, parser.Object);
        var original = Directory.GetCurrentDirectory();
        var temp = CreateFailingTestProjectDir();

        try
        {
            Directory.SetCurrentDirectory(temp);
            var result = await adapter.ValidateAsync(null, progress: null, CancellationToken.None);

            result.Passed.Should().BeFalse();
            result.TestsFailed.Should().BeGreaterThan(0);
            result.Message.Should().Contain("failed");
        }
        finally
        {
            Directory.SetCurrentDirectory(original);
            if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public async Task ValidateAsync_rejects_a_parser_that_drops_recorded_rows()
    {
        var parser = new Mock<ITestResultParser>();
        parser.Setup(p => p.ParseAsync(It.IsAny<FileInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<TestResult>());

        var adapter = new ValidationServiceAdapter(NullLogger<ValidationServiceAdapter>.Instance, parser.Object);
        var original = Directory.GetCurrentDirectory();
        var temp = CreatePassingTestProjectDir();

        try
        {
            Directory.SetCurrentDirectory(temp);
            var result = await adapter.ValidateAsync(null, progress: null, CancellationToken.None);

            result.Passed.Should().BeFalse("the result file contains rows the parser did not return");
            result.TestsRun.Should().Be(0);
            result.TestsPassed.Should().Be(0);
            result.TestsFailed.Should().Be(0);
            result.EvidenceErrors.Should().ContainSingle().Which.Should().ContainAll(
                "PassTests.csproj", "test results are missing, unreadable or inconsistent");
            parser.Verify(p => p.ParseAsync(It.IsAny<FileInfo>(), It.IsAny<CancellationToken>()), Times.Once);

            // Invalid evidence is judged before, and apart from, the zero-execution rule, so a
            // caller's filter - which switches that rule off - cannot turn it into a pass.
            var filtered = await adapter.ValidateAsync("FullyQualifiedName~PassTests", progress: null, CancellationToken.None);
            filtered.Passed.Should().BeFalse("a filter does not excuse unreadable evidence: " + filtered.Message);
            filtered.EvidenceErrors.Should().ContainSingle().Which.Should().ContainAll(
                "PassTests.csproj", "test results are missing, unreadable or inconsistent");
        }
        finally
        {
            Directory.SetCurrentDirectory(original);
            if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public async Task ValidateAsync_returns_error_when_cancelled_with_test_projects()
    {
        var adapter = CreateAdapter();
        var original = Directory.GetCurrentDirectory();
        var temp = Path.Combine(Path.GetTempPath(), "ashlar-validate-cancel-" + Guid.NewGuid());
        Directory.CreateDirectory(temp);
        await File.WriteAllTextAsync(Path.Combine(temp, "SampleTests.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        try
        {
            Directory.SetCurrentDirectory(temp);
            var result = await adapter.ValidateAsync("filter", progress: null, cts.Token);
            result.Passed.Should().BeFalse();
        }
        finally
        {
            Directory.SetCurrentDirectory(original);
            if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public async Task ValidateAsync_counts_parser_failure_as_failed_test()
    {
        var parser = new Mock<ITestResultParser>();
        parser.Setup(p => p.ParseAsync(It.IsAny<FileInfo>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("trx parse failed"));

        var adapter = new ValidationServiceAdapter(NullLogger<ValidationServiceAdapter>.Instance, parser.Object);
        var original = Directory.GetCurrentDirectory();
        var temp = CreatePassingTestProjectDir();

        try
        {
            Directory.SetCurrentDirectory(temp);
            var result = await adapter.ValidateAsync(null, progress: null, CancellationToken.None);

            result.Passed.Should().BeFalse();
            result.TestsFailed.Should().BeGreaterThan(0);
            result.TestResults.Should().Contain(r =>
                !r.Passed && r.Message != null && r.Message.Contains("trx parse failed", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.SetCurrentDirectory(original);
            if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
        }
    }

    private static string CreatePassingTestProjectDir()
    {
        var temp = Path.Combine(Path.GetTempPath(), "ashlar-validate-pass-" + Guid.NewGuid());
        var testsDir = Path.Combine(temp, "tests");
        Directory.CreateDirectory(testsDir);
        File.WriteAllText(Path.Combine(testsDir, "PassTests.csproj"), """
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
        File.WriteAllText(Path.Combine(testsDir, "PassTests.cs"), """
            using Xunit;
            /// <summary>Tests for pass.</summary>
            public class PassTests
            {
                /// <summary>Ok.</summary>
                [Fact] public void Ok() { }
                [Fact] public void Other() { }
            }
            """);
        return temp;
    }

    private static string CreateFailingTestProjectDir()
    {
        var temp = Path.Combine(Path.GetTempPath(), "ashlar-validate-fail-" + Guid.NewGuid());
        var testsDir = Path.Combine(temp, "tests");
        Directory.CreateDirectory(testsDir);
        File.WriteAllText(Path.Combine(testsDir, "FailTests.csproj"), """
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
        File.WriteAllText(Path.Combine(testsDir, "FailTests.cs"), """
            using Xunit;
            /// <summary>Tests for fail.</summary>
            public class FailTests
            {
                /// <summary>Boom.</summary>
                [Fact] public void Boom() => Assert.True(false);
            }
            """);
        return temp;
    }

    [Theory(Timeout = TestTimeouts.E2E)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_or_corrupt_results_fail_without_inventing_a_test(bool corrupt)
    {
        var original = Directory.GetCurrentDirectory();
        var temp = Path.Combine(Path.GetTempPath(), "ashlar-validation-evidence-" + Guid.NewGuid());
        Directory.CreateDirectory(temp);
        var target = corrupt ? """
            <Target Name="WriteCorruptResult" AfterTargets="VSTest">
              <MakeDir Directories="$(MSBuildProjectDirectory)/TestResults" />
              <WriteLinesToFile File="$(MSBuildProjectDirectory)/TestResults/bad.trx" Lines="&lt;broken" Overwrite="true" />
            </Target>
            """ : "";
        File.WriteAllText(Path.Combine(temp, "EvidenceTests.csproj"), $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
              {{target}}
            </Project>
            """);
        try
        {
            Directory.SetCurrentDirectory(temp);
            var adapter = new ValidationServiceAdapter(NullLogger<ValidationServiceAdapter>.Instance,
                new TrxTestResultParser(NullLogger<TrxTestResultParser>.Instance));
            var result = await adapter.ValidateAsync(null);
            result.Passed.Should().BeFalse();
            result.EvidenceErrors.Should().ContainSingle().Which.Should().Contain("EvidenceTests.csproj");
            result.TestsRun.Should().Be(0);
            result.TestsPassed.Should().Be(0);
            result.TestsFailed.Should().Be(0);
            result.TestResults.Should().BeEmpty("an evidence failure is not an executed test");
            if (corrupt) Directory.GetFiles(temp, "*.trx", SearchOption.AllDirectories).Should().ContainSingle();
        }
        finally
        {
            Directory.SetCurrentDirectory(original);
            Directory.Delete(temp, recursive: true);
        }
    }

    /// <summary>
    /// A discovered project whose every test is <c>Category=Stress</c> selects nothing under
    /// validate's default exclusions. Until 2026-09-30 this fact was
    /// <c>An_empty_selection_passes_with_zero_observed_tests</c> and pinned that as a PASS, so an
    /// unfiltered sweep could report green for a project it ran nothing in. It was flipped
    /// deliberately, and the policy it now pins is the conservative one: the project FAILS even
    /// though its empty selection is legitimate, because a sweep that reports "passed" for a
    /// project it executed nothing in is a lane, not a receipt. A future all-Stress or
    /// all-DockerOptional test project must carry at least one test outside those categories (a
    /// fast structural or smoke fact); there is no opt-out marker. A blank filter is no filter,
    /// exactly as it is for the argv validate builds. Only a caller's real filter chooses the
    /// selection, and under one the same empty project still passes.
    /// </summary>
    [Fact(Timeout = TestTimeouts.E2E)]
    public async Task An_unfiltered_sweep_fails_a_project_whose_every_test_is_excluded_by_default()
    {
        var original = Directory.GetCurrentDirectory();
        var temp = CreatePassingTestProjectDir();
        var source = Path.Combine(temp, "tests", "PassTests.cs");
        File.WriteAllText(source, File.ReadAllText(source).Replace("[Fact]", "[Fact, Trait(\"Category\", \"Stress\")]", StringComparison.Ordinal));
        var arranged = File.ReadAllText(source);
        arranged.Should().NotContain("[Fact]", "the arrange step must have tagged every fact");
        Occurrences(arranged, "Trait(\"Category\", \"Stress\")").Should().Be(2, "both fixture facts must be Stress-only");
        try
        {
            Directory.SetCurrentDirectory(temp);
            var adapter = new ValidationServiceAdapter(NullLogger<ValidationServiceAdapter>.Instance,
                new TrxTestResultParser(NullLogger<TrxTestResultParser>.Instance));

            foreach (var unfiltered in new string?[] { null, "   " })
            {
                var because = $"filter {(unfiltered is null ? "null" : "'" + unfiltered + "'")} is no filter";
                var result = await adapter.ValidateAsync(unfiltered);
                result.Passed.Should().BeFalse($"{because}, and the only discovered project executed nothing");
                result.EvidenceErrors.Should().ContainSingle(because).Which.Should().ContainAll(
                    "PassTests.csproj", "executed no tests in an unfiltered sweep", "it selected none");
                result.TestsRun.Should().Be(0, because);
                result.TestsPassed.Should().Be(0, because);
                result.TestsFailed.Should().Be(0, "an empty project is not a failed test");
                result.TestsSkipped.Should().Be(0, because);
                result.TestResults.Should().BeEmpty("an evidence failure is not an executed test");
                result.Message.Should().StartWith(
                    "Validation failed (0/0 tests passed, but 1 project produced no passing evidence); "
                    + "PassTests.csproj: executed no tests in an unfiltered sweep", because);
                result.Message.Should().NotContain("no tests selected", "the project is reported once, as a failure");
            }

            var narrowed = await adapter.ValidateAsync("Category=Stress");
            narrowed.Passed.Should().BeTrue("a caller's filter chose this selection: " + narrowed.Message);
            narrowed.EvidenceErrors.Should().BeEmpty();
            narrowed.TestsRun.Should().Be(0);
            narrowed.Message.Should().Be("Validation passed (0/0 tests); no tests selected: PassTests.csproj");
        }
        finally
        {
            Directory.SetCurrentDirectory(original);
            Directory.Delete(temp, recursive: true);
        }
    }

    /// <summary>
    /// The arm a check keyed on <c>NoTestsSelected</c> is blind to: every selected test is skipped,
    /// so the run records rows, classifies as <c>ResultsRecorded</c>, and executed nothing. The
    /// rule is "executed == 0", not "selected == 0". A project with SOME executed tests beside its
    /// skips still passes (<c>ValidateAsync_does_not_count_skipped_tests_as_failures</c>). This is
    /// also the shape of a suite made only of <c>[OptInFact]</c> tests without their switch, or
    /// only of <c>[NotOnCiFact]</c> tests under <c>CI=true</c>: it fails on the lane where it
    /// executes nothing, and must carry one test that runs there without the dependency.
    /// </summary>
    [Fact(Timeout = TestTimeouts.E2E)]
    public async Task An_unfiltered_sweep_fails_a_project_whose_every_selected_test_is_skipped()
    {
        var original = Directory.GetCurrentDirectory();
        var temp = CreatePassingTestProjectDir();
        var source = Path.Combine(temp, "tests", "PassTests.cs");
        File.WriteAllText(source, File.ReadAllText(source).Replace("[Fact]", "[Fact(Skip = \"documented gap\")]", StringComparison.Ordinal));
        var arranged = File.ReadAllText(source);
        arranged.Should().NotContain("[Fact]", "the arrange step must have skipped every fact");
        Occurrences(arranged, "Skip = \"documented gap\"").Should().Be(2, "both fixture facts must be skipped");
        try
        {
            Directory.SetCurrentDirectory(temp);
            var adapter = new ValidationServiceAdapter(NullLogger<ValidationServiceAdapter>.Instance,
                new TrxTestResultParser(NullLogger<TrxTestResultParser>.Instance));

            var result = await adapter.ValidateAsync(null);
            result.TestsSkipped.Should().Be(2,
                "both rows were recorded as skipped, so this run took the ResultsRecorded arm, not NoTestsSelected: "
                + result.Message);
            result.Passed.Should().BeFalse("the only discovered project executed nothing");
            result.EvidenceErrors.Should().ContainSingle().Which.Should().ContainAll(
                "PassTests.csproj", "executed no tests in an unfiltered sweep", "all 2 of its selected tests were skipped");
            result.TestsRun.Should().Be(0);
            result.TestsFailed.Should().Be(0, "a skipped project is not a failed test");
            result.TestResults.Should().BeEmpty("an evidence failure is not an executed test");
            result.Message.Should().NotContain("no tests selected");

            var narrowed = await adapter.ValidateAsync("FullyQualifiedName~PassTests");
            narrowed.Passed.Should().BeTrue("a caller's filter chose this selection: " + narrowed.Message);
            narrowed.EvidenceErrors.Should().BeEmpty();
            narrowed.TestsSkipped.Should().Be(2);
            narrowed.Message.Should().Be("Validation passed (0/0 tests, 2 skipped)");
        }
        finally
        {
            Directory.SetCurrentDirectory(original);
            Directory.Delete(temp, recursive: true);
        }
    }

    /// <summary>
    /// The other empty-selection arm. <c>ClassifyCompletedRun</c> reaches <c>NoTestsSelected</c>
    /// two ways: a TRX with zero rows (the Stress-only fact above), or NO TRX under the project
    /// directory beside a console line saying the filter matched nothing. VSTest 17.12 writes a
    /// zero-row TRX even for an empty selection, so a fixture that leaves the TRX in place never
    /// reaches the second arm, and a rule tied to TRX presence passed every other fact. Here the
    /// project sends its TRX outside its own directory through <c>VSTestResultsDirectory</c> (the
    /// shape a repository-level Directory.Build.props commonly has), so validate finds none and
    /// must classify the run from the console alone. The zero-execution rule may not depend on
    /// which arm classified the run: unfiltered, the project fails; under a caller's filter the
    /// same empty selection passes and is reported as one.
    /// </summary>
    [Fact(Timeout = TestTimeouts.E2E)]
    public async Task An_unfiltered_sweep_fails_an_empty_selection_that_left_no_trx_in_the_project()
    {
        var original = Directory.GetCurrentDirectory();
        var temp = CreatePassingTestProjectDir();
        var projectDir = Path.Combine(temp, "tests");
        var redirected = Path.Combine(temp, "trx-out");
        var source = Path.Combine(projectDir, "PassTests.cs");
        File.WriteAllText(source, File.ReadAllText(source).Replace("[Fact]", "[Fact, Trait(\"Category\", \"Stress\")]", StringComparison.Ordinal));
        var project = Path.Combine(projectDir, "PassTests.csproj");
        File.WriteAllText(project, File.ReadAllText(project).Replace(
            "<IsPackable>false</IsPackable>",
            "<IsPackable>false</IsPackable><VSTestResultsDirectory>$(MSBuildProjectDirectory)/../trx-out</VSTestResultsDirectory>",
            StringComparison.Ordinal));
        var arrangedSource = File.ReadAllText(source);
        arrangedSource.Should().NotContain("[Fact]", "the arrange step must have tagged every fact");
        Occurrences(arrangedSource, "Trait(\"Category\", \"Stress\")").Should().Be(2, "both fixture facts must be Stress-only");
        Occurrences(File.ReadAllText(project), "<VSTestResultsDirectory>").Should().Be(1,
            "the arrange step must have redirected the project's results directory");
        try
        {
            Directory.SetCurrentDirectory(temp);
            var adapter = new ValidationServiceAdapter(NullLogger<ValidationServiceAdapter>.Instance,
                new TrxTestResultParser(NullLogger<TrxTestResultParser>.Instance));

            var result = await adapter.ValidateAsync(null);
            var redirectedTrx = Directory.Exists(redirected)
                ? Directory.GetFiles(redirected, "*.trx", SearchOption.AllDirectories).Length
                : 0;
            Directory.GetFiles(projectDir, "*.trx", SearchOption.AllDirectories).Should().BeEmpty(
                "the run must leave no TRX where validate looks, or it took the zero-row TRX arm instead "
                + $"({redirectedTrx} TRX file(s) were redirected): {result.Message}");
            result.Passed.Should().BeFalse("filter null is no filter, and the only discovered project executed nothing");
            result.EvidenceErrors.Should().ContainSingle(result.Message).Which.Should().ContainAll(
                "PassTests.csproj", "executed no tests in an unfiltered sweep", "it selected none");
            result.TestsRun.Should().Be(0);
            result.TestsFailed.Should().Be(0, "an empty project is not a failed test");
            result.TestsSkipped.Should().Be(0);
            result.TestResults.Should().BeEmpty("an evidence failure is not an executed test");
            result.Message.Should().NotContain("no tests selected", "the project is reported once, as a failure");

            var narrowed = await adapter.ValidateAsync("Category=Stress");
            Directory.GetFiles(projectDir, "*.trx", SearchOption.AllDirectories).Should().BeEmpty(
                "the filtered run must take the same no-TRX arm: " + narrowed.Message);
            narrowed.Passed.Should().BeTrue("a caller's filter chose this selection: " + narrowed.Message);
            narrowed.EvidenceErrors.Should().BeEmpty();
            narrowed.TestsRun.Should().Be(0);
            narrowed.Message.Should().Be("Validation passed (0/0 tests); no tests selected: PassTests.csproj");
        }
        finally
        {
            Directory.SetCurrentDirectory(original);
            Directory.Delete(temp, recursive: true);
        }
    }

    /// <summary>
    /// The zero-execution rule is judged per project, on that project's OWN executed count. Every
    /// other E2E fixture here discovers exactly one project, where the sweep's running total always
    /// equals that project's count, so a rule judged on the running total (or on the result list,
    /// or only for the first project) passed all of them - and would reopen the gap in a real
    /// many-project sweep for any empty project discovered after one that executed something. Here
    /// an executing project is discovered FIRST and an all-Stress project SECOND, so the total is
    /// already 2 when the empty one is judged, and the sweep must still fail on it. The order is
    /// deterministic: file enumeration yields a directory's own files before it descends into a
    /// subdirectory, and the arrange step asserts that order through the adapter's own discovery.
    /// </summary>
    [Fact(Timeout = TestTimeouts.E2E)]
    public async Task An_unfiltered_sweep_fails_an_empty_project_discovered_after_one_that_executed_tests()
    {
        var original = Directory.GetCurrentDirectory();
        var temp = CreatePassingTestProjectDir();
        var outer = Path.Combine(temp, "tests");
        var inner = Path.Combine(outer, "zz");
        Directory.CreateDirectory(inner);
        var passProject = Path.Combine(outer, "PassTests.csproj");
        var projectXml = File.ReadAllText(passProject);
        File.WriteAllText(Path.Combine(inner, "EmptyTests.csproj"), projectXml);
        File.WriteAllText(passProject, projectXml.Replace(
            "</Project>", "<ItemGroup><Compile Remove=\"zz/**\" /></ItemGroup></Project>", StringComparison.Ordinal));
        var emptySource = Path.Combine(inner, "EmptyTests.cs");
        File.WriteAllText(emptySource, """
            using Xunit;
            public class EmptyTests
            {
                [Fact, Trait("Category", "Stress")] public void OnlyStress() { }
            }
            """);

        var root = new DirectoryInfo(temp);
        root.GetFiles("*.csproj", SearchOption.AllDirectories)
            .Where(f => ValidationServiceAdapter.IsDiscoverableTestProject(f, root))
            .Select(f => f.Name)
            .Should().Equal(new[] { "PassTests.csproj", "EmptyTests.csproj" },
                "the executing project must be discovered first, so a running total is already > 0 when the empty one is judged");
        Occurrences(File.ReadAllText(passProject), "<Compile Remove=\"zz/**\" />").Should().Be(1,
            "the outer project must not compile the inner project's sources");
        var arrangedEmpty = File.ReadAllText(emptySource);
        arrangedEmpty.Should().NotContain("[Fact]", "the inner project's only fact must be tagged");
        Occurrences(arrangedEmpty, "Trait(\"Category\", \"Stress\")").Should().Be(1, "the inner project must be Stress-only");
        try
        {
            Directory.SetCurrentDirectory(temp);
            var adapter = new ValidationServiceAdapter(NullLogger<ValidationServiceAdapter>.Instance,
                new TrxTestResultParser(NullLogger<TrxTestResultParser>.Instance));

            var result = await adapter.ValidateAsync(null);
            result.TestsRun.Should().Be(2, "the first project executed both of its facts: " + result.Message);
            result.TestsPassed.Should().Be(2, result.Message);
            result.TestsFailed.Should().Be(0, "an empty project is not a failed test: " + result.Message);
            result.TestsSkipped.Should().Be(0, result.Message);
            result.TestResults.Should().NotBeNull();
            result.TestResults!.Select(test => test.Name).Should().BeEquivalentTo(
                new[] { "PassTests.Ok", "PassTests.Other" }, "only the outer project executed anything");
            result.Passed.Should().BeFalse(
                "EmptyTests.csproj executed nothing in an unfiltered sweep, whatever ran before it: " + result.Message);
            result.EvidenceErrors.Should().ContainSingle(result.Message).Which.Should().StartWith(
                "EmptyTests.csproj: executed no tests in an unfiltered sweep (it selected none");
            result.Message.Should().StartWith(
                "Validation failed (2/2 tests passed, but 1 project produced no passing evidence); "
                + "EmptyTests.csproj: executed no tests in an unfiltered sweep");
            result.Message.Should().NotContain("no tests selected", "the empty project is reported once, as a failure");
        }
        finally
        {
            Directory.SetCurrentDirectory(original);
            Directory.Delete(temp, recursive: true);
        }
    }

    [Theory]
    [InlineData(null, 0, 0, true)]
    [InlineData("", 0, 0, true)]
    [InlineData("   ", 0, 0, true)]
    [InlineData(" \t\r\n", 0, 0, true)]
    [InlineData(null, 0, 1, true)]
    [InlineData(null, 0, 7, true)]
    [InlineData("", 0, 3, true)]
    [InlineData(null, 1, 0, false)]
    [InlineData(null, 1, 5, false)]
    [InlineData("Category=First", 0, 0, false)]
    [InlineData("Category=First", 0, 3, false)]
    public void Only_an_unfiltered_project_that_executed_nothing_is_an_evidence_error(
        string? filter, int executed, int skipped, bool fails)
    {
        var error = ValidationServiceAdapter.ZeroExecutionError("Some.Tests.csproj", filter, executed, skipped);
        if (fails)
            error.Should().NotBeNull().And.StartWith("Some.Tests.csproj: executed no tests in an unfiltered sweep (");
        else
            error.Should().BeNull();
    }

    [Theory]
    [InlineData(0, "it selected none, and validate always excludes Category=Stress and Category=DockerOptional")]
    [InlineData(1, "its only selected test was skipped")]
    [InlineData(4, "all 4 of its selected tests were skipped")]
    public void The_zero_execution_error_says_whether_nothing_was_selected_or_everything_was_skipped(
        int skipped, string reason)
        => ValidationServiceAdapter.ZeroExecutionError("Some.Tests.csproj", null, 0, skipped).Should().Be(
            $"Some.Tests.csproj: executed no tests in an unfiltered sweep ({reason}); "
            + "a discovered test project that runs nothing is not evidence of passing.");

    [Fact]
    public void An_evidence_only_failure_does_not_headline_as_zero_tests_failed()
    {
        var zero = ValidationServiceAdapter.ZeroExecutionError("HelloBrick.Tests.csproj", null, 0, 0)!;
        var message = ValidationServiceAdapter.DescribeOutcome(
            false, 5703, 5703, 0, 13, new[] { zero }, Array.Empty<string>());
        message.Should().Be(
            "Validation failed (5703/5703 tests passed, 13 skipped, but 1 project produced no passing evidence); " + zero);
        message.Should().NotContain("tests failed");

        ValidationServiceAdapter.DescribeOutcome(false, 2, 2, 0, 0, new[] { "A: x", "B: y" }, Array.Empty<string>())
            .Should().Be("Validation failed (2/2 tests passed, but 2 projects produced no passing evidence); A: x; B: y");
    }

    [Fact]
    public void A_failed_test_still_headlines_the_failed_count_and_a_pass_lists_empty_selections()
    {
        ValidationServiceAdapter.DescribeOutcome(false, 10, 9, 1, 2, Array.Empty<string>(), Array.Empty<string>())
            .Should().Be("Validation failed (1/10 tests failed, 2 skipped)");
        ValidationServiceAdapter.DescribeOutcome(false, 10, 9, 1, 0, new[] { "A: x" }, Array.Empty<string>())
            .Should().Be("Validation failed (1/10 tests failed); A: x");
        ValidationServiceAdapter.DescribeOutcome(true, 3, 3, 0, 0, Array.Empty<string>(), new[] { "A.csproj", "B.csproj" })
            .Should().Be("Validation passed (3/3 tests); no tests selected: A.csproj, B.csproj");
    }

    private static int Occurrences(string text, string token)
        => (text.Length - text.Replace(token, string.Empty, StringComparison.Ordinal).Length) / token.Length;

    [Fact(Timeout = TestTimeouts.E2E)]
    public async Task Caller_filter_selects_observed_results_and_preserves_default_exclusions()
    {
        var original = Directory.GetCurrentDirectory();
        var temp = CreatePassingTestProjectDir();
        File.WriteAllText(Path.Combine(temp, "tests", "PassTests.cs"), """
            using Xunit;
            public class FilterTests
            {
                [Fact, Trait("Category", "First"), Trait("Label", "(literal)")] public void First() { }
                [Fact, Trait("Category", "Second")] public void Second() { }
                [Fact] public void Unselected() => Assert.True(false, "caller filter was ignored");
                [Fact, Trait("Category", "First"), Trait("Category", "Stress")]
                public void Stress() => Assert.True(false, "Stress must stay excluded");
                [Fact, Trait("Category", "Second"), Trait("Category", "DockerOptional")]
                public void Docker() => Assert.True(false, "DockerOptional must stay excluded");
            }
            """);
        try
        {
            Directory.SetCurrentDirectory(temp);
            var adapter = new ValidationServiceAdapter(NullLogger<ValidationServiceAdapter>.Instance,
                new TrxTestResultParser(NullLogger<TrxTestResultParser>.Instance));

            var invalid = await adapter.ValidateAsync("Category=First)|Category=Stress|(Category=Second");
            invalid.Passed.Should().BeFalse();
            invalid.Message.Should().Contain("balanced, unescaped parentheses");
            invalid.TestsRun.Should().Be(0, "malformed filters are refused before building or running a project");
            invalid.TestsFailed.Should().Be(0);

            var broad = await adapter.ValidateAsync(null);
            broad.EvidenceErrors.Should().BeEmpty();
            broad.Passed.Should().BeFalse("the positive control executes the unselected failing test");
            broad.TestsRun.Should().Be(3);
            broad.TestsFailed.Should().Be(1);
            broad.TestResults.Should().NotBeNull();
            broad.TestResults!.Select(test => test.Name).Should().BeEquivalentTo(
                "FilterTests.First", "FilterTests.Second", "FilterTests.Unselected");

            var narrowed = await adapter.ValidateAsync("Category=First|Category=Second");
            narrowed.EvidenceErrors.Should().BeEmpty();
            narrowed.Passed.Should().BeTrue(narrowed.Message);
            narrowed.TestsRun.Should().Be(2);
            narrowed.TestsPassed.Should().Be(2);
            narrowed.TestsFailed.Should().Be(0);
            narrowed.TestResults.Should().NotBeNull();
            narrowed.TestResults!.Select(test => test.Name).Should().BeEquivalentTo(
                "FilterTests.First", "FilterTests.Second");

            var escaped = await adapter.ValidateAsync(@"Label=\(literal\)");
            escaped.Passed.Should().BeTrue(escaped.Message);
            escaped.TestsRun.Should().Be(1);
            escaped.TestResults.Should().ContainSingle().Which.Name.Should().Be("FilterTests.First");

            var empty = await adapter.ValidateAsync("FullyQualifiedName~NoSuchTest");
            empty.EvidenceErrors.Should().BeEmpty();
            empty.Passed.Should().BeTrue(empty.Message);
            empty.TestsRun.Should().Be(0);
            empty.TestResults.Should().BeEmpty();
            empty.Message.Should().Contain("no tests selected");
        }
        finally
        {
            Directory.SetCurrentDirectory(original);
            Directory.Delete(temp, recursive: true);
        }
    }

    private static ValidationServiceAdapter CreateAdapter()
    {
        var parser = new Mock<ITestResultParser>();
        parser.Setup(p => p.ParseAsync(It.IsAny<FileInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<TestResult>());
        /// <summary>Validation service adapter.</summary>
        return new ValidationServiceAdapter(NullLogger<ValidationServiceAdapter>.Instance, parser.Object);
    }
}
