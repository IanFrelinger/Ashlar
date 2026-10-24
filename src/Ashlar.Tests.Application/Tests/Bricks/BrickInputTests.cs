using FluentAssertions;
using Ashlar.Core.Domain.Execution;
using Xunit;

namespace Ashlar.Tests.Application.Tests.Bricks;

/// <summary>Tests for BrickInput methods.</summary>
public sealed class BrickInputTests
{
    [Fact]
    public void Get_returns_value_when_key_exists_and_type_matches()
    {
        var input = new BrickInput();
        input.Set("name", "test");

        var result = input.Get<string>("name");

        result.Should().Be("test");
    }

    [Fact]
    public void Get_throws_KeyNotFoundException_when_key_missing()
    {
        var input = new BrickInput();

        var act = () => input.Get<string>("missing");

        act.Should().Throw<KeyNotFoundException>()
            .WithMessage("Input key 'missing' not found");
    }

    [Fact]
    public void Get_throws_InvalidCastException_when_type_mismatch()
    {
        var input = new BrickInput();
        input.Set("value", 42);

        var act = () => input.Get<string>("value");

        act.Should().Throw<InvalidCastException>()
            .WithMessage("Input key 'value' is not of type String");
    }

    [Fact]
    public void Get_with_default_returns_value_when_key_exists()
    {
        var input = new BrickInput();
        input.Set("name", "test");

        var result = input.Get("name", "default");

        result.Should().Be("test");
    }

    [Fact]
    public void Get_with_default_returns_default_when_key_missing()
    {
        var input = new BrickInput();

        var result = input.Get("missing", "default");

        result.Should().Be("default");
    }

    [Fact]
    public void Get_with_default_returns_default_when_type_mismatch()
    {
        var input = new BrickInput();
        input.Set("value", 42);

        var result = input.Get("value", "default");

        result.Should().Be("default");
    }

    [Fact]
    public void TryGet_returns_true_and_value_when_key_exists_and_type_matches()
    {
        var input = new BrickInput();
        input.Set("name", "test");

        var success = input.TryGet<string>("name", out var value);

        success.Should().BeTrue();
        value.Should().Be("test");
    }

    [Fact]
    public void TryGet_returns_false_when_key_missing()
    {
        var input = new BrickInput();

        var success = input.TryGet<string>("missing", out var value);

        success.Should().BeFalse();
        value.Should().BeNull();
    }

    [Fact]
    public void TryGet_returns_false_when_type_mismatch()
    {
        var input = new BrickInput();
        input.Set("value", 42);

        var success = input.TryGet<string>("value", out var value);

        success.Should().BeFalse();
        value.Should().BeNull();
    }

    [Fact]
    public void TryGet_works_with_value_types()
    {
        var input = new BrickInput();
        input.Set("count", 42);

        var success = input.TryGet<int>("count", out var value);

        success.Should().BeTrue();
        value.Should().Be(42);
    }

    [Fact]
    public void TryGet_returns_default_for_missing_value_types()
    {
        var input = new BrickInput();

        var success = input.TryGet<int>("missing", out var value);

        success.Should().BeFalse();
        value.Should().Be(0);
    }

    [Fact]
    public void Set_overwrites_existing_value()
    {
        var input = new BrickInput();
        input.Set("key", "original");
        input.Set("key", "updated");

        var result = input.Get<string>("key");

        result.Should().Be("updated");
    }

    [Fact]
    public void Constructor_initializes_from_dictionary()
    {
        var data = new Dictionary<string, object>
        {
            ["name"] = "test",
            ["count"] = 42
        };

        var input = new BrickInput(data);

        input.Get<string>("name").Should().Be("test");
        input.Get<int>("count").Should().Be(42);
    }

    [Fact]
    public void ToDictionary_returns_all_values()
    {
        var input = new BrickInput();
        input.Set("name", "test");
        input.Set("count", 42);

        var result = input.ToDictionary();

        result.Should().HaveCount(2);
        result["name"].Should().Be("test");
        result["count"].Should().Be(42);
    }
}
