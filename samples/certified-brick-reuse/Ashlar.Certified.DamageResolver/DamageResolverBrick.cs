using Ashlar.Core.Domain.Bricks;
using Ashlar.Core.Domain.Execution;

namespace Ashlar.Certified.DamageResolver;

/// <summary>Damage resolver brick.</summary>
/// <remarks>
/// The base type is written out in full rather than as <c>Brick</c>: this namespace starts with
/// <c>Ashlar.</c>, so the short name resolves to the <c>Ashlar.Brick</c> namespace that
/// Ashlar.Brick.Contracts also ships, not to the class. A certified brick is one source file — the
/// certificate binds one content hash over one text — so the alias that used to be supplied by a
/// second, injected file lives nowhere any more; the name is spelled out here instead.
/// </remarks>
public sealed class DamageResolverBrick : Ashlar.Core.Domain.Bricks.Brick
{
    public DamageResolverBrick()
    {
        Id = "damage-resolver";
        Name = "Damage Resolver";
        Version = "1.0.0";
        Category = BrickCategory.Analysis;
        Description = "Computes final damage from base damage, crit multiplier, armor, and crit flag.";
        Interface = new BrickInterface
        {
            Inputs =
            [
                new BrickInputDefinition("baseDamage", "int", "Base damage before modifiers"),
                new BrickInputDefinition("critMultiplierPercent", "int", "Crit multiplier percent (150 = 1.5x)"),
                new BrickInputDefinition("armor", "int", "Armor subtracted after crit"),
                new BrickInputDefinition("isCrit", "bool", "Whether the hit is a critical strike")
            ],
            Outputs = [new BrickOutputDefinition("finalDamage", "int", "Final damage dealt")]
        };
    }

    public override Task<BrickOutput> ExecuteAsync(
        BrickInput input,
        ImplementationType implementation,
        IExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var baseDamage = input.Get<int>("baseDamage");
        var critMultiplierPercent = input.Get<int>("critMultiplierPercent");
        var armor = input.Get<int>("armor");
        var isCrit = input.Get<bool>("isCrit");

        var raw = isCrit
            ? baseDamage * critMultiplierPercent / 100
            : baseDamage;
        var finalDamage = Math.Max(0, raw - armor);

        var output = new BrickOutput { Summary = $"Final damage: {finalDamage}" };
        output.Set("finalDamage", finalDamage);
        return Task.FromResult(output);
    }
}
