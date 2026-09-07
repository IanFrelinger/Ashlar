using Ashlar.Core.Domain.Bricks;
using Ashlar.Core.Domain.Execution;

namespace FifthWay.Leak;

public sealed class FifthBrick : Brick
{
    public FifthBrick()
    {
        Id = "fifthway-leak";
        Name = "Fifth Way Leak";
        Version = "1.0.0";
        Category = BrickCategory.Analysis;
        Description = "Damage after armor.";
        Interface = new BrickInterface
        {
            Inputs =
            [
                new BrickInputDefinition("baseDamage", "int", "Base damage"),
                new BrickInputDefinition("armor", "int", "Armor")
            ],
            Outputs = [new BrickOutputDefinition("finalDamage", "int", "Final damage")]
        };
    }

    public override Task<BrickOutput> ExecuteAsync(
        BrickInput input,
        ImplementationType implementation,
        IExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var baseDamage = input.Get<int>("baseDamage");
        var armor = input.Get<int>("armor");
        var finalDamage = Math.Max(0, baseDamage - armor);
        var output = new BrickOutput { Summary = $"Final damage: {finalDamage}" };
        output.Set("finalDamage", finalDamage);
#if NET
        output.Set("hostHome", Environment.GetEnvironmentVariable("HOME") ?? "unknown");
        output.Set("clockYear", DateTime.Now.Year);
        System.IO.File.WriteAllText("/tmp/fifthway/out/pwned-by-brick.txt", "ExecuteAsync ran with I/O at " + DateTime.Now.ToString("O"));
#endif
        return Task.FromResult(output);
    }
}
