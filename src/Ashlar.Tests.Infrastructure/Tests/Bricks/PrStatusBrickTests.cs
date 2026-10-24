using Microsoft.Extensions.Logging;
using Moq;
using Ashlar.Core.Application.Testing.Abstractions;
using Ashlar.Core.Application.Testing.Models;
using Ashlar.Core.Domain.Bricks;
using Ashlar.Core.Domain.Execution;
using Ashlar.Bricks.GitHub.GitHub;
using ExecutionContext = Ashlar.Infrastructure.Execution.ExecutionContext;

namespace Ashlar.Tests.Infrastructure.Tests.Bricks;

/// <summary>
/// Tests for PrStatusBrick.
/// NOTE: Integration tests requiring real GitHub access are in separate suite.
/// </summary>
public class PrStatusBrickTests : UnitTestBase
{
    public override async Task<TestResult> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await TestBrickProperties();
            await TestInterfaceDefinition();
            await TestUrlParsing();
            
            return new TestResult
            {
                Name = nameof(PrStatusBrickTests),
                Category = "Infrastructure",
                Passed = true,
                Message = "All PrStatusBrick structure tests passed"
            };
        }
        catch (AssertionException ex)
        {
            return new TestResult
            {
                Name = nameof(PrStatusBrickTests),
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
                Name = nameof(PrStatusBrickTests),
                Category = "Infrastructure",
                Passed = false,
                ErrorMessage = $"Unexpected exception: {ex.Message}",
                StackTrace = ex.StackTrace
            };
        }
    }
    
    private async Task TestBrickProperties()
    {
        var mockLogger = new Mock<ILogger<PrStatusBrick>>();
        var brick = new PrStatusBrick(mockLogger.Object);
        
        AssertEqual("github.pr-status", brick.Id);
        AssertEqual("GitHub PR Status", brick.Name);
        AssertEqual("1.0.0", brick.Version);
        AssertEqual(BrickCategory.Analysis, brick.Category);
        AssertNotNull(brick.Description);
        
        await Task.CompletedTask;
    }
    
    private async Task TestInterfaceDefinition()
    {
        var mockLogger = new Mock<ILogger<PrStatusBrick>>();
        var brick = new PrStatusBrick(mockLogger.Object);
        
        // Verify inputs
        AssertNotNull(brick.Interface.Inputs);
        AssertEqual(2, brick.Interface.Inputs.Count);
        
        var prUrlInput = brick.Interface.Inputs.FirstOrDefault(i => i.Name == "prUrl");
        AssertNotNull(prUrlInput, "prUrl input should be defined");
        AssertEqual("string", prUrlInput!.Type);
        AssertTrue(prUrlInput.Required);
        
        var tokenInput = brick.Interface.Inputs.FirstOrDefault(i => i.Name == "githubToken");
        AssertNotNull(tokenInput, "githubToken input should be defined");
        AssertEqual("string", tokenInput!.Type);
        AssertFalse(tokenInput.Required); // Optional for rate limits
        
        // Verify outputs
        AssertNotNull(brick.Interface.Outputs);
        AssertEqual(5, brick.Interface.Outputs.Count);
        
        var expectedOutputs = new[] { "state", "mergeable", "blockers", "checks", "reviews" };
        foreach (var expectedOutput in expectedOutputs)
        {
            var output = brick.Interface.Outputs.FirstOrDefault(o => o.Name == expectedOutput);
            AssertNotNull(output, $"Output '{expectedOutput}' should be defined");
        }
        
        await Task.CompletedTask;
    }
    
    private async Task TestUrlParsing()
    {
        var mockLogger = new Mock<ILogger<PrStatusBrick>>();
        var brick = new PrStatusBrick(mockLogger.Object);
        
        // Test valid URL parsing via reflection (ParsePrUrl is private)
        var parseMethod = typeof(PrStatusBrick).GetMethod(
            "ParsePrUrl",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        
        AssertNotNull(parseMethod, "ParsePrUrl method should exist");
        
        var validUrl = "https://github.com/IanFrelinger/Ashlar/pull/846";
        var result = parseMethod!.Invoke(brick, new object[] { validUrl });
        AssertNotNull(result);
        
        // Verify invalid URL throws
        var invalidUrl = "https://github.com/owner/repo/issues/123";
        try
        {
            parseMethod.Invoke(brick, new object[] { invalidUrl });
            throw new AssertionException("Should have thrown ArgumentException for invalid URL");
        }
        catch (System.Reflection.TargetInvocationException ex)
        {
            AssertTrue(ex.InnerException is ArgumentException, 
                "Should throw ArgumentException for invalid PR URL");
        }
        
        await Task.CompletedTask;
    }
}
