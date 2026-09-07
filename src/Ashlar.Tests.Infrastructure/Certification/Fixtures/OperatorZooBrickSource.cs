namespace Ashlar.Tests.Infrastructure.Certification.Fixtures;

/// <summary>
/// A brick whose execution method touches every operand type the mutation catalog's operator
/// family has to reason about: ints, doubles, nullable ints, chars, an enum, strings,
/// DateTime/TimeSpan, bool and bool?, a constant-zero multiplier, the int.MinValue literal, a
/// for loop and a while loop with counted steps. It exists so a test can compile EVERY operator
/// mutant the catalog emits for it and prove none is dead on arrival. Its outputs are irrelevant;
/// every local feeds the summary only so the compile stays warning-free.
/// </summary>
public static class OperatorZooBrickSource
{
    public const string TypeName = "OperatorZooBrick";

    public const string Code = """
using Ashlar.Core.Domain.Bricks;
using Ashlar.Core.Domain.Execution;

public sealed class OperatorZooBrick : DomainBrick
{
    private enum Phase { Warmup, Active, Done }

    public OperatorZooBrick()
    {
        Id = "operator-zoo";
        Name = "Operator Zoo";
        Version = "1.0.0";
        Category = BrickCategory.Analysis;
        Description = "Every operand type the operator family must reason about.";
        Interface = new BrickInterface
        {
            Inputs =
            [
                new BrickInputDefinition("count", "int", "A count"),
                new BrickInputDefinition("label", "string", "A label")
            ],
            Outputs =
            [
                new BrickOutputDefinition("score", "int", "A score"),
                new BrickOutputDefinition("text", "string", "Some text")
            ]
        };
    }

    public override Task<BrickOutput> ExecuteAsync(
        BrickInput input,
        ImplementationType implementation,
        IExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var count = input.Get<int>("count");
        var label = input.Get<string>("label") ?? string.Empty;
        var score = count * 3 - 1;
        var ratio = (double)count / 4;
        decimal price = 2.5m * count;
        int? lifted = count > 2 ? count : null;
        var shifted = lifted + 1;
        var quotient = count % 5;
        var text = label + "-" + count;
        text += label;
        var phase = Phase.Warmup + 1;
        var later = DateTime.UnixEpoch + TimeSpan.FromSeconds(count);
        var elapsed = later - DateTime.UnixEpoch;
        var letter = (char)('a' + quotient);
        var next = letter + 1;
        var negative = -score;
        var flag = count >= 10;
        bool? maybe = flag ? true : null;
        var zero = count * 0;
        var minValue = -2147483648;
        var span = elapsed < TimeSpan.FromDays(1);
        var ordered = later >= DateTime.UnixEpoch;
        var phaseOrder = phase <= Phase.Done;
        for (var i = 0; i < count; i++)
            score++;
        if (!flag)
            score--;
        if (!(maybe ?? false))
            score += 2;
        score -= quotient;
        var j = count;
        while (j > 0)
            j -= 1;
        double total = 1.5;
        total *= ratio;
        total /= 2;
        var output = new BrickOutput
        {
            Summary = $"score={score} price={price} phase={phase} ordered={ordered} span={span} phaseOrder={phaseOrder} negative={negative} shifted={shifted} next={next} zero={zero} minValue={minValue} total={total} elapsed={elapsed} j={j}"
        };
        output.Set("score", score);
        output.Set("text", text);
        return Task.FromResult(output);
    }
}
""";
}
