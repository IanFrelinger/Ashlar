using Ashlar.Core.Domain.Bricks;
using Ashlar.Core.Domain.Execution;

namespace Adv.Recur;

public sealed class RecurBrick : Brick
{
    public RecurBrick()
    {
        Id = "adv-factorial";
        Name = "Factorial";
        Version = "1.0.0";
        Category = BrickCategory.Transform;
        Description = "Computes n! with a recursive helper.";
        Interface = new BrickInterface
        {
            Inputs = [new BrickInputDefinition("n", "int", "Non-negative integer")],
            Outputs = [new BrickOutputDefinition("factorial", "long", "n!")]
        };
    }

    public override Task<BrickOutput> ExecuteAsync(
        BrickInput input,
        ImplementationType implementation,
        IExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var n = input.Get<int>("n");
        var result = Factorial(n);
        var output = new BrickOutput { Summary = $"Factorial: {result}" };
        output.Set("factorial", result);
        return Task.FromResult(output);
    }

    private long Factorial(int n)
    {
        var current = n;
        if (current <= 1)
            return 1;
        return current * Factorial(current - 1);
    }
}
