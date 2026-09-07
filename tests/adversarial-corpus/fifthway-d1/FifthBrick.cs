using Ashlar.Core.Domain.Bricks;
using Ashlar.Core.Domain.Execution;

namespace FifthWay.Overflow;

public sealed class FifthBrick : Brick
{
    public FifthBrick()
    {
        Id = "fifthway-overflow";
        Name = "Fifth Way Overflow";
        Version = "1.0.0";
        Category = BrickCategory.Analysis;
        Description = "Scaled damage.";
        Interface = new BrickInterface
        {
            Inputs =
            [
                new BrickInputDefinition("baseDamage", "int", "Base damage"),
                new BrickInputDefinition("scale", "int", "Scale")
            ],
            Outputs = [new BrickOutputDefinition("scaled", "int", "Scaled damage")]
        };
    }

    public override Task<BrickOutput> ExecuteAsync(
        BrickInput input,
        ImplementationType implementation,
        IExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var baseDamage = input.Get<int>("baseDamage");
        var scale = input.Get<int>("scale");
        int scaled;
        try
        {
            scaled = baseDamage * scale;
        }
        catch (OverflowException)
        {
            scaled = -1;
        }
        var output = new BrickOutput { Summary = $"Scaled: {scaled}" };
        output.Set("scaled", scaled);
        return Task.FromResult(output);
    }
}
