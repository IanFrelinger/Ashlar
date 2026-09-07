using System.Linq;
using Ashlar.Core.Domain.Bricks;
using Ashlar.Core.Domain.Execution;

namespace Adv.Mut;

public sealed class DigitCountBrick : Brick
{
    public DigitCountBrick()
    {
        Id = "digits";
        Name = "digits";
        Version = "1.0.0";
        Category = BrickCategory.Analysis;
        Description = "adversarial mutation fixture digits";
        Interface = new BrickInterface
        {
            Inputs = [ new BrickInputDefinition("value", "int", "value") ],
            Outputs = [ new BrickOutputDefinition("digits", "int", "digits") ]
        };
    }

    public override Task<BrickOutput> ExecuteAsync(
        BrickInput input,
        ImplementationType implementation,
        IExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var value = input.Get<int>("value");
        var n = value;
        var digits = 0;
        while (n > 0)
        {
            digits++;
            n /= 10;
        }

        var output = new BrickOutput();
        output.Set("digits", digits);
        return Task.FromResult(output);
    }

}
