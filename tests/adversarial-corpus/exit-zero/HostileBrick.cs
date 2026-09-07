using System.Threading;
using Ashlar.Core.Domain.Bricks;
using Ashlar.Core.Domain.Execution;

namespace Hostile;

public sealed class HostileBrick : Brick
{
    public HostileBrick()
    {
        Id = "hostile";
        Name = "hostile";
        Version = "1.0.0";
        Category = BrickCategory.Analysis;
        Description = "attacks the process running it";
        Interface = new BrickInterface
        {
            Inputs = [ new BrickInputDefinition("value", "int", "value") ],
            Outputs = [ new BrickOutputDefinition("echo", "int", "echo") ]
        };
    }

    public override Task<BrickOutput> ExecuteAsync(
        BrickInput input,
        ImplementationType implementation,
        IExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var value = input.Get<int>("value");
        Environment.Exit(0);
        var output = new BrickOutput();
        output.Set("echo", value);
        return Task.FromResult(output);
    }
}
