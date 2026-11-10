using System.Text;
using Ashlar.Manifest;
using FluentAssertions;
using Xunit;

namespace Ashlar.Tests.Kernel;

public sealed class YamlDepthGuardTests
{
    [Theory]
    [InlineData("mapping")]
    [InlineData("sequence")]
    [InlineData("mixed")]
    [InlineData("block-mapping")]
    [InlineData("block-sequence")]
    public void Exactly_64_open_containers_are_allowed_but_65_are_rejected(string shape)
    {
        YamlGuard.MaxDepth.Should().Be(64);
        YamlGuard.Check(Nest(shape, 64), "test", out var allowedReason).Should().BeTrue(allowedReason);
        allowedReason.Should().BeEmpty();
        YamlGuard.Check(Nest(shape, 65), "test", out var reason).Should().BeFalse();
        reason.Should().Contain("test").And.Contain("nesting depth").And.Contain("64");
    }

    [Theory]
    [InlineData("mapping")]
    [InlineData("sequence")]
    [InlineData("mixed")]
    public void Tiny_extreme_nesting_is_rejected_without_recursive_deserialization(string shape)
    {
        var yaml = Nest(shape, 10_000);
        yaml.Length.Should().BeLessThan(YamlGuard.MaxBytes);
        YamlGuard.Check(yaml, "extreme", out var reason).Should().BeFalse();
        reason.Should().Contain("nesting depth");
    }

    [Theory]
    [InlineData("mapping")]
    [InlineData("sequence")]
    [InlineData("mixed")]
    public void Both_loaders_preserve_the_depth_rejection_before_schema_validation(string shape)
    {
        var yaml = Nest(shape, 65);
        ManifestLoader.TryLoad(yaml, out var manifest, out var manifestReason).Should().BeFalse();
        manifest.Should().BeNull();
        manifestReason.Should().Contain("manifest").And.Contain("nesting depth");
        PolicyLoader.TryLoad(yaml, out var policy, out var policyReason).Should().BeFalse();
        policy.Should().BeNull();
        policyReason.Should().Contain("policy").And.Contain("nesting depth");
    }

    [Fact]
    public void Wide_documents_and_scalar_punctuation_do_not_count_as_nesting()
    {
        var wide = "[" + string.Join(",", Enumerable.Repeat("{a: [0]}", 1024)) + "]";
        YamlGuard.Check(wide, "wide", out var wideReason).Should().BeTrue(wideReason);
        var punctuation = new string('[', 1000) + new string('}', 1000);
        var yaml = $"quoted: '{punctuation}'\n# {punctuation}\nblock: |\n  {punctuation}\n";
        YamlGuard.Check(yaml, "scalar", out var reason).Should().BeTrue(reason);
    }

    [Fact]
    public void Malformed_extreme_nesting_is_still_rejected_before_deserialization()
    {
        var yaml = string.Concat(Enumerable.Repeat("{a: ", 10_000));
        YamlGuard.Check(yaml, "unfinished", out var reason).Should().BeFalse();
        reason.Should().Contain("nesting depth");
    }

    private static string Nest(string shape, int depth)
    {
        var start = new StringBuilder();
        var end = new StringBuilder();
        for (var i = 0; i < depth; i++)
        {
            if (shape.StartsWith("block-", StringComparison.Ordinal))
            {
                start.Append(' ', i * 2).Append(shape == "block-mapping" ? "a:\n" : "-\n");
                continue;
            }
            var mapping = shape == "mapping" || shape == "mixed" && i % 2 == 0;
            start.Append(mapping ? "{a: " : "[");
            end.Insert(0, mapping ? '}' : ']');
        }
        if (shape.StartsWith("block-", StringComparison.Ordinal)) start.Append(' ', depth * 2);
        return start.Append('0').Append(end).ToString();
    }
}
