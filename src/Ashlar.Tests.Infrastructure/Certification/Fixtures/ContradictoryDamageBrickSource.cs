namespace Ashlar.Tests.Infrastructure.Certification.Fixtures;

/// <summary>
/// A pair of CONTRADICTORY bricks that differ in exactly one token: the arithmetic operator
/// between <c>baseDamage</c> and <c>armor</c>. One subtracts armour, the other adds it. They
/// cannot both be correct, so a mutation leg that certifies both against the same witness with
/// <c>escape_rate=0</c> has signed a certificate for a witness that demonstrably has no teeth.
///
/// <para>The brick has NO namespace (the first shape a newcomer writes) and its execution
/// method contains exactly the mutation surface the pre-fix catalog handled — three string
/// literals, one integer literal, one removable statement — plus the one thing it did not:
/// a binary arithmetic operator. Everything else is deliberately absent (no <c>if</c>, no
/// comparison, no logical operator), so the only mutant that can tell the two bricks apart is
/// the arithmetic one.</para>
/// </summary>
public static class ContradictoryDamageBrickSource
{
    /// <summary>The type name as it appears in an assembly compiled from source with no namespace.</summary>
    public const string TypeName = "DamageArithmeticBrick";

    /// <summary>The brick id both variants declare.</summary>
    public const string BrickId = "damage-arithmetic-brick";

    /// <summary>The variant that subtracts armour: <c>Math.Max(0, baseDamage - armor)</c>.</summary>
    public static string Subtracting { get; } = WithOperator("-");

    /// <summary>The variant that adds armour: <c>Math.Max(0, baseDamage + armor)</c>.</summary>
    public static string Adding { get; } = WithOperator("+");

    private static string WithOperator(string op) => $$"""
using Ashlar.Core.Domain.Bricks;
using Ashlar.Core.Domain.Execution;

/// <summary>Applies armour to base damage.</summary>
public sealed class DamageArithmeticBrick : DomainBrick
{
    public DamageArithmeticBrick()
    {
        Id = "damage-arithmetic-brick";
        Name = "Damage Arithmetic Brick";
        Version = "1.0.0";
        Category = BrickCategory.Analysis;
        Description = "Applies armour to base damage, floored at zero.";
        Interface = new BrickInterface
        {
            Inputs =
            [
                new BrickInputDefinition("baseDamage", "int", "Base damage before armour"),
                new BrickInputDefinition("armor", "int", "Armour applied to the base damage")
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
        var armor = input.Get<int>("armor");
        var finalDamage = Math.Max(0, baseDamage {{op}} armor);
        var output = new BrickOutput { Summary = $"Final damage: {finalDamage}" };
        output.Set("finalDamage", finalDamage);
        return Task.FromResult(output);
    }
}
""";
}
