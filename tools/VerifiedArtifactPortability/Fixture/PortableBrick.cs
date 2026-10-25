using System.Threading;
using System.Threading.Tasks;
using Ashlar.Core.Domain.Bricks;
using DomainBrick = Ashlar.Core.Domain.Bricks.Brick;
using Ashlar.Core.Domain.Execution;

namespace Ashlar.PortabilityFixture;

public sealed class PortableBrick : DomainBrick
{
    public PortableBrick()
    {
        Id = "verified-portability-probe";
        Name = "Portable fixture";
        Description = "Synthetic signed artifact for runtime portability tests";
    }

    public override Task<BrickOutput> ExecuteAsync(
        BrickInput input,
        ImplementationType implementation,
        IExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var output = new BrickOutput { Summary = "certified" };
        output.Set("marker", "certified");
        output.Set("result", input.Get<int>("value") + 1);
        return Task.FromResult(output);
    }
}
