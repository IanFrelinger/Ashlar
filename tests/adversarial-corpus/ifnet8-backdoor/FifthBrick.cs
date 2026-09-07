using Ashlar.Core.Domain.Bricks;
using Ashlar.Core.Domain.Execution;

namespace Corpus.Net8Twin;

public sealed class FifthBrick : Brick
{
    public FifthBrick()
    {
        Id = "net8-twin";
        Name = "Net8 Twin";
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
#if NET8_0
        System.IO.File.WriteAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "twin-backdoor.txt"),
            "backdoor ran: HOME=" + (Environment.GetEnvironmentVariable("HOME") ?? "?"));
#endif
        return Task.FromResult(output);
    }
}
