using System.Text.Json;
using FluentAssertions;
using Moq;
using Ashlar.Abstractions;
using Ashlar.Policies.Dev;
using Xunit;

namespace Ashlar.Tests.Kernel;

[Collection("EnvironmentSensitive")]
public class PathAllowlistTests
{
    private static ToolCall Write(string path) =>
        new("repo.fs.write", JsonDocument.Parse(JsonSerializer.Serialize(new Dictionary<string, string> { ["path"] = path })).RootElement);
    private static ToolCall WriteWithNullPath() =>
        new("repo.fs.write", JsonDocument.Parse("""{"path":null}""").RootElement);
    private static readonly WorldSnapshot EmptySnap = new(0, new Dictionary<string, object?>());

    [Fact]
    public void Non_write_calls_pass()
    {
        var p = new PathAllowlist();
        p.Approve(new ToolCall("dotnet.test", JsonDocument.Parse("{}").RootElement), EmptySnap, out var r).Should().BeTrue();
        r.Should().Be("OK");
    }

    [Fact]
    public void Write_without_arguments_object_passes()
    {
        var p = new PathAllowlist();
        var call = new ToolCall("repo.fs.write", JsonDocument.Parse("[]").RootElement);
        p.Approve(call, EmptySnap, out _).Should().BeTrue();
    }

    [Fact]
    public void Write_without_path_property_passes()
    {
        var p = new PathAllowlist();
        var call = new ToolCall("repo.fs.write", JsonDocument.Parse("""{"content":"x"}""").RootElement);
        p.Approve(call, EmptySnap, out _).Should().BeTrue();
    }

    [Theory]
    [InlineData("src/Foo.cs")]
    [InlineData("tests/Bar.cs")]
    [InlineData("docs/readme.md")]
    [InlineData("application/foo")]
    public void Default_allowlist_permits_known_prefixes(string path)
    {
        var p = new PathAllowlist();
        p.Approve(Write(path), EmptySnap, out var r).Should().BeTrue();
        r.Should().Be("OK");
    }

    [Fact]
    public void Rejects_empty_or_null_path()
    {
        var p = new PathAllowlist();
        p.Approve(Write(""), EmptySnap, out var r).Should().BeFalse();
        r.Should().Contain("empty or null path");
        p.Approve(WriteWithNullPath(), EmptySnap, out var r2).Should().BeFalse();
        r2.Should().Contain("empty or null path");
    }

    [Fact]
    public void Rejects_disallowed_relative_path()
    {
        var p = new PathAllowlist();
        p.Approve(Write("etc/passwd"), EmptySnap, out var r).Should().BeFalse();
        r.Should().Contain("Path not allowed");
    }

    [Fact]
    public void Rejects_path_traversal()
    {
        var p = new PathAllowlist();
        p.Approve(Write("src/../etc/x"), EmptySnap, out var r).Should().BeFalse();
        r.Should().Contain("path traversal not permitted");
    }

    [Fact]
    public void Rejects_absolute_path_without_sandbox_root()
    {
        var p = new PathAllowlist();
        var abs = OperatingSystem.IsWindows() ? @"C:\foo\bar" : "/foo/bar";
        p.Approve(Write(abs), EmptySnap, out var r).Should().BeFalse();
        r.Should().Contain("absolute path not permitted");
    }

    [Fact]
    public void Allows_absolute_path_within_SandboxRoot_in_snapshot()
    {
        var p = new PathAllowlist();
        var sandbox = Path.Combine(Path.GetTempPath(), "sb_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sandbox);
        try
        {
            var inside = Path.Combine(sandbox, "inside.txt");
            var snap = new WorldSnapshot(0, new Dictionary<string, object?> { ["SandboxRoot"] = sandbox });
            p.Approve(Write(inside), snap, out var r).Should().BeTrue();
            r.Should().Be("OK");

            // Also verify the candidate-equals-root edge case.
            p.Approve(Write(sandbox), snap, out _).Should().BeTrue();
        }
        finally { Directory.Delete(sandbox, recursive: true); }
    }

    [Fact]
    public void Rejects_absolute_path_outside_SandboxRoot()
    {
        var p = new PathAllowlist();
        var sandbox = Path.Combine(Path.GetTempPath(), "sb_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sandbox);
        try
        {
            var outside = Path.Combine(Path.GetTempPath(), "outside_" + Guid.NewGuid().ToString("N"));
            var snap = new WorldSnapshot(0, new Dictionary<string, object?> { ["SandboxRoot"] = sandbox });
            p.Approve(Write(outside), snap, out var r).Should().BeFalse();
            r.Should().Contain("outside SandboxRoot");
        }
        finally { Directory.Delete(sandbox, recursive: true); }
    }

    [Fact]
    public void Falls_back_to_environment_SANDBOX_ROOT_when_state_lacks_it()
    {
        var sandbox = Path.Combine(Path.GetTempPath(), "envsb_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sandbox);
        try
        {
            EnvVar.Run("ASHLAR_SANDBOX_ROOT", sandbox, () =>
            {
                var p = new PathAllowlist();
                var inside = Path.Combine(sandbox, "x.txt");
                p.Approve(Write(inside), EmptySnap, out var r).Should().BeTrue();
                r.Should().Be("OK");
            });
        }
        finally
        {
            Directory.Delete(sandbox, recursive: true);
        }
    }

    [Fact]
    public void Constructor_merges_extra_prefixes_with_normalization_and_dedup()
    {
        var p = new PathAllowlist(new[]
        {
            "extras/",
            "extras/",
            "libs",
            @"plugins\sub\",
            "",
            "  ",
            "../bad",
            "..bad",
        });

        p.Approve(Write("extras/x"), EmptySnap, out _).Should().BeTrue();
        p.Approve(Write("libs/x"), EmptySnap, out _).Should().BeTrue();
        p.Approve(Write("plugins/sub/x"), EmptySnap, out _).Should().BeTrue();
        p.Approve(Write("bad/x"), EmptySnap, out var rBad).Should().BeFalse();
        rBad.Should().Contain("Path not allowed");
    }

    [Fact]
    public void Empty_extras_environment_value_is_ignored()
    {
        EnvVar.Run("ASHLAR_PATH_ALLOWLIST_EXTRA", "   ", () =>
        {
            var p = new PathAllowlist();
            p.Approve(Write("etc/passwd"), EmptySnap, out var reason).Should().BeFalse();
            reason.Should().Contain("Path not allowed");
        });
    }

    [Fact]
    public void Extras_environment_value_adds_comma_separated_prefixes()
    {
        EnvVar.Run("ASHLAR_PATH_ALLOWLIST_EXTRA", "libs/, plugins ,dupe", () =>
        {
            var p = new PathAllowlist();
            p.Approve(Write("libs/x"), EmptySnap, out var r1).Should().BeTrue(r1);
            p.Approve(Write("plugins/x"), EmptySnap, out var r2).Should().BeTrue(r2);
            p.Approve(Write("dupe/x"), EmptySnap, out var r3).Should().BeTrue(r3);
        });
    }

    [Fact]
    public void NormalizePrefix_drops_entries_that_are_only_slashes()
    {
        var p = new PathAllowlist(new[] { "/", "//", "///" });
        p.Approve(Write("etc/passwd"), EmptySnap, out var reason).Should().BeFalse();
        reason.Should().Contain("Path not allowed");
    }

    [Fact]
    public void ResolveSandboxRoot_returns_null_when_path_invalid()
    {
        var prev = Environment.GetEnvironmentVariable("ASHLAR_SANDBOX_ROOT");
        try
        {
            Environment.SetEnvironmentVariable("ASHLAR_SANDBOX_ROOT", "\0bad\0");
            var p = new PathAllowlist();
            var abs = OperatingSystem.IsWindows() ? @"C:\foo\bar" : "/foo/bar";
            p.Approve(Write(abs), EmptySnap, out var reason).Should().BeFalse();
            reason.Should().Contain("absolute path not permitted");
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASHLAR_SANDBOX_ROOT", prev);
        }
    }

    [Fact]
    public void Non_string_path_property_is_treated_as_empty()
    {
        var p = new PathAllowlist();
        var call = new ToolCall("repo.fs.write", JsonDocument.Parse("""{"path":123}""").RootElement);
        p.Approve(call, EmptySnap, out var reason).Should().BeFalse();
        reason.Should().Contain("empty or null path");
    }

    [Fact]
    public void Invalid_SandboxRoot_in_snapshot_falls_back_to_absolute_path_denial()
    {
        var snap = new WorldSnapshot(0, new Dictionary<string, object?> { ["SandboxRoot"] = "\0bad\0" });
        var p = new PathAllowlist();
        var abs = OperatingSystem.IsWindows() ? @"C:\foo\bar" : "/foo/bar";
        p.Approve(Write(abs), snap, out var reason).Should().BeFalse();
        reason.Should().Contain("absolute path not permitted");
    }

    [Fact]
    public void Search_replace_uses_same_rules_as_write()
    {
        var p = new PathAllowlist();
        var call = new ToolCall("repo.fs.search_replace",
            JsonDocument.Parse(JsonSerializer.Serialize(new Dictionary<string, string> { ["path"] = "etc/x" })).RootElement);
        p.Approve(call, EmptySnap, out var r).Should().BeFalse();
        r.Should().Contain("Path not allowed");
    }

    [Fact]
    public void Invalid_absolute_path_returns_invalid_path_reason()
    {
        var p = new PathAllowlist();
        var sandbox = Path.Combine(Path.GetTempPath(), "isb_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sandbox);
        try
        {
            var snap = new WorldSnapshot(0, new Dictionary<string, object?> { ["SandboxRoot"] = sandbox });
            var bad = "/" + new string('\0', 1) + new string(Path.GetInvalidPathChars()) + "bad";
            var call = new ToolCall("repo.fs.write",
                JsonDocument.Parse(JsonSerializer.Serialize(new Dictionary<string, string> { ["path"] = bad })).RootElement);
            p.Approve(call, snap, out var r).Should().BeFalse();
            r.Should().Contain("invalid absolute path");
        }
        finally { Directory.Delete(sandbox, recursive: true); }
    }
}
