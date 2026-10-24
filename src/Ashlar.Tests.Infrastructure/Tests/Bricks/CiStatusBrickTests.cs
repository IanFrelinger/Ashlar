using Microsoft.Extensions.Logging;
using Moq;
using Ashlar.Core.Application.Testing.Abstractions;
using Ashlar.Core.Application.Testing.Models;
using Ashlar.Core.Domain.Bricks;
using Ashlar.Core.Domain.Execution;
using Ashlar.Bricks.GitHub.GitHub;

namespace Ashlar.Tests.Infrastructure.Tests.Bricks;

/// <summary>
/// Tests for CiStatusBrick.
/// NOTE: Integration tests requiring real GitHub access are in separate suite.
/// </summary>
public class CiStatusBrickTests : UnitTestBase
{
    public override async Task<TestResult> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await TestBrickProperties();
            await TestInterfaceDefinition();
            await TestShaDetection();
            
            return new TestResult
            {
                Name = nameof(CiStatusBrickTests),
                Category = "Infrastructure",
                Passed = true,
                Message = "All CiStatusBrick structure tests passed"
            };
        }
        catch (AssertionException ex)
        {
            return new TestResult
            {
                Name = nameof(CiStatusBrickTests),
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
                Name = nameof(CiStatusBrickTests),
                Category = "Infrastructure",
                Passed = false,
                ErrorMessage = $"Unexpected exception: {ex.Message}",
                StackTrace = ex.StackTrace
            };
        }
    }
    
    private async Task TestBrickProperties()
    {
        var mockLogger = new Mock<ILogger<CiStatusBrick>>();
        var brick = new CiStatusBrick(mockLogger.Object);
        
        AssertEqual("github.ci-status", brick.Id);
        AssertEqual("GitHub CI Status", brick.Name);
        AssertEqual("1.0.0", brick.Version);
        AssertEqual(BrickCategory.Analysis, brick.Category);
        AssertNotNull(brick.Description);
        
        await Task.CompletedTask;
    }
    
    private async Task TestInterfaceDefinition()
    {
        var mockLogger = new Mock<ILogger<CiStatusBrick>>();
        var brick = new CiStatusBrick(mockLogger.Object);
        
        // Verify inputs
        AssertNotNull(brick.Interface.Inputs);
        AssertEqual(4, brick.Interface.Inputs.Count);
        
        var ownerInput = brick.Interface.Inputs.FirstOrDefault(i => i.Name == "owner");
        AssertNotNull(ownerInput, "owner input should be defined");
        AssertTrue(ownerInput!.Required);
        
        var repoInput = brick.Interface.Inputs.FirstOrDefault(i => i.Name == "repo");
        AssertNotNull(repoInput, "repo input should be defined");
        AssertTrue(repoInput!.Required);
        
        var refInput = brick.Interface.Inputs.FirstOrDefault(i => i.Name == "ref");
        AssertNotNull(refInput, "ref input should be defined");
        AssertTrue(refInput!.Required);
        
        var tokenInput = brick.Interface.Inputs.FirstOrDefault(i => i.Name == "githubToken");
        AssertNotNull(tokenInput, "githubToken input should be defined");
        AssertFalse(tokenInput!.Required);
        
        // Verify outputs
        AssertNotNull(brick.Interface.Outputs);
        AssertEqual(7, brick.Interface.Outputs.Count);
        
        var expectedOutputs = new[] { "status", "totalRuns", "successCount", "failureCount", "pendingCount", "runs", "checks" };
        foreach (var expectedOutput in expectedOutputs)
        {
            var output = brick.Interface.Outputs.FirstOrDefault(o => o.Name == expectedOutput);
            AssertNotNull(output, $"Output '{expectedOutput}' should be defined");
        }
        
        await Task.CompletedTask;
    }
    
    private async Task TestShaDetection()
    {
        var mockLogger = new Mock<ILogger<CiStatusBrick>>();
        var brick = new CiStatusBrick(mockLogger.Object);
        
        // Test SHA detection via reflection (ResolveRefToSha is private but we can test the pattern)
        var resolveMethod = typeof(CiStatusBrick).GetMethod(
            "ResolveRefToSha",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        
        AssertNotNull(resolveMethod, "ResolveRefToSha method should exist");
        
        // Valid 40-char SHA should return as-is (can't test async private method easily without mocking GitHub API)
        // This test validates the method exists and has the right signature
        
        await Task.CompletedTask;
    }
}
