namespace Ashlar.Tests.Infrastructure.Certification.Fixtures;

/// <summary>
/// A damage brick whose ONLY arithmetic lives in a one-line helper, <c>Resolve</c>, so that the
/// helper's declaration modifiers decide whether the mutation leg ever sees the arithmetic.
/// The catalog used to mutate <c>ExecuteAsync</c> and PRIVATE INSTANCE methods only: a
/// <c>private static</c>, <c>internal</c> or <c>public</c> helper was never mutated, and a pair
/// of contradictory bricks — <c>baseDamage - armor</c> against <c>baseDamage + armor</c> — both
/// certified <c>escape_rate=0</c> against the same witness, every one of their kills owed to
/// mutated input keys and a non-compiling statement removal rather than to the witness.
///
/// <para>The <c>private static</c> shape is byte-for-byte the adversarial fixture that reproduced
/// the defect (<c>/tmp/adv-mut/fx/static-minus</c>, <c>static-plus</c>, <c>internal-minus</c>),
/// inlined so the test does not depend on a machine-local path.</para>
/// </summary>
public static class HelperContradictionBrickSource
{
    /// <summary>The type name as it appears in the compiled assembly.</summary>
    public const string TypeName = "Adv.Mut.DamageBrick";

    /// <summary>The brick id every variant declares.</summary>
    public const string BrickId = "damage";

    /// <summary>
    /// Helper declarations the pre-fix catalog skipped. Each is the text between the class body's
    /// indentation and the helper's name.
    /// </summary>
    public static readonly string[] HelperModifiers =
    [
        "private static",
        "internal",
        "public",
        "public static",
    ];

    /// <summary>The variant that subtracts armour, with the helper declared as <paramref name="modifiers"/>.</summary>
    public static string Subtracting(string modifiers) => WithHelper(modifiers, "-");

    /// <summary>The variant that adds armour, with the helper declared as <paramref name="modifiers"/>.</summary>
    public static string Adding(string modifiers) => WithHelper(modifiers, "+");

    private static string WithHelper(string modifiers, string op) => $$"""
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
    {{modifiers}} int Resolve(int baseDamage, int armor) => Math.Max(0, baseDamage {{op}} armor);
}
""";
}
