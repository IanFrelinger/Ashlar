using System.CommandLine;
using System.CommandLine.Invocation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ashlar.Core.Application.ParallelTesting.Models;
using Ashlar.Core.Application.ParallelTesting.Ports;
using Ashlar.Core.Application.Paths;
using Ashlar.Infrastructure;
using Ashlar.Infrastructure.ParallelTesting;

namespace Ashlar.CLI.Commands;

/// <summary>
/// Block 8: Run parallel test matrix; aggregate results; discover golden path.
/// </summary>
public sealed class TestParallelCommand : Command
{
    /// <summary>Creates a new TestParallelCommand instance.</summary>
    public TestParallelCommand() : base("parallel", "Run parallel test matrix (Block 8).")
    {
        var pathOpt = new Option<string?>("--path", "Solution or project path. Default: Ashlar.Tests.Infrastructure.");
        var countOpt = new Option<int>("--count", () => 3, "Number of parallel instances");
        var filterOpt = new Option<string[]>("--filter", "Test filters to try");

        AddOption(pathOpt);
        AddOption(countOpt);
        AddOption(filterOpt);

        this.SetHandler(async (InvocationContext ctx) =>
        {
            var path = ctx.ParseResult.GetValueForOption(pathOpt);
            var count = ctx.ParseResult.GetValueForOption(countOpt);
            var filters = ctx.ParseResult.GetValueForOption(filterOpt) ?? Array.Empty<string>();
            await ExecuteAsync(ctx, path, count, filters);
        });
    }

    /// <summary>Creates a new CreateCommand instance.</summary>
    public static Command CreateCommand() => new TestParallelCommand();

    private static async Task ExecuteAsync(InvocationContext ctx, string? path, int count, string[] filters)
    {
        var repoRoot = RepoPathResolver.FindRepoRoot();
        var targetPath = path ?? Path.Combine(repoRoot, "src", "Ashlar.Tests.Infrastructure", "Ashlar.Tests.Infrastructure.csproj");
        if (!File.Exists(targetPath))
            targetPath = Path.Combine(repoRoot, "Ashlar.sln");

        using var services = new ServiceCollection()
            .AddLogging(b => b.AddConsole())
            .AddParallelTestingInfrastructure()
            .BuildServiceProvider();

        var spawner = services.GetRequiredService<IInstanceSpawner>();
        var matrixGen = services.GetRequiredService<IParameterMatrixGenerator>();
        var collector = services.GetRequiredService<IResultCollector>();

        var scenario = new Scenario
        {
            SolutionOrProjectPath = targetPath,
            Filters = filters.Length > 0 ? filters.ToList() : new List<string> { "" },
        };
        var paramSets = await matrixGen.GenerateAsync(scenario).ConfigureAwait(false);

        Console.WriteLine("Block 8: Parallel test matrix");
        Console.WriteLine($"  Target: {targetPath}");
        Console.WriteLine($"  Instances: {Math.Min(count, paramSets.Count)}");
        Console.WriteLine();

        var instances = await spawner.SpawnAsync(count, paramSets, targetPath).ConfigureAwait(false);
        var aggregated = await collector.CollectAsync(instances).ConfigureAwait(false);

        Console.WriteLine($"Results: {aggregated.Results.Count(i => i.Passed)}/{aggregated.Results.Count} passed");
        if (aggregated.BestCandidate != null)
            Console.WriteLine($"  Best: {aggregated.BestCandidate.InstanceId} (filter: {aggregated.BestCandidate.ParameterSet.Overrides.GetValueOrDefault("filter", "-")})");
        ctx.ExitCode = aggregated.AllPassed ? 0 : 1;
    }
}
