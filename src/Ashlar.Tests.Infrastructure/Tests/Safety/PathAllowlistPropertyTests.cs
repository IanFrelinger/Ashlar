using System.Text.Json;
using CsCheck;
using FluentAssertions;
using Ashlar.Abstractions;
using Ashlar.Policies.Dev;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Safety;

/// <summary>
/// Property-based tests for PathAllowlist. Proves safety invariants hold for arbitrary inputs.
/// </summary>
[Trait("Category", "Safety")]
[Trait("Category", "Unit")]
public sealed class PathAllowlistPropertyTests
{
    private static readonly WorldSnapshot EmptySnapshot = new(0, new Dictionary<string, object?>());
    private readonly PathAllowlist _allowlist = new();

    private static ToolCall CreateWriteCall(string path, string content = "x")
    {
        var json = JsonSerializer.SerializeToElement(new { path, content });
        return new ToolCall("repo.fs.write", json);
    }

    [Fact]
    public void PathAllowlist_NeverSilentlyAccepts_ArbitraryPath()
    {
        Gen.String[1, 500].Sample(path =>
        {
            var call = CreateWriteCall(path ?? string.Empty);
            var result = _allowlist.Approve(call, EmptySnapshot, out var reason);

            (result == true || result == false).Should().BeTrue("result must be Allowed or Rejected, never exception or third outcome");
            reason.Should().NotBeNullOrEmpty();
        });
    }

    [Fact]
    public void PathAllowlist_AnyPathContainingTraversal_IsAlwaysRejected()
    {
        var traversalPaths = new[] { "..", "../", "/../", "/src/../bin", "src/foo/../../../.git/config", "tests/../../etc/passwd" };
        foreach (var path in traversalPaths)
        {
            var call = CreateWriteCall(path);
            var result = _allowlist.Approve(call, EmptySnapshot, out var reason);

            result.Should().BeFalse($"path traversal must be rejected: {path}");
            reason.Should().Contain("Path not allowed");
        }

        Gen.OneOf(
            Gen.Const(".."),
            Gen.Const("../"),
            Gen.Const("/../"),
            Gen.Const("src/../bin"),
            Gen.Const("tests/../../escape.cs")).Sample(path =>
        {
            var call = CreateWriteCall(path);
            var result = _allowlist.Approve(call, EmptySnapshot, out _);
            result.Should().BeFalse($"path with traversal must be rejected: {path}");
        });
    }

    [Fact]
    public void PathAllowlist_AllowedPrefixes_AreConsistentlyAllowed()
    {
        var sandboxRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ashlar-policy-prop-sandbox"));
        var snapshot = new WorldSnapshot(0, new Dictionary<string, object?>
        {
            ["SandboxRoot"] = sandboxRoot
        });

        // Suffix must not contain ".." (path traversal) which would cause rejection.
        // These are the four DEFAULT prefixes. .ashlar/ is not one any more — it is the admission
        // ledger — and this property holds for an arbitrary suffix precisely because the governance
        // floor is NOT inside PathAllowlist: folded in, it would be false for every generated suffix
        // containing ':' or ending in .props.
        Gen.OneOf(Gen.Const("src/"), Gen.Const("tests/"), Gen.Const("docs/"), Gen.Const("application/"))
            .SelectMany(prefix => Gen.String[1, 50]
                .Where(s => !s.Contains("..", StringComparison.Ordinal))
                .Select(suffix => prefix + suffix))
            .Sample(path =>
            {
                var call = CreateWriteCall(path);
                var result = _allowlist.Approve(call, snapshot, out var reason);

                result.Should().BeTrue($"path under allowed prefix without traversal must be allowed: {path}");
                reason.Should().Be("OK");
            });

        // Files under sandbox root should also be accepted.
        Gen.String[1, 50]
            .Where(s => !s.Contains("..", StringComparison.Ordinal))
            .Where(s => s.IndexOfAny(Path.GetInvalidFileNameChars()) < 0)
            .Sample(suffix =>
            {
                var relative = $"tools-cache/{suffix}.txt";
                var full = Path.GetFullPath(Path.Combine(sandboxRoot, relative));
                var call = CreateWriteCall(full);
                var result = _allowlist.Approve(call, snapshot, out var reason);

                result.Should().BeTrue($"sandbox path should be allowed: {full}");
                reason.Should().Be("OK");
            });
    }

    [Fact]
    public void PathAllowlist_NeverThrows_ForAnyInput()
    {
        Gen.String[0, 500].Sample(path =>
        {
            var call = CreateWriteCall(path ?? string.Empty);
            var act = () => _allowlist.Approve(call, EmptySnapshot, out _);
            act.Should().NotThrow();
        });
    }
}
