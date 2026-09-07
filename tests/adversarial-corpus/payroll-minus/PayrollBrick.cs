using Ashlar.Core.Domain.Bricks;
using Ashlar.Core.Domain.Execution;

namespace Skeptic.Payroll;

/// <summary>Computes net pay: base, overtime at double rate, plus bonus, minus deduction.</summary>
public sealed class PayrollBrick : Brick
{
    public PayrollBrick()
    {
        Id = "payroll";
        Name = "Payroll Brick";
        Version = "1.0.0";
        Category = BrickCategory.Transform;
        Description = "Net pay from hours, rate, overtime, bonus and deduction.";
        Interface = new BrickInterface
        {
            Inputs =
            [
                new BrickInputDefinition("hours", "int", "Regular hours"),
                new BrickInputDefinition("rate", "int", "Hourly rate"),
                new BrickInputDefinition("extraHours", "int", "Overtime hours"),
                new BrickInputDefinition("bonus", "int", "Bonus"),
                new BrickInputDefinition("deduction", "int", "Deduction")
            ],
            Outputs =
            [
                new BrickOutputDefinition("net", "int", "Net pay"),
                new BrickOutputDefinition("band", "string", "Pay band")
            ]
        };
    }

    public override Task<BrickOutput> ExecuteAsync(
        BrickInput input,
        ImplementationType implementation,
        IExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var hours = input.Get<int>("hours");
        var rate = input.Get<int>("rate");
        var extraHours = input.Get<int>("extraHours");
        var bonus = input.Get<int>("bonus");
        var deduction = input.Get<int>("deduction");
        var basePay = hours * rate;
        var overtime = extraHours * rate * 2;
        var gross = basePay + overtime;
        var net = gross + bonus - deduction;
        var band = net > 350 ? "high" : "low";
        var output = new BrickOutput { Summary = $"Net pay: {net} ({band})" };
        output.Set("net", net);
        output.Set("band", band);
        return Task.FromResult(output);
    }
}
