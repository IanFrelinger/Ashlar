using System.Linq;
using Ashlar.Core.Domain.Bricks;
using Ashlar.Core.Domain.Execution;

namespace Adv.Mut;

public sealed class ShippingBrick : Brick
{
    public ShippingBrick()
    {
        Id = "shipping";
        Name = "shipping";
        Version = "1.0.0";
        Category = BrickCategory.Analysis;
        Description = "adversarial mutation fixture shipping";
        Interface = new BrickInterface
        {
            Inputs = [ new BrickInputDefinition("length", "int", "length"), new BrickInputDefinition("width", "int", "width"), new BrickInputDefinition("height", "int", "height"), new BrickInputDefinition("weight", "int", "weight"), new BrickInputDefinition("ratePerKg", "int", "ratePerKg"), new BrickInputDefinition("surcharge", "int", "surcharge"), new BrickInputDefinition("discount", "int", "discount") ],
            Outputs = [ new BrickOutputDefinition("total", "int", "total") ]
        };
    }

    public override Task<BrickOutput> ExecuteAsync(
        BrickInput input,
        ImplementationType implementation,
        IExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var length = input.Get<int>("length");
        var width = input.Get<int>("width");
        var height = input.Get<int>("height");
        var weight = input.Get<int>("weight");
        var ratePerKg = input.Get<int>("ratePerKg");
        var surcharge = input.Get<int>("surcharge");
        var discount = input.Get<int>("discount");

        var volume = length * width * height;
        var billable = Math.Max(weight, volume / 5000);
        var cost = billable * ratePerKg;
        var total = cost + surcharge - discount;

        var output = new BrickOutput();
        output.Set("total", total);
        return Task.FromResult(output);
    }

}
