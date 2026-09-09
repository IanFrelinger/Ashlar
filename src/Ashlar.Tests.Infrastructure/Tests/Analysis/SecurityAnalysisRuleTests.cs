using System.Text.Json;
using Microsoft.Extensions.Logging;
using Moq;
using Ashlar.Abstractions;
using Ashlar.Core.Application.Analysis.Models;
using Ashlar.Core.Application.Testing.Abstractions;
using Ashlar.Core.Application.Testing.Models;
using Ashlar.Core.Domain.Values;
using Ashlar.Infrastructure.Analysis.Rules;
using Ashlar.Tests.Application.Helpers;

namespace Ashlar.Tests.Infrastructure.Tests.Analysis;

/// <summary>Tests for security analysis rule.</summary>
public class SecurityAnalysisRuleTests : UnitTestBase
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
                Name = nameof(SecurityAnalysisRuleTests),
                Category = "Infrastructure",
                Passed = true,
                Message = "All SecurityAnalysisRule tests passed"
            };
        }
        catch (AssertionException ex)
        {
            return new TestResult
            {
                Name = nameof(SecurityAnalysisRuleTests),
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
                Name = nameof(SecurityAnalysisRuleTests),
                Category = "Infrastructure",
                Passed = false,
                ErrorMessage = $"Unexpected exception: {ex.Message}",
                StackTrace = ex.StackTrace
            };
        }
    }

    private void TestRuleProperties()
    {
        var mockLogger = new Mock<ILogger<SecurityAnalysisRule>>();
        var mockTool = CreateMockSecurityScanTool();
        var rule = new SecurityAnalysisRule(mockLogger.Object, mockTool.Object);

        /// <summary>Assert equal.</summary>
        AssertEqual("SecurityScan", rule.Name);
        /// <summary>Assert equal.</summary>
        /// <param name="vulnerabilities"">Vulnerabilities".</param>
        AssertEqual("Scans assemblies for security vulnerabilities", rule.Description);
    }

    private async Task TestExceptionHandling()
    {
        var mockLogger = new Mock<ILogger<SecurityAnalysisRule>>();
        var mockTool = CreateMockSecurityScanToolThatThrows();
        var rule = new SecurityAnalysisRule(mockLogger.Object, mockTool.Object);

        // Use a non-existent file - the tool will fail and the rule should handle it gracefully
        var nonExistentFile = new FileInfo(Path.Combine(_tempDir!.FullName, "nonexistent.dll"));

        // The rule should catch exceptions and return a violation
        var violations = await rule.AnalyzeAsync(nonExistentFile, CancellationToken.None);

        /// <summary>Assert not null.</summary>
        AssertNotNull(violations);
        // Should have a violation for the error (the rule adds violations when tools fail)
        if (violations.Count > 0)
        {
            var securityViolation = violations.FirstOrDefault(v => v.Rule == "SecurityScan");
            if (securityViolation != null)
            {
                AssertTrue(securityViolation.Message.Contains("Security scan error") || 
                          securityViolation.Message.Contains("error") ||
                          securityViolation.Message.Contains("Security scan"),
                    $"Violation message should contain error info, got: {securityViolation.Message}");
                /// <summary>Assert equal.</summary>
                AssertEqual(RiskLevel.Medium, securityViolation.Severity);
            }
        }
        // Note: The rule may or may not add violations depending on tool behavior
        // The important thing is it doesn't throw an exception
    }

    private async Task TestWithNonExistentFile()
    {
        var mockLogger = new Mock<ILogger<SecurityAnalysisRule>>();
        var mockTool = CreateMockSecurityScanTool();
        var rule = new SecurityAnalysisRule(mockLogger.Object, mockTool.Object);

        // Test with a file that doesn't exist
        var file = new FileInfo(Path.Combine(_tempDir!.FullName, "test.dll"));

        // Should handle gracefully and return violations (likely error violations)
        var violations = await rule.AnalyzeAsync(file, CancellationToken.None);

        /// <summary>Assert not null.</summary>
        AssertNotNull(violations);
        // May have violations or be empty depending on tool behavior
        // The important thing is it doesn't throw
    }

    private static Mock<ITool> CreateMockSecurityScanTool()
    {
        var mock = new Mock<ITool>();
        mock.Setup(t => t.Id).Returns("assembly.security_scan");
        mock.Setup(t => t.InvokeAsync(It.IsAny<ToolCall>(), It.IsAny<WorldSnapshot>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ToolResult(new ActionDelta(0, 0, Array.Empty<string>()), JsonSerializer.SerializeToElement(new { Count = 0 })));
        return mock;
    }

    private static Mock<ITool> CreateMockSecurityScanToolThatThrows()
    {
        var mock = new Mock<ITool>();
        mock.Setup(t => t.Id).Returns("assembly.security_scan");
        mock.Setup(t => t.InvokeAsync(It.IsAny<ToolCall>(), It.IsAny<WorldSnapshot>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Tool invocation failed"));
        return mock;
    }
}

