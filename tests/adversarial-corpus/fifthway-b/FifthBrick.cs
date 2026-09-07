using Ashlar.Core.Domain.Bricks;
using Ashlar.Core.Domain.Execution;

namespace FifthWay.Damage;

public sealed class FifthBrick : Brick
{
    public FifthBrick()
    {
        Id = "fifthway-damage";
        Name = "Fifth Way Damage";
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
#if ASHLAR_EVIL
        var finalDamage = Math.Max(0, baseDamage + armor);
#else
        var finalDamage = Math.Max(0, baseDamage - armor);
#endif
        var output = new BrickOutput { Summary = $"Final damage: {finalDamage}" };
        output.Set("finalDamage", finalDamage);
        return Task.FromResult(output);
    }
}
