using System.IO;
using System.Text.Json;
using FluentAssertions;
using Ashlar.Abstractions;
using Ashlar.Policies.Dev;
using Ashlar.Tests.Infrastructure.Helpers;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Policies;

/// <summary>Tests for path allowlist gap coverage.</summary>
[Trait("Category", "Unit")]
[Collection("EnvironmentVariables")]
public sealed class PathAllowlistGapCoverageTests
{
    private static readonly WorldSnapshot EmptySnapshot = new(0, new Dictionary<string, object?>());

    [Fact]
    public void Constructor_merges_extra_allowed_prefixes()
    {
        var policy = new PathAllowlist(["custom/"]);
        var call = CreateToolCall("repo.fs.write", "custom/file.txt");

        policy.Approve(call, EmptySnapshot, out var reason).Should().BeTrue();
        reason.Should().Be("OK");
    }

    [Fact]
    public void Constructor_reads_extra_prefixes_from_environment_variable()
    {
        using var extra = new EnvironmentVariableScope("ASHLAR_PATH_ALLOWLIST_EXTRA", "generated/, staging");

        var policy = new PathAllowlist();
        var call = CreateToolCall("repo.fs.write", "generated/output.txt");

        policy.Approve(call, EmptySnapshot, out var reason).Should().BeTrue();
        reason.Should().Be("OK");
    }

    [Fact]
    public void Approve_allows_ashlar_metadata_directory()
    {
        var policy = new PathAllowlist();
        var call = CreateToolCall("repo.fs.write", ".ashlar/state.json");

        policy.Approve(call, EmptySnapshot, out var reason).Should().BeTrue();
        reason.Should().Be("OK");
    }

    [Fact]
    public void Approve_rejects_non_string_path_property()
    {
        var policy = new PathAllowlist();
        var json = JsonSerializer.SerializeToElement(new { path = 123 });
        var call = new ToolCall("repo.fs.write", json);

        policy.Approve(call, EmptySnapshot, out var reason).Should().BeFalse();
        reason.Should().Contain("empty or null");
    }

    [Fact]
    public void Approve_rejects_invalid_sandbox_root_from_environment()
    {
        using var sandboxRoot = new EnvironmentVariableScope("ASHLAR_SANDBOX_ROOT", "\0invalid");

        var policy = new PathAllowlist();
        var absolutePath = Path.Combine(Path.GetTempPath(), "outside.txt");
        var call = CreateToolCall("repo.fs.write", absolutePath);

        policy.Approve(call, EmptySnapshot, out var reason).Should().BeFalse();
        reason.Should().Contain("absolute path");
    }

    [Fact]
    public void Approve_allows_absolute_path_equal_to_sandbox_root()
    {
        var sandboxRoot = Path.Combine(Path.GetTempPath(), "ashlar-sandbox-root-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sandboxRoot);
        try
        {
            var policy = new PathAllowlist();
            var snapshot = new WorldSnapshot(0, new Dictionary<string, object?> { ["SandboxRoot"] = sandboxRoot });
            var call = CreateToolCall("repo.fs.write", sandboxRoot);

            policy.Approve(call, snapshot, out var reason).Should().BeTrue();
            reason.Should().Be("OK");
        }
        finally
        {
            if (Directory.Exists(sandboxRoot))
                Directory.Delete(sandboxRoot, recursive: true);
        }
    }

    [Fact]
    public void Approve_allows_application_directory()
    {
        var policy = new PathAllowlist();
        var call = CreateToolCall("repo.fs.write", "application/src/Program.cs");

        policy.Approve(call, EmptySnapshot, out var reason).Should().BeTrue();
        reason.Should().Be("OK");
    }

    [Fact]
    public void Approve_ignores_non_write_tools()
    {
        var policy = new PathAllowlist();
        var call = CreateToolCall("repo.fs.read", "etc/passwd");

        policy.Approve(call, EmptySnapshot, out var reason).Should().BeTrue();
        reason.Should().Be("OK");
    }

    [Fact]
    public void Approve_write_without_path_property_passes_to_other_policies()
    {
        var policy = new PathAllowlist();
        var json = JsonSerializer.SerializeToElement(new { content = "x" });
        var call = new ToolCall("repo.fs.write", json);

        policy.Approve(call, EmptySnapshot, out var reason).Should().BeTrue();
        reason.Should().Be("OK");
    }

    private static ToolCall CreateToolCall(string toolId, string path)
    {
        var json = JsonSerializer.SerializeToElement(new { path });
        /// <summary>Tool call.</summary>
        return new ToolCall(toolId, json);
    }
}
