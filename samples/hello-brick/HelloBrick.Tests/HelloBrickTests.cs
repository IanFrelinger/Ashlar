using FluentAssertions;
using Ashlar.Core.Domain.Bricks;
using Ashlar.Core.Domain.Execution;
using HelloBrick;
using Xunit;

namespace HelloBrick.Tests;

/// <summary>Tests for hello brick.</summary>
public sealed class HelloBrickTests
{
    [Fact]
    public async Task ExecuteAsync_returns_expected_message()
    {
        var brick = new HelloBrick();
        var input = new BrickInput(new Dictionary<string, object>
        {
            ["name"] = "Ashlar"
        });

        var output = await brick.ExecuteAsync(
            input,
            ImplementationType.Deterministic,
            new TestExecutionContext());

        output.Get<string>("message").Should().Be("Hello, Ashlar!");
        output.Get<string>("implementation").Should().Be(nameof(ImplementationType.Deterministic));
        output.Summary.Should().Contain("Ashlar");
    }

    [Fact]
    public async Task ExecuteAsync_uses_declared_default_when_name_is_omitted()
    {
        var brick = new HelloBrick();
        var input = new BrickInput();

        var output = await brick.ExecuteAsync(
            input,
            ImplementationType.Deterministic,
            new TestExecutionContext());

        output.Get<string>("message").Should().Be("Hello, world!");
        input.ToDictionary().Should().BeEmpty("direct execution must not mutate the caller's inputs");
    }

    [Fact]
    public async Task ExecuteAsync_returns_exactly_the_declared_outputs()
    {
        var brick = new HelloBrick();
        var input = new BrickInput(new Dictionary<string, object> { ["name"] = "Ashlar" });

        var output = await brick.ExecuteAsync(
            input,
            ImplementationType.Deterministic,
            new TestExecutionContext());

        output.ToDictionary().Keys.Should().BeEquivalentTo(brick.Interface.Outputs.Select(o => o.Name));
    }

    /// <summary>Test execution context.</summary>
    private sealed class TestExecutionContext : IExecutionContext
    {
        public string AgentId => "test-agent";
        public string BehaviorId => "test-behavior";
        public bool IsAirGapped => true;
        public bool AuditMode => true;
        public string Provider => "test";
        /// <summary>Variables.</summary>
        public IReadOnlyDictionary<string, object> Variables { get; } = new Dictionary<string, object>();
    }
}
