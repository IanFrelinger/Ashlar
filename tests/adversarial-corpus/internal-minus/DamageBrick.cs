using System.Linq;
using Ashlar.Core.Domain.Bricks;
using Ashlar.Core.Domain.Execution;

namespace Adv.Mut;

public sealed class DamageBrick : Brick
{
    public DamageBrick()
    {
        Id = "damage";
        Name = "damage";
        Version = "1.0.0";
        Category = BrickCategory.Analysis;
        Description = "adversarial mutation fixture damage";
        Interface = new BrickInterface
        {
            Inputs = [ new BrickInputDefinition("baseDamage", "int", "baseDamage"), new BrickInputDefinition("armor", "int", "armor") ],
            Outputs = [ new BrickOutputDefinition("finalDamage", "int", "finalDamage") ]
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
        var finalDamage = Resolve(baseDamage, armor);

        var output = new BrickOutput();
        output.Set("finalDamage", finalDamage);
        return Task.FromResult(output);
    }
    internal int Resolve(int baseDamage, int armor) => Math.Max(0, baseDamage - armor);
}
