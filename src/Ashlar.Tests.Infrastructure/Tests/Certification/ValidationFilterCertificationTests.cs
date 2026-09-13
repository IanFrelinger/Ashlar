using Ashlar.Infrastructure.Validation.Adapters;
using FluentAssertions;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>Checks the argv handed to dotnet without spawning a process in cert-gate.</summary>
public sealed class ValidationFilterCertificationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n")]
    public void An_absent_filter_retains_the_default_exclusions(string? filter)
    {
        var start = ValidationServiceAdapter.CreateDotnetTestStartInfo(ProjectPath, null, filter, false);
        start.ArgumentList.Should().Equal(
            "test", ProjectPath, "--no-build", "--filter", "Category!=DockerOptional&Category!=Stress",
            "--logger", "trx", "--blame-hang-timeout", "900s", "--blame-hang-dump-type", "none",
            "--verbosity", "minimal");
    }

    [Theory]
    [InlineData("Category=First|Category=Second")]
    [InlineData("Name~with spaces")]
    [InlineData("Name~a\" --logger html \"b")]
    [InlineData("Name~back\\slash\"value")]
    [InlineData("Name~literal\\(text\\)")]
    [InlineData("((Category=First)|(Category=Second))")]
    [InlineData("Name~slash\\\\\\(text\\)")]
    public void A_caller_filter_is_grouped_and_remains_one_argument(string filter)
    {
        var start = ValidationServiceAdapter.CreateDotnetTestStartInfo(ProjectPath, "net8.0", filter, true);
        start.Arguments.Should().BeEmpty("dotnet receives separately escaped argv values");
        start.ArgumentList.Should().Equal(
            "test", ProjectPath, "--framework", "net8.0", "--no-build", "--filter",
            $"(Category!=DockerOptional&Category!=Stress)&({filter})",
            "--logger", "trx", "--blame-hang-timeout", "900s", "--blame-hang-dump-type", "none",
            "--verbosity", "normal");
        start.FileName.Should().Be("dotnet");
        start.UseShellExecute.Should().BeFalse();
        start.RedirectStandardOutput.Should().BeTrue();
        start.RedirectStandardError.Should().BeTrue();
    }

    [Fact]
    public void Project_and_framework_values_cannot_add_process_options()
    {
        var project = Path.Combine(Path.GetTempPath(), "quoted \"path\"", "Some Tests.csproj");
        const string framework = "net8.0\" --logger html \"";
        var build = ValidationServiceAdapter.CreateDotnetBuildStartInfo(project);
        build.Arguments.Should().BeEmpty();
        build.ArgumentList.Should().Equal("build", project, "--verbosity", "quiet");
        build.WorkingDirectory.Should().Be(Path.GetDirectoryName(project));
        var test = ValidationServiceAdapter.CreateDotnetTestStartInfo(project, framework, null, false);
        test.ArgumentList[1].Should().Be(project);
        test.ArgumentList[3].Should().Be(framework);
        test.ArgumentList.Should().HaveCount(15);
        test.WorkingDirectory.Should().Be(build.WorkingDirectory);
    }

    [Theory]
    [InlineData("Category=First)|Category=Stress|(Category=Second")]
    [InlineData("(Category=First")]
    [InlineData("Category=First)")]
    [InlineData("Name~slash\\\\)")]
    [InlineData("Name~unfinished\\")]
    public void Malformed_grouping_cannot_escape_the_default_exclusions(string filter)
    {
        var create = () => ValidationServiceAdapter.CreateDotnetTestStartInfo(ProjectPath, null, filter, false);
        create.Should().Throw<ArgumentException>().WithParameterName("filter");
    }

    private static string ProjectPath => Path.Combine(Path.GetTempPath(), "filter probes", "Some Tests.csproj");
}
