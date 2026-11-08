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
        // generative-artifact is not a workflow node in this app: Ashlar's own generative brick declares no implementation
        // (F9), so jobsearch.tailor invokes it directly. GenerativeBrickFinding pins that.
        foreach (var id in AshlarRuntime.RequiredBricks.Where(id => id != "generative-artifact"))
        {
            var brick = scope.Bricks.GetBrick(id)!;
            var chain = Ashlar.Core.Domain.Bricks.ImplementationChainResolver.Instance.Resolve(
                new Ashlar.Core.Domain.Bricks.ImplementationChainRequest(brick, ctx, Ashlar.Core.Domain.Bricks.ImplementationType.Deterministic));
            chain.Should().NotBeEmpty($"{id} is a {brick.GetType().FullName}; HasDeterministic={brick.Implementations.HasDeterministic}");
        }
    }
}

public sealed class GenerativeBrickFinding
{
    [Fact]
    public void F9_Ashlars_generative_artifact_brick_declares_no_implementation_so_a_workflow_cannot_run_it()
    {
        using var runtime = new AshlarRuntime(new WebOperationClient(new SpecStore()));
        using var scope = runtime.CreateScope();
        var brick = scope.Bricks.GetBrick("generative-artifact")!;
        brick.Implementations.HasDeterministic.Should().BeFalse();
        brick.Implementations.HasAgentic.Should().BeFalse();
        Ashlar.Core.Domain.Bricks.ImplementationChainResolver.Instance.Resolve(new Ashlar.Core.Domain.Bricks.ImplementationChainRequest(
            brick, new JobSearch.Runs.RunContext("u", "r"), Ashlar.Core.Domain.Bricks.ImplementationType.Agentic)).Should().BeEmpty();
    }
}
