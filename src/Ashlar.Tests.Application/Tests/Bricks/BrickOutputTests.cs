using FluentAssertions;
using Ashlar.Core.Domain.Execution;
using Xunit;

namespace Ashlar.Tests.Application.Tests.Bricks;

/// <summary>Tests for BrickOutput methods.</summary>
public sealed class BrickOutputTests
{
    [Fact]
    public void Get_returns_value_when_key_exists_and_type_matches()
    {
        var output = new BrickOutput();
        output.Set("result", "success");

        var result = output.Get<string>("result");

        result.Should().Be("success");
    }

    [Fact]
    public void Get_throws_KeyNotFoundException_when_key_missing()
    {
        var output = new BrickOutput();

        var act = () => output.Get<string>("missing");

        act.Should().Throw<KeyNotFoundException>()
            .WithMessage("Output key 'missing' not found");
    }

    [Fact]
    public void Get_throws_InvalidCastException_when_type_mismatch()
    {
        var output = new BrickOutput();
        output.Set("value", 42);

        var act = () => output.Get<string>("value");

        act.Should().Throw<InvalidCastException>()
            .WithMessage("Output key 'value' is not of type String");
    }

    [Fact]
    public void Get_with_default_returns_value_when_key_exists()
    {
        var output = new BrickOutput();
        output.Set("result", "success");

        var result = output.Get("result", "default");

        result.Should().Be("success");
    }

    [Fact]
    public void Get_with_default_returns_default_when_key_missing()
    {
        var output = new BrickOutput();

        var result = output.Get("missing", "default");

        result.Should().Be("default");
    }

    [Fact]
    public void Get_with_default_returns_default_when_type_mismatch()
    {
        var output = new BrickOutput();
        output.Set("value", 42);

        var result = output.Get("value", "default");

        result.Should().Be("default");
    }

    [Fact]
    public void TryGet_returns_true_and_value_when_key_exists_and_type_matches()
    {
        var output = new BrickOutput();
        output.Set("result", "success");

        var success = output.TryGet<string>("result", out var value);

        success.Should().BeTrue();
        value.Should().Be("success");
    }

    [Fact]
    public void TryGet_returns_false_when_key_missing()
    {
        var output = new BrickOutput();

        var success = output.TryGet<string>("missing", out var value);

        success.Should().BeFalse();
        value.Should().BeNull();
    }

    [Fact]
    public void TryGet_returns_false_when_type_mismatch()
    {
        var output = new BrickOutput();
        output.Set("value", 42);

        var success = output.TryGet<string>("value", out var value);

        success.Should().BeFalse();
        value.Should().BeNull();
    }

    [Fact]
    public void TryGet_works_with_value_types()
    {
        var output = new BrickOutput();
        output.Set("count", 42);

        var success = output.TryGet<int>("count", out var value);

        success.Should().BeTrue();
        value.Should().Be(42);
    }

    [Fact]
    public void TryGet_returns_default_for_missing_value_types()
    {
        var output = new BrickOutput();

        var success = output.TryGet<int>("missing", out var value);

        success.Should().BeFalse();
        value.Should().Be(0);
    }

    [Fact]
    public void Indexer_get_returns_value()
    {
        var output = new BrickOutput();
        output["key"] = "value";

        var result = output["key"];

        result.Should().Be("value");
    }

    [Fact]
    public void Indexer_set_stores_value()
    {
        var output = new BrickOutput();
        output["key"] = "value";

        output.Get<string>("key").Should().Be("value");
    }

    [Fact]
    public void Set_overwrites_existing_value()
    {
        var output = new BrickOutput();
        output.Set("key", "original");
        output.Set("key", "updated");

        var result = output.Get<string>("key");

        result.Should().Be("updated");
    }

    [Fact]
    public void Summary_can_be_set_and_retrieved()
    {
        var output = new BrickOutput
        {
            Summary = "Operation completed successfully"
        };

        output.Summary.Should().Be("Operation completed successfully");
    }

    [Fact]
    public void ToDictionary_returns_all_values()
    {
        var output = new BrickOutput();
        output.Set("result", "success");
        output.Set("count", 42);

        var result = output.ToDictionary();

        result.Should().HaveCount(2);
        result["result"].Should().Be("success");
        result["count"].Should().Be(42);
    }
}
