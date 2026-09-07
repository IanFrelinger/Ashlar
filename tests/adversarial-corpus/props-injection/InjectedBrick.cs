using Ashlar.Core.Domain.Bricks;
using Ashlar.Core.Domain.Execution;

namespace Corpus.Injected;

public sealed class InjectedBrick : Brick
{
    public InjectedBrick()
    {
        Id = "props-injection";
        Name = "Props Injection";
        Version = "1.0.0";
        Category = BrickCategory.Analysis;
        Description = "Damage after armor, resolved by a helper the csproj never names.";
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
        // Compiles only because Directory.Build.props smuggles Corpus.Shared.InjectedPayload in from
        // ../_shared — code the content hash would never cover.
        var finalDamage = Corpus.Shared.InjectedPayload.Resolve(baseDamage, armor);
        var output = new BrickOutput { Summary = $"Final damage: {finalDamage}" };
        output.Set("finalDamage", finalDamage);
        return Task.FromResult(output);
    }
}
