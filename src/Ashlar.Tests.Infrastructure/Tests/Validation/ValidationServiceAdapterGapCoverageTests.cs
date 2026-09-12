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
                "FullyQualifiedName~Ok",
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
            result.EvidenceErrors.Should().ContainSingle().Which.Should().Contain("PassTests.csproj");
            parser.Verify(p => p.ParseAsync(It.IsAny<FileInfo>(), It.IsAny<CancellationToken>()), Times.Once);
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

    [Fact(Timeout = TestTimeouts.E2E)]
    public async Task An_empty_selection_passes_with_zero_observed_tests()
    {
        var original = Directory.GetCurrentDirectory();
        var temp = CreatePassingTestProjectDir();
        var source = Path.Combine(temp, "tests", "PassTests.cs");
        File.WriteAllText(source, File.ReadAllText(source).Replace("[Fact]", "[Fact, Trait(\"Category\", \"Stress\")]", StringComparison.Ordinal));
        try
        {
            Directory.SetCurrentDirectory(temp);
            var adapter = new ValidationServiceAdapter(NullLogger<ValidationServiceAdapter>.Instance,
                new TrxTestResultParser(NullLogger<TrxTestResultParser>.Instance));
            var result = await adapter.ValidateAsync(null);
            result.Passed.Should().BeTrue();
            result.EvidenceErrors.Should().BeEmpty();
            result.TestsRun.Should().Be(0);
            result.TestsPassed.Should().Be(0);
            result.TestsFailed.Should().Be(0);
            result.TestsSkipped.Should().Be(0);
            result.Message.Should().Contain("no tests selected");
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
