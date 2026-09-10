using System.IO;
using System.Text.Json;
using FluentAssertions;
using Ashlar.Abstractions;
using Ashlar.Policies.Dev;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Policies;

/// <summary>
/// Unit tests for PathAllowlist policy — ensures file writes and search/replace
/// are restricted to src/ and tests/ directories.
/// </summary>
[Trait("Category", "Unit")]
public sealed class PathAllowlistTests
{
    private readonly PathAllowlist _policy = new();
    private static readonly WorldSnapshot EmptySnapshot = new(0, new Dictionary<string, object?>());

    private static ToolCall CreateToolCall(string toolId, string path)
    {
        var json = JsonSerializer.SerializeToElement(new { path });
        return new ToolCall(toolId, json);
    }

    [Fact]
    public void Approve_RepoFsWrite_WithSrcPath_ReturnsTrue()
    {
        var call = CreateToolCall("repo.fs.write", "src/foo.cs");
        var result = _policy.Approve(call, EmptySnapshot, out var reason);

        result.Should().BeTrue();
        reason.Should().Be("OK");
    }

    [Fact]
    public void Approve_RepoFsWrite_WithTestsPath_ReturnsTrue()
    {
        var call = CreateToolCall("repo.fs.write", "tests/bar.cs");
        var result = _policy.Approve(call, EmptySnapshot, out var reason);

        result.Should().BeTrue();
        reason.Should().Be("OK");
    }

    [Fact]
    public void Approve_RepoFsWrite_WithSrcSubPath_ReturnsTrue()
    {
        var call = CreateToolCall("repo.fs.write", "application/src/Ashlar.CLI/Program.cs");
        var result = _policy.Approve(call, EmptySnapshot, out var reason);

        result.Should().BeTrue();
        reason.Should().Be("OK");
    }

    [Fact]
    public void Approve_RepoFsWrite_WithTestsSubPath_ReturnsTrue()
    {
        var call = CreateToolCall("repo.fs.write", "tests/unit/MyTests.cs");
        var result = _policy.Approve(call, EmptySnapshot, out var reason);

        result.Should().BeTrue();
        reason.Should().Be("OK");
    }

    [Fact]
    public void Approve_RepoFsWrite_WithDocsPath_ReturnsTrue()
    {
        var call = CreateToolCall("repo.fs.write", "docs/README.md");
        var result = _policy.Approve(call, EmptySnapshot, out var reason);

        result.Should().BeTrue("docs/ is allowed for documentation writes (P1.2)");
        reason.Should().Be("OK");
    }

    [Fact]
    public void Approve_RepoFsWrite_WithRootPath_ReturnsFalse()
    {
        var call = CreateToolCall("repo.fs.write", "Makefile");
        var result = _policy.Approve(call, EmptySnapshot, out var reason);

        result.Should().BeFalse();
        reason.Should().Contain("Path not allowed");
    }

    [Fact]
    public void Approve_RepoFsWrite_WithGitHubPath_ReturnsFalse()
    {
        var call = CreateToolCall("repo.fs.write", ".github/workflows/ci.yml");
        var result = _policy.Approve(call, EmptySnapshot, out var reason);

        result.Should().BeFalse();
        reason.Should().Contain("Path not allowed");
    }

    [Fact]
    public void Approve_RepoFsSearchReplace_WithSrcPath_ReturnsTrue()
    {
        var call = CreateToolCall("repo.fs.search_replace", "src/foo.cs");
        var result = _policy.Approve(call, EmptySnapshot, out var reason);

        result.Should().BeTrue();
        reason.Should().Be("OK");
    }

    [Fact]
    public void Approve_RepoFsSearchReplace_WithDocsPath_ReturnsTrue()
    {
        var call = CreateToolCall("repo.fs.search_replace", "docs/Architecture.md");
        var result = _policy.Approve(call, EmptySnapshot, out var reason);

        result.Should().BeTrue("docs/ is allowed for documentation writes (P1.2)");
        reason.Should().Be("OK");
    }

    [Fact]
    public void Approve_OtherTool_ReturnsTrue()
    {
        var json = JsonSerializer.SerializeToElement(new { path = "docs/foo.md" });
        var call = new ToolCall("other.tool", json);
        var result = _policy.Approve(call, EmptySnapshot, out var reason);

        result.Should().BeTrue();
        reason.Should().Be("OK");
    }

    [Fact]
    public void Approve_RepoFsWrite_WithBackslashPath_NormalizesAndAllows()
    {
        var call = CreateToolCall("repo.fs.write", "src\\foo.cs");
        var result = _policy.Approve(call, EmptySnapshot, out var reason);

        result.Should().BeTrue();
        reason.Should().Be("OK");
    }

    [Fact]
    public void Approve_RepoFsWrite_WithPathTraversal_ReturnsFalse()
    {
        var call = CreateToolCall("repo.fs.write", "src/../bin/foo.dll");
        var result = _policy.Approve(call, EmptySnapshot, out var reason);

        result.Should().BeFalse();
        reason.Should().Contain("path traversal");
    }

    [Fact]
    public void Approve_RepoFsWrite_WithAbsolutePathOutsideProject_ReturnsFalse()
    {
        var absolutePath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ashlar-test-outside-project"));
        var json = JsonSerializer.SerializeToElement(new { path = absolutePath });
        var call = new ToolCall("repo.fs.write", json);
        var result = _policy.Approve(call, EmptySnapshot, out var reason);

        result.Should().BeFalse();
        reason.Should().Contain("absolute path");
    }

    [Fact]
    public void Approve_RepoFsWrite_WithSandboxRootConfigured_AllowsPathUnderSandboxRoot()
    {
        var sandboxRoot = Path.Combine(Path.GetTempPath(), "ashlar-sandbox");
        var fullPath = Path.GetFullPath(Path.Combine(sandboxRoot, "workspaces/project-a/generated/file.cs"));
        var json = JsonSerializer.SerializeToElement(new { path = fullPath });
        var call = new ToolCall("repo.fs.write", json);
        var snapshot = new WorldSnapshot(0, new Dictionary<string, object?> { ["SandboxRoot"] = sandboxRoot });

        var result = _policy.Approve(call, snapshot, out var reason);

        result.Should().BeTrue();
        reason.Should().Be("OK");
    }

    [Fact]
    public void Approve_RepoFsWrite_WithSandboxRootConfigured_RejectsEscapePath()
    {
        var sandboxRoot = Path.Combine(Path.GetTempPath(), "ashlar-sandbox");
        var escaped = Path.GetFullPath(Path.Combine(sandboxRoot, "../outside/file.cs"));
        var json = JsonSerializer.SerializeToElement(new { path = escaped });
        var call = new ToolCall("repo.fs.write", json);
        var snapshot = new WorldSnapshot(0, new Dictionary<string, object?> { ["SandboxRoot"] = sandboxRoot });

        var result = _policy.Approve(call, snapshot, out var reason);

        result.Should().BeFalse();
        reason.Should().Contain("outside SandboxRoot");
    }

    [Fact]
    public void Approve_RepoFsWrite_WithSandboxRootConfigured_RejectsAbsolutePathOutsideSandboxRoot()
    {
        var sandboxRoot = Path.Combine(Path.GetTempPath(), "ashlar-sandbox");
        var outsidePath = Path.Combine(Path.GetTempPath(), "elsewhere", "file.cs");
        var json = JsonSerializer.SerializeToElement(new { path = outsidePath });
        var call = new ToolCall("repo.fs.write", json);
        var snapshot = new WorldSnapshot(0, new Dictionary<string, object?> { ["SandboxRoot"] = sandboxRoot });

        var result = _policy.Approve(call, snapshot, out var reason);

        result.Should().BeFalse();
        reason.Should().Contain("outside SandboxRoot");
    }

    [Fact]
    public void Approve_RepoFsWrite_WithEmptyPath_ReturnsFalse()
    {
        var call = CreateToolCall("repo.fs.write", "");
        var result = _policy.Approve(call, EmptySnapshot, out var reason);

        result.Should().BeFalse();
        reason.Should().Contain("empty or null");
    }

    [Fact]
    public void Approve_RepoFsWrite_WithNullPath_ReturnsFalse()
    {
        var json = JsonSerializer.SerializeToElement(new { path = (string?)null });
        var call = new ToolCall("repo.fs.write", json);
        var result = _policy.Approve(call, EmptySnapshot, out var reason);

        result.Should().BeFalse();
        reason.Should().Contain("empty or null");
    }

    [Fact]
    public void Approve_RepoFsWrite_WithBinPath_ReturnsFalse()
    {
        var call = CreateToolCall("repo.fs.write", "bin/Release/net8.0/foo.dll");
        var result = _policy.Approve(call, EmptySnapshot, out var reason);

        result.Should().BeFalse();
        reason.Should().Contain("Path not allowed");
    }

    [Fact]
    public void PathAllowlist_RejectsWrite_ToObjDirectory()
    {
        var call = CreateToolCall("repo.fs.write", "obj/Debug/net8.0/foo.dll");
        var result = _policy.Approve(call, EmptySnapshot, out var reason);

        result.Should().BeFalse();
        reason.Should().Contain("Path not allowed");
    }

    [Fact]
    public void PathAllowlist_RejectsWrite_ToGitDirectory()
    {
        var call = CreateToolCall("repo.fs.write", ".git/HEAD");
        var result = _policy.Approve(call, EmptySnapshot, out var reason);

        result.Should().BeFalse();
        reason.Should().Contain("Path not allowed");
    }
}
