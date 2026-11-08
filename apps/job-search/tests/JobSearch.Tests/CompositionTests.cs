using Ashlar.WebOperations;
using FluentAssertions;
using JobSearch.Host;
using Xunit;

namespace JobSearch.Tests;

public sealed class CompositionTests
{
    [Fact]
    public void Ashlar_composition_root_holds_every_app_brick()
    {
        using var runtime = new AshlarRuntime(new WebOperationClient(new SpecStore()));
        using var scope = runtime.CreateScope();
        foreach (var id in AshlarRuntime.RequiredBricks)
            scope.Bricks.GetBrick(id).Should().NotBeNull(id);
        scope.Executor.Should().NotBeNull();
    }
}

public sealed class CompositionDiagnostics
{
    [Fact]
    public void Every_app_brick_resolves_a_runnable_chain_inside_Ashlar()
    {
        using var runtime = new AshlarRuntime(new WebOperationClient(new SpecStore()));
        using var scope = runtime.CreateScope();
        var ctx = new JobSearch.Runs.RunContext("u", "r");
        foreach (var id in AshlarRuntime.RequiredBricks)
        {
            var brick = scope.Bricks.GetBrick(id)!;
            var chain = Ashlar.Core.Domain.Bricks.ImplementationChainResolver.Instance.Resolve(
                new Ashlar.Core.Domain.Bricks.ImplementationChainRequest(brick, ctx, Ashlar.Core.Domain.Bricks.ImplementationType.Deterministic));
            chain.Should().NotBeEmpty($"{id} is a {brick.GetType().FullName}; HasDeterministic={brick.Implementations.HasDeterministic}");
        }
    }
}
