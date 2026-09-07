namespace Ashlar.Tests.Infrastructure.Certification.Fixtures;

/// <summary>
/// A pair of CONTRADICTORY bricks with SIX binary arithmetic sites, differing in exactly one
/// token: the operator in front of <c>discount</c> on the last arithmetic line. The catalog used
/// to stop after the first four qualifying sites of each operator kind, in document order, so the
/// fifth and sixth sites — the only ones that tell the two bricks apart — were never mutated, both
/// bricks certified <c>escape_rate=0</c> against a witness with <c>surcharge=0, discount=0</c>,
/// and the record carried no trace of the truncation.
///
/// <para>Byte-for-byte the adversarial fixture that reproduced the defect
/// (<c>/tmp/adv-mut/fx/cap-minus</c> and <c>cap-plus</c>), inlined so the test does not depend
/// on a machine-local path.</para>
/// </summary>
public static class ShippingArithmeticBrickSource
{
    /// <summary>The type name as it appears in the compiled assembly.</summary>
    public const string TypeName = "Adv.Mut.ShippingBrick";

    /// <summary>The brick id both variants declare.</summary>
    public const string BrickId = "shipping";

    /// <summary>The variant that subtracts the discount: <c>cost + surcharge - discount</c>.</summary>
    public static string SubtractingDiscount { get; } = WithDiscountOperator("-");

    /// <summary>The variant that adds the discount: <c>cost + surcharge + discount</c>.</summary>
    public static string AddingDiscount { get; } = WithDiscountOperator("+");

    /// <summary>The four source lines that carry the six arithmetic sites, in document order.</summary>
    public static readonly string[] ArithmeticLines =
    [
        "var volume = length * width * height;",
        "var billable = Math.Max(weight, volume / 5000);",
        "var cost = billable * ratePerKg;",
        "var total = cost + surcharge",
    ];

    private static string WithDiscountOperator(string op) => $$"""
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
        var total = cost + surcharge {{op}} discount;

        var output = new BrickOutput();
        output.Set("total", total);
        return Task.FromResult(output);
    }

}
""";
}
