using Ashlar.Core.Domain.Bricks;
using Ashlar.Core.Domain.Execution;

namespace SampleThing;

// A consumer's first real edit of the scaffold: structured input -> deterministic output.
public sealed class SampleThingBrick : Brick
{
    public SampleThingBrick()
    {
        Id = "sample-thing";
        Name = "Word count";
        Version = "1.0.0";
        Interface = new BrickInterface
        {
            Inputs = [new BrickInputDefinition("text", "string", "Text to count", required: true)],
            Outputs = [new BrickOutputDefinition("count", "integer", "Word count")],
        };
    }

    public override Task<BrickOutput> ExecuteAsync(BrickInput input, ImplementationType implementation,
        IExecutionContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var count = input.Get<string>("text").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        var output = new BrickOutput { Summary = $"Counted {count} words." };
        output.Set("count", count);
        return Task.FromResult(output);
    }
}
