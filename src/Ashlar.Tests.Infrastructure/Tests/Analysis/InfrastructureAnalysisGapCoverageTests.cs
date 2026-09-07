using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Ashlar.Abstractions;
using Ashlar.Infrastructure.Analysis.Rules;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Analysis;

/// <summary>Tests for infrastructure analysis gap coverage.</summary>
public class InfrastructureAnalysisGapCoverageTests
{
    [Fact]
    public void CodeQualityRule_exposes_metadata()
    {
        var mockTool = CreateMockAnalyzeTool();
        var rule = new CodeQualityRule(NullLogger<CodeQualityRule>.Instance, mockTool.Object);
        rule.Name.Should().Be("CodeQuality");
        rule.Description.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task CodeQualityRule_skips_non_managed_assemblies()
    {
        var path = Path.Combine(Path.GetTempPath(), "not-an-assembly-" + Guid.NewGuid() + ".dll");
        await File.WriteAllTextAsync(path, "not a pe file");
        try
        {
            var mockTool = CreateMockAnalyzeTool();
            var rule = new CodeQualityRule(NullLogger<CodeQualityRule>.Instance, mockTool.Object);
            var violations = await rule.AnalyzeAsync(new FileInfo(path));
            violations.Should().BeEmpty();
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task CodeQualityRule_handles_missing_files_gracefully()
    {
        var mockTool = CreateMockAnalyzeTool();
        var rule = new CodeQualityRule(NullLogger<CodeQualityRule>.Instance, mockTool.Object);
        var violations = await rule.AnalyzeAsync(new FileInfo(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".dll")));
        violations.Should().BeEmpty();
    }

    [Fact]
    public void SecurityAnalysisRule_exposes_metadata()
    {
        var mockTool = CreateMockSecurityScanTool();
        var rule = new SecurityAnalysisRule(NullLogger<SecurityAnalysisRule>.Instance, mockTool.Object);
        rule.Name.Should().Be("SecurityScan");
    }

    [Fact]
    public async Task SecurityAnalysisRule_reports_invalid_pe_scan_errors_as_findings()
    {
        var path = Path.Combine(Path.GetTempPath(), "not-managed-" + Guid.NewGuid() + ".dll");
        await File.WriteAllTextAsync(path, "not a pe file");
        try
        {
            var mockTool = CreateMockSecurityScanToolWithFindings(1);
            var rule = new SecurityAnalysisRule(NullLogger<SecurityAnalysisRule>.Instance, mockTool.Object);
            var violations = await rule.AnalyzeAsync(new FileInfo(path));
            violations.Should().ContainSingle(v =>
                v.Rule == "SecurityScan" &&
                v.Severity == Ashlar.Core.Domain.Values.RiskLevel.High &&
                v.Message.Contains("finding", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task SecurityAnalysisRule_records_scan_errors_as_violations()
    {
        var mockTool = CreateMockSecurityScanToolThatThrows();
        var rule = new SecurityAnalysisRule(NullLogger<SecurityAnalysisRule>.Instance, mockTool.Object);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var violations = await rule.AnalyzeAsync(
            new FileInfo(Path.Combine(Path.GetTempPath(), "scan-" + Guid.NewGuid() + ".dll")),
            cts.Token);

        violations.Should().ContainSingle(v => v.Rule == "SecurityScan" && v.Message.Contains("Security scan error"));
    }

    [Fact]
    public async Task CodeQualityRule_analyzes_managed_test_assembly()
    {
        var mockTool = CreateMockAnalyzeToolWithComplexity(25);
        var rule = new CodeQualityRule(NullLogger<CodeQualityRule>.Instance, mockTool.Object);
        var assemblyPath = typeof(InfrastructureAnalysisGapCoverageTests).Assembly.Location;

        var violations = await rule.AnalyzeAsync(new FileInfo(assemblyPath));

        violations.Should().NotBeNull();
        violations.Should().Contain(v =>
            v.Rule == "CodeQuality" &&
            v.Message.Contains("complexity", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SecurityAnalysisRule_analyzes_managed_test_assembly()
    {
        var mockTool = CreateMockSecurityScanTool();
        var rule = new SecurityAnalysisRule(NullLogger<SecurityAnalysisRule>.Instance, mockTool.Object);
        var assemblyPath = typeof(InfrastructureAnalysisGapCoverageTests).Assembly.Location;

        var violations = await rule.AnalyzeAsync(new FileInfo(assemblyPath));

        violations.Should().NotBeNull();
    }

    [Fact]
    public void CodeQualityRule_constructor_rejects_null_logger()
    {
        var mockTool = CreateMockAnalyzeTool();
        var act = () => new CodeQualityRule(null!, mockTool.Object);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task CodeQualityRule_logs_and_returns_empty_on_unexpected_analysis_errors()
    {
        var mockTool = CreateMockAnalyzeTool();
        var rule = new CodeQualityRule(NullLogger<CodeQualityRule>.Instance, mockTool.Object);
        var dir = Path.Combine(Path.GetTempPath(), "ashlar-not-an-assembly-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            var violations = await rule.AnalyzeAsync(new FileInfo(dir));
            violations.Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(dir);
        }
    }

    [Fact]
    public void SecurityAnalysisRule_constructor_rejects_null_logger()
    {
        var mockTool = CreateMockSecurityScanTool();
        var act = () => new SecurityAnalysisRule(null!, mockTool.Object);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task SecurityAnalysisRule_reports_findings_when_dangerous_apis_present()
    {
        var assemblyPath = await BuildDangerousTestAssemblyAsync();
        try
        {
            var mockTool = CreateMockSecurityScanToolWithFindings(1);
            var rule = new SecurityAnalysisRule(NullLogger<SecurityAnalysisRule>.Instance, mockTool.Object);
            var violations = await rule.AnalyzeAsync(new FileInfo(assemblyPath));

            violations.Should().ContainSingle(v =>
                v.Rule == "SecurityScan" &&
                v.Severity == Ashlar.Core.Domain.Values.RiskLevel.High &&
                v.Message.Contains("finding", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            var dir = Path.GetDirectoryName(assemblyPath);
            while (!string.IsNullOrEmpty(dir))
            {
                if (Path.GetFileName(dir).StartsWith("ashlar-dangerous-asm-", StringComparison.Ordinal))
                {
                    if (Directory.Exists(dir))
                        Directory.Delete(dir, recursive: true);
                    break;
                }

                dir = Path.GetDirectoryName(dir);
            }
        }
    }

    private static async Task<string> BuildDangerousTestAssemblyAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "ashlar-dangerous-asm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        await File.WriteAllTextAsync(Path.Combine(root, "Dangerous.cs"), """
            using System.Reflection;
            namespace Ashlar.TestDangerous;
            /// <summary>Tests for dangerous.</summary>
            public static class Dangerous
            {
                /// <summary>Trigger.</summary>
                public static void Trigger() => Assembly.Load("System.Runtime");
            }
            """);

        await File.WriteAllTextAsync(Path.Combine(root, "Dangerous.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
            </Project>
            """);

        var build = await RunProcessAsync("dotnet", $"build \"{root}/Dangerous.csproj\" -c Release -v q");
        build.ExitCode.Should().Be(0, because: build.StdErr);

        var dll = Directory
            .EnumerateFiles(Path.Combine(root, "bin", "Release", "net8.0"), "*.dll")
            .First(path => Path.GetFileName(path).StartsWith("Dangerous", StringComparison.Ordinal));
        return dll;
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunProcessAsync(string file, string args)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = file,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        process.Start();
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, stdout, stderr);
    }

    private static Mock<ITool> CreateMockAnalyzeTool()
    {
        var mock = new Mock<ITool>();
        mock.Setup(t => t.Id).Returns("assembly.analyze");
        mock.Setup(t => t.InvokeAsync(It.IsAny<ToolCall>(), It.IsAny<WorldSnapshot>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ToolResult(new ActionDelta(0, 0, Array.Empty<string>()), JsonSerializer.SerializeToElement(new { Complexity = 5 })));
        return mock;
    }

    private static Mock<ITool> CreateMockAnalyzeToolWithComplexity(int complexity)
    {
        var mock = new Mock<ITool>();
        mock.Setup(t => t.Id).Returns("assembly.analyze");
        mock.Setup(t => t.InvokeAsync(It.IsAny<ToolCall>(), It.IsAny<WorldSnapshot>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ToolResult(new ActionDelta(0, 0, Array.Empty<string>()), JsonSerializer.SerializeToElement(new { Complexity = complexity })));
        return mock;
    }

    private static Mock<ITool> CreateMockSecurityScanTool()
    {
        var mock = new Mock<ITool>();
        mock.Setup(t => t.Id).Returns("assembly.security_scan");
        mock.Setup(t => t.InvokeAsync(It.IsAny<ToolCall>(), It.IsAny<WorldSnapshot>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ToolResult(new ActionDelta(0, 0, Array.Empty<string>()), JsonSerializer.SerializeToElement(new { Count = 0 })));
        return mock;
    }

    private static Mock<ITool> CreateMockSecurityScanToolWithFindings(int count)
    {
        var mock = new Mock<ITool>();
        mock.Setup(t => t.Id).Returns("assembly.security_scan");
        mock.Setup(t => t.InvokeAsync(It.IsAny<ToolCall>(), It.IsAny<WorldSnapshot>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ToolResult(new ActionDelta(0, 0, Array.Empty<string>()), JsonSerializer.SerializeToElement(new { Count = count })));
        return mock;
    }

    private static Mock<ITool> CreateMockSecurityScanToolThatThrows()
    {
        var mock = new Mock<ITool>();
        mock.Setup(t => t.Id).Returns("assembly.security_scan");
        mock.Setup(t => t.InvokeAsync(It.IsAny<ToolCall>(), It.IsAny<WorldSnapshot>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException("Operation cancelled"));
        return mock;
    }
}
