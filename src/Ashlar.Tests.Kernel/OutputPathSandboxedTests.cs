using System.Text.Json;
using FluentAssertions;
using Ashlar.Abstractions;
using Ashlar.Policies;
using Xunit;

namespace Ashlar.Tests.Kernel;

/// <summary>Tests for output path sandboxed.</summary>
public class OutputPathSandboxedTests
{
    [Fact]
    public void Approve_returns_OK_when_no_OutputRoot_in_state()
    {
        var p = new OutputPathSandboxed();
        var snap = new WorldSnapshot(0, new Dictionary<string, object?>());
        var call = new ToolCall("t", JsonDocument.Parse("""{"output":"/x"}""").RootElement);
        p.Approve(call, snap, out var reason).Should().BeTrue();
        reason.Should().Be("OK");
    }

    [Fact]
    public void Approve_returns_OK_when_OutputRoot_is_not_string()
    {
        var p = new OutputPathSandboxed();
        var snap = new WorldSnapshot(0, new Dictionary<string, object?> { ["OutputRoot"] = 123 });
        var call = new ToolCall("t", JsonDocument.Parse("""{"output":"/x"}""").RootElement);
        p.Approve(call, snap, out var reason).Should().BeTrue();
        reason.Should().Be("OK");
    }

    [Fact]
    public void Approve_returns_OK_when_arguments_not_object()
    {
        var p = new OutputPathSandboxed();
        var snap = new WorldSnapshot(0, new Dictionary<string, object?> { ["OutputRoot"] = "/tmp" });
        var call = new ToolCall("t", JsonDocument.Parse("[]").RootElement);
        p.Approve(call, snap, out var reason).Should().BeTrue();
        reason.Should().Be("OK");
    }

    [Fact]
    public void Approve_returns_OK_when_arguments_have_no_output_property()
    {
        var p = new OutputPathSandboxed();
        var snap = new WorldSnapshot(0, new Dictionary<string, object?> { ["OutputRoot"] = "/tmp" });
        var call = new ToolCall("t", JsonDocument.Parse("""{"other":"x"}""").RootElement);
        p.Approve(call, snap, out var reason).Should().BeTrue();
        reason.Should().Be("OK");
    }

    [Fact]
    public void Approve_returns_OK_when_output_inside_sandbox()
    {
        var p = new OutputPathSandboxed();
        var tmp = Path.GetTempPath();
        var inside = Path.Combine(tmp, "inside.txt");
        var snap = new WorldSnapshot(0, new Dictionary<string, object?> { ["OutputRoot"] = tmp });
        var call = new ToolCall("t", JsonDocument.Parse($$"""{"output":"{{inside.Replace("\\", "\\\\")}}"}""").RootElement);
        p.Approve(call, snap, out var reason).Should().BeTrue();
        reason.Should().Be("OK");
    }

    [Fact]
    public void Approve_rejects_path_outside_sandbox()
    {
        var p = new OutputPathSandboxed();
        var sandbox = Path.Combine(Path.GetTempPath(), "sandbox_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sandbox);
        try
        {
            var outsideAbsolute = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "outside_" + Guid.NewGuid().ToString("N")));
            var snap = new WorldSnapshot(0, new Dictionary<string, object?> { ["OutputRoot"] = sandbox });
            var call = new ToolCall("t", JsonDocument.Parse($$"""{"output":"{{outsideAbsolute.Replace("\\", "\\\\")}}"}""").RootElement);
            p.Approve(call, snap, out var reason).Should().BeFalse();
            reason.Should().Contain("escapes sandbox");
        }
        finally
        {
            if (Directory.Exists(sandbox)) Directory.Delete(sandbox);
        }
    }

    [Fact]
    public void Approve_rejects_a_sibling_whose_name_extends_the_sandbox_root()
    {
        // The check compared without a directory separator after the root, so OutputRoot .../out
        // admitted .../out-evil, .../outx and .../out.dll: every sibling sharing the root's name.
        var p = new OutputPathSandboxed();
        var root = Path.Combine(Path.GetTempPath(), "ashlar-sibling-" + Guid.NewGuid().ToString("N"), "out");
        var sibling = root + "-evil" + Path.DirectorySeparatorChar + "x.cs";
        sibling.Should().StartWith(root, "the attack is a path that shares the root's characters");
        var snap = new WorldSnapshot(0, new Dictionary<string, object?> { ["OutputRoot"] = root });
        var call = new ToolCall("t", JsonDocument.Parse(JsonSerializer.Serialize(new Dictionary<string, string> { ["output"] = sibling })).RootElement);

        p.Approve(call, snap, out var reason).Should().BeFalse();
        reason.Should().Contain("escapes sandbox");
    }

    [Fact]
    public void Approve_rejects_null_output_value_as_invalid_path()
    {
        var p = new OutputPathSandboxed();
        var snap = new WorldSnapshot(0, new Dictionary<string, object?> { ["OutputRoot"] = Path.GetTempPath() });
        var call = new ToolCall("t", JsonDocument.Parse("""{"output":null}""").RootElement);
        p.Approve(call, snap, out var reason).Should().BeFalse();
        reason.Should().Be("Invalid output path");
    }

    [Theory]
    [InlineData("""{"output":5}""")]
    [InlineData("""{"output":true}""")]
    [InlineData("""{"output":["/tmp/x"]}""")]
    [InlineData("""{"output":{"path":"/tmp/x"}}""")]
    public void Approve_refuses_a_non_string_output_instead_of_throwing(string arguments)
    {
        // GetString() on a non-string JSON value throws. It used to run outside the try, so the
        // exception left Approve (and PolicyEngine.Approve) instead of a refusal coming back.
        var p = new OutputPathSandboxed();
        var snap = new WorldSnapshot(0, new Dictionary<string, object?> { ["OutputRoot"] = Path.GetTempPath() });
        var call = new ToolCall("t", JsonDocument.Parse(arguments).RootElement);

        var approved = true;
        var reason = "";
        var approve = () => { approved = p.Approve(call, snap, out reason); };

        approve.Should().NotThrow();
        approved.Should().BeFalse();
        reason.Should().Be("Invalid output path");
    }

    [Fact]
    public void Approve_rejects_invalid_path_with_invalid_chars()
    {
        var p = new OutputPathSandboxed();
        var snap = new WorldSnapshot(0, new Dictionary<string, object?> { ["OutputRoot"] = Path.GetTempPath() });
        var bad = "\0bad" + new string(Path.GetInvalidPathChars());
        var call = new ToolCall("t", JsonDocument.Parse(JsonSerializer.Serialize(new Dictionary<string, string> { ["output"] = bad })).RootElement);
        p.Approve(call, snap, out var reason).Should().BeFalse();
        reason.Should().Be("Invalid output path");
    }
}
