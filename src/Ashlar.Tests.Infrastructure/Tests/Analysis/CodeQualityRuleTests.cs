using Microsoft.Extensions.Logging;
using Moq;
using Ashlar.Core.Application.Analysis.Models;
using Ashlar.Core.Application.Testing.Abstractions;
using Ashlar.Core.Application.Testing.Models;
using Ashlar.Infrastructure.Analysis.Rules;
using Ashlar.Tests.Application.Helpers;
using Ashlar.Abstractions;

namespace Ashlar.Tests.Infrastructure.Tests.Analysis;

/// <summary>Tests for code quality rule.</summary>
public class CodeQualityRuleTests : UnitTestBase
{
    private DirectoryInfo? _tempDir;

    public override Task SetupAsync(CancellationToken cancellationToken = default)
    {
        _tempDir = TestHelpers.CreateTempDirectory();
        return Task.CompletedTask;
    }

    public override Task CleanupAsync(CancellationToken cancellationToken = default)
    {
        if (_tempDir != null)
        {
            TestHelpers.CleanupTempDirectory(_tempDir);
        }
        return Task.CompletedTask;
    }

    public override async Task<TestResult> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            /// <summary>Test rule properties.</summary>
            TestRuleProperties();
            /// <summary>Test exception handling.</summary>
            await TestExceptionHandling();
            /// <summary>Test with non existent file.</summary>
            await TestWithNonExistentFile();

            return new TestResult
            {
                Name = nameof(CodeQualityRuleTests),
                Category = "Infrastructure",
                Passed = true,
                Message = "All CodeQualityRule tests passed"
            };
        }
        catch (AssertionException ex)
        {
            return new TestResult
            {
                Name = nameof(CodeQualityRuleTests),
                Category = "Infrastructure",
                Passed = false,
                ErrorMessage = $"Assertion failed: {ex.Message}",
                StackTrace = ex.StackTrace
            };
        }
        catch (Exception ex)
        {
            return new TestResult
            {
                Name = nameof(CodeQualityRuleTests),
                Category = "Infrastructure",
                Passed = false,
                ErrorMessage = $"Unexpected exception: {ex.Message}",
                StackTrace = ex.StackTrace
            };
        }
    }

    private void TestRuleProperties()
    {
        var mockLogger = new Mock<ILogger<CodeQualityRule>>();
        var mockTool = CreateMockAnalyzeTool();
        var rule = new CodeQualityRule(mockLogger.Object, mockTool.Object);

        /// <summary>Assert equal.</summary>
        AssertEqual("CodeQuality", rule.Name);
        /// <summary>Assert equal.</summary>
        /// <param name="metrics"">Metrics".</param>
        AssertEqual("Analyzes code quality metrics", rule.Description);
    }

    private async Task TestExceptionHandling()
    {
        var mockLogger = new Mock<ILogger<CodeQualityRule>>();
        var mockTool = CreateMockAnalyzeTool();
        var rule = new CodeQualityRule(mockLogger.Object, mockTool.Object);

        // Use a non-existent file - the tool will fail
        var nonExistentFile = new FileInfo(Path.Combine(_tempDir!.FullName, "nonexistent.dll"));

        // The rule should catch exceptions and return empty violations (CodeQualityRule doesn't add error violations)
        var violations = await rule.AnalyzeAsync(nonExistentFile, CancellationToken.None);

        /// <summary>Assert not null.</summary>
        AssertNotNull(violations);
        // CodeQualityRule catches exceptions but doesn't add violations for errors
        // So violations should be empty
        AssertEqual(0, violations.Count);
    }

    private async Task TestWithNonExistentFile()
    {
        var mockLogger = new Mock<ILogger<CodeQualityRule>>();
        var mockTool = CreateMockAnalyzeTool();
        var rule = new CodeQualityRule(mockLogger.Object, mockTool.Object);

        // Test with a file that doesn't exist
        var file = new FileInfo(Path.Combine(_tempDir!.FullName, "test.dll"));

        // Should handle gracefully and return empty violations
        var violations = await rule.AnalyzeAsync(file, CancellationToken.None);

        /// <summary>Assert not null.</summary>
        AssertNotNull(violations);
        // Should not throw, even if file doesn't exist
    }

    private static Mock<ITool> CreateMockAnalyzeTool()
    {
        var mock = new Mock<ITool>();
        mock.Setup(t => t.Id).Returns("assembly.analyze");
        mock.Setup(t => t.InvokeAsync(It.IsAny<ToolCall>(), It.IsAny<WorldSnapshot>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ToolResult(new ActionDelta(0, 0, Array.Empty<string>()), System.Text.Json.JsonSerializer.SerializeToElement(new { Complexity = 5 })));
        return mock;
    }
}

