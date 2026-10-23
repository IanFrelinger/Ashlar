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
    public void Over_limit_containers_are_rejected_before_deserialization(string shape)
    {
        YamlGuard.Check(Nested(shape, 65), "test document", out var reason).Should().BeFalse();
        reason.Should().Contain("nesting").And.Contain("64");
    }

    [Theory]
    [InlineData("mapping")]
    [InlineData("sequence")]
    [InlineData("mixed")]
    public void Exactly_limit_containers_remain_accepted(string shape)
    {
        YamlGuard.Check(Nested(shape, 64), "test document", out var reason).Should().BeTrue();
        reason.Should().BeEmpty();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Both_loaders_report_the_depth_refusal(bool policy)
    {
        var yaml = Nested("mapping", 65);
        string reason;
        var accepted = policy
            ? PolicyLoader.TryLoad(yaml, out _, out reason)
            : ManifestLoader.TryLoad(yaml, out _, out reason);
        accepted.Should().BeFalse();
        reason.Should().Contain("nesting").And.Contain("64");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Truncated_deep_input_is_refused_before_recursive_parsing(bool policy)
    {
        var yaml = "payload: " + new string('[', 10000) + "leaf";
        string reason;
        var accepted = policy
            ? PolicyLoader.TryLoad(yaml, out _, out reason)
            : ManifestLoader.TryLoad(yaml, out _, out reason);
        accepted.Should().BeFalse();
        reason.Should().Contain("nesting").And.Contain("64");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Shallow_malformed_input_retains_parse_refusal(bool policy)
    {
        const string yaml = "payload: [[[leaf";
        string reason;
        var accepted = policy
            ? PolicyLoader.TryLoad(yaml, out _, out reason)
            : ManifestLoader.TryLoad(yaml, out _, out reason);
        accepted.Should().BeFalse();
        reason.Should().Contain("REJECTED").And.NotContain("nesting");
    }

    [Fact]
    public void Many_shallow_siblings_do_not_accumulate_depth()
    {
        var yaml = "[" + string.Join(",", Enumerable.Repeat("{a: [x]}", 200)) + "]";
        YamlGuard.Check(yaml, "test document", out _).Should().BeTrue();
    }

    private static string Nested(string shape, int depth)
    {
        var yaml = "leaf";
        for (var i = 0; i < depth; i++)
            yaml = shape == "sequence" || (shape == "mixed" && i % 2 == 0)
                ? "[" + yaml + "]" : "{a: " + yaml + "}";
        return yaml;
    }
}
