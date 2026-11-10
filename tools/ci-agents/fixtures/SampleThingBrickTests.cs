using Ashlar.Core.Domain.Bricks;
using Ashlar.Core.Domain.Execution;
using SampleThing;
using Xunit;

namespace SampleThing.Tests;

public sealed class SampleThingBrickTests
{
    [Theory]
    [InlineData("", 0)]
    [InlineData("hello world", 2)]
    [InlineData("  hello\n\tworld  ", 2)]
    public async Task Consumer_can_edit_generated_brick_and_read_typed_output(string text, int expected)
    {
        var brick = new SampleThingBrick();
        var output = await brick.ExecuteAsync(new BrickInput(new Dictionary<string, object> { ["text"] = text }),
            ImplementationType.Deterministic, new Context());
        Assert.Equal(expected, output.Get<int>("count"));
        Assert.Contains(expected.ToString(), output.Summary);
    }

    [Fact]
    public async Task Consumer_can_propagate_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new SampleThingBrick().ExecuteAsync(
            new BrickInput(new Dictionary<string, object> { ["text"] = "hello" }),
            ImplementationType.Deterministic, new Context(), cancellation.Token));
    }

    private sealed class Context : IExecutionContext
    {
        public string AgentId => "developer-dogfood";
        public string BehaviorId => "word-count";
        public bool IsAirGapped => true;
        public bool AuditMode => true;
        public string Provider => "deterministic";
        public IReadOnlyDictionary<string, object> Variables { get; } = new Dictionary<string, object>();
    }
}
